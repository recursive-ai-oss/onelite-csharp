using SQLitePCL;

namespace RecursiveAI.OneLite.SQLitePCLRaw.Tests;

public class TailProbe
{
    [Fact]
    public void TailIsTheRest()
    {
        OneLiteBatteries.Init();
        Assert.Equal(0, raw.sqlite3_open(":memory:", out sqlite3 db));
        Assert.Equal(0, raw.sqlite3_prepare_v2(db, "CREATE TABLE t(a); INSERT INTO t VALUES(1);", out sqlite3_stmt st, out string tail));
        Assert.Equal(" INSERT INTO t VALUES(1);", tail);
        raw.sqlite3_finalize(st);
        Assert.Equal(0, raw.sqlite3_prepare_v2(db, "INSERT INTO t VALUES(1);", out st, out string tail2) - 1);
        Assert.Equal("", tail2 ?? "");
    }

    [Fact]
    public void UnclosedHandlesAreFinalizedSafely()
    {
        OneLiteBatteries.Init();
        Leak();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static void Leak()
    {
        Assert.Equal(0, raw.sqlite3_open(":memory:", out sqlite3 db));
        Assert.Equal(0, raw.sqlite3_prepare_v2(db, "CREATE TABLE t(a)", out sqlite3_stmt st, out string _));
        Assert.Equal(101, raw.sqlite3_step(st));
        GC.KeepAlive(st);
    }
}
