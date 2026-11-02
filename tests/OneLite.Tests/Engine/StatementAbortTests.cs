using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneLite.Storage.Pager;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// A write statement that is driven by hand and stopped while a pager call is suspended: the completion of that call is still waiting when the
/// statement fails, and the rollback must not mistake it for one of its own.
/// </summary>
public sealed class StatementAbortTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-abort-" + Guid.NewGuid().ToString("N"));

    public StatementAbortTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Exec(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private static long Count(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        Assert.True(st.Step());
        return st.GetInt64(0);
    }

    [Theory]
    [InlineData(ResumeState.JournalOpen)]
    [InlineData(ResumeState.JournalFlush)]
    public void AStatementFailingAfterASuspendedPagerCallRollsBackCleanly(ResumeState stopAfter)
    {
        string path = Path.Combine(_dir, "a" + stopAfter + ".db");
        using var db = Database.Open(path, new DatabaseOptions { CacheSize = -8, YieldEvery = 1 });
        using Connection conn = db.Connect();
        Exec(conn, "create table t(a integer primary key, b blob)");
        Exec(conn, "insert into t select value, zeroblob(300) from generate_series(1, 50)");
        using Statement st = conn.Prepare("insert into t select value + 1000, zeroblob(3000) from generate_series(1, 400)");
        bool aborted = false;
        var phases = new List<ResumeState>();
        StepStatus s;
        for (; ; )
        {
            s = st.Advance();
            if (s != StepStatus.Pending)
            {
                break;
            }

            ResumeState phase = st.PendingIo.Phase;
            phases.Add(phase);
            conn.Pager.ExecuteSync();   // the completion is now waiting for the operation that asked for it
            if (!aborted && phase == stopAfter)
            {
                aborted = true;
                st.AbortForRollback();
            }
        }

        string trace = aborted ? string.Join(",", phases) : "never aborted: " + string.Join(",", phases);
        Assert.True(aborted, trace);
        Assert.True(s == StepStatus.Error, s + " " + st.Error?.Message + " " + trace);
        Assert.NotEqual("InvalidOperationException", st.Error!.GetType().Name);
        Assert.Equal(50, Count(conn, "select count(*) from t"));
    }
}
