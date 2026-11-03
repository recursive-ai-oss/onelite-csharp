using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

internal sealed partial class Shell
{
    /// <summary>Reads records from delimited text. CSV rules: a field may be quoted with double quotes (a doubled quote is one quote), quoted fields may hold separators and newlines, CRLF ends a record.</summary>
    private sealed class DelimitedReader
    {
        private readonly string _text;
        private readonly string _colSep;
        private readonly string _rowSep;
        private readonly bool _csv;
        private int _pos;

        public DelimitedReader(string text, string colSep, string rowSep, bool csv)
        {
            _text = text;
            _colSep = colSep;
            _rowSep = rowSep;
            _csv = csv;
        }

        /// <summary>The 1-based line on which the last record read started.</summary>
        public int Line { get; private set; } = 1;

        private int _line = 1;

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
                bool quoted = _csv && _pos < _text.Length && _text[_pos] == '"';
                if (quoted)
                {
                    _pos++;
                    while (_pos < _text.Length)
                    {
                        char c = _text[_pos];
                        if (c == '"')
                        {
                            if (_pos + 1 < _text.Length && _text[_pos + 1] == '"')
                            {
                                sb.Append('"');
                                _pos += 2;
                                continue;
                            }

                            _pos++;
                            break;
                        }

                        if (c == '\n')
                        {
                            _line++;
                        }

                        sb.Append(c);
                        _pos++;
                    }
                }

                // Unquoted tail (or the whole field) up to the next separator or record end.
                while (_pos < _text.Length)
                {
                    if (string.CompareOrdinal(_text, _pos, _colSep, 0, _colSep.Length) == 0)
                    {
                        break;
                    }

                    if (string.CompareOrdinal(_text, _pos, _rowSep, 0, _rowSep.Length) == 0)
                    {
                        break;
                    }

                    if (_csv && _text[_pos] == '\r' && _pos + 1 < _text.Length && _text[_pos + 1] == '\n')
                    {
                        break;
                    }

                    sb.Append(_text[_pos]);
                    _pos++;
                }

                fields.Add(sb.ToString());
                if (_pos >= _text.Length)
                {
                    return true;
                }

                if (string.CompareOrdinal(_text, _pos, _colSep, 0, _colSep.Length) == 0)
                {
                    _pos += _colSep.Length;
                    if (_pos >= _text.Length)
                    {
                        fields.Add(string.Empty);
                        return true;
                    }

                    continue;
                }

                if (_csv && _text[_pos] == '\r')
                {
                    _pos += 2;
                }
                else
                {
                    _pos += _rowSep.Length;
                }

                _line++;
                return true;
            }
        }
    }

    private bool DotImport(string[] a)
    {
        bool csv = false, ascii = false;
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
            text = File.ReadAllText(file, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DotFail($"cannot open \"{file}\"");
        }

        Connection? conn = Conn();
        if (conn is null)
        {
            return false;
        }

        var reader = new DelimitedReader(text, colSep, rowSep, csv);
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
