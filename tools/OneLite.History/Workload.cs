using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RecursiveAI.OneLite.History;

/// <summary>Shape of the random workload.</summary>
public sealed class WorkloadOptions
{
    /// <summary>List-append or register. Default list-append.</summary>
    public HistoryModel Model { get; set; } = HistoryModel.ListAppend;

    /// <summary>Concurrent clients (threads or processes). Default 4. At most 63: the worker number is packed into every written value.</summary>
    public int Workers { get; set; } = 4;

    /// <summary>Transactions each client runs. Default 1000.</summary>
    public int TxnsPerWorker { get; set; } = 1000;

    /// <summary>Fewest operations per transaction. Default 1.</summary>
    public int MinOps { get; set; } = 1;

    /// <summary>Most operations per transaction. Default 4.</summary>
    public int MaxOps { get; set; } = 4;

    /// <summary>Probability that an operation is a read. Default 0.5.</summary>
    public double ReadFraction { get; set; } = 0.5;

    /// <summary>Keys in play at once. Smaller means more contention. Default 4.</summary>
    public int ActiveKeys { get; set; } = 4;

    /// <summary>Each client moves its key window up by one every this many transactions, which retires keys so lists stay short (Elle does the same). Default 5.</summary>
    public int ShiftEvery { get; set; } = 5;

    /// <summary>Seed; worker <c>i</c> uses a generator derived from (seed, i). Default 1.</summary>
    public int Seed { get; set; } = 1;

    /// <summary>The highest key any client can use.</summary>
    public int MaxKey => ((TxnsPerWorker - 1) / ShiftEvery) + ActiveKeys;
}

/// <summary>Generates transactions and runs them against a client, recording the history.</summary>
public static class Workload
{
    /// <summary>Nanoseconds on a monotonic clock shared by every process on the machine.</summary>
    public static long Now()
    {
        var t = Stopwatch.GetTimestamp();
        return Stopwatch.Frequency == 1_000_000_000L ? t : (long)(t * (1e9 / Stopwatch.Frequency));
    }

    /// <summary>Runs one client's share of the workload.</summary>
    public static List<Txn> RunWorker(IHistoryClient client, WorkloadOptions o, int worker)
    {
        if (o.Workers > 63)
        {
            throw new ArgumentException("at most 63 workers");
        }

        // A seeded generator makes the plan reproducible; the interleaving is up to the scheduler.
#pragma warning disable CA5394 // not used for security
        var rng = new Random(unchecked((o.Seed * 7919) + worker));
#pragma warning restore CA5394
        var txns = new List<Txn>(o.TxnsPerWorker);
        var counter = 0;
        var mops = new List<Mop>();
        for (var i = 0; i < o.TxnsPerWorker; i++)
        {
            var baseKey = i / o.ShiftEvery;
            var n = o.MinOps + rng.Next(o.MaxOps - o.MinOps + 1);
            mops.Clear();
            var t0 = Now();
            var st = client.Begin();
            for (var j = 0; j < n && st == OpStatus.Ok; j++)
            {
                var key = baseKey + rng.Next(o.ActiveKeys);
                if (rng.NextDouble() < o.ReadFraction)
                {
                    if (o.Model == HistoryModel.ListAppend)
                    {
                        st = client.ReadList(key, out var list);
                        mops.Add(Mop.ReadList(key, st == OpStatus.Ok ? list : []));
                    }
                    else
                    {
                        st = client.ReadRegister(key, out var v);
                        mops.Add(Mop.ReadRegister(key, st == OpStatus.Ok ? v : 0));
                    }
                }
                else
                {
                    var value = (++counter << 6) | worker;
                    if (o.Model == HistoryModel.ListAppend)
                    {
                        mops.Add(Mop.AppendList(key, value));
                        st = client.Append(key, value);
                    }
                    else
                    {
                        mops.Add(Mop.WriteRegister(key, value));
                        st = client.Write(key, value);
                    }
                }
            }

            if (st == OpStatus.Ok)
            {
                st = client.Commit();
            }

            var t1 = Now();
            var outcome = st switch { OpStatus.Ok => TxnOutcome.Ok, OpStatus.Fail => TxnOutcome.Fail, _ => TxnOutcome.Info };
            txns.Add(new Txn(0, worker, t0, outcome == TxnOutcome.Info ? Txn.NeverCompleted : t1, outcome, mops.ToArray()));
        }

        return txns;
    }

    /// <summary>Runs all clients as threads of this process and returns the merged history, closed by a final read of every key.</summary>
    public static History RunThreads(IHistoryClientFactory factory, WorkloadOptions o)
    {
        factory.Prepare();
        var shards = new List<Txn>[o.Workers];
        var errors = new Exception?[o.Workers];
        var threads = new Thread[o.Workers];
        using var gate = new Barrier(o.Workers);
        for (var w = 0; w < o.Workers; w++)
        {
            var worker = w;
            threads[w] = new Thread(() =>
            {
                try
                {
                    using var client = factory.Create(worker);
                    gate.SignalAndWait();
                    shards[worker] = RunWorker(client, o, worker);
                }
#pragma warning disable CA1031 // reported to the caller after the join
                catch (Exception e)
#pragma warning restore CA1031
                {
                    errors[worker] = e;
                    gate.RemoveParticipant();
                }
            });
            threads[w].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        foreach (var e in errors)
        {
            if (e is not null)
            {
                throw new InvalidOperationException("a worker failed: " + e.Message, e);
            }
        }

        return Merge(factory, o, shards);
    }

    /// <summary>Merges per-worker transactions, renumbers them in invocation order and appends a final read of every key.</summary>
    public static History Merge(IHistoryClientFactory factory, WorkloadOptions o, IEnumerable<List<Txn>> shards)
    {
        var all = new List<Txn>();
        foreach (var s in shards)
        {
            all.AddRange(s);
        }

        using (var client = factory.Create(o.Workers))
        {
            for (var attempt = 0; attempt < 1000; attempt++)
            {
                var t = FinalRead(client, o, all.Count);
                if (t is not null)
                {
                    all.Add(t);
                    break;
                }
            }
        }

        all.Sort((a, b) => a.Invoke.CompareTo(b.Invoke));
        for (var i = 0; i < all.Count; i++)
        {
            all[i].Id = i;
        }

        return new History(o.Model, all);
    }

    private static Txn? FinalRead(IHistoryClient client, WorkloadOptions o, int id)
    {
        var mops = new List<Mop>();
        var t0 = Now();
        var st = client.Begin();
        for (var key = 0; key <= o.MaxKey && st == OpStatus.Ok; key++)
        {
            if (o.Model == HistoryModel.ListAppend)
            {
                st = client.ReadList(key, out var l);
                mops.Add(Mop.ReadList(key, l));
            }
            else
            {
                st = client.ReadRegister(key, out var v);
                mops.Add(Mop.ReadRegister(key, v));
            }
        }

        if (st == OpStatus.Ok)
        {
            st = client.Commit();
        }

        return st == OpStatus.Ok ? new Txn(id, o.Workers, t0, Now(), TxnOutcome.Ok, mops.ToArray()) : null;
    }

    /// <summary>Writes a worker's shard file (used by child processes).</summary>
    public static void WriteShard(string path, List<Txn> txns)
    {
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        foreach (var t in txns)
        {
            HistoryIO.WriteTxn(w, t);
        }
    }
}
