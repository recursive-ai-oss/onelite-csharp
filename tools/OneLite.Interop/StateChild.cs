using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Interop;

/// <summary>
/// The process that leaves a database in a state a plain SQL script cannot: WAL files with different checkpoint histories and
/// rollback journals that are hot because the writer died mid-transaction. The generator starts it, waits for READY and kills it.
/// Lines on standard output: <c>COMMIT k size</c> after commit k (size = bytes of the -wal file), <c>BASE</c>, then <c>READY</c>.
/// </summary>
internal static class StateChild
{
    private const string CheckpointPrefix = "PRAGMA wal_checkpoint";
    private const string ReaderMarker = "@reader";

    /// <summary>The steps of a WAL plan after the base content. Checkpoints and the reader marker only mean something to a live connection.</summary>
    public static List<string> WalPlan(string plan, Settings s)
    {
        IReadOnlyList<string> t = Scripts.WalTransactions(s);
        return plan switch
        {
            "uncheckpointed" => [t[0], t[1], t[2], t[3]],
            "ckpt-restart" => [t[0], CheckpointPrefix + "(FULL);", t[1]],
            "ckpt-truncate" => [t[0], t[1], CheckpointPrefix + "(TRUNCATE);"],
            "ckpt-reader" => [t[0], ReaderMarker, CheckpointPrefix + "(PASSIVE);", t[1], t[2], t[3]],
            "big-txn" => ["BEGIN;\n" + Scripts.BigTransactionBody(s, "update") + "\nCOMMIT;\n"],
            _ => throw new ArgumentException("unknown WAL plan " + plan),
        };
    }

    /// <summary>The logical transactions of a plan (what a replay on a fresh database applies).</summary>
    public static List<string> Logical(List<string> plan)
        => plan.FindAll(x => !x.StartsWith(CheckpointPrefix, StringComparison.Ordinal) && x != ReaderMarker);

    public static int Run(string[] a)
    {
        string db = a[0], kind = a[1], plan = a[2];
        var s = new Settings(int.Parse(a[3], CultureInfo.InvariantCulture), a[4], int.Parse(a[5], CultureInfo.InvariantCulture));
        using SqliteDb conn = SqliteDb.Open(db);
        conn.Exec("PRAGMA synchronous=OFF;");
        conn.Exec(Scripts.Pragmas(s));
        SqliteDb? reader = null;
        if (kind == "wal")
        {
            conn.Exec("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            if (plan == "big-txn")
            {
                conn.Exec("PRAGMA cache_size=3;");
            }
            conn.Exec(Scripts.Base(s, withPragmas: false));
            Report("COMMIT 1 " + WalSize(db));
            int k = 1;
            foreach (string step in WalPlan(plan, s))
            {
                if (step == ReaderMarker)
                {
                    // A second connection holds a read snapshot, so the checkpoint cannot pass it and the log is not restarted.
                    reader = SqliteDb.Open(db);
                    reader.Exec("BEGIN;");
                    _ = reader.Scalar("select count(*) from t1");
                    continue;
                }
                conn.Exec(step);
                if (!step.StartsWith(CheckpointPrefix, StringComparison.Ordinal))
                {
                    k++;
                    Report("COMMIT " + k.ToString(CultureInfo.InvariantCulture) + " " + WalSize(db).ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        else
        {
            // plan = <body>[:<journal mode>], for example update:persist
            string[] parts = plan.Split(':');
            string mode = parts.Length > 1 ? parts[1].ToUpperInvariant() : "DELETE";
            conn.Exec("PRAGMA journal_mode=" + mode + ";");
            conn.Exec(Scripts.Base(s, withPragmas: false));
            Report("BASE");
            conn.Exec("PRAGMA cache_size=3;");   // tiny cache: modified pages are written to the database file before the commit
            conn.Exec("BEGIN;");
            conn.Exec(Scripts.BigTransactionBody(s, parts[0]));
        }
        Report("READY");
        Thread.Sleep(Timeout.Infinite);
        reader?.Dispose();
        return 0;
    }

    private static long WalSize(string db) => File.Exists(db + "-wal") ? new FileInfo(db + "-wal").Length : 0;

    private static void Report(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}
