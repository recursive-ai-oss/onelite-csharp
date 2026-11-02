using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Forensics;

namespace RecursiveAI.OneLite.ForensicsTool;

/// <summary>
/// Log snapshots against ground truth. The pinned shell makes N commits to a database in WAL mode without checkpointing (a second connection stays
/// open so closing the committing one never checkpoints), with checkpoints and log restarts in between. After every commit a copy of the files is
/// rendered by the shell: that is the truth for "the state as of commit k". The final file set is then read with the forensic reader, and each
/// "as of commit" snapshot has to equal the shell's rendering of the matching copy.
/// </summary>
internal static class WalSnap
{
    private sealed record Variant(string Name, int[] Phases, string Between, bool ReaderAt30, int PageSize);

    private static readonly Dictionary<string, string[]> Tables = new() { ["a"] = ["id", "v", "n"], ["b"] = ["k", "x"] };

    public static int Run(string[] args)
    {
        string work = args.SkipWhile(x => x != "--work").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "forensic-walsnap");
        int seed = int.Parse(args.SkipWhile(x => x != "--seed").Skip(1).FirstOrDefault() ?? "1", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(work);
        var variants = new List<Variant>();
        foreach (int ps in new[] { 1024, 4096 })
        {
            variants.Add(new Variant("restart3", [60, 40, 25], "passive", false, ps));
            variants.Add(new Variant("truncate2", [55, 55], "truncate", false, ps));
            variants.Add(new Variant("reader-partial", [80], "none", true, ps));
            variants.Add(new Variant("single120", [120], "none", false, ps));
        }

        string? only = args.SkipWhile(x => x != "--variant").Skip(1).FirstOrDefault();
        if (only != null)
        {
            variants = [.. variants.Where(v => v.Name == only)];
        }

        int bad = 0;
        Console.WriteLine("variant\tpage\tcommits\tgens\tcur_commits\tcur_equal\tcur_flagged\tcur_flagged_equal\tstale_commits\tstale_matching_a_truth_state\tshm");
        foreach (Variant v in variants)
        {
            foreach (bool withShm in new[] { true, false })
            {
                bad += RunOne(v, work, seed, withShm);
            }
        }

        Console.WriteLine(bad == 0 ? "OK: every unflagged current-generation commit equals the shell's rendering" : $"FAILED: {bad} unflagged commits differ");
        return bad == 0 ? 0 : 1;
    }

    private static int RunOne(Variant v, string work, int seed, bool withShm)
    {
        string dir = Path.Combine(work, $"{v.Name}-{v.PageSize}");
        if (Directory.Exists(dir) && withShm)
        {
            Directory.Delete(dir, true);
        }

        string db = Path.Combine(dir, "live.db");
        string finalDb = Path.Combine(dir, "final.db");
        var truth = new List<string>();
        int total = 0;
        if (withShm)
        {
            Directory.CreateDirectory(dir);
            var rnd = new Random(seed + v.PageSize);
            Common.Sql(db, $"pragma page_size={v.PageSize};\npragma journal_mode=wal;\nbegin;\ncreate table a(id integer primary key, v text, n integer);\ncreate table b(k text, x integer);\ncommit;\n");
            using Process holder = Proc.Start(OraclePaths.Cli, [db], redirectIn: true);
            holder.StandardInput.Write("pragma journal_mode=wal;\nselect 1;\n");
            holder.StandardInput.Flush();
            Process? reader = null;
            int n = 0;
            truth.Add(Truth(db, dir, 0));
            for (int ph = 0; ph < v.Phases.Length; ph++)
            {
                for (int i = 0; i < v.Phases[ph]; i++)
                {
                    n++;
                    if (v.ReaderAt30 && n == 30)
                    {
                        reader = Proc.Start(OraclePaths.Cli, [db], redirectIn: true);
                        reader.StandardInput.Write("begin;\nselect count(*) from a;\nselect count(*) from b;\n");
                        reader.StandardInput.Flush();
                        System.Threading.Thread.Sleep(300);
                    }

                    if (v.ReaderAt30 && n == 60)
                    {
                        Common.Sql(db, "pragma wal_checkpoint(passive);\n", allowError: true);
                    }

                    Common.Sql(db, "pragma wal_autocheckpoint=0;\nbegin;\n" + Statement(rnd, n) + "commit;\n");
                    string t = Truth(db, dir, n);
                    if (t != truth[^1])
                    {
                        truth.Add(t);
                    }
                }

                if (ph < v.Phases.Length - 1)
                {
                    Common.Sql(db, v.Between == "truncate" ? "pragma wal_checkpoint(truncate);\n" : "pragma wal_checkpoint(passive);\n", allowError: true);
                }
            }

            Common.CopySet(db, finalDb);
            reader?.StandardInput.Close();
            reader?.WaitForExit();
            reader?.Dispose();
            holder.StandardInput.Close();
            holder.WaitForExit();
            File.WriteAllText(Path.Combine(dir, "truth.txt"), string.Join('\u0001', truth));
        }
        else
        {
            File.Delete(finalDb + "-shm");
            truth = [.. File.ReadAllText(Path.Combine(dir, "truth.txt")).Split('\u0001')];
        }

        total = truth.Count - 1;
        using ForensicDatabase fdb = ForensicDatabase.Open(finalDb);
        WalInfo wal = fdb.Wal!;
        int gens = wal.Generations.Count;
        var cur = wal.Commits.Where(c => c.Generation == 0).ToList();
        int curEqual = 0, flagged = 0, flaggedEqual = 0, bad = 0, staleN = 0, staleMatch = 0;
        for (int j = 0; j < cur.Count; j++)
        {
            ForensicSnapshot s = fdb.OpenSnapshot(SnapshotSelector.AsOf(cur[j].Id));
            string got = Common.RenderSnapshot(s, Tables.Keys);
            int expected = total - cur.Count + 1 + j;
            if (expected < 0)
            {
                continue; // a commit that belongs to the setup, before any state we recorded
            }

            bool eq = expected < truth.Count && got == truth[expected];
            if (s.DatabaseMayBeNewer)
            {
                flagged++;
                if (eq)
                {
                    flaggedEqual++;
                }
            }
            else if (eq)
            {
                curEqual++;
            }
            else
            {
                bad++;
                Console.WriteLine($"  DIFF {v.Name}/{v.PageSize} commit {cur[j].Id} (expected truth {expected}) shm={withShm}");
            }
        }

        foreach (WalCommit c in wal.Commits.Where(c => c.Generation != 0))
        {
            staleN++;
            try
            {
                string got = Common.RenderSnapshot(fdb.OpenSnapshot(SnapshotSelector.AsOf(c.Id)), Tables.Keys);
                if (truth.Contains(got))
                {
                    staleMatch++;
                }
            }
            catch (OneLiteException cex) when (cex.Code == ResultCode.Corrupt)
            {
                // A partial generation can leave a page missing.
            }
        }

        Console.WriteLine($"{v.Name}\t{v.PageSize}\t{wal.Commits.Count}\t{gens}\t{cur.Count}\t{curEqual}\t{flagged}\t{flaggedEqual}\t{staleN}\t{staleMatch}\t{(withShm ? "with" : "without")}\tshm={(fdb.Shm == null ? "none" : $"valid={fdb.Shm.Valid},matches={fdb.Shm.MatchesWal},backfilled={fdb.Shm.Backfilled},mx={fdb.Shm.MaxFrame}")}\ttruth={truth.Count}");
        return bad;
    }

    private static string Statement(Random r, int n)
    {
        string txt = "v" + n + "-" + new string((char)('a' + (n % 26)), r.Next(5, 60));
        switch (r.Next(6))
        {
            case 0:
            case 1:
                return $"insert into a(v,n) values('{txt}', {n});\n";
            case 2:
                return $"update a set v='{txt}-upd', n={n} where id=(select abs(random()) % (select max(id)+1 from a));\n";
            case 3:
                return $"insert into b values('k{n}-{txt}', {n});\n";
            case 4:
                return $"insert into a(v,n) values('{new string('z', r.Next(300, 1500))}', {n});\n";
            default:
                return $"delete from a where id=(select min(id) from a where n % 3 = {n % 3});\ninsert into b values('d{n}', {n});\n";
        }
    }

    private static string Truth(string db, string dir, int k)
    {
        string copy = Path.Combine(dir, "truth", $"c{k}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        foreach (string f in new[] { "", "-wal", "-shm" })
        {
            File.Delete(copy + f);
        }

        File.Copy(db, copy, true);
        if (File.Exists(db + "-wal"))
        {
            File.Copy(db + "-wal", copy + "-wal", true);
        }

        string s = Common.RenderSql(copy, Tables);
        foreach (string f in new[] { "", "-wal", "-shm" })
        {
            File.Delete(copy + f);
        }

        return s;
    }
}
