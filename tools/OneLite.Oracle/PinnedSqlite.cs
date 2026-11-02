using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.Oracle;

/// <summary>Operations on the pinned oracle that the harness tools share.</summary>
public static class PinnedSqlite
{
    private static readonly string[] Suffixes = ["", "-wal", "-shm", "-journal"];

    /// <summary>
    /// The header every oracle run prints: versions of the shell and the native library and the compile options.
    /// Throws when either is not the pinned release.
    /// </summary>
    public static string Header()
    {
        var sb = new StringBuilder();
        Proc.Result cli = Proc.Run(OraclePaths.Cli, ["--version"]);
        string cliVersion = cli.Stdout.Trim();
        using SqliteDb db = SqliteDb.Open(":memory:");
        string ver = db.Scalar("select sqlite_version()")!;
        string src = db.Scalar("select sqlite_source_id()")!;
        var opts = new List<string>();
        foreach (string[] row in db.QueryCanonical("pragma compile_options"))
        {
            opts.Add(row[0]);
        }
        sb.Append("# sqlite3 shell: ").Append(cliVersion).Append('\n');
        sb.Append("# native library: sqlite_version() ").Append(ver).Append(", sqlite_source_id() ").Append(src).Append('\n');
        // The shell and the native library are different builds of the same release and their compile options differ (for example
        // foreign keys default to on in the native library, and only the shell has the dbstat table), so both are reported.
        Proc.Result shellOpts = Proc.Run(OraclePaths.Cli, ["-batch", "-init", "/dev/null", ":memory:", "pragma compile_options"]);
        sb.Append("# sqlite3 shell compile_options: ").Append(string.Join(' ', shellOpts.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))).Append('\n');
        sb.Append("# native library compile_options: ").Append(string.Join(' ', opts.ConvertAll(o => o.StartsWith("T:\"", StringComparison.Ordinal) ? o[3..^1] : o))).Append('\n');
        if (ver != OraclePaths.PinnedVersion || !cliVersion.StartsWith(OraclePaths.PinnedVersion + " ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("oracle is not the pinned release " + OraclePaths.PinnedVersion + ": shell '" + cliVersion + "', library " + ver);
        }
        return sb.ToString();
    }

    /// <summary>Rows of <c>PRAGMA integrity_check</c>; a sound database yields the single row <c>ok</c>.</summary>
    public static List<string> IntegrityCheck(string dbPath)
    {
        using SqliteDb db = SqliteDb.Open(dbPath, create: false);
        var rows = new List<string>();
        using SqliteStatement st = db.Prepare("pragma integrity_check");
        while (st.Step())
        {
            rows.Add(st.TextOrNull(0) ?? "");
        }
        return rows;
    }

    /// <summary>True when integrity_check returns exactly <c>ok</c>.</summary>
    public static bool IntegrityOk(string dbPath, out string detail)
    {
        List<string> rows = IntegrityCheck(dbPath);
        detail = string.Join("; ", rows);
        return rows.Count == 1 && rows[0] == "ok";
    }

    /// <summary>
    /// The canonical rendering of everything a reader must reproduce from a database file: header-level pragmas, the schema
    /// (without root pages, which are layout and may differ between writers) and every table's rows in scan order. Opens the
    /// file read-write so a hot journal or WAL is recovered, so callers pass a copy.
    /// </summary>
    public static string RenderAll(string dbPath)
    {
        using SqliteDb db = SqliteDb.Open(dbPath, create: false);
        var sb = new StringBuilder();
        db.RenderScript(
            "pragma page_size; pragma encoding; pragma auto_vacuum; pragma user_version; pragma application_id; pragma freelist_count; pragma page_count;"
            + "select type, name, tbl_name, sql from sqlite_schema order by rowid;", sb);
        foreach (string[] t in db.QueryCanonical("select name from sqlite_schema where type = 'table' and sql not like 'CREATE VIRTUAL%' order by rowid"))
        {
            // t[0] is canonical text: T:"name"; strip the wrapper to quote the identifier (identifiers here contain no escapes).
            string name = t[0][3..^1];
            sb.Append("table\t").Append(name).Append('\n');
            db.RenderScript("select * from \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";", sb);
        }
        return sb.ToString();
    }

    /// <summary>Copies a database and its side files (<c>-wal</c>, <c>-shm</c>, <c>-journal</c>) to a new base path.</summary>
    public static void CopyFileSet(string srcDb, string destDb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destDb))!);
        foreach (string suffix in Suffixes)
        {
            if (File.Exists(srcDb + suffix))
            {
                File.Copy(srcDb + suffix, destDb + suffix, overwrite: true);
            }
            else if (File.Exists(destDb + suffix))
            {
                File.Delete(destDb + suffix);
            }
        }
    }

    /// <summary>Deletes a database and its side files.</summary>
    public static void DeleteFileSet(string db)
    {
        foreach (string suffix in Suffixes)
        {
            File.Delete(db + suffix);
        }
    }
}
