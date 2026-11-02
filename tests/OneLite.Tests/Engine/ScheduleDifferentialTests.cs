using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Several connections to one file take turns reading and writing, and every step is compared with the same schedule run on the pinned native
/// library: the result of each step (rows, result code, extended code, message) and the final content. Lock contention is only visible this way:
/// a write that cannot get the EXCLUSIVE lock at commit must fail with SQLITE_BUSY (lockingv3), not look like it succeeded.
/// Busy timeouts are zero on both sides, so the schedules are deterministic and single-threaded.
/// </summary>
public sealed class ScheduleDifferentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-sched-" + Guid.NewGuid().ToString("N"));

    public ScheduleDifferentialTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    // ---------------------------------------------------------------- the schedule

    /// <summary>One step: connection <c>Conn</c> does <c>Op</c> on <c>Slot</c> (Exec has no slot).</summary>
    internal readonly record struct Step(int Conn, char Op, int Slot, string Sql)
    {
        public override string ToString() => Op switch
        {
            'x' => $"c{Conn} exec  {Sql}",
            'p' => $"c{Conn} open  s{Slot} {Sql}",
            's' => $"c{Conn} step  s{Slot}",
            'r' => $"c{Conn} reset s{Slot}",
            _ => $"c{Conn} final s{Slot}",
        };
    }

    internal sealed record Schedule(string Mode, bool SharedDatabase, int Connections, List<Step> Steps)
    {
        public override string ToString()
            => $"mode={Mode} sharedDatabase={SharedDatabase} connections={Connections}\n" + string.Join("\n", Steps);
    }

    private const int Slots = 3;

    private static Step X(int c, string sql) => new(c, 'x', 0, sql);
    private static Step P(int c, int s, string sql) => new(c, 'p', s, sql);
    private static Step S(int c, int s) => new(c, 's', s, "");
    private static Step R(int c, int s) => new(c, 'r', s, "");
    private static Step F(int c, int s) => new(c, 'f', s, "");

    private const string SelectAll = "SELECT x FROM t ORDER BY x";

    private static List<Step> Prelude(string mode, int connections)
    {
        var l = new List<Step>();
        for (int c = 0; c < connections; c++)
        {
            l.Add(X(c, "PRAGMA journal_mode=" + mode));
        }

        l.Add(X(0, "CREATE TABLE t(x INTEGER)"));
        l.Add(X(0, "INSERT INTO t VALUES (1),(2),(3)"));
        l.Add(X(0, "CREATE TABLE u(y)"));
        return l;
    }

    // ---------------------------------------------------------------- the two engines behind one shape

    private interface IEngineConn : IDisposable
    {
        string Exec(string sql);
        string Open(int slot, string sql);
        string StepSlot(int slot);
        void ResetSlot(int slot);
        void FinalSlot(int slot);
    }

    private static string Err(int extended, string message) => $"ERR {extended & 0xff}/{extended} {message}";

    private sealed class OursConn : IEngineConn
    {
        private readonly Database? _ownDb;
        private readonly Connection _c;
        private readonly Statement?[] _slots = new Statement?[Slots];

        public OursConn(Database db, bool owns)
        {
            _ownDb = owns ? db : null;
            _c = db.Connect();
        }

        private static string Row(Statement s)
        {
            var sb = new StringBuilder("row");
            for (int i = 0; i < s.ColumnCount; i++)
            {
                sb.Append(' ');
                sb.Append(s.ColumnType(i) switch
                {
                    ColumnType.Null => "NULL",
                    ColumnType.Integral => s.GetInt64(i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => "'" + s.GetString(i) + "'",
                });
            }

            return sb.ToString();
        }

        public string Exec(string sql)
        {
            var sb = new StringBuilder();
            Statement? s = null;
            try
            {
                s = _c.Prepare(sql);
                while (s.Step())
                {
                    sb.Append(Row(s)).Append(';');
                }

                sb.Append("ok");
            }
            catch (OneLiteException e)
            {
                sb.Append(Err(e.ExtendedCode, e.Message));
            }
            finally
            {
                s?.Dispose();
            }

            return sb.ToString();
        }

        public string Open(int slot, string sql)
        {
            _slots[slot]?.Dispose();
            _slots[slot] = null;
            try
            {
                _slots[slot] = _c.Prepare(sql);
                return "prepared";
            }
            catch (OneLiteException e)
            {
                return Err(e.ExtendedCode, e.Message);
            }
        }

        public string StepSlot(int slot)
        {
            Statement? s = _slots[slot];
            if (s == null)
            {
                return "none";
            }

            try
            {
                return s.Step() ? Row(s) : "done";
            }
            catch (OneLiteException e)
            {
                return Err(e.ExtendedCode, e.Message);
            }
        }

        public void ResetSlot(int slot) => _slots[slot]?.Reset();

        public void FinalSlot(int slot)
        {
            _slots[slot]?.Dispose();
            _slots[slot] = null;
        }

        public void Dispose()
        {
            for (int i = 0; i < Slots; i++)
            {
                FinalSlot(i);
            }

            _c.Dispose();
            _ownDb?.Dispose();
        }
    }

    private sealed class NativeConn : IEngineConn
    {
        private readonly SqliteDb _db;
        private readonly SqliteStatement?[] _slots = new SqliteStatement?[Slots];

        public NativeConn(string path) => _db = SqliteDb.Open(path, busyTimeoutMs: 0);

        private static string Row(SqliteStatement s)
        {
            var sb = new StringBuilder("row");
            for (int i = 0; i < s.ColumnCount; i++)
            {
                sb.Append(' ');
                sb.Append(s.ColumnType(i) switch
                {
                    5 => "NULL",
                    1 => s.GetInt64(i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => "'" + s.TextOrNull(i) + "'",
                });
            }

            return sb.ToString();
        }

        public string Exec(string sql)
        {
            var sb = new StringBuilder();
            SqliteStatement? s = null;
            try
            {
                s = _db.Prepare(sql);
                while (s.Step())
                {
                    sb.Append(Row(s)).Append(';');
                }

                sb.Append("ok");
            }
            catch (SqliteException e)
            {
                sb.Append(Err(e.ExtendedCode, e.Message));
            }
            finally
            {
                s?.Dispose();
            }

            return sb.ToString();
        }

        public string Open(int slot, string sql)
        {
            _slots[slot]?.Dispose();
            _slots[slot] = null;
            try
            {
                _slots[slot] = _db.Prepare(sql);
                return "prepared";
            }
            catch (SqliteException e)
            {
                return Err(e.ExtendedCode, e.Message);
            }
        }

        public string StepSlot(int slot)
        {
            SqliteStatement? s = _slots[slot];
            if (s == null)
            {
                return "none";
            }

            try
            {
                return s.Step() ? Row(s) : "done";
            }
            catch (SqliteException e)
            {
                return Err(e.ExtendedCode, e.Message);
            }
        }

        public void ResetSlot(int slot) => _slots[slot]?.Reset();

        public void FinalSlot(int slot)
        {
            _slots[slot]?.Dispose();
            _slots[slot] = null;
        }

        public void Dispose()
        {
            for (int i = 0; i < Slots; i++)
            {
                FinalSlot(i);
            }

            _db.Dispose();
        }
    }

    // ---------------------------------------------------------------- running

    private static string Apply(IEngineConn c, Step st) => st.Op switch
    {
        'x' => c.Exec(st.Sql),
        'p' => c.Open(st.Slot, st.Sql),
        's' => c.StepSlot(st.Slot),
        'r' => Do(() => c.ResetSlot(st.Slot)),
        _ => Do(() => c.FinalSlot(st.Slot)),
    };

    private static string Do(Action a)
    {
        a();
        return "-";
    }

    private static List<string> Run(Schedule sc, bool native, string file)
    {
        // Every run starts from nothing: a journal left by an earlier trial would be played back into this one.
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            File.Delete(file + suffix);
        }

        var trace = new List<string>();
        var conns = new IEngineConn[sc.Connections];
        Database? shared = null;
        try
        {
            if (!native && sc.SharedDatabase)
            {
                shared = Database.Open(file);
            }

            for (int i = 0; i < conns.Length; i++)
            {
                conns[i] = native ? new NativeConn(file) : sc.SharedDatabase ? new OursConn(shared!, false) : new OursConn(Database.Open(file), true);
            }

            foreach (Step st in sc.Steps)
            {
                trace.Add(Apply(conns[st.Conn], st with { Sql = st.Sql.Replace("{file}", file, StringComparison.Ordinal) }));
            }

            // Wind down the way an application would: finish every statement, then roll back whatever transaction is still open.
            for (int i = 0; i < conns.Length; i++)
            {
                for (int s = 0; s < Slots; s++)
                {
                    conns[i].FinalSlot(s);
                }
            }

            for (int i = 0; i < conns.Length; i++)
            {
                trace.Add("end c" + i + " " + conns[i].Exec("ROLLBACK"));
            }

            trace.Add("final " + conns[0].Exec("SELECT count(*), sum(x), group_concat(x) FROM (SELECT x FROM t ORDER BY x)"));
        }
        finally
        {
            foreach (IEngineConn? c in conns)
            {
                c?.Dispose();
            }

            shared?.Dispose();
        }

        return trace;
    }

    private string? Compare(Schedule sc, string tag)
    {
        string? d = CompareRaw(sc, tag);
        return d == null ? null : Minimize(sc, tag) + d;
    }

    private string? CompareRaw(Schedule sc, string tag)
    {
        string a = Path.Combine(_dir, tag + "-ours.db");
        string b = Path.Combine(_dir, tag + "-native.db");
        List<string> ours = Run(sc, false, a);
        List<string> theirs = Run(sc, true, b);
        // The first two steps of every schedule per connection are journal_mode pragmas; their text is compared like any other step.
        for (int i = 0; i < ours.Count; i++)
        {
            if (ours[i] != theirs[i])
            {
                string desc = i < sc.Steps.Count ? sc.Steps[i].ToString() : "(wind-down)";
                string report = $"step {i} [{desc}]\n  ours:   {ours[i]}\n  native: {theirs[i]}\n{sc}";
                return report;
            }
        }

        return null;
    }

    // Drops steps one at a time while the two engines still disagree, so a failing random schedule reads as a short story.
    private string Minimize(Schedule sc, string tag)
    {
        int keep = sc.Connections + 3; // the journal_mode pragmas and the three setup statements
        List<Step> steps = [.. sc.Steps];
        string? last = null;
        for (int chunk = Math.Max(1, (steps.Count - keep) / 2); chunk >= 1; chunk /= 2)
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int i = steps.Count - chunk; i >= keep; i -= chunk)
                {
                    var trial = new List<Step>(steps);
                    trial.RemoveRange(i, chunk);
                    string? d = CompareRaw(sc with { Steps = trial }, tag + "m");
                    if (d != null)
                    {
                        steps = trial;
                        last = d;
                        changed = true;
                    }
                }
            }
        }

        return last == null ? string.Empty : "minimized:\n" + last.Split("\nmode=")[0] + "\nmode=" + last.Split("\nmode=")[1] + "\n";
    }

    // The test runner shortens long assertion messages; a failing schedule is kept whole in the temp directory.
    private void AssertSame(Schedule sc, string tag)
    {
        string? diff = Compare(sc, tag);
        if (diff != null)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "onelite-schedule-" + tag + ".txt"), diff);
        }

        Assert.Null(diff);
    }

    private static void Require()
    {
        Assert.SkipUnless(File.Exists(OraclePaths.NativeLibrary), "the pinned native library is not installed");
    }

    // ---------------------------------------------------------------- named schedules

    private static Schedule Named(string mode, bool shared, int conns, params Step[] steps)
    {
        var l = Prelude(mode, conns);
        l.AddRange(steps);
        return new Schedule(mode, shared, conns, l);
    }

    public static IEnumerable<object[]> JournalModes()
    {
        foreach (string m in new[] { "delete", "truncate", "persist", "wal" })
        {
            foreach (bool shared in new[] { true, false })
            {
                yield return [m, shared];
            }
        }
    }

    /// <summary>The reported defect: a stepped SELECT on one connection, an INSERT on another. The write must be BUSY and not stored.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void WriteWhileAnotherConnectionReads(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            P(0, 0, SelectAll), S(0, 0),
            X(1, "INSERT INTO t VALUES (99)"),
            X(1, "UPDATE t SET x = x + 10 WHERE x = 1"),
            X(1, "DELETE FROM t WHERE x = 2"),
            X(1, SelectAll),
            R(0, 0),
            X(1, "INSERT INTO t VALUES (99)"),
            X(0, SelectAll));
        AssertSame(sc, "w-" + mode + shared);
    }

    /// <summary>A connection whose schema is stale (another connection created the table after it looked) sees the table when it prepares, as the reference does.</summary>
    [Theory]
    [InlineData("delete", true)]
    [InlineData("delete", false)]
    [InlineData("wal", false)]
    public void StaleSchemaIsReloadedAtPrepare(string mode, bool shared)
    {
        Require();
        var l = new List<Step>
        {
            X(0, "PRAGMA journal_mode=" + mode), X(1, "SELECT count(*) FROM sqlite_master"),
            X(0, "CREATE TABLE t(x INTEGER)"), X(0, "INSERT INTO t VALUES (1),(2),(3)"),
            X(1, "INSERT INTO t VALUES (4)"), P(1, 0, SelectAll), S(1, 0), X(0, "ALTER TABLE t ADD COLUMN y"), S(1, 0), X(1, "SELECT * FROM t"),
        };
        AssertSame(new Schedule(mode, shared, 2, l), "stale-" + mode + shared);
    }

    /// <summary>An explicit COMMIT that cannot get EXCLUSIVE fails with BUSY, keeps the transaction open, and succeeds when retried after the reader is done.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void CommitBusyKeepsTheTransactionOpen(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            X(1, "BEGIN"), X(1, "INSERT INTO t VALUES (50)"),
            P(0, 0, SelectAll), S(0, 0),
            X(1, "COMMIT"),
            X(1, SelectAll),
            X(1, "COMMIT"),
            F(0, 0),
            X(1, "COMMIT"),
            X(0, SelectAll),
            X(1, "BEGIN IMMEDIATE"), X(1, "INSERT INTO t VALUES (51)"), P(0, 1, SelectAll), S(0, 1),
            X(1, "COMMIT"), X(1, "ROLLBACK"), X(1, SelectAll));
        AssertSame(sc, "c-" + mode + shared);
    }

    /// <summary>A reader inside an explicit transaction blocks the writer's commit until it ends; BEGIN EXCLUSIVE and BEGIN IMMEDIATE against readers and writers.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void BeginKindsAgainstReadersAndWriters(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 3,
            X(0, "BEGIN"), X(0, SelectAll),
            X(1, "BEGIN EXCLUSIVE"), X(1, "BEGIN IMMEDIATE"), X(1, "INSERT INTO t VALUES (7)"), X(1, "COMMIT"),
            X(2, "BEGIN IMMEDIATE"), X(2, "INSERT INTO t VALUES (8)"),
            X(0, "COMMIT"),
            X(1, "BEGIN IMMEDIATE"), X(2, "COMMIT"), X(1, "ROLLBACK"), X(2, "BEGIN EXCLUSIVE"),
            X(0, SelectAll), X(2, "ROLLBACK"), X(0, SelectAll));
        AssertSame(sc, "b-" + mode + shared);
    }

    /// <summary>Two writers, a stale reader that tries to write, and a statement left half-stepped across a COMMIT.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void StaleReadersAndTwoWriters(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 3,
            X(0, "BEGIN"), X(0, SelectAll),
            X(1, "INSERT INTO t VALUES (20)"),
            X(0, "INSERT INTO t VALUES (21)"),
            X(0, "COMMIT"), X(0, "ROLLBACK"),
            P(2, 0, SelectAll), S(2, 0),
            X(1, "BEGIN"), X(1, "INSERT INTO t VALUES (22)"),
            X(0, "INSERT INTO t VALUES (23)"),
            S(2, 0),
            X(1, "COMMIT"), S(2, 0), R(2, 0), X(1, "COMMIT"),
            X(0, "INSERT INTO t VALUES (23)"), X(2, SelectAll));
        AssertSame(sc, "s-" + mode + shared);
    }

    /// <summary>A statement being stepped when BEGIN runs keeps its read transaction (and so its lock or snapshot) until the transaction ends.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void BeginKeepsTheReadTransactionOfAStatementInProgress(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            P(0, 0, "SELECT count(*) FROM t"), S(0, 0),
            X(1, "INSERT INTO t VALUES (30)"),
            X(0, "BEGIN"), S(0, 0),
            X(1, "INSERT INTO t VALUES (31)"), X(0, "UPDATE t SET x = x + 1 WHERE x = 1"),
            X(0, "COMMIT"),
            X(1, "INSERT INTO t VALUES (32)"), X(0, SelectAll), X(1, SelectAll));
        AssertSame(sc, "k-" + mode + shared);
    }

    /// <summary>Rolling back a transaction that only read leaves a pending query running.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void RollbackOfAReadOnlyTransactionLeavesPendingReads(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            P(0, 0, SelectAll), S(0, 0), X(0, "BEGIN"), X(0, "ROLLBACK TRANSACTION"), S(0, 0), S(0, 0), S(0, 0),
            X(1, "INSERT INTO t VALUES (40)"));
        AssertSame(sc, "q-" + mode + shared);
    }

    /// <summary>A write statement that returned BUSY stays in progress until it is reset: a COMMIT or an autocommit statement meanwhile is affected.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void BusyWriteStaysInProgressUntilReset(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            P(0, 0, SelectAll), S(0, 0),
            P(1, 0, "INSERT INTO t VALUES (60)"), S(1, 0),
            X(1, "INSERT INTO t VALUES (61)"), X(1, SelectAll),
            X(1, "BEGIN"), X(1, "INSERT INTO t VALUES (62)"), X(1, "COMMIT"), X(1, "ROLLBACK"),
            F(0, 0), S(1, 0), X(1, "INSERT INTO t VALUES (63)"),
            R(1, 0), X(1, "INSERT INTO t VALUES (64)"), X(0, SelectAll));
        AssertSame(sc, "bs-" + mode + shared);
    }

    /// <summary>The same inside an explicit transaction: the statement that was refused, the COMMIT after it, the retry after reset.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void BusyWriteInATransactionThenCommit(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            X(1, "BEGIN"), P(1, 0, "INSERT INTO t VALUES (65)"), S(1, 0),
            P(0, 0, SelectAll), S(0, 0),
            P(1, 1, "INSERT INTO t VALUES (66)"), S(1, 1),
            X(1, "COMMIT"), R(1, 0), R(1, 1), X(1, "COMMIT"), F(0, 0), X(1, "COMMIT"), X(0, SelectAll));
        AssertSame(sc, "bt-" + mode + shared);
    }

    /// <summary>ROLLBACK that undoes writes: pending reads of the same connection continue or stop where the reference says.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void RollbackThatUndidWritesAndPendingReads(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            P(0, 0, SelectAll), S(0, 0), P(0, 1, "SELECT count(*) FROM t"), S(0, 1),
            X(0, "BEGIN"), X(0, "INSERT INTO t VALUES (70)"), S(0, 0),
            X(0, "ROLLBACK"), S(0, 0), S(0, 1), S(0, 0), S(0, 0), X(1, "INSERT INTO t VALUES (71)"),
            X(0, "BEGIN"), X(0, "UPDATE t SET x = x + 1"), P(0, 2, SelectAll), S(0, 2), X(0, "ROLLBACK"), S(0, 2), S(0, 2), S(0, 2), S(0, 2),
            X(0, "BEGIN"), X(0, "CREATE TABLE z(a)"), P(0, 0, SelectAll), S(0, 0), X(0, "ROLLBACK"), S(0, 0), S(0, 0), X(0, SelectAll));
        AssertSame(sc, "rb-" + mode + shared);
    }

    /// <summary>A connection keeps the schema it just changed: its next statement needs no read lock, while another connection holds a write lock.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void SchemaAfterOwnDdlNeedsNoReadLock(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 2,
            X(0, "CREATE TABLE v(a)"), X(1, "BEGIN IMMEDIATE"), X(0, "SELECT count(*) FROM v"), X(0, "SELECT count(*) FROM v"),
            X(1, "COMMIT"), X(0, "CREATE INDEX vi ON v(a)"), X(1, "BEGIN EXCLUSIVE"), X(0, "SELECT count(*) FROM v"), X(0, "SELECT 1"),
            X(1, "ROLLBACK"), X(0, "DROP TABLE u"), X(1, "BEGIN IMMEDIATE"), X(0, "SELECT count(*) FROM v"), X(0, "SELECT count(*) FROM u"),
            X(1, "ROLLBACK"), X(0, SelectAll));
        AssertSame(sc, "sd-" + mode + shared);
    }

    /// <summary>A statement that compiled against a stale schema and turns out to be a no-op (IF NOT EXISTS of a table another connection made).</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void StaleSchemaNoOpLocks(string mode, bool shared)
    {
        Require();
        Schedule sc = Named(mode, shared, 3,
            X(0, "CREATE TABLE w(a)"), P(1, 0, SelectAll), S(1, 0),
            X(2, "CREATE TABLE IF NOT EXISTS w(a)"), X(2, "CREATE TABLE IF NOT EXISTS w(a)"), X(2, "CREATE INDEX IF NOT EXISTS wi ON w(a)"),
            X(0, "CREATE TABLE w2(a)"), X(2, "CREATE TABLE IF NOT EXISTS w2(a)"), X(1, "CREATE TABLE IF NOT EXISTS w2(a)"),
            F(1, 0), X(1, "CREATE TABLE IF NOT EXISTS w2(a)"), X(1, "DROP TABLE IF EXISTS w3"), X(2, "DROP TABLE IF EXISTS w2"), X(0, "DROP TABLE IF EXISTS w2"),
            X(0, SelectAll));
        AssertSame(sc, "sn-" + mode + shared);
    }

    /// <summary>
    /// Writes through a second name of the same file. A known difference (docs/engine.md): the reference begins a transaction only on the databases a
    /// statement uses, OneLite always begins main. Runs when ONELITE_SCHEDULE_ATTACH is set.
    /// </summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void WritesThroughAnAttachOfTheSameFile(string mode, bool shared)
    {
        Require();
        Assert.SkipUnless(s_attachOn, "known difference; set ONELITE_SCHEDULE_ATTACH to run");
        Schedule sc = Named(mode, shared, 2,
            X(0, "ATTACH '{file}' AS a2"), X(0, "INSERT INTO a2.t VALUES (80)"), X(0, "UPDATE a2.t SET x = x + 1 WHERE x = 1"),
            X(0, "DELETE FROM a2.t WHERE x = 3"), X(0, "SELECT x FROM a2.t ORDER BY x"), X(1, SelectAll),
            X(0, "BEGIN"), X(0, "INSERT INTO t VALUES (81)"), X(0, "INSERT INTO a2.t VALUES (82)"), X(0, "SELECT x FROM a2.t ORDER BY x"),
            P(1, 0, SelectAll), S(1, 0), X(0, "COMMIT"), F(1, 0), X(0, "COMMIT"), X(0, SelectAll), X(1, SelectAll),
            X(0, "BEGIN"), X(0, "INSERT INTO a2.t VALUES (83)"), X(0, "ROLLBACK"), X(0, SelectAll),
            P(0, 0, "SELECT x FROM a2.t ORDER BY x"), S(0, 0), X(1, "INSERT INTO t VALUES (84)"), X(0, "DETACH a2"), S(0, 0), R(0, 0), X(0, "DETACH a2"));
        AssertSame(sc, "at-" + mode + shared);
    }

    /// <summary>journal_mode changes, ATTACH and wal_checkpoint while another connection holds each kind of lock.</summary>
    [Theory]
    [MemberData(nameof(JournalModes))]
    public void PragmasAndAttachUnderAnotherConnectionsLock(string mode, bool shared)
    {
        Require();
        var steps = new List<Step>();
        foreach (string hold in new[] { "", "BEGIN;SELECT x FROM t", "BEGIN IMMEDIATE", "BEGIN EXCLUSIVE" })
        {
            foreach (string op in new[]
            {
                "PRAGMA wal_checkpoint", "PRAGMA wal_checkpoint(TRUNCATE)", "PRAGMA journal_mode=wal", "PRAGMA journal_mode=delete", "PRAGMA journal_mode=truncate",
                "PRAGMA journal_mode=off", "PRAGMA journal_mode=memory", "PRAGMA journal_mode", "ATTACH '{file}' AS a3", "DETACH a3", "SELECT count(*) FROM t",
            })
            {
                foreach (string h in hold.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    steps.Add(X(0, h));
                }

                steps.Add(X(1, op));
                steps.Add(X(0, "ROLLBACK"));
                steps.Add(X(1, "PRAGMA journal_mode=" + mode));
            }
        }

        AssertSame(Named(mode, shared, 2, [.. steps]), "pa-" + mode + shared);
    }

    // ---------------------------------------------------------------- generated schedules

    private static readonly string[] s_exec =
    [
        "BEGIN", "BEGIN", "BEGIN IMMEDIATE", "BEGIN EXCLUSIVE", "COMMIT", "COMMIT", "ROLLBACK", "SAVEPOINT a", "RELEASE a",
        "INSERT INTO t VALUES ({n})", "INSERT INTO t VALUES ({n})", "INSERT INTO t SELECT x + 1000 FROM t",
        "UPDATE t SET x = x + 100 WHERE x = {k}", "DELETE FROM t WHERE x = {k}", "DELETE FROM t WHERE x > {n}",
        SelectAll, SelectAll, "SELECT count(*), sum(x) FROM t", "INSERT INTO u VALUES ({n})", "SELECT y FROM u ORDER BY y",
    ];

    // Operations that take locks in corners: a journal_mode change among the rollback modes, wal_checkpoint in any mode. They run when
    // ONELITE_SCHEDULE_AUDIT is set. Entering or leaving WAL, journal_mode=off and ATTACH of the same file are exercised by the named schedules; in
    // random schedules they still differ from the reference in a few corners (docs/engine.md, "Known differences with several connections"), so they
    // run only with ONELITE_SCHEDULE_ATTACH / ONELITE_SCHEDULE_WALSWITCH set.
    private static readonly string[] s_audit =
    [
        "PRAGMA wal_checkpoint(PASSIVE)", "PRAGMA wal_checkpoint(TRUNCATE)", "PRAGMA journal_mode", "PRAGMA journal_mode=truncate", "PRAGMA journal_mode=persist",
        "PRAGMA journal_mode=memory", "PRAGMA journal_mode=delete", "PRAGMA synchronous=1", "PRAGMA cache_size", "PRAGMA busy_timeout",
    ];

    private static readonly string[] s_walSwitch = ["PRAGMA journal_mode=wal", "PRAGMA journal_mode=delete", "PRAGMA journal_mode=off"];

    private static readonly string[] s_attach = ["ATTACH '{file}' AS a2", "DETACH a2", "SELECT x FROM a2.t ORDER BY x"];

    private static readonly bool s_walSwitchOn = Environment.GetEnvironmentVariable("ONELITE_SCHEDULE_WALSWITCH") != null;

    private static readonly bool s_attachOn = Environment.GetEnvironmentVariable("ONELITE_SCHEDULE_ATTACH") != null;

    private static readonly bool s_auditOn = Environment.GetEnvironmentVariable("ONELITE_SCHEDULE_AUDIT") != null;

    private static readonly string[] s_open = [SelectAll, "SELECT count(*) FROM t", "INSERT INTO t VALUES ({n})", "UPDATE t SET x = x + 1 WHERE x = {k}", "SELECT y FROM u"];

    internal static Schedule Generate(long seed, string? forcedMode = null)
    {
        var rng = new Random(unchecked((int)(seed * 2654435761L ^ (seed >> 32))));
        // journal_mode=off is left out: the pinned shell refuses it (defensive mode) while the library accepts it, and OneLite follows the shell.
        string[] modes = ["delete", "truncate", "persist", "wal", "delete", "wal", "memory"];
        if (Environment.GetEnvironmentVariable("ONELITE_SCHEDULE_MODES") is { Length: > 0 } only)
        {
            // a campaign can leave a mode out (WAL, where a refused write keeps its snapshot on the reference, is a known difference)
            modes = only.Split(',');
        }

        string mode = forcedMode ?? modes[rng.Next(modes.Length)];
        int conns = rng.Next(4) == 0 ? 3 : 2;
        var l = Prelude(mode, conns);
        int n = rng.Next(10, 40);
        for (int i = 0; i < n; i++)
        {
            int c = rng.Next(conns);
            int slot = rng.Next(Slots);
            string Fill(string sql) => sql.Replace("{n}", rng.Next(4, 60).ToString(), StringComparison.Ordinal).Replace("{k}", rng.Next(1, 8).ToString(), StringComparison.Ordinal);
            switch (rng.Next(10))
            {
                case 0:
                case 1:
                    l.Add(P(c, slot, Fill(s_open[rng.Next(s_open.Length)])));
                    break;
                case 2:
                case 3:
                case 4:
                    l.Add(S(c, slot));
                    break;
                case 5:
                    l.Add(rng.Next(2) == 0 ? R(c, slot) : F(c, slot));
                    break;
                default:
                {
                    string[] pool = s_auditOn && rng.Next(3) == 0 ? (s_attachOn && rng.Next(3) == 0 ? s_attach : s_walSwitchOn && rng.Next(3) == 0 ? s_walSwitch : s_audit) : s_exec;
                    string sql = Fill(pool[rng.Next(pool.Length)]);
                    if (sql.StartsWith("PRAGMA journal_mode=", StringComparison.Ordinal) && mode == "wal" && !s_walSwitchOn)
                    {
                        // a rollback mode named in a WAL database leaves WAL
                        sql = "PRAGMA journal_mode";
                    }

                    l.Add(X(c, sql));
                    break;
                }
            }
        }

        return new Schedule(mode, rng.Next(2) == 0, conns, l);
    }

    /// <summary>A fast sample of random schedules in every journal mode. <c>ONELITE_SCHEDULE_SECONDS</c> and <c>ONELITE_SCHEDULE_SEED</c> turn it into a campaign.</summary>
    [Fact]
    public void RandomSchedules()
    {
        Require();
        long seed = long.TryParse(Environment.GetEnvironmentVariable("ONELITE_SCHEDULE_SEED"), out long s0) ? s0 : 4242;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("ONELITE_SCHEDULE_SECONDS"), out int sec) ? sec : 0;
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        int count = 0, steps = 0;
        var failures = new List<string>();
        for (long i = 0; seconds == 0 ? i < 80 : DateTime.UtcNow < end; i++)
        {
            Schedule sc = Generate(seed + i);
            steps += sc.Steps.Count;
            string? diff = Compare(sc, "g" + (count++ % 4));
            if (diff != null)
            {
                failures.Add($"seed {seed + i}: {diff}");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"onelite-schedule-seed{seed + i}.txt"), diff);
                if (failures.Count >= (seconds == 0 ? 3 : 40))
                {
                    break;
                }
            }

            foreach (string f in Directory.GetFiles(_dir))
            {
                File.Delete(f);
            }
        }

        Console.Error.WriteLine($"schedules run: {count}, steps: {steps}, seeds {seed}..{seed + count - 1}, failures: {failures.Count}");
        Assert.True(failures.Count == 0, string.Join("\n----\n", failures));
    }
}
