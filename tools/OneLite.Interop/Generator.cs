using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Interop;

/// <summary>
/// Builds the generated interop corpus with the pinned sqlite3: every b-tree page type, overflow chains (table cells and index
/// keys), freelists, the three auto-vacuum modes, three text encodings, page sizes 512 to 65536, reserved bytes, WITHOUT ROWID
/// and STRICT tables, WAL files in several states and rollback journals left hot by a killed writer. Each file gets a manifest
/// entry with how it was made and the dump and canonical rendering the pinned sqlite3 produces after recovery.
/// </summary>
internal sealed class Generator
{
    private static readonly int[] PageSizes = [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];
    private static readonly string[] Encodings = ["UTF-8", "UTF-16le", "UTF-16be"];

    private static readonly string[] CheckTags = ["int", "dump", "render", "replay"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _out;
    private readonly string _tmp;
    private readonly string _only;
    private readonly Manifest _manifest = new();

    private Generator(string outDir, string only)
    {
        _out = outDir;
        _only = only;
        _tmp = Path.Combine(outDir, ".work");
    }

    public static int Run(string[] args)
    {
        string? outDir = null, only = "";
        bool force = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--only": only = args[++i]; break;
                case "--force": force = true; break;
                default: Console.Error.WriteLine("gen: unknown option " + args[i]); return 2;
            }
        }
        outDir ??= Path.Combine(OraclePaths.CorpusRoot, "generated");
        outDir = Path.GetFullPath(outDir);
        if (Directory.Exists(outDir) && Directory.GetFileSystemEntries(outDir).Length > 0)
        {
            if (!force)
            {
                Console.Error.WriteLine("gen: " + outDir + " is not empty (use --force to regenerate)");
                return 2;
            }
            Directory.Delete(outDir, recursive: true);
        }
        var g = new Generator(outDir, only ?? "");
        return g.Generate();
    }

    private int Generate()
    {
        string header = PinnedSqlite.Header();
        Console.Write(header);
        _manifest.Oracle = header;
        foreach (string d in new[] { "db", "scripts", "expected", ".work" })
        {
            Directory.CreateDirectory(Path.Combine(_out, d));
        }

        // Layout matrix: every page size, encoding and auto-vacuum mode.
        foreach (int p in PageSizes)
        {
            foreach (string enc in Encodings)
            {
                for (int av = 0; av <= 2; av++)
                {
                    var s = new Settings(p, enc, av);
                    AddScriptCase($"m-p{p}-{s.EncodingTag}-av{av}", ["matrix"], s,
                        "sqlite3 shell runs the base script with the layout pragmas first");
                }
            }
        }

        // Reserved bytes per page: set with the shell's .filectrl and applied by VACUUM (header offset 20).
        foreach ((int p, int res, string enc, int av) in new[] { (512, 32, "UTF-8", 0), (1024, 8, "UTF-16le", 0), (4096, 16, "UTF-8", 2), (4096, 255, "UTF-8", 0), (16384, 64, "UTF-16be", 1), (65536, 8, "UTF-8", 0) })
        {
            var s = new Settings(p, enc, av, res);
            AddScriptCase($"res-p{p}-r{res}-{s.EncodingTag}-av{av}", ["reserved-bytes"], s,
                $"base script, then .filectrl reserve_bytes {res} and VACUUM", writable: false, post: $".filectrl reserve_bytes {res}\nVACUUM;\n");
        }

        // A WAL-mode database that was closed cleanly: file-format versions 2 in the header, no -wal file.
        foreach (int p in new[] { 4096, 512 })
        {
            var s = new Settings(p, "UTF-8", 0);
            AddScriptCase($"walmode-clean-p{p}", ["wal", "wal-header-only"], s,
                "base script in journal_mode=WAL, closed cleanly (header versions 2, no -wal)", writable: false, pre: "PRAGMA journal_mode=WAL;\n");
        }

        // WAL states.
        foreach (int p in new[] { 512, 4096, 65536 })
        {
            var s = new Settings(p, "UTF-8", 0);
            GenerateWalCases(s);
        }
        // WAL with other layouts, to cover pointer maps and UTF-16 frames.
        GenerateWalCases(new Settings(4096, "UTF-16le", 2), only: ["uncheckpointed"]);
        GenerateWalCases(new Settings(1024, "UTF-16be", 1), only: ["ckpt-reader"]);

        // Hot rollback journals.
        foreach (int p in new[] { 512, 4096, 65536 })
        {
            foreach (string body in new[] { "update", "grow", "drop" })
            {
                AddHotCase($"hot-p{p}-{body}", new Settings(p, "UTF-8", 0), body);
            }
        }
        AddHotCase("hot-p4096-update-persist", new Settings(4096, "UTF-8", 0), "update:persist");
        AddHotCase("hot-p4096-update-truncate", new Settings(4096, "UTF-8", 0), "update:truncate");
        AddHotCase("hot-p4096-update-utf16be", new Settings(4096, "UTF-16be", 0), "update");
        AddHotCase("hot-p4096-update-av1", new Settings(4096, "UTF-8", 1), "update");
        AddHotCase("hot-p1024-update-av2", new Settings(1024, "UTF-8", 2), "update");
        AddHotCase("hot-p1024-grow-av2", new Settings(1024, "UTF-8", 2), "grow");

        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }
        File.WriteAllText(Path.Combine(_out, "manifest.json"),
            JsonSerializer.Serialize(_manifest, JsonOptions) + "\n");
        PrintSummary();
        return 0;
    }

    private bool Wanted(string name) => _only.Length == 0 || name.Contains(_only, StringComparison.Ordinal);

    // ---- cases built from a script ----

    private void AddScriptCase(string name, string[] cats, Settings s, string how, bool writable = true, string post = "", string pre = "")
    {
        if (!Wanted(name))
        {
            return;
        }
        string baseScript = Scripts.Base(s);
        string db = Path.Combine(_out, "db", name + ".db");
        string scriptRel = "scripts/" + name + ".sql";
        File.WriteAllText(Path.Combine(_out, scriptRel), baseScript);
        // The pre-script goes after the layout pragmas (journal_mode=WAL has to come once the page size is set).
        string full = pre.Length == 0 ? baseScript : Scripts.Pragmas(s) + pre + Scripts.Base(s, withPragmas: false);
        SqliteCli.ApplyScript(db, full + post);
        var c = NewCase(name, cats, s, how);
        c.Script = scriptRel;
        c.Writable = writable;
        Finish(c, [], expectedScript: baseScript);
    }

    // ---- WAL cases ----

    private void GenerateWalCases(Settings s, string[]? only = null)
    {
        string[] plans = only ?? ["uncheckpointed", "ckpt-restart", "ckpt-truncate", "ckpt-reader", "big-txn"];
        string lay = $"p{s.PageSize}" + (s.Encoding != "UTF-8" ? "-" + s.EncodingTag : "") + (s.AutoVacuum != 0 ? "-av" + s.AutoVacuum : "");
        foreach (string plan in plans)
        {
            string name = $"wal-{plan}-{lay}";
            string noShm = $"wal-noshm-{plan}-{lay}";
            bool torn = plan == "uncheckpointed";
            if (!Wanted(name) && !Wanted(noShm) && !(torn && _only.Contains("torn", StringComparison.Ordinal)) && !(torn && _only.Contains("garbage", StringComparison.Ordinal)))
            {
                continue;
            }
            string db = Path.Combine(_out, "db", name + ".db");
            List<string> steps = StateChild.WalPlan(plan, s);
            List<string> logical = StateChild.Logical(steps);
            List<long> walSizes = RunChild(db, "wal", plan, s);   // walSizes[k-1] = size of the -wal after commit k
            string replay = Scripts.Base(s) + string.Concat(logical);
            string how = $"a live writer (WAL, autocheckpoint off) commits the base content then the plan '{plan}', and is killed; -wal and -shm are left as the dead process left them";
            if (Wanted(name))
            {
                var c = NewCase(name, ["wal"], s, how);
                c.WalState = DescribeWal(plan);
                Finish(c, ["-wal", "-shm"], replay);
            }
            if (plan is "uncheckpointed" or "ckpt-restart" or "ckpt-reader" or "big-txn")
            {
                // The same log without its -shm: a reader must rebuild the index from the log alone.
                DeriveWal(db, noShm, s, ["wal", "wal-no-shm"], how + "; the -shm file is removed", replay, w => File.Delete(w + "-shm"), DescribeWal(plan) + "; no -shm");
            }
            if (torn)
            {
                // The last commit is damaged in three ways. The log is valid only up to the commit before it (walformat: a frame
                // counts only if its salts match the header and its checksum continues the chain).
                int frame = 24 + s.PageSize;
                int k = walSizes.Count;
                string withoutLast = Scripts.Base(s) + string.Concat(logical.GetRange(0, logical.Count - 1));
                long before = walSizes[k - 2];
                DeriveWal(db, $"wal-torn-partial-{lay}", s, ["wal", "wal-torn"], how + $"; the -wal is cut {frame / 2} bytes into the first frame after commit {k - 1} and the -shm removed",
                    withoutLast, w => { Truncate(w + "-wal", before + frame / 2); File.Delete(w + "-shm"); }, "the last commit is torn: the log ends inside a frame");
                DeriveWal(db, $"wal-torn-partial-shm-{lay}", s, ["wal", "wal-torn"], how + "; the -wal is cut mid-frame but the stale -shm of the dead writer stays",
                    withoutLast, w => Truncate(w + "-wal", before + frame / 2), "the last commit is torn, stale -shm kept");
                DeriveWal(db, $"wal-torn-checksum-{lay}", s, ["wal", "wal-torn"], how + "; one byte inside the last commit's final frame is flipped, so the checksum chain breaks there; -shm removed",
                    withoutLast, w => { FlipByte(w + "-wal", walSizes[k - 1] - 7); File.Delete(w + "-shm"); }, "the last commit's final frame fails its checksum");
                DeriveWal(db, $"wal-garbage-tail-{lay}", s, ["wal", "wal-torn"], how + "; 100 junk bytes are appended after the last valid frame; -shm removed",
                    replay, w => { Append(w + "-wal", 100); File.Delete(w + "-shm"); }, "a valid log followed by junk shorter than a frame");
            }
            if (!Wanted(name))
            {
                PinnedSqlite.DeleteFileSet(db);
            }
        }
    }

    private static void CopySet(string src, string dest)
    {
        foreach (string sfx in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (File.Exists(src + sfx))
            {
                File.Copy(src + sfx, dest + sfx, overwrite: true);
            }
        }
    }

    private void DeriveWal(string srcDb, string name, Settings s, string[] cats, string how, string expectedScript, Action<string> mutate, string walState)
    {
        if (!Wanted(name))
        {
            return;
        }
        string db = Path.Combine(_out, "db", name + ".db");
        CopySet(srcDb, db);
        mutate(db);
        var c = NewCase(name, cats, s, how);
        c.WalState = walState;
        Finish(c, ["-wal", "-shm"], expectedScript);
    }

    private static string DescribeWal(string plan) => plan switch
    {
        "uncheckpointed" => "all commits are in the log, none checkpointed",
        "ckpt-restart" => "checkpointed completely, then the log restarted by the next writer: stale frames from the old log remain after the new ones",
        "ckpt-truncate" => "checkpoint(TRUNCATE): the -wal file is empty, all content is in the database file",
        "ckpt-reader" => "a reader held back the checkpoint: part of the log is copied into the database, later commits only in the log",
        "big-txn" => "one transaction larger than the page cache: frames written before the commit frame",
        _ => plan,
    };

    private static void Truncate(string path, long length)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write);
        f.SetLength(length);
    }

    private static void FlipByte(string path, long offset)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        f.Position = offset;
        int b = f.ReadByte();
        f.Position = offset;
        f.WriteByte((byte)(b ^ 0x5a));
    }

    private static void Append(string path, int n)
    {
        using var f = new FileStream(path, FileMode.Append, FileAccess.Write);
        var junk = new byte[n];
        Array.Fill(junk, (byte)0xA5);
        f.Write(junk);
    }

    // ---- hot journal cases ----

    private void AddHotCase(string name, Settings s, string plan)
    {
        if (!Wanted(name))
        {
            return;
        }
        string db = Path.Combine(_out, "db", name + ".db");
        RunChild(db, "hot", plan, s);
        string journal = db + "-journal";
        if (!File.Exists(journal) || new FileInfo(journal).Length < 512)
        {
            throw new InvalidOperationException(name + ": the killed writer left no journal content");
        }
        string how = $"a writer with a tiny page cache runs '{plan}' inside an open transaction and is killed before COMMIT; the rollback journal is left hot (mode {(plan.Contains(':') ? plan[(plan.IndexOf(':') + 1)..] : "delete")})";
        var c = NewCase(name, ["hot-journal"], s, how);
        c.HotJournal = true;
        Finish(c, ["-journal"], Scripts.Base(s));
    }

    // ---- the writer process ----

    private static List<long> RunChild(string db, string kind, string plan, Settings s)
    {
        PinnedSqlite.DeleteFileSet(db);
        (string exe, List<string> args) = Proc.Self(["child-state", db, kind, plan, s.PageSize.ToString(CultureInfo.InvariantCulture), s.Encoding, s.AutoVacuum.ToString(CultureInfo.InvariantCulture)]);
        using Process p = Proc.Start(exe, args);
        var sizes = new List<long>();
        var errTask = p.StandardError.ReadToEndAsync();
        var sw = Stopwatch.StartNew();
        bool ready = false;
        while (sw.Elapsed < TimeSpan.FromMinutes(5))
        {
            Task<string?> line = p.StandardOutput.ReadLineAsync();
            if (!line.Wait(TimeSpan.FromMinutes(5) - sw.Elapsed) || line.Result == null)
            {
                break;
            }
            string l = line.Result;
            if (l.StartsWith("COMMIT ", StringComparison.Ordinal))
            {
                sizes.Add(long.Parse(l.Split(' ')[2], CultureInfo.InvariantCulture));
            }
            else if (l == "READY")
            {
                ready = true;
                break;
            }
        }
        // Only this process, which we started, is killed: SIGKILL gives it no chance to clean up its -wal, -shm or journal.
        p.Kill();
        p.WaitForExit();
        if (!ready)
        {
            throw new InvalidOperationException($"state writer for {db} ({kind} {plan}) never became ready: {errTask.GetAwaiter().GetResult()}");
        }
        return sizes;
    }

    // ---- checking and recording a case ----

    private static CorpusCase NewCase(string name, string[] cats, Settings s, string how) => new()
    {
        Name = name,
        Categories = [.. cats],
        How = how,
        PageSize = s.PageSize,
        Encoding = s.Encoding,
        AutoVacuum = s.AutoVacuum,
        ReservedBytes = s.Reserved,
        Db = "db/" + name + ".db",
    };

    private void Finish(CorpusCase c, string[] sideSuffixes, string expectedScript)
    {
        string db = Path.Combine(_out, c.Db);
        // Hash exactly what the generator left on disk, before any open recovers it.
        foreach (string sfx in sideSuffixes.Prepend(""))
        {
            string f = db + sfx;
            if (File.Exists(f))
            {
                c.Files[c.Db + sfx] = new FileInfoEntry { Size = new FileInfo(f).Length, Sha256 = Sha256Of(f) };
            }
        }
        if (c.HotJournal)
        {
            byte[] j = File.ReadAllBytes(db + "-journal");
            ReadOnlySpan<byte> magic = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];   // atomiccommit: journal header magic
            if (j.Length < 8 || !j.AsSpan(0, 8).SequenceEqual(magic))
            {
                throw new InvalidOperationException(c.Name + ": the journal has no valid header, so it would not be hot");
            }
        }
        ValidateHeader(c, db);

        // Recovery check: each look at the file happens on its own fresh copy.
        string Work(string tag) => Path.Combine(_tmp, c.Name + "." + tag + ".db");
        PinnedSqlite.CopyFileSet(db, Work("int"));
        if (!PinnedSqlite.IntegrityOk(Work("int"), out string detail))
        {
            throw new InvalidOperationException($"{c.Name}: integrity_check after recovery: {detail}");
        }
        c.IntegrityAfterRecovery = "ok";
        if (c.HotJournal)
        {
            c.RecoveryChangedDb = Sha256Of(Work("int")) != c.Files[c.Db].Sha256;
        }
        PinnedSqlite.CopyFileSet(db, Work("dump"));
        string dump = SqliteCli.Dump(Work("dump"));
        PinnedSqlite.CopyFileSet(db, Work("render"));
        string render = PinnedSqlite.RenderAll(Work("render"));

        // The state the recovered file must be in, built independently by replaying the same SQL into a fresh database.
        string replayDb = Work("replay");
        PinnedSqlite.DeleteFileSet(replayDb);
        SqliteCli.ApplyScript(replayDb, expectedScript);
        string replayDump = SqliteCli.Dump(replayDb);
        if (replayDump != dump)
        {
            File.WriteAllText(Path.Combine(_tmp, c.Name + ".expected.dump"), replayDump);
            File.WriteAllText(Path.Combine(_tmp, c.Name + ".actual.dump"), dump);
            throw new InvalidOperationException($"{c.Name}: the recovered file's dump differs from a replay of its transactions (see {_tmp})");
        }

        c.ExpectedDump = "expected/" + c.Name + ".dump";
        c.ExpectedRender = "expected/" + c.Name + ".render";
        File.WriteAllText(Path.Combine(_out, c.ExpectedDump), dump);
        File.WriteAllText(Path.Combine(_out, c.ExpectedRender), render);
        c.ExpectedDumpSha256 = Sha256Of(Path.Combine(_out, c.ExpectedDump));
        c.ExpectedRenderSha256 = Sha256Of(Path.Combine(_out, c.ExpectedRender));

        // Informational coverage of page kinds (the oracle's dbstat) on the recovered copy.
        // dbstat exists only in the shell build, so ask the shell.
        foreach (string[] r in SqliteCli.QueryCsv(Work("int"),
            "select (coalesce(s.type,'table') = 'index' or coalesce(s.sql,'') like '%WITHOUT ROWID%'), d.pagetype, count(*) " +
            "from dbstat d left join sqlite_schema s on s.name = d.name group by 1, 2;"))
        {
            c.Coverage[(r[0] == "1" ? "index" : "table") + "-" + r[1]] = long.Parse(r[2], CultureInfo.InvariantCulture);
        }
        using (SqliteDb d = SqliteDb.Open(Work("int"), readOnly: false, create: false))
        {
            c.Coverage["freelist-pages"] = long.Parse(d.Scalar("pragma freelist_count")!, CultureInfo.InvariantCulture);
            c.Coverage["page-count"] = long.Parse(d.Scalar("pragma page_count")!, CultureInfo.InvariantCulture);
        }
        foreach (string tag in CheckTags)
        {
            PinnedSqlite.DeleteFileSet(Work(tag));
        }
        _manifest.Cases.Add(c);
        Console.WriteLine($"  {c.Name}: ok ({c.Files.Count} file(s), {c.Files[c.Db].Size} bytes)");
    }

    private static void ValidateHeader(CorpusCase c, string db)
    {
        // fileformat2 §1.3: the 100-byte database header. Check that the layout asked for is the layout the file has.
        byte[] h = new byte[100];
        using (var f = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (f.Read(h, 0, 100) < 100)
            {
                c.FormatVersions = [];   // the file has no first page yet (everything is still in the log)
                return;
            }
        }
        int ps = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(16));
        if (ps == 1)
        {
            ps = 65536;
        }
        int enc = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(56));
        string encName = enc switch { 1 => "UTF-8", 2 => "UTF-16le", 3 => "UTF-16be", _ => "?" };
        int largestRoot = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(52));
        int incr = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(64));
        int av = largestRoot == 0 ? 0 : incr != 0 ? 2 : 1;
        c.FormatVersions = [h[18], h[19]];
        if (h[20] != c.ReservedBytes)
        {
            throw new InvalidOperationException($"{c.Name}: reserved bytes {h[20]}, asked for {c.ReservedBytes}");
        }
        if (ps != c.PageSize || (enc != 0 && encName != c.Encoding) || (enc != 0 && av != c.AutoVacuum))
        {
            throw new InvalidOperationException($"{c.Name}: file header says page size {ps}, {encName}, auto_vacuum {av}; asked for {c.PageSize}, {c.Encoding}, {c.AutoVacuum}");
        }
    }

    private static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    private void PrintSummary()
    {
        var byCat = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cover = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (CorpusCase c in _manifest.Cases)
        {
            foreach (string cat in c.Categories)
            {
                byCat[cat] = byCat.GetValueOrDefault(cat) + 1;
            }
            foreach (KeyValuePair<string, long> kv in c.Coverage)
            {
                cover[kv.Key] = cover.GetValueOrDefault(kv.Key) + (kv.Value > 0 ? 1 : 0);
            }
        }
        Console.WriteLine($"cases: {_manifest.Cases.Count}");
        foreach (KeyValuePair<string, int> kv in byCat)
        {
            Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }
        Console.WriteLine("files containing at least one page of kind:");
        foreach (KeyValuePair<string, long> kv in cover)
        {
            Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }
    }
}
