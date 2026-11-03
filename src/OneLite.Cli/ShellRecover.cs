using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.Forensics;

namespace RecursiveAI.OneLite.Cli;

internal sealed partial class Shell
{
    private static readonly string[] RowidNames = ["rowid", "_rowid_", "oid"];

    /// <summary>
    /// <c>.recover</c>: writes the live rows of the damaged or intact file, then the rows carved out of free space, as SQL.
    /// The file is read through the forensic API only: no locks, no journal, no checkpoint.
    /// </summary>
    private bool DotRecover(string[] a)
    {
        string? outFile = null;
        double minConfidence = 0.5;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--min-confidence" && i + 1 < a.Length)
            {
                double.TryParse(a[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out minConfidence);
            }
            else if (a[i].StartsWith('-'))
            {
                return DotFail($"unknown option: {a[i]}");
            }
            else
            {
                outFile = a[i];
            }
        }

        if (_key is not null)
        {
            return DotFail(".recover does not read encrypted databases");
        }

        ByteOut dest = _out;
        bool ownsDest = false;
        if (outFile is not null)
        {
            dest = new ByteOut(new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), true);
            ownsDest = true;
        }

        try
        {
            using ForensicDatabase fdb = ForensicDatabase.Open(_dbPath);
            ForensicSnapshot snap = fdb.OpenSnapshot();
            using var fmtDb = Database.Open(":memory:");
            using Connection fmt = fmtDb.Connect();
            using Statement fmtStmt = fmt.Prepare("SELECT ?1");
            IReadOnlyList<ForensicTable> tables = snap.Tables;
            IReadOnlyList<CarvedRecord> carved = snap.Carve(new CarveOptions { MinConfidence = minConfidence });

            dest.Write("-- OneLite .recover: live rows first, then rows carved from free space.\n");
            dest.Write("-- Carved rows are guesses: each has a comment with its source and confidence, and goes in with INSERT OR IGNORE.\n");
            foreach (string note in snap.Notes)
            {
                dest.Write("-- note: " + note.Replace('\n', ' ') + "\n");
            }

            dest.Write("PRAGMA foreign_keys=OFF;\nBEGIN;\n");
            long liveRows = 0, carvedLoaded = 0, carvedComment = 0;
            var writable = new Dictionary<string, ForensicTable>(StringComparer.OrdinalIgnoreCase);
            foreach (ForensicTable t in tables.Where(t => t.Type == "table"))
            {
                if (t.Sql is null || t.Sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isSeq = t.Name == "sqlite_sequence";
                if (t.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) && !isSeq)
                {
                    continue;
                }

                if (!isSeq)
                {
                    dest.Write(t.Sql + ";\n");
                }
                else
                {
                    dest.Write("DELETE FROM sqlite_sequence;\n");
                }

                if (!t.CanReadRows)
                {
                    dest.Write($"-- table {t.Name}: its definition could not be understood, no rows recovered\n");
                    continue;
                }

                writable[t.Name] = t;
                List<ForensicRow> rows;
                try
                {
                    rows = snap.ReadRows(t);
                }
                catch (OneLiteException e)
                {
                    dest.Write($"-- table {t.Name}: reading stopped: {e.Message.Replace('\n', ' ')}\n");
                    continue;
                }

                IReadOnlyList<string> cols = t.ColumnNames;
                bool useRowid = !t.WithoutRowid && !isSeq && !cols.Any(c => RowidNames.Contains(c, StringComparer.OrdinalIgnoreCase));
                string colList = (useRowid ? "rowid," : string.Empty) + string.Join(",", cols.Select(SqlText.QuoteIdent));
                foreach (ForensicRow r in rows)
                {
                    dest.Write("INSERT INTO " + SqlText.QuoteIdent(t.Name) + "(" + colList + ") VALUES(");
                    if (useRowid)
                    {
                        dest.Write(r.Rowid.ToString(CultureInfo.InvariantCulture) + (r.Values.Length > 0 ? "," : string.Empty));
                    }

                    WriteValues(dest, fmtStmt, r.Values, r.Values.Length);
                    dest.Write(");\n");
                    liveRows++;
                }
            }

            foreach (CarvedRecord c in carved.OrderByDescending(c => c.Confidence))
            {
                string source = $"source={c.Source} file={c.File} page={c.Page} offset={c.PageOffset}" + (c.WalFrame > 0 ? $" wal_frame={c.WalFrame}" : string.Empty);
                string head = $"-- carved: table={c.Table ?? "?"} {source} confidence={c.Confidence.ToString("F2", CultureInfo.InvariantCulture)}" + (c.Truncated ? " truncated" : string.Empty) + (c.PrefixReconstructed ? " prefix-reconstructed" : string.Empty);
                if (c.Table is not null && writable.TryGetValue(c.Table, out ForensicTable? t) && t.Name != "sqlite_sequence" && c.Values.Length > 0 && c.Values.Length <= t.ColumnNames.Count)
                {
                    dest.Write(head + "\n");
                    dest.Write("INSERT OR IGNORE INTO " + SqlText.QuoteIdent(t.Name) + "(" + string.Join(",", t.ColumnNames.Take(c.Values.Length).Select(SqlText.QuoteIdent)) + ") VALUES(");
                    WriteValues(dest, fmtStmt, c.Values, c.Values.Length);
                    dest.Write(");\n");
                    carvedLoaded++;
                }
                else
                {
                    dest.Write(head + " (not loaded) values=");
                    var tmp = new MemoryStream();
                    using (var mo = new ByteOut(tmp, false))
                    {
                        WriteValues(mo, fmtStmt, c.Values, c.Values.Length);
                    }

                    dest.Write(Encoding.UTF8.GetString(tmp.ToArray()).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\n");
                    carvedComment++;
                }
            }

            foreach (ForensicTable t in tables.Where(t => t.Type == "index" && t.Sql is not null))
            {
                dest.Write(t.Sql + ";\n");
            }

            dest.Write("COMMIT;\n");
            ErrLine($"recovered {liveRows} live rows from {writable.Count} tables; carved {carvedLoaded + carvedComment} records ({carvedLoaded} loaded as INSERT OR IGNORE, {carvedComment} written as comments)");
        }
        catch (Exception e) when (e is OneLiteException or NotADatabaseException or IOException or ForensicChangedException)
        {
            return DotFail(e.Message);
        }
        finally
        {
            if (ownsDest)
            {
                dest.Dispose();
            }
        }

        return true;
    }

    private static void WriteValues(ByteOut o, Statement fmt, object?[] values, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                o.Write(",");
            }

            object? v = values[i];
            switch (v)
            {
                case null:
                    o.Write("NULL");
                    break;
                case long l:
                    o.Write(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case double d:
                    fmt.Reset();
                    fmt.Bind(1, d);
                    fmt.Step();
                    o.Write(fmt.GetTextUtf8(0));
                    fmt.Reset();
                    break;
                case string s:
                    o.Write(Renderer.QuoteText(Encoding.UTF8.GetBytes(s)));
                    break;
                case byte[] b:
                    o.Write("x'" + Convert.ToHexStringLower(b) + "'");
                    break;
                default:
                    o.Write("NULL");
                    break;
            }
        }
    }
}
