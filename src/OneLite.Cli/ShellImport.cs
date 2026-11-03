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
                    while (true)
                    {
                        if (_pos >= _text.Length)
                        {
                            _report("unterminated \"-quoted field");
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

                            _report("unescaped \" character");
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
                    return true;
                }

                if (At(_pos, _colSep))
                {
                    _pos += _colSep.Length;
                    if (_pos >= _text.Length)
                    {
                        fields.Add(string.Empty);
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

            var names = fields.Select(f => f.Length == 0 ? "?" : f).ToList();
            var renamed = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in names)
            {
                counts[n] = counts.GetValueOrDefault(n) + 1;
            }

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                if (counts[names[i]] > 1)
                {
                    int k = seen[names[i]] = seen.GetValueOrDefault(names[i]) + 1;
                    string nn = names[i] + "_" + k.ToString(CultureInfo.InvariantCulture);
                    renamed.Add($"\"{names[i]}\" to \"{nn}\"");
                    names[i] = nn;
                }
            }

            if (renamed.Count > 0)
            {
                ErrLine($"Columns renamed during .import {file} due to duplicates:\n" + string.Join(",\n", renamed));
            }

            string ddl = $"CREATE TABLE {target}(\n" + string.Join(", ", names.Select(SqlText.AlwaysQuoteIdent)) + ");";
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

                for (int i = 0; i < ncol; i++)
                {
                    if (i < fields.Count)
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
