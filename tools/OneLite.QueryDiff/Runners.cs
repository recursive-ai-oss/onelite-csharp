using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.QueryDiff;

/// <summary>One value, as a comparison key and as text for reports.</summary>
internal readonly record struct Cell(string Key, string Show);

/// <summary>What running one query produced: column names, rows, and the error that ended it, if any.</summary>
internal sealed class Outcome
{
    internal List<string> Names = [];
    internal List<Cell[]> Rows = [];
    internal bool HasError;

    /// <summary>"parse" when preparing failed, "run" when stepping did.</summary>
    internal string Phase = "";
    internal int Code;
    internal string Message = "";

    /// <summary>The statement needed a synchronous backend call the cold async mode refuses (see <see cref="Qd"/>).</summary>
    internal bool NeedsBlocking;

    internal string ErrorText => HasError ? $"{Phase} error ({Code}): {Message}" : "";
}

internal static class Canon
{
    internal static Cell Null() => new("N", "NULL");

    internal static Cell Int(long v) => new("I:" + v.ToString(CultureInfo.InvariantCulture), v.ToString(CultureInfo.InvariantCulture));

    // The shell prints a negative zero as 0.0, so both sides are compared as +0.
    internal static Cell Real(double d) => new("R:" + BitConverter.DoubleToInt64Bits(d == 0 ? 0.0 : d).ToString("x16", CultureInfo.InvariantCulture), d.ToString("R", CultureInfo.InvariantCulture) + "r");

    internal static Cell Text(ReadOnlySpan<byte> utf8) => new("T:" + Convert.ToHexString(utf8), "'" + Encoding.UTF8.GetString(utf8).Replace("\n", "\\n", StringComparison.Ordinal) + "'");

    internal static Cell Blob(ReadOnlySpan<byte> b) => new("B:" + Convert.ToHexString(b), "x'" + Convert.ToHexString(b) + "'");
}

/// <summary>Runs queries on OneLite.</summary>
internal static class OursRunner
{
    internal static Outcome Run(Connection conn, string sql, bool cold = false)
    {
        var o = new Outcome();
        Statement st;
        try
        {
            st = Qd.Prepare(conn, sql, cold);
        }
        catch (OneLiteException e)
        {
            o.NeedsBlocking = Qd.NeedsBlocking(e);
            o.HasError = true;
            o.Phase = "parse";
            o.Code = e.Code;
            o.Message = e.Message;
            return o;
        }

        try
        {
            try
            {
                int n = st.ColumnCount;
                for (int i = 0; i < n; i++)
                {
                    o.Names.Add(st.ColumnName(i));
                }

                while (Qd.Step(st))
                {
                    var row = new Cell[n];
                    for (int i = 0; i < n; i++)
                    {
                        row[i] = st.ColumnType(i) switch
                        {
                            ColumnType.Null => Canon.Null(),
                            ColumnType.Integral => Canon.Int(st.GetInt64(i)),
                            ColumnType.Real => Canon.Real(st.GetDouble(i)),
                            ColumnType.Text => Canon.Text(st.GetTextUtf8(i)),
                            _ => Canon.Blob(st.GetBlob(i)),
                        };
                    }

                    o.Rows.Add(row);
                }
            }
            catch (OneLiteException e)
            {
                o.NeedsBlocking = Qd.NeedsBlocking(e);
                o.HasError = true;
                o.Phase = "run";
                o.Code = e.Code;
                o.Message = e.Message;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // An engine bug must show up as a difference for this query, not take the whole campaign down.
                o.HasError = true;
                o.Phase = "run";
                o.Message = "internal error: " + e.GetType().Name + " " + e.StackTrace?.Split('\n')[0].Trim();
            }
        }
        finally
        {
            Qd.Close(st, cold);
        }

        return o;
    }
}

/// <summary>Runs queries on the pinned sqlite3 shell in quote mode and parses what it prints.</summary>
internal static class ShellRunner
{
    internal static Outcome[] RunBatch(string dbPath, IReadOnlyList<string> queries, bool readOnly = true)
    {
        var sb = new StringBuilder();
        sb.Append(".mode quote\n.headers on\n");
        for (int i = 0; i < queries.Count; i++)
        {
            sb.Append(".print @@Q").Append(i).Append("@@\n").Append(queries[i]).Append(";\n");
        }

        sb.Append(".print @@END@@\n");
        var args = new List<string> { "-c", File.Exists("/usr/bin/stdbuf") ? "exec stdbuf -o0 \"$0\" \"$@\" 2>&1" : "exec \"$0\" \"$@\" 2>&1", OraclePaths.Cli, "-batch", "-init", "/dev/null" };
        if (readOnly)
        {
            args.Add("-readonly");
        }

        args.Add(dbPath);
        return Parse(RunLatin1("/bin/sh", args, sb.ToString()), queries.Count);
    }

    // Standard output as Latin-1 (one char per byte) so invalid UTF-8 in a result is compared exactly; input goes in as UTF-8.
    private static string RunLatin1(string exe, List<string> args, string stdin)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.Latin1,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        Task<string> outT = p.StandardOutput.ReadToEndAsync();
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        if (!p.WaitForExit(TimeSpan.FromMinutes(20)))
        {
            p.Kill();
            throw new TimeoutException("sqlite3 shell timed out");
        }

        return outT.GetAwaiter().GetResult();
    }

    internal static Outcome[] Parse(string output, int count)
    {
        var result = new Outcome[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = new Outcome();
        }

        Outcome? cur = null;
        bool sawHeader = false;
        string[] raw = output.Split('\n');
        for (int li = 0; li < raw.Length; li++)
        {
            string line = raw[li];
            // A text value with embedded newlines spans several lines: join until the quotes balance.
            while (!line.StartsWith("@@", StringComparison.Ordinal) && !line.StartsWith("Parse error", StringComparison.Ordinal) && !line.StartsWith("Error", StringComparison.Ordinal)
                && !line.StartsWith("Runtime error", StringComparison.Ordinal) && !line.StartsWith("  ", StringComparison.Ordinal)
                && line.Count(ch => ch == '\'') % 2 == 1 && li + 1 < raw.Length)
            {
                line = line + "\n" + raw[++li];
            }

            if (line.StartsWith("@@Q", StringComparison.Ordinal) && line.EndsWith("@@", StringComparison.Ordinal))
            {
                int idx = int.Parse(line.AsSpan(3, line.Length - 5), CultureInfo.InvariantCulture);
                cur = result[idx];
                sawHeader = false;
                continue;
            }

            if (line.StartsWith("@@END", StringComparison.Ordinal))
            {
                cur = null;
                continue;
            }

            if (cur is null || line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("Parse error", StringComparison.Ordinal) || line.StartsWith("Error", StringComparison.Ordinal) || line.StartsWith("Runtime error", StringComparison.Ordinal))
            {
                cur.HasError = true;
                cur.Phase = line.StartsWith("Parse error", StringComparison.Ordinal) ? "parse" : "run";
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                string msg = colon < 0 ? line : line[(colon + 2)..];
                cur.Code = 1;
                if (msg.EndsWith(')') && msg.LastIndexOf(" (", StringComparison.Ordinal) is int p and > 0 && int.TryParse(msg.AsSpan(p + 2, msg.Length - p - 3), out int code))
                {
                    cur.Code = code;
                    msg = msg[..p];
                }

                cur.Message = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(msg));
                continue;
            }

            if (line.StartsWith("  ", StringComparison.Ordinal) && cur.HasError)
            {
                continue; // the quoted statement and the caret line after an error
            }

            if (!sawHeader)
            {
                sawHeader = true;
                foreach (Cell c in ParseRow(line))
                {
                    cur.Names.Add(Encoding.UTF8.GetString(Convert.FromHexString(c.Key[2..])));
                }

                continue;
            }

            try
            {
                cur.Rows.Add(ParseRow(line));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new FormatException("cannot parse shell row: " + (line.Length > 300 ? line[..100] + "..." + line[^150..] + " len=" + line.Length : line), ex);
            }
        }

        return result;
    }

    /// <summary>Parses one line of quote-mode output.</summary>
    internal static Cell[] ParseRow(string line)
    {
        var cells = new List<Cell>();
        int p = 0;
        while (p <= line.Length)
        {
            if (p == line.Length)
            {
                break;
            }

            if (line[p] == '\'')
            {
                string s = ReadQuoted(line, ref p);
                cells.Add(Canon.Text(Encoding.Latin1.GetBytes(s)));
            }
            else if (string.CompareOrdinal(line, p, "unistr('", 0, 8) == 0)
            {
                p += 7;
                string s = Unistr(ReadQuoted(line, ref p));
                p++; // ')'
                cells.Add(Canon.Text(Encoding.Latin1.GetBytes(s)));
            }
            else if ((line[p] == 'x' || line[p] == 'X') && p + 1 < line.Length && line[p + 1] == '\'')
            {
                int e = line.IndexOf('\'', p + 2);
                cells.Add(Canon.Blob(Convert.FromHexString(line.AsSpan(p + 2, e - p - 2))));
                p = e + 1;
            }
            else
            {
                int e = line.IndexOf(',', p);
                if (e < 0)
                {
                    e = line.Length;
                }

                string tok = line[p..e];
                p = e;
                if (tok == "NULL")
                {
                    cells.Add(Canon.Null());
                }
                else if (long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long iv))
                {
                    cells.Add(Canon.Int(iv));
                }
                else if (tok == "Inf")
                {
                    cells.Add(Canon.Real(double.PositiveInfinity));
                }
                else if (tok == "-Inf")
                {
                    cells.Add(Canon.Real(double.NegativeInfinity));
                }
                else
                {
                    cells.Add(Canon.Real(double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture)));
                }
            }

            if (p < line.Length)
            {
                if (line[p] != ',')
                {
                    throw new FormatException("bad row: " + line);
                }

                p++;
            }
        }

        return [.. cells];
    }

    private static string ReadQuoted(string line, ref int p)
    {
        var sb = new StringBuilder();
        p++;
        while (true)
        {
            int q = line.IndexOf('\'', p);
            if (q < 0)
            {
                throw new FormatException("unterminated quote: " + line);
            }

            sb.Append(line, p, q - p);
            if (q + 1 < line.Length && line[q + 1] == '\'')
            {
                sb.Append('\'');
                p = q + 2;
                continue;
            }

            p = q + 1;
            return sb.ToString();
        }
    }

    // The shell's output is read as Latin-1 so every byte survives; an escape therefore adds the code point's UTF-8 bytes as chars.
    private static string Latin1Utf8(string codePoint) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(codePoint));

    private static string Unistr(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }

            char n = s[i + 1];
            if (n == '\\')
            {
                sb.Append('\\');
                i++;
            }
            else if (n == 'u' && i + 5 < s.Length + 0)
            {
                sb.Append(Latin1Utf8(char.ConvertFromUtf32(int.Parse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
                i += 5;
            }
            else if (n == 'U')
            {
                sb.Append(Latin1Utf8(char.ConvertFromUtf32(int.Parse(s.AsSpan(i + 2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
                i += 9;
            }
            else if (Uri.IsHexDigit(n) && i + 4 < s.Length + 0)
            {
                sb.Append(Latin1Utf8(char.ConvertFromUtf32(int.Parse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
                i += 4;
            }
            else
            {
                sb.Append('\\');
            }
        }

        return sb.ToString();
    }
}

internal static class Compare
{
    /// <summary>Returns null when the outcomes agree, else a description of the first difference.</summary>
    internal static string? Diff(Outcome ours, Outcome shell, bool ordered)
    {
        if (ours.HasError != shell.HasError)
        {
            return $"ours: {(ours.HasError ? ours.ErrorText : ours.Rows.Count + " rows")}; sqlite3: {(shell.HasError ? shell.ErrorText : shell.Rows.Count + " rows")}";
        }

        if (ours.HasError)
        {
            // The shell prints no code for SQLITE_MISMATCH (20) either, so it reads as 1; the pinned library reports 20 for "datatype mismatch".
            bool mismatchCode = ours.Code == 20 && shell.Code == 1 && ours.Message == "datatype mismatch";
            if (ours.Phase != shell.Phase || (ours.Code != shell.Code && !mismatchCode) || ours.Message != shell.Message)
            {
                // The shell reports the primary code only when it is not SQLITE_ERROR.
                return $"error differs: ours {ours.ErrorText}; sqlite3 {shell.ErrorText}";
            }
        }

        if (ours.Rows.Count != shell.Rows.Count)
        {
            return $"row count: ours {ours.Rows.Count}, sqlite3 {shell.Rows.Count}";
        }

        if (ours.Rows.Count > 0 && !ours.Names.SequenceEqual(shell.Names))
        {
            return $"column names: ours [{string.Join("|", ours.Names)}], sqlite3 [{string.Join("|", shell.Names)}]";
        }

        IEnumerable<Cell[]> a = ours.Rows, b = shell.Rows;
        if (!ordered)
        {
            a = a.OrderBy(Join, StringComparer.Ordinal);
            b = b.OrderBy(Join, StringComparer.Ordinal);
        }

        int i = 0;
        foreach ((Cell[] x, Cell[] y) in a.Zip(b))
        {
            if (Join(x) != Join(y))
            {
                string sx = string.Join(", ", x.Select(c => c.Show)), sy = string.Join(", ", y.Select(c => c.Show));
                if (sx == sy)
                {
                    // Same rendering, different bytes (invalid UTF-8, NUL): show the exact keys.
                    sx = string.Join(", ", x.Select(NormKey));
                    sy = string.Join(", ", y.Select(NormKey));
                }

                return $"row {i}: ours ({sx}); sqlite3 ({sy})";
            }

            i++;
        }

        return null;
    }

    private static string Join(Cell[] row) => string.Join("\u0001", row.Select(NormKey));

    /// <summary>
    /// The comparison key of a cell. One known harness limit is folded here: the shell prints a text value only up to its first NUL.
    /// </summary>
    private static string NormKey(Cell c)
    {
        if (!c.Key.StartsWith("T:", StringComparison.Ordinal))
        {
            return c.Key;
        }

        byte[] b = Convert.FromHexString(c.Key.AsSpan(2));
        int z = Array.IndexOf(b, (byte)0);
        if (z >= 0)
        {
            b = b[..z];
        }

        return "T:" + Convert.ToHexString(b);
    }
}
