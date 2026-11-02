using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RecursiveAI.OneLite.History.Tests;

public class SyntheticHistoryTests
{
    private static readonly IsolationLevel[] Order =
    [
        IsolationLevel.ReadCommitted,
        IsolationLevel.SnapshotIsolation,
        IsolationLevel.Serializable,
        IsolationLevel.StrictSerializable,
    ];

    private static readonly int[] ExpectedPair = [1, 2];

    public static TheoryData<string> CaseNames()
    {
        var d = new TheoryData<string>();
        foreach (var n in Cases.Names)
        {
            d.Add(n);
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void VerdictsAndAnomaliesMatch(string name)
    {
        var c = Cases.All.Single(x => x.Name == name);
        var r = Checker.Check(c.Build(), new CheckOptions { RealtimeVersions = c.RealtimeVersions });
        for (var i = 0; i < Order.Length; i++)
        {
            Assert.True(
                c.Valid[i] == r.IsValid(Order[i]),
                $"{name}: expected {Levels.Name(Order[i])} valid={c.Valid[i]} but found [{string.Join(", ", r.Counts.Select(kv => Levels.Label(kv.Key) + "x" + kv.Value))}]");
        }

        foreach (var e in c.Expect)
        {
            Assert.True(r.Counts.ContainsKey(e), $"{name}: expected {Levels.Label(e)} among [{string.Join(", ", r.Counts.Keys.Select(Levels.Label))}]");
        }

        if (c.Expect.Length == 0)
        {
            Assert.Empty(r.Counts);
        }
    }

    [Fact]
    public void CycleIsMinimalAndNamesTheTransactions()
    {
        var c = Cases.All.Single(x => x.Name == "list write skew G2-item");
        var r = Checker.Check(c.Build());
        var a = r.Examples.Single(x => x.Class == AnomalyClass.G2Item);
        Assert.Equal(2, a.Cycle!.Count);
        Assert.Equal(ExpectedPair, a.Txns.Select(i => c.Build().Txns[i].Id).OrderBy(x => x).ToArray());
        var text = Report.Text(r);
        Assert.Contains("[G2-item]", text);
        Assert.Contains("T1 -rw-> T2", text);
        Assert.Contains("snapshot-isolation: valid", text);
        Assert.Contains("serializable: INVALID", text);
    }

    [Fact]
    public void JsonReportParses()
    {
        var r = Checker.Check(Cases.All.Single(x => x.Name == "list lost update").Build());
        using var doc = JsonDocument.Parse(Report.Json(r));
        var v = doc.RootElement.GetProperty("verdicts");
        Assert.True(v.GetProperty("read-committed").GetProperty("valid").GetBoolean());
        Assert.False(v.GetProperty("snapshot-isolation").GetProperty("valid").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("anomalies").GetArrayLength() > 0);
    }

    [Fact]
    public void FileRoundTripKeepsEverything()
    {
        var path = Path.Combine(Path.GetTempPath(), "history-rt-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            foreach (var c in Cases.All)
            {
                var h = c.Build();
                HistoryIO.Write(path, h);
                var back = HistoryIO.Read(path);
                Assert.Equal(h.Model, back.Model);
                Assert.Equal(h.Txns.Count, back.Txns.Count);
                var a = Checker.Check(h, new CheckOptions { RealtimeVersions = c.RealtimeVersions });
                var b = Checker.Check(back, new CheckOptions { RealtimeVersions = c.RealtimeVersions });
                Assert.Equal(a.Counts.OrderBy(x => x.Key), b.Counts.OrderBy(x => x.Key));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReusedWriteValueIsRejected()
    {
        var h = new History(HistoryModel.ListAppend, [Cases.C(1, Cases.A(1, 7)), Cases.C(2, Cases.A(1, 7))]);
        Assert.Throws<HistoryFormatException>(() => Checker.Check(h));
    }

    [Fact]
    public void InfoOutcomeIsNotACompletedTransaction()
    {
        // An unknown-outcome write that is observed counts as committed, and a real-time edge never leaves it.
        var h = new History(HistoryModel.ListAppend, [Cases.Unknown(1, Cases.A(1, 1)), Cases.At(2, 100, 110, Cases.R(1, 1)), Cases.At(3, 200, 210, Cases.R(1, 1))]);
        var r = Checker.Check(h);
        Assert.True(r.IsValid(IsolationLevel.StrictSerializable));
        Assert.Equal(1, r.EdgeCounts[EdgeKind.Rt]); // only 2 → 3
    }
}

public class RunnerTests
{
    [Theory]
    [InlineData(HistoryModel.ListAppend)]
    [InlineData(HistoryModel.Register)]
    public void InMemoryClientIsStrictSerializable(HistoryModel model)
    {
        var o = new WorkloadOptions { Model = model, Workers = 4, TxnsPerWorker = 1500 };
        var h = Workload.RunThreads(new MemoryClientFactory(), o);
        var r = Checker.Check(h, new CheckOptions { RealtimeVersions = model == HistoryModel.Register });
        Assert.True(r.IsValid(IsolationLevel.StrictSerializable), Report.Text(r));
        Assert.Equal(4 * 1500, h.Txns.Count - 1);
    }

    [Theory]
    [InlineData(HistoryModel.ListAppend)]
    [InlineData(HistoryModel.Register)]
    public void NativeSqliteIsSerializable(HistoryModel model)
    {
        var lib = Environment.GetEnvironmentVariable("ONELITE_SQLITE3_LIB");
        if (string.IsNullOrEmpty(lib))
        {
            Assert.Skip("set ONELITE_SQLITE3_LIB to the sqlite3 shared library to run this test");
        }

        var db = Path.Combine(Path.GetTempPath(), "history-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var f = new SqliteClientFactory(lib, db, true, "wal", SqliteClientKind.Strict);
            var h = Workload.RunThreads(f, new WorkloadOptions { Model = model, Workers = 4, TxnsPerWorker = 400 });
            var r = Checker.Check(h, new CheckOptions { RealtimeVersions = model == HistoryModel.Register });
            Assert.True(r.IsValid(IsolationLevel.StrictSerializable), Report.Text(r));
        }
        finally
        {
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                File.Delete(db + s);
            }
        }
    }

    [Fact]
    public void ShimsAreCaughtAsExpected()
    {
        var lib = Environment.GetEnvironmentVariable("ONELITE_SQLITE3_LIB");
        if (string.IsNullOrEmpty(lib))
        {
            Assert.Skip("set ONELITE_SQLITE3_LIB to the sqlite3 shared library to run this test");
        }

        var db = Path.Combine(Path.GetTempPath(), "history-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var o = new WorkloadOptions { Workers = 6, TxnsPerWorker = 600, ActiveKeys = 3 };
            var bad = Checker.Check(Workload.RunThreads(new SqliteClientFactory(lib, db, true, "wal", SqliteClientKind.NoValidationShim), o));
            Assert.False(bad.IsValid(IsolationLevel.SnapshotIsolation), Report.Text(bad));
            File.Delete(db);
            File.Delete(db + "-wal");
            File.Delete(db + "-shm");
            var si = Checker.Check(Workload.RunThreads(new SqliteClientFactory(lib, db, true, "wal", SqliteClientKind.SnapshotShim), o));
            Assert.True(si.IsValid(IsolationLevel.SnapshotIsolation), Report.Text(si));
            Assert.False(si.IsValid(IsolationLevel.Serializable), Report.Text(si));
        }
        finally
        {
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                File.Delete(db + s);
            }
        }
    }
}
