using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Forensics;

namespace RecursiveAI.OneLite.ForensicsTool;

/// <summary>Helpers shared by the forensic checks.</summary>
internal static class Common
{
    /// <summary>Runs SQL through the pinned <c>sqlite3</c> shell against <paramref name="db"/> and returns its output.</summary>
    public static string Sql(string db, string sql, bool allowError = false)
    {
        Proc.Result r = Proc.Run(OraclePaths.Cli, [db], sql, timeout: TimeSpan.FromMinutes(5));
        if (!allowError && (r.ExitCode != 0 || r.Stderr.Length > 0))
        {
            throw new InvalidOperationException($"sqlite3 failed ({r.ExitCode}): {r.Stderr}\n{sql[..Math.Min(200, sql.Length)]}");
        }

        return r.Stdout;
    }

    public static string Quote(object? v) => v switch
    {
        null => "NULL",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        byte[] b => "X'" + Convert.ToHexString(b) + "'",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("unexpected value type"),
    };

    /// <summary>The names of the files that belong to a database, those that exist.</summary>
    public static List<string> FileSet(string db) => new[] { db, db + "-wal", db + "-shm", db + "-journal" }.Where(File.Exists).ToList();

    public sealed record FileState(string Name, string Sha256, long Length, long MTimeTicks);

    public static List<FileState> Snapshot(string db)
    {
        var l = new List<FileState>();
        foreach (string f in FileSet(db))
        {
            l.Add(new FileState(Path.GetFileName(f), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), new FileInfo(f).Length, File.GetLastWriteTimeUtc(f).Ticks));
        }

        return l;
    }

    public static string Hex(byte[] b) => Convert.ToHexString(b);

    /// <summary>Renders every table of a snapshot as sorted lines "table|rowid|v1|v2...", the same shape as <see cref="RenderSql"/>.</summary>
    public static string RenderSnapshot(ForensicSnapshot s, IReadOnlyCollection<string> tables)
    {
        var sb = new StringBuilder();
        foreach (ForensicTable t in s.Tables.Where(t => t.Type == "table" && tables.Contains(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            foreach (ForensicRow r in s.ReadRows(t))
            {
                sb.Append(t.Name).Append('|').Append(r.Rowid.ToString(CultureInfo.InvariantCulture));
                foreach (object? v in r.Values)
                {
                    sb.Append('|').Append(Quote(v));
                }

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>The same rendering, produced by the pinned shell with <c>quote()</c>.</summary>
    public static string RenderSql(string db, Dictionary<string, string[]> tableCols)
    {
        var sb = new StringBuilder();
        foreach ((string t, string[] cols) in tableCols.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string expr = "'" + t + "|'||rowid" + string.Concat(cols.Select(c => "||'|'||quote(" + c + ")"));
            sb.Append(Sql(db, $"select {expr} from {t} order by rowid;"));
        }

        return sb.ToString();
    }

    public static void CopySet(string db, string destDb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destDb)!);
        foreach (string f in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (File.Exists(db + f))
            {
                File.Copy(db + f, destDb + f, true);
            }
        }
    }
}
