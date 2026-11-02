using System;
using System.Linq;
using RecursiveAI.OneLite.Storage.Forensics;

namespace RecursiveAI.OneLite.ForensicsTool;

/// <summary>Prints what the reader sees in one file set: header, log generations and commits, wal-index, journal, and the latest snapshot.</summary>
internal static class Info
{
    public static int Run(string[] args)
    {
        using ForensicDatabase db = ForensicDatabase.Open(args[0]);
        Console.WriteLine($"page size {db.PageSize}, file {db.DatabaseFileLength} bytes, header: {(db.Header == null ? "none" : $"enc {db.Header.Encoding}, counter {db.Header.ChangeCounter}, size {db.Header.SizeInHeader} valid={db.Header.SizeFieldValid}, freelist {db.Header.FreelistCount}")}");
        if (db.Wal is { } w)
        {
            Console.WriteLine($"wal: usable={w.Usable} reason={w.UnusableReason} len={w.FileLength} frames={w.Frames.Count} torn={w.TornTailBytes} chainEnd={w.ValidChainEnd} lastCommit={w.LastCommitFrame} salts={w.Salt1:x8}/{w.Salt2:x8}");
            foreach (WalGeneration g in w.Generations)
            {
                Console.WriteLine($"  gen {g.Number}: frames {g.FirstFrame}..{g.LastFrame} current={g.IsCurrent} fromFrame1={g.StartsAtFrameOne}");
            }

            Console.WriteLine($"  commits {w.Commits.Count}: " + string.Join(' ', w.Commits.Take(8).Select(c => $"#{c.Id}(g{c.Generation} f{c.FirstFrame}-{c.LastFrame} size {c.DatabaseSizePages})")));
            Console.WriteLine($"  frame states: " + string.Join(' ', w.Frames.GroupBy(f => f.State).Select(g => $"{g.Key}={g.Count()}")));
        }

        if (db.Shm is { } s)
        {
            Console.WriteLine($"shm: valid={s.Valid} mxFrame={s.MaxFrame} backfilled={s.Backfilled} matchesWal={s.MatchesWal}");
        }

        if (db.Journal is { } j)
        {
            Console.WriteLine($"journal: header={j.HeaderValid} records={j.Records.Count} checksumsOk={j.Records.Count(r => r.ChecksumOk)} originalPages={j.OriginalDatabasePages}");
        }

        try
        {
            ForensicSnapshot snap = db.OpenSnapshot();
            Console.WriteLine($"latest: pages {snap.PageCount}, notes: {string.Join("; ", snap.Notes)}");
            SpaceReport sp = snap.Space();
            Console.WriteLine($"space: trunks {sp.FreelistTrunks.Count} leaves {sp.FreelistLeaves.Count} lost {sp.LostPages.Count} regions {sp.FreeRegions.Count} frag {sp.FragmentedBytes} trailing {sp.TrailingBytes} anomalies {sp.Anomalies.Count}");
            foreach (string a in sp.Anomalies.Take(5))
            {
                Console.WriteLine("  anomaly: " + a);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("latest snapshot failed: " + e);
        }

        return 0;
    }
}
