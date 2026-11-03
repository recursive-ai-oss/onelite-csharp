using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

internal sealed partial class Shell
{
    /// <summary>
    /// Reads records from delimited text with CSV quoting whatever the separators are: a field that starts with a double quote runs to the closing
    /// quote (a doubled quote is one quote), may hold separators and newlines, and a quote followed by anything else is kept and reported.
    /// A newline ends a record (CRLF too when the record separator is a newline).
    /// </summary>
    private sealed class DelimitedReader
    {
        private readonly string _text;
        private readonly string _colSep;
        private readonly string _rowSep;
        private readonly bool _raw;
        private readonly Action<string> _report;
        private int _pos;
        private int _line = 1;

        public DelimitedReader(string text, string colSep, string rowSep, bool raw, Action<string> report)
        {
            _raw = raw;
            _text = text;
            _colSep = colSep;
            _rowSep = rowSep;
            _report = report;
        }

        /// <summary>The 1-based line on which the last record read started.</summary>
        public int Line { get; private set; } = 1;

        /// <summary>True if the last record read ended with the end of the text rather than a newline.</summary>
        public bool EndedAtEof { get; private set; }

        /// <summary>True if the last record ended with a separator and then the end of the text: its last field is empty and, when the record is short, NULL.</summary>
        public bool EndsInSeparator { get; private set; }

        private bool At(int i, string s) => s.Length > 0 && string.CompareOrdinal(_text, i, s, 0, s.Length) == 0;

        private int RowEndLength(int i)
        {
            if (At(i, _rowSep))
            {
                return _rowSep.Length;
            }

            return _rowSep == "\n" && At(i, "\r\n") ? 2 : 0;
        }

        public bool Next(List<string> fields)
        {
            fields.Clear();
            EndedAtEof = false;
            EndsInSeparator = false;
            if (_pos >= _text.Length)
            {
                return false;
            }

            Line = _line;
            while (true)
            {
                var sb = new StringBuilder();
                if (!_raw && _text[_pos] == '"')
                {
                    _pos++;
                    int fieldLine = _line;
                    while (true)
                    {
                        if (_pos >= _text.Length)
                        {
                            int recordLine = Line;
                            Line = fieldLine;
                            _report("unterminated \"-quoted field");
                            Line = recordLine;
                            break;
                        }

                        char c = _text[_pos];
                        if (c == '"')
                        {
                            int next = _pos + 1;
                            if (next < _text.Length && _text[next] == '"')
                            {
                                sb.Append('"');
                                _pos += 2;
                                continue;
                            }

                            if (next >= _text.Length || At(next, _colSep) || RowEndLength(next) > 0)
                            {
                                _pos = next;
                                break;
                            }

                            // A quote before a carriage return that does not end the row is kept without a message, as the shell does.
                            if (_text[next] != '\r')
                            {
                                // reported at the line the quote is on; "unterminated" is reported at the line the field began on
                                int began = Line;
                                Line = _line;
                                _report("unescaped \" character");
                                Line = began;
                            }

                            sb.Append('"');
                            _pos++;
                            continue;
                        }

                        if (c == '\n')
                        {
                            _line++;
                        }

                        sb.Append(c);
                        _pos++;
                    }
                }
                else
                {
                    while (_pos < _text.Length && !At(_pos, _colSep) && RowEndLength(_pos) == 0)
                    {
                        sb.Append(_text[_pos]);
                        _pos++;
                    }
                }

                fields.Add(sb.ToString());
                if (_pos >= _text.Length)
                {
                    EndedAtEof = true;
                    return true;
                }

                if (At(_pos, _colSep))
                {
                    _pos += _colSep.Length;
                    if (_pos >= _text.Length)
                    {
                        fields.Add(string.Empty);
                        EndedAtEof = true;
                        EndsInSeparator = true;
                        return true;
                    }

                    continue;
                }

                _pos += RowEndLength(_pos);
                _line++;
                return true;
            }
        }
    }

    /// <summary>The column names for the table .import creates, four to a line as the shell lays them out.</summary>
    private static string ColumnList(List<string> names)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(i % 4 == 0 ? ",\n " : ", ");
            }

            sb.Append(SqlText.AlwaysQuoteIdent(names[i]));
        }

        return sb.ToString();
    }

    private bool DotImport(string[] a)
    {
        bool ascii = false;
        bool csv = false;
        int skip = 0;
        string schema = "main";
        var pos = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--csv":
                    csv = true;
                    break;
                case "--ascii":
                    ascii = true;
                    break;
                case "-v":
                    break;
                case "--skip" when i + 1 < a.Length:
                    int.TryParse(a[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out skip);
                    break;
                case "--schema" when i + 1 < a.Length:
                    schema = a[++i];
                    break;
                default:
                    if (a[i].StartsWith('-') && a[i].Length > 1)
                    {
                        return DotFail($"unknown option: {a[i]}");
                    }

                    pos.Add(a[i]);
                    break;
            }
        }

        if (pos.Count < 1)
        {
            ErrLine(ContextPrefix() + "Missing FILE argument");
            Fail();
            return false;
        }

        if (pos.Count != 2)
        {
            return DotUsage(".import", "?OPTIONS? FILE TABLE");
        }

        string file = pos[0];
        string table = pos[1];
        string colSep = _st.ColSep;
        string rowSep = _st.RowSep;
        if (csv)
        {
            colSep = ",";
            rowSep = "\n";
        }
        else if (ascii)
        {
            colSep = "\u001f";
            rowSep = "\u001e";
        }
        else if (_st.Mode == OutputMode.Csv)
        {
            csv = true;
            colSep = ",";
            rowSep = "\n";
        }

        string text;
        try
        {
            text = new UTF8Encoding(false).GetString(File.ReadAllBytes(file));
            if (!ascii && text.StartsWith('\uFEFF'))
            {
                text = text[1..];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrLine(ContextPrefix() + $"cannot open \"{file}\"");
            Fail();
            return false;
        }

        Connection? conn = Conn();
        if (conn is null)
        {
            return false;
        }

        DelimitedReader? reader = null;
        reader = new DelimitedReader(text, colSep, rowSep, ascii, msg => ErrLine($"{file}:{reader!.Line}: {msg}"));
        var fields = new List<string>();
        for (int i = 0; i < skip; i++)
        {
            if (!reader.Next(fields))
            {
                break;
            }
        }

        string qtable = SqlText.AlwaysQuoteIdent(table);
        string target = schema == "main" ? qtable : SqlText.AlwaysQuoteIdent(schema) + "." + qtable;
        List<string?[]> info = Query($"PRAGMA {SqlText.AlwaysQuoteIdent(schema)}.table_info({qtable})");
        List<string>? firstRow = null;
        if (info.Count == 0)
        {
            if (!reader.Next(fields))
            {
                ErrLine($"{file}: empty file");
                Fail();
                return false;
            }

            if (reader.EndsInSeparator && fields.Count > 1 && fields[^1].Length == 0)
            {
                // A header line cut off by the end of the file right after a separator names no column after it.
                fields.RemoveAt(fields.Count - 1);
            }

            var names = fields.Select(f => f.Length == 0 ? "?" : f).ToList();
            var renamed = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in names)
            {
                counts[n] = counts.GetValueOrDefault(n) + 1;
            }

            // A name used more than once is renamed in every column that has it, to name_N with N the column's position; the number is
            // zero-padded to the narrowest width at which none of the new names equals a name the header already has.
            var original = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string dup in counts.Where(kv => kv.Value > 1).Select(kv => kv.Key))
            {
                int w = 0;
                for (; ; w++)
                {
                    bool clash = false;
                    for (int i = 0; i < names.Count && !clash; i++)
                    {
                        clash = string.Equals(names[i], dup, StringComparison.OrdinalIgnoreCase)
                            && original.Contains(names[i] + "_" + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(w, '0'));
                    }

                    if (!clash)
                    {
                        break;
                    }
                }

                widths[dup] = w;
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (counts[names[i]] > 1)
                {
                    string nn = names[i] + "_" + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(widths[names[i]], '0');
                    renamed.Add($"\"{names[i]}\" to \"{nn}\"");
                    names[i] = nn;
                }
            }

            if (renamed.Count > 0)
            {
                ErrLine($"Columns renamed during .import {file} due to duplicates:\n" + string.Join(",\n", renamed));
            }

            string ddl = $"CREATE TABLE {target}(\n" + ColumnList(names) + ");";
            try
            {
                RunSilent(ddl);
            }
            catch (OneLiteException e)
            {
                return DotFail(e.Message);
            }

            info = Query($"PRAGMA {SqlText.AlwaysQuoteIdent(schema)}.table_info({qtable})");
        }
        else
        {
            firstRow = null;
        }

        _ = firstRow;
        int ncol = info.Count;
        string insert = $"INSERT INTO {target} VALUES(" + string.Join(",", Enumerable.Repeat("?", ncol)) + ")";
        bool inTx = !conn.AutoCommit;
        try
        {
            if (!inTx)
            {
                RunSilent("BEGIN");
            }

            using Statement ins = conn.Prepare(insert);
            while (reader.Next(fields))
            {
                if (fields.Count != ncol)
                {
                    ErrLine($"{file}:{reader.Line}: expected {ncol} columns but found {fields.Count} - " + (fields.Count < ncol ? "filling the rest with NULL" : "extras ignored"));
                }

                int present = reader.EndsInSeparator && fields.Count < ncol ? fields.Count - 1 : fields.Count;
                for (int i = 0; i < ncol; i++)
                {
                    if (i < present)
                    {
                        ins.Bind(i + 1, fields[i]);
                    }
                    else
                    {
                        ins.BindNull(i + 1);
                    }
                }

                try
                {
                    ins.Step();
                }
                finally
                {
                    ins.Reset();
                }
            }

            if (!inTx)
            {
                RunSilent("COMMIT");
            }
        }
        catch (OneLiteException e)
        {
            if (!inTx && !conn.AutoCommit)
            {
                RunSilent("ROLLBACK");
            }

            ErrLine($"{file}:{reader.Line}: INSERT failed: {e.Message}");
            Fail();
            return false;
        }

        return true;
    }
}
