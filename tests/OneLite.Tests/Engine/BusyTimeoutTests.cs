using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// <c>PRAGMA busy_timeout</c> (c3ref busy_timeout, busy_handler): a connection with no transaction open waits for a lock up to the timeout; one that already
/// holds a read transaction and wants to write gets SQLITE_BUSY at once, because waiting could deadlock. Outcomes were observed on the pinned reference library.
/// </summary>
public sealed class BusyTimeoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-busy-" + Guid.NewGuid().ToString("N"));

    public BusyTimeoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Run(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private (Database, Connection, Database, Connection) Pair(string mode)
    {
        string path = Path.Combine(_dir, mode + ".db");
        Database da = Database.Open(path);
        Connection a = da.Connect();
        Database db = Database.Open(path);
        Connection b = db.Connect();
        Run(a, "PRAGMA journal_mode=" + mode);
        Run(a, "CREATE TABLE t(x)");
        Run(a, "INSERT INTO t VALUES(1)");
        Run(b, "PRAGMA busy_timeout=300");
        return (da, a, db, b);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("wal")]
    public void AWriterWaitsForTheTimeoutThenGetsBusy(string mode)
    {
        (Database da, Connection a, Database db, Connection b) = Pair(mode);
        using (da)
        using (a)
        using (db)
        using (b)
        {
            Run(a, "BEGIN IMMEDIATE");
            var sw = Stopwatch.StartNew();
            OneLiteException e = Assert.Throws<OneLiteException>(() => Run(b, "BEGIN IMMEDIATE"));
            Assert.Equal(ResultCode.Busy, e.Code);
            Assert.True(sw.ElapsedMilliseconds >= 280, $"gave up after {sw.ElapsedMilliseconds} ms");
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("wal")]
    public void AWriterGetsInOnceTheLockIsReleased(string mode)
    {
        (Database da, Connection a, Database db, Connection b) = Pair(mode);
        using (da)
        using (a)
        using (db)
        using (b)
        {
            Run(a, "BEGIN IMMEDIATE");
            var t = new Thread(() =>
            {
                Thread.Sleep(100);
                Run(a, "COMMIT");
            });
            t.Start();
            Run(b, "BEGIN IMMEDIATE");
            t.Join();
            Run(b, "COMMIT");
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("wal")]
    public void AReaderThatWantsToWriteDoesNotWait(string mode)
    {
        (Database da, Connection a, Database db, Connection b) = Pair(mode);
        using (da)
        using (a)
        using (db)
        using (b)
        {
            Run(a, "BEGIN");
            using Statement reading = a.Prepare("SELECT * FROM t");
            Assert.True(reading.Step());
            Run(b, "BEGIN IMMEDIATE");
            Run(b, "INSERT INTO t VALUES(2)");
            Run(a, "PRAGMA busy_timeout=2000");
            var sw = Stopwatch.StartNew();
            OneLiteException e = Assert.Throws<OneLiteException>(() => Run(a, "INSERT INTO t VALUES(3)"));
            Assert.Equal(ResultCode.Busy, e.Code);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"waited {sw.ElapsedMilliseconds} ms");
        }
    }
}
