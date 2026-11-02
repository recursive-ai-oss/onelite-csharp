using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>
/// Locks against a live <c>sqlite3</c> process on the same file: the outcome a peer gets for each lock we hold, and the lock we get while the peer holds one
/// (sqlite.org lockingv3 for the database file, walformat for the shared-memory slots). Needs the pinned shell and a platform where record locks reach other processes.
/// </summary>
public sealed class LockInteropTests : IDisposable
{
    private const VfsOpenOptions Db = VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-lock-" + Guid.NewGuid().ToString("N"));

    public LockInteropTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string MakeDb(string name, bool wal)
    {
        Assert.SkipUnless(FileVfs.HasMultiProcessLocks, "this platform has no cross-process record locks");
        SqliteOracle.Require();
        string path = Path.Combine(_dir, name);
        (int exit, string output) = SqliteOracle.Run(path, (wal ? "pragma journal_mode=wal;" : string.Empty) + "create table t(x); insert into t values(1);");
        Assert.True(exit == 0, output);
        return path;
    }

    private static string Outcome(string db, string sql)
    {
        (int exit, string output) = SqliteOracle.Run(db, sql);
        if (SqliteOracle.IsBusy(output))
        {
            return "BUSY";
        }

        Assert.True(exit == 0, "unexpected sqlite3 failure: " + output);
        return "ok";
    }

    private static IVfsFile Hold(string db, LockLevel level)
    {
        IVfsFile f = FileVfs.Instance.Open(db, Db);
        foreach (LockLevel step in new[] { LockLevel.Shared, LockLevel.Reserved, LockLevel.Pending, LockLevel.Exclusive })
        {
            if (step > level)
            {
                break;
            }

            // A lone connection goes from RESERVED straight to EXCLUSIVE; PENDING is only a stop on the way when asked for.
            if (step == LockLevel.Pending && level == LockLevel.Exclusive)
            {
                continue;
            }

            Assert.True(f.Lock(step), $"we could not take {step}");
        }

        return f;
    }

    [Theory]
    [InlineData(LockLevel.Shared, "ok", "BUSY", "ok")]
    [InlineData(LockLevel.Reserved, "ok", "BUSY", "BUSY")]
    [InlineData(LockLevel.Pending, "BUSY", "BUSY", "BUSY")]
    [InlineData(LockLevel.Exclusive, "BUSY", "BUSY", "BUSY")]
    public void WhatASqlite3PeerSeesWhileWeHoldALock(LockLevel ours, string read, string write, string beginImmediate)
    {
        string db = MakeDb("a-" + ours + ".db", false);
        using IVfsFile f = Hold(db, ours);
        string r = Outcome(db, "select count(*) from t;");
        string w = Outcome(db, "insert into t values(2);");
        string bi = Outcome(db, "begin immediate;");
        OutcomeTable.Add($"| we hold {ours} | sqlite3 read: {r} | sqlite3 write: {w} | sqlite3 BEGIN IMMEDIATE: {bi} |");
        Assert.Equal((read, write, beginImmediate), (r, w, bi));
        f.Unlock(LockLevel.None);
        Assert.Equal("ok", Outcome(db, "insert into t values(3);")); // nothing is left behind
    }

    [Fact]
    public void WeAreBlockedByASqlite3ReaderReservedWriterAndExclusiveWriter()
    {
        string db = MakeDb("b.db", false);

        using (var reader = new SqliteOracle.Session(db))
        {
            reader.Run("begin; select count(*) from t;");
            using IVfsFile f = FileVfs.Instance.Open(db, Db);
            bool shared = f.Lock(LockLevel.Shared);
            bool reserved = f.Lock(LockLevel.Reserved);
            bool exclusive = f.Lock(LockLevel.Exclusive);
            OutcomeTable.Add($"| sqlite3 holds SHARED | we SHARED: {Ok(shared)} | we RESERVED: {Ok(reserved)} | we EXCLUSIVE: {Ok(exclusive)} |");
            Assert.True(shared && reserved);
            Assert.False(exclusive);
            f.Unlock(LockLevel.None);
            reader.Run("rollback;");
        }

        using (var writer = new SqliteOracle.Session(db))
        {
            writer.Run("begin immediate;");
            using IVfsFile f = FileVfs.Instance.Open(db, Db);
            bool shared = f.Lock(LockLevel.Shared);
            bool reserved = f.Lock(LockLevel.Reserved);
            bool check = f.CheckReservedLock();
            bool exclusive = f.Lock(LockLevel.Exclusive);
            OutcomeTable.Add($"| sqlite3 holds RESERVED | we SHARED: {Ok(shared)} | we RESERVED: {Ok(reserved)} | CheckReservedLock: {check} | we EXCLUSIVE: {Ok(exclusive)} |");
            Assert.True(shared);
            Assert.False(reserved);
            Assert.True(check);
            Assert.False(exclusive);
            f.Unlock(LockLevel.None);
            writer.Run("rollback;");
            Assert.False(f.CheckReservedLock());
        }

        using (var excl = new SqliteOracle.Session(db))
        {
            excl.Run("begin exclusive; insert into t values(5);");
            using IVfsFile f = FileVfs.Instance.Open(db, Db);
            bool shared = f.Lock(LockLevel.Shared);
            OutcomeTable.Add($"| sqlite3 holds EXCLUSIVE | we SHARED: {Ok(shared)} | CheckReservedLock: {f.CheckReservedLock()} |");
            Assert.False(shared);
            Assert.True(f.CheckReservedLock());
            excl.Run("rollback;");
            Assert.True(f.Lock(LockLevel.Shared));
        }
    }

    [Fact]
    public void WeAreKeptOutByASqlite3WriterWaitingInPending()
    {
        string db = MakeDb("pending.db", false);
        using var reader = new SqliteOracle.Session(db);
        using var writer = new SqliteOracle.Session(db);
        reader.Run("begin; select count(*) from t;");
        writer.Run(".timeout 10000");
        writer.Send("insert into t values(9);");   // takes PENDING and waits for the reader to leave
        Thread.Sleep(800);
        using IVfsFile f = FileVfs.Instance.Open(db, Db);
        bool shared = f.Lock(LockLevel.Shared);
        OutcomeTable.Add($"| sqlite3 holds PENDING (writer waiting for a reader) | we SHARED: {Ok(shared)} |");
        Assert.False(shared);
        reader.Run("rollback;");
        writer.Wait();
        Assert.True(f.Lock(LockLevel.Shared));
    }

    [Fact]
    public void ClosingAnotherConnectionOfOursKeepsOurLocksVisibleToSqlite3()
    {
        string db = MakeDb("hazard.db", false);
        using IVfsFile a = FileVfs.Instance.Open(db, Db);
        Assert.True(a.Lock(LockLevel.Shared));
        Assert.True(a.Lock(LockLevel.Reserved));
        Assert.Equal("BUSY", Outcome(db, "begin immediate;"));

        IVfsFile b = FileVfs.Instance.Open(db, Db);
        Assert.True(b.Lock(LockLevel.Shared));
        b.Unlock(LockLevel.None);
        b.Dispose();                                    // the close that would drop every lock of the process on a naive implementation

        string after = Outcome(db, "begin immediate;");
        OutcomeTable.Add($"| two connections of ours; one closes while the other holds RESERVED | sqlite3 BEGIN IMMEDIATE: {after} |");
        Assert.Equal("BUSY", after);
        Assert.Equal("ok", Outcome(db, "select count(*) from t;"));
        a.Unlock(LockLevel.None);
        Assert.Equal("ok", Outcome(db, "begin immediate;"));
    }

    [Fact]
    public void ControlCaseASecondDescriptorClosingReallyDropsPosixLocks()
    {
        // Shows the hazard is real here, so the previous test can fail: a plain second open/close of the file by the same process drops its locks.
        string db = MakeDb("control.db", false);
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX locks only");
        using SafeFileHandle h = NativeFile.OpenForLocking(db, true, false, false, out _)!;
        var locks = new PosixRangeLocks(h, writable: true);
        Assert.True(locks.Try(0x40000001L, 1, RangeLockKind.Write));
        Assert.Equal("BUSY", Outcome(db, "begin immediate;"));
        NativeFile.OpenForLocking(db, false, false, false, out _)!.Dispose();
        Assert.Equal("ok", Outcome(db, "begin immediate;"));
    }

    // ---- wal-index slots ----

    private static (SqliteOracle.Session Keeper, IVfsFile File, IVfsShm Shm) OpenLiveWal(string db)
    {
        var keeper = new SqliteOracle.Session(db);
        keeper.Run("begin; select count(*) from t;");   // a read mark and the in-use lock stay held, so the wal-index stays valid
        IVfsFile f = FileVfs.Instance.Open(db, Db);
        IVfsShm shm = f.Shm!;
        Assert.False(shm.Map(0, false).IsEmpty);
        return (keeper, f, shm);
    }

    [Fact]
    public void ReadMarkHeldBySqlite3BlocksOurExclusiveOnThatSlot()
    {
        string db = MakeDb("shm1.db", true);
        (SqliteOracle.Session keeper, IVfsFile f, IVfsShm shm) = OpenLiveWal(db);
        using (keeper)
        using (f)
        {
            var busy = new List<int>();
            for (int slot = 3; slot < 8; slot++)
            {
                if (shm.Lock(slot, 1, ShmLockOptions.Lock | ShmLockOptions.Exclusive))
                {
                    shm.Lock(slot, 1, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);
                }
                else
                {
                    busy.Add(slot);
                }
            }

            OutcomeTable.Add($"| sqlite3 WAL reader holds a read mark | our EXCLUSIVE refused on slots: {string.Join(",", busy)} (of 3..7) |");
            Assert.Single(busy);
            Assert.True(shm.Lock(busy[0], 1, ShmLockOptions.Lock | ShmLockOptions.Shared));   // a shared hold is compatible
            shm.Lock(busy[0], 1, ShmLockOptions.Unlock | ShmLockOptions.Shared);
            keeper.Run("rollback;");
            Assert.True(shm.Lock(3, 5, ShmLockOptions.Lock | ShmLockOptions.Exclusive));
            shm.Lock(3, 5, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);
        }
    }

    [Fact]
    public void WalWriteLockHeldBySqlite3BlocksUs()
    {
        string db = MakeDb("shm2.db", true);
        (SqliteOracle.Session keeper, IVfsFile f, IVfsShm shm) = OpenLiveWal(db);
        using (keeper)
        using (f)
        {
            using var writer = new SqliteOracle.Session(db);
            writer.Run("begin immediate;");
            bool ex = shm.Lock(0, 1, ShmLockOptions.Lock | ShmLockOptions.Exclusive);
            bool sh = shm.Lock(0, 1, ShmLockOptions.Lock | ShmLockOptions.Shared);
            OutcomeTable.Add($"| sqlite3 holds the WAL write lock (BEGIN IMMEDIATE) | we EXCLUSIVE slot 0: {Ok(ex)} | we SHARED slot 0: {Ok(sh)} |");
            Assert.False(ex);
            Assert.False(sh);
            writer.Run("rollback;");
            Assert.True(shm.Lock(0, 1, ShmLockOptions.Lock | ShmLockOptions.Exclusive));
            shm.Lock(0, 1, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);
        }
    }

    [Fact]
    public void WhatASqlite3PeerSeesWhileWeHoldWalLocks()
    {
        string db = MakeDb("shm3.db", true);
        (SqliteOracle.Session keeper, IVfsFile f, IVfsShm shm) = OpenLiveWal(db);
        using (keeper)
        using (f)
        {
            Assert.True(shm.Lock(0, 1, ShmLockOptions.Lock | ShmLockOptions.Exclusive));
            string write = Outcome(db, "insert into t values(2);");
            string read = Outcome(db, "select count(*) from t;");
            OutcomeTable.Add($"| we hold the WAL write lock (slot 0) | sqlite3 write: {write} | sqlite3 read: {read} |");
            Assert.Equal("BUSY", write);
            Assert.Equal("ok", read);
            shm.Lock(0, 1, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);
            Assert.Equal("ok", Outcome(db, "insert into t values(2);"));

            // A checkpoint needs the checkpoint lock (slot 1): while we hold it, a passive checkpoint reports busy in its first column.
            Assert.True(shm.Lock(1, 1, ShmLockOptions.Lock | ShmLockOptions.Exclusive));
            (int _, string ck) = SqliteOracle.Run(db, "pragma wal_checkpoint(PASSIVE);");
            OutcomeTable.Add($"| we hold the checkpoint lock (slot 1) | sqlite3 wal_checkpoint(PASSIVE): {ck.Trim()} |");
            Assert.StartsWith("1|", ck.Trim(), StringComparison.Ordinal);
            shm.Lock(1, 1, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);

            // A read mark we hold shared does not stop the peer's reads.
            Assert.True(shm.Lock(3, 5, ShmLockOptions.Lock | ShmLockOptions.Shared));
            Assert.Equal("ok", Outcome(db, "select count(*) from t;"));
            shm.Lock(3, 5, ShmLockOptions.Unlock | ShmLockOptions.Shared);
        }
    }

    [Fact]
    public async Task TheMappedWalIndexShowsAPeersCommit()
    {
        // The wal-index we map is the memory sqlite3 writes: after a peer commits, the frame-count word (walformat: mxFrame at byte 16 of the header) has moved.
        string db = MakeDb("shm4.db", true);
        (SqliteOracle.Session keeper, IVfsFile f, IVfsShm shm) = OpenLiveWal(db);
        using (keeper)
        using (f)
        {
            ShmRegion region = shm.Map(0, false);
            uint before = ShmAccessor.ReadUInt32(region.Memory, 16);
            Assert.Equal("ok", Outcome(db, "insert into t values(7);"));
            await Task.Yield();
            uint after = ShmAccessor.ReadUInt32(region.Memory, 16);
            OutcomeTable.Add($"| sqlite3 appends a WAL frame | mxFrame word in the mapped wal-index: {before} -> {after} |");
            Assert.True(after > before);
        }
    }

    private static string Ok(bool granted) => granted ? "ok" : "BUSY";
}
