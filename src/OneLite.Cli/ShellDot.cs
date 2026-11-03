using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

internal sealed partial class Shell
{
    private static readonly string[] HelpLines =
    [
        ".backup ?DB? FILE        Back up DB (default \"main\") to FILE",
        ".bail on|off             Stop after hitting an error.  Default OFF",
        ".cd DIRECTORY            Change the working directory to DIRECTORY",
        ".changes on|off          Show the number of rows changed by SQL",
        ".databases               List names and files of attached databases",
        ".dump ?OBJECTS?          Render database content as SQL (--nosys --data-only --schema-only --preserve-rowids)",
        ".echo on|off             Turn command echo on or off",
        ".eqp on|off|full         Show the query plan before each query",
        ".exit ?CODE?             Exit this program with return-code CODE",
        ".headers on|off          Turn display of headers on or off",
        ".help ?-all? ?PATTERN?   Show help text for PATTERN",
        ".import FILE TABLE       Import data from FILE into TABLE (--csv --ascii --skip N --schema S)",
        ".indexes ?TABLE?         Show names of indexes (of tables matching LIKE pattern TABLE)",
        ".key KEY                 Use KEY as the passphrase of an encrypted (SQLCipher-format) database",
        ".mode MODE ?OPTIONS?     Set output mode: ascii box column csv html insert json line list markdown qbox quote table tabs tcl",
        ".nullvalue STRING        Use STRING in place of NULL values",
        ".once ?FILE?             Output for the next SQL command only to FILE",
        ".open ?OPTIONS? ?FILE?   Close existing database and reopen FILE (--readonly, --new)",
        ".output ?FILE?           Send output to FILE or stdout if FILE is omitted",
        ".print STRING...         Print literal STRING",
        ".quit                    Exit this program",
        ".read FILE               Read input from FILE",
        ".recover ?FILE?          Write recovered and carved rows as SQL (never modifies the database); see docs/cli.md",
        ".schema ?PATTERN?        Show the CREATE statements matching the LIKE pattern PATTERN",
        ".separator COL ?ROW?     Change the column and row separators",
        ".show                    Show the current values for various settings",
        ".tables ?TABLE?          List names of tables matching LIKE pattern TABLE",
        ".timer on|off            Turn SQL timer on or off",
        ".width NUM1 NUM2 ...     Set minimum column widths for columnar output",
    ];

    /// <summary>Splits a dot-command line into words: white-space separated, with 'single' quotes (verbatim) and "double" quotes (backslash escapes).</summary>
    private static List<string> SplitWords(string line, List<int> starts)
    {
        var words = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            starts.Add(i);
            var sb = new StringBuilder();
            if (line[i] is '\'' or '"')
            {
                char q = line[i++];
                while (i < line.Length && line[i] != q)
                {
                    if (q == '"' && line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        char e = line[i];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => e });
                    }
                    else
                    {
                        sb.Append(line[i]);
                    }

                    i++;
                }

                i++;
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    sb.Append(line[i++]);
                }
            }

            words.Add(sb.ToString());
        }

        return words;
    }

    /// <summary>Reads an on/off argument. Anything else is reported and taken as off, as the reference shell does.</summary>
    private bool BoolArg(string s, out bool value)
    {
        value = false;
        if (s.Equals("on", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (!(s.Equals("off", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase) || s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            ErrLine($"ERROR: Not a boolean value: \"{s}\". Assuming \"no\".");
        }

        return true;
    }

    private bool DotFail(string message)
    {
        ErrLine("Error: " + message);
        Fail();
        return false;
    }

    private bool DotUsage(string cmd, string usage)
    {
        ErrLine($"Usage: {cmd} {usage}");
        Fail();
        return false;
    }

    /// <summary>Runs one dot-command line. Returns false after an error.</summary>
    public bool RunDot(string line, int lineNo)
    {
        var starts = new List<int>();
        List<string> w = SplitWords(line.TrimEnd(), starts);
        if (w.Count == 0)
        {
            return true;
        }

        string cmd = w[0].ToLowerInvariant();
        string[] a = [.. w.Skip(1)];
        _dotLine = line;
        _dotLineNo = lineNo;
        _dotStarts = starts;
        bool ok = RunDotCore(cmd, a, line, lineNo, starts);
        if (_onceActive && cmd is not ".once")
        {
            EndOnce();
        }

        return ok;
    }

    private bool RunDotCore(string cmd, string[] a, string line, int lineNo, List<int> starts)
    {
        switch (cmd)
        {
            case ".quit":
            case ".exit":
                _quit = true;
                if (a.Length > 0 && int.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                {
                    _exitCode = code;
                }

                return true;
            case ".help":
                foreach (string h in HelpLines)
                {
                    if (a.Length == 0 || a.Where(x => x != "-all").All(x => h.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    {
                        _out.Write(h + "\n");
                    }
                }

                return true;
            case ".bail":
                if (a.Length != 1 || !BoolArg(a[0], out bool bail))
                {
                    return DotUsage(".bail", "on|off");
                }

                _bail = bail;
                return true;
            case ".echo":
                if (a.Length != 1 || !BoolArg(a[0], out bool echo))
                {
                    return DotUsage(".echo", "on|off");
                }

                _echo = echo;
                return true;
            case ".changes":
                if (a.Length != 1 || !BoolArg(a[0], out bool ch))
                {
                    return DotUsage(".changes", "on|off");
                }

                _changes = ch;
                return true;
            case ".timer":
                if (a.Length != 1 || !BoolArg(a[0], out bool tm))
                {
                    return DotUsage(".timer", "on|off");
                }

                _timer = tm;
                return true;
            case ".headers":
            case ".header":
                if (a.Length != 1 || !BoolArg(a[0], out bool hd))
                {
                    return DotUsage(".headers", "on|off");
                }

                _st.Headers = hd;
                return true;
            case ".eqp":
                if (a.Length != 1)
                {
                    return DotUsage(".eqp", "on|off|full");
                }

                _eqp = a[0].ToLowerInvariant() switch { "on" or "yes" or "1" or "true" => "on", "full" => "full", "trigger" => "trigger", _ => "off" };
                return true;
            case ".explain":
                return true;
            case ".nullvalue":
                if (a.Length != 1)
                {
                    return DotUsage(".nullvalue", "STRING");
                }

                _st.NullValue = a[0];
                return true;
            case ".separator":
                if (a.Length is < 1 or > 2)
                {
                    return DotUsage(".separator", "COL ?ROW?");
                }

                _st.ColSep = a[0];
                if (a.Length == 2)
                {
                    _st.RowSep = a[1];
                }

                return true;
            case ".width":
                _st.Widths = [.. a.Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0)];
                return true;
            case ".mode":
                return DotMode(a, line, lineNo, starts);
            case ".show":
                DotShow();
                return true;
            case ".print":
                _out.Write(string.Join(" ", a) + "\n");
                return true;
            case ".cd":
                if (a.Length != 1)
                {
                    return DotUsage(".cd", "DIRECTORY");
                }

                try
                {
                    Directory.SetCurrentDirectory(a[0]);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return DotFail($"cannot change to directory \"{a[0]}\"");
                }

                return true;
            case ".output":
                SetOutput(a.Length > 0 ? a[0] : null, false);
                return true;
            case ".once":
                SetOutput(a.Length > 0 ? a[^1] : null, true);
                return true;
            case ".open":
                return DotOpen(a);
            case ".key":
                if (a.Length != 1)
                {
                    return DotUsage(".key", "KEY");
                }

                _key = a[0];
                CloseDb();
                return true;
            case ".read":
                return DotRead(a);
            case ".databases":
                return DotDatabases();
            case ".tables":
                return DotTables(a);
            case ".indexes":
            case ".indices":
                return DotIndexes(a);
            case ".schema":
                return DotSchema(a);
            case ".dump":
                return DotDump(a);
            case ".import":
                return DotImport(a);
            case ".recover":
                return DotRecover(a);
            case ".shell":
            case ".system":
                return DotShell(a);
            case ".backup":
            case ".save":
                return DotBackup(a);
            default:
                return DotFail($"unknown command or invalid arguments:  \"{cmd[1..]}\". Enter \".help\" for help");
        }
    }

    private string _dotLine = string.Empty;
    private int _dotLineNo;
    private List<int> _dotStarts = [];

    /// <summary>Reports a bad option the way the reference shell does: the command line again, with a caret under the word, each line prefixed by where the command came from.</summary>
    private bool OptionError(int word, string what, string? hint = null)
    {
        string p = _where == "in" ? $"argv[{_argIndex}]: " : $"line {Math.Max(_dotLineNo, 1)}: ";
        ErrLine(p + _dotLine.TrimEnd());
        ErrLine(p + new string(' ', _dotStarts[Math.Min(word, _dotStarts.Count - 1)]) + "^--- " + what);
        if (hint is not null)
        {
            ErrLine(p + hint);
        }

        Fail();
        return false;
    }

    private bool DotMode(string[] a, string line, int lineNo, List<int> starts)
    {
        if (a.Length == 0)
        {
            string name = ModeName(_st.Mode);
            _out.Write(_st.Mode == OutputMode.Insert ? $".mode insert --tablename \"{_st.InsertTable}\"\n" : $".mode {name}\n");
            return true;
        }

        string m = a[0].ToLowerInvariant();
        OutputMode? mode = m switch
        {
            "list" => OutputMode.List, "csv" => OutputMode.Csv, "tabs" => OutputMode.Tabs, "ascii" => OutputMode.Ascii, "line" => OutputMode.Line,
            "json" => OutputMode.Json, "column" => OutputMode.Column, "box" => OutputMode.Box, "table" => OutputMode.Table, "markdown" => OutputMode.Markdown,
            "html" => OutputMode.Html, "quote" => OutputMode.Quote, "qbox" => OutputMode.Qbox, "insert" => OutputMode.Insert, "tcl" => OutputMode.Tcl,
            _ => null,
        };
        if (mode is null)
        {
            return OptionError(1, "unknown mode", "Use \".help .mode\" for more info");
        }

        for (int i = 1; i < a.Length; i++)
        {
            if (a[i].Equals("--tablename", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length)
            {
                _st.InsertTable = a[++i];
            }
            else if (a[i] is "--wrap" or "--escape" or "--null" or "--quote" or "-w" && i + 1 < a.Length && a[i] != "--quote")
            {
                i++;
            }
            else if (a[i].StartsWith('-'))
            {
                if (a[i] is not ("--ww" or "--quote" or "--noquote" or "-x" or "--align"))
                {
                    return OptionError(i + 1, "bad option", "Use \".help .mode\" for more info");
                }
            }
            else if (mode == OutputMode.Insert)
            {
                _st.InsertTable = a[i];
            }
        }

        SetMode(mode.Value);
        return true;
    }

    private void SetMode(OutputMode mode)
    {
        _st.Mode = mode;
        _st.NullValue = mode switch { OutputMode.Html or OutputMode.Json => "null", OutputMode.Qbox or OutputMode.Quote or OutputMode.Insert => "NULL", OutputMode.Tcl => "\"\"", _ => string.Empty };
        switch (mode)
        {
            case OutputMode.Csv:
                _st.ColSep = ",";
                _st.RowSep = "\r\n";
                break;
            case OutputMode.Tabs:
                _st.ColSep = "\t";
                _st.RowSep = "\n";
                break;
            case OutputMode.Ascii:
                _st.ColSep = "\u001f";
                _st.RowSep = "\u001e";
                break;
            case OutputMode.List:
                _st.ColSep = "|";
                _st.RowSep = "\n";
                break;
            case OutputMode.Tcl:
                _st.ColSep = " ";
                _st.RowSep = "\n";
                break;
            default:
                break;
        }

        if (mode is OutputMode.Column or OutputMode.Box or OutputMode.Table or OutputMode.Markdown or OutputMode.Html or OutputMode.Qbox)
        {
            _st.Headers = true;
        }
    }

    private static string ModeName(OutputMode m) => m.ToString().ToLowerInvariant();

    /// <summary>Applies a command-line mode option such as <c>-csv</c>; the aligned modes also turn headers on, as the reference shell does.</summary>
    public void SetModeFromOption(OutputMode mode) => SetMode(mode);

    private void DotShow()
    {
        string Q(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        void Row(string k, string v) => _out.Write(k.PadLeft(12) + ": " + v + "\n");
        Row("echo", _echo ? "on" : "off");
        Row("eqp", _eqp);
        Row("explain", "auto");
        Row("headers", _st.Headers ? "on" : "off");
        Row("mode", ModeName(_st.Mode));
        Row("nullvalue", Q(_st.NullValue));
        Row("output", _outputName);
        Row("colseparator", Q(_st.ColSep));
        Row("rowseparator", Q(_st.RowSep));
        Row("stats", "off");
        Row("width", string.Join(" ", _st.Widths.Select(x => x.ToString(CultureInfo.InvariantCulture))));
        Row("filename", _dbPath);
    }

    private bool DotOpen(string[] a)
    {
        bool ro = false;
        bool create = false;
        string? file = null;
        foreach (string s in a)
        {
            if (s == "--readonly")
            {
                ro = true;
            }
            else if (s == "--new")
            {
                create = true;
            }
            else if (s.StartsWith('-'))
            {
                return DotFail($"unknown option: {s}");
            }
            else
            {
                file = s;
            }
        }

        CloseDb();
        if (create && file is not null && File.Exists(file))
        {
            File.Delete(file);
        }

        _readOnly = ro;
        _dbPath = file ?? ":memory:";
        return true;
    }

    private bool DotRead(string[] a)
    {
        if (a.Length != 1)
        {
            return DotUsage(".read", "FILE");
        }

        if (!File.Exists(a[0]))
        {
            return DotFail($"cannot open \"{a[0]}\"");
        }

        using var r = new StreamReader(a[0], Encoding.UTF8);
        string saved = _where;
        RunReader(r, false);
        _where = saved;
        return _exitCode == 0;
    }

    private bool DotDatabases()
    {
        foreach (string?[] r in Query("PRAGMA database_list"))
        {
            string file = string.IsNullOrEmpty(r[2]) || _dbPath is ":memory:" or "" && r[1] == "main" ? "\"\"" : r[2]!;
            _out.Write($"{r[1]}: {file} {(_readOnly ? "r/o" : "r/w")}\n");
        }

        return true;
    }

    // ------------------------------------------------------------------ listings

    private void PrintNames(List<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        // Names go down the columns, in as few rows as leave a gap of at least 3 spaces between columns inside 80 columns; the gap is at most 5.
        int rows = 1;
        int cols;
        int[] widths;
        int gap;
        while (true)
        {
            cols = (names.Count + rows - 1) / rows;
            widths = new int[cols];
            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < rows; r++)
                {
                    int i = c * rows + r;
                    if (i < names.Count)
                    {
                        widths[c] = Math.Max(widths[c], SqlText.DisplayWidth(SqlText.Utf8(names[i])));
                    }
                }
            }

            gap = cols > 1 ? Math.Min(5, (80 - widths.Sum()) / (cols - 1)) : 5;
            if (gap >= 3 || rows >= names.Count)
            {
                break;
            }

            rows++;
        }

        gap = Math.Max(gap, 1);
        for (int r = 0; r < rows; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < cols; c++)
            {
                int i = c * rows + r;
                if (i >= names.Count)
                {
                    break;
                }

                string n = names[i];
                sb.Append(n);
                if (i + rows < names.Count)
                {
                    sb.Append(' ', widths[c] - SqlText.DisplayWidth(SqlText.Utf8(n)) + gap);
                }
            }

            _out.Write(sb + "\n");
        }
    }

    private bool DotTables(string[] a)
    {
        string pat = a.Length > 0 ? a[0] : "%";
        var names = new List<string>();
        foreach (string?[] db in Query("PRAGMA database_list"))
        {
            string schema = db[1]!;
            string q = SqlText.AlwaysQuoteIdent(schema);
            string master = schema == "temp" ? "sqlite_temp_schema" : q + ".sqlite_schema";
            foreach (string?[] r in Query($"SELECT name FROM {master} WHERE type IN ('table','view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name LIKE ?1", pat))
            {
                names.Add(schema is "main" ? r[0]! : schema + "." + r[0]!);
            }
        }

        names.Sort(StringComparer.Ordinal);
        PrintNames(names);
        return true;
    }

    private bool DotIndexes(string[] a)
    {
        string pat = a.Length > 0 ? a[0] : "%";
        var names = new List<string>();
        foreach (string?[] db in Query("PRAGMA database_list"))
        {
            string schema = db[1]!;
            string master = schema == "temp" ? "sqlite_temp_schema" : SqlText.AlwaysQuoteIdent(schema) + ".sqlite_schema";
            foreach (string?[] r in Query($"SELECT name FROM {master} WHERE type='index' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name LIKE ?1", pat))
            {
                names.Add(schema is "main" ? r[0]! : schema + "." + r[0]!);
            }
        }

        names.Sort(StringComparer.Ordinal);
        PrintNames(names);
        return true;
    }

    private bool DotSchema(string[] a)
    {
        string? pat = null;
        foreach (string s in a)
        {
            if (!s.StartsWith("--", StringComparison.Ordinal))
            {
                pat = s;
            }
        }

        foreach (string schema in new[] { "main", "temp" })
        {
            string master = schema == "temp" ? "sqlite_temp_schema" : "sqlite_schema";
            string sql = $"SELECT sql, type, name FROM {master} WHERE sql NOT NULL" + (pat is null ? string.Empty : " AND tbl_name LIKE ?1") + " ORDER BY rowid";
            List<string?[]> rows = pat is null ? Query(sql) : Query(sql, pat);
            foreach (string?[] r in rows)
            {
                string text = r[0]!;
                if (schema == "temp")
                {
                    text = AddTempPrefix(text);
                }

                bool isVirtual = text.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);
                if (r[1] == "view" || isVirtual)
                {
                    string comment = ColumnComment(schema, r[2]!);
                    _out.Write(text + (comment.Length > 0 ? "\n" + comment : string.Empty) + ";\n");
                }
                else
                {
                    _out.Write(text + ";\n");
                }
            }
        }

        return true;
    }

    private static string AddTempPrefix(string sql)
    {
        foreach (string kw in new[] { "CREATE TABLE ", "CREATE VIEW ", "CREATE INDEX ", "CREATE UNIQUE INDEX ", "CREATE TRIGGER ", "CREATE VIRTUAL TABLE " })
        {
            if (sql.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
            {
                return sql[..kw.Length] + "temp." + sql[kw.Length..];
            }
        }

        return sql;
    }

    private string ColumnComment(string schema, string name)
    {
        try
        {
            List<string?[]> cols = Query($"PRAGMA {SqlText.AlwaysQuoteIdent(schema)}.table_info({SqlText.AlwaysQuoteIdent(name)})");
            var names = cols.Select(c => c[1]!).ToList();
            if (names.Count == 0 && Conn() is { } conn)
            {
                // A virtual table's declared columns: read them off a statement that selects from it.
                using Statement st = conn.Prepare($"SELECT * FROM {SqlText.AlwaysQuoteIdent(schema)}.{SqlText.AlwaysQuoteIdent(name)}");
                for (int i = 0; i < st.ColumnCount; i++)
                {
                    names.Add(st.ColumnName(i));
                }
            }

            if (names.Count == 0)
            {
                return string.Empty;
            }

            return "/* " + SqlText.QuoteIdent(name) + "(" + string.Join(",", names.Select(SqlText.QuoteIdent)) + ") */";
        }
        catch (OneLiteException)
        {
            return string.Empty;
        }
    }

    private bool DotShell(string[] a)
    {
        if (a.Length == 0)
        {
            return DotUsage(".shell", "CMD ARGS...");
        }

        _out.Flush();
        try
        {
            bool win = OperatingSystem.IsWindows();
            var psi = new System.Diagnostics.ProcessStartInfo(win ? "cmd" : "/bin/sh") { UseShellExecute = false, RedirectStandardOutput = !ReferenceEquals(_out, _stdout) };
            psi.ArgumentList.Add(win ? "/c" : "-c");
            psi.ArgumentList.Add(string.Join(" ", a));
            using System.Diagnostics.Process? p = System.Diagnostics.Process.Start(psi);
            if (p is not null)
            {
                if (psi.RedirectStandardOutput)
                {
                    using var ms = new MemoryStream();
                    p.StandardOutput.BaseStream.CopyTo(ms);
                    _out.Write(ms.ToArray());
                }

                p.WaitForExit();
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return DotFail("cannot run the command: " + e.Message);
        }

        return true;
    }

    private bool DotBackup(string[] a)
    {
        _ = a;
        return DotFail("this command is not supported by onelite yet");
    }
}
