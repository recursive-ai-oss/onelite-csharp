using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

/// <summary>The interactive and scriptable shell: input loop, SQL execution and output, the connection and the settings.</summary>
internal sealed partial class Shell : IDisposable
{
    private readonly Settings _st = new();
    private ByteOut _out;
    private readonly ByteOut _stdout;
    private readonly ByteOut _stderr;
    private bool _onceActive;
    private Database? _db;
    private Connection? _conn;
    private string _dbPath = ":memory:";
    private bool _readOnly;
    private string? _key;
    private bool _bail;
    private bool _echo;
    private bool _timer;
    private bool _changes;
    private string _eqp = "off";
    private bool _quit;
    private int _exitCode;
    private string _outputName = "stdout";

    /// <summary>How the text being run is named in error messages: "near line N" for a script, "in the 2nd command line argument" for an argument.</summary>
    private string _where = "near line";
    private int _argIndex;

    public Shell(Stream stdout, Stream stderr)
    {
        _stdout = new ByteOut(stdout, false);
        _stderr = new ByteOut(stderr, false);
        _out = _stdout;
    }

    /// <summary>Writes text to the current output.</summary>
    public void Print(string text) => _out.Write(text);

    public bool Bail { get => _bail; set => _bail = value; }

    public bool Echo { get => _echo; set => _echo = value; }

    public Settings Config => _st;

    public int ExitCode => _exitCode;

    public bool Quit => _quit;

    public bool ReadOnly { get => _readOnly; set => _readOnly = value; }

    public string? Key { get => _key; set => _key = value; }

    public string DbPath { get => _dbPath; set => _dbPath = value; }

    public void Dispose()
    {
        CloseDb();
        if (!ReferenceEquals(_out, _stdout))
        {
            _out.Dispose();
        }

        _stdout.Flush();
        _stderr.Flush();
    }

    // ------------------------------------------------------------------ messages

    public void ErrLine(string text)
    {
        _stdout.Flush();
        _out.Flush();
        _stderr.Write(text + "\n");
        _stderr.Flush();
    }

    private void Fail() => _exitCode = 1;

    // ------------------------------------------------------------------ connection

    private void CloseDb()
    {
        _conn?.Dispose();
        _conn = null;
        _db?.Dispose();
        _db = null;
    }

    /// <summary>Opens the database file on first use, like the reference shell.</summary>
    private Connection? Conn()
    {
        if (_conn is not null)
        {
            return _conn;
        }

        try
        {
            var opts = new DatabaseOptions();
            _db = _readOnly ? Database.OpenReadOnly(_dbPath, opts) : Database.Open(_dbPath, opts);
            _conn = _db.Connect();
            if (_key is not null)
            {
                RunSilent("PRAGMA key = " + SqlText.QuoteString(_key));
            }
        }
        catch (OneLiteException e)
        {
            ErrLine($"Error: unable to open database \"{_dbPath}\": {e.Message}");
            CloseDb();
            Fail();
        }

        return _conn;
    }

    private void RunSilent(string sql)
    {
        using Statement st = _conn!.Prepare(sql);
        while (st.Step())
        {
        }
    }

    /// <summary>Runs a query on the connection and gives every row as strings (null for NULL).</summary>
    private List<string?[]> Query(string sql, params string[] args)
    {
        var rows = new List<string?[]>();
        Connection? c = Conn();
        if (c is null)
        {
            return rows;
        }

        using Statement st = c.Prepare(sql);
        for (int i = 0; i < args.Length; i++)
        {
            st.Bind(i + 1, args[i]);
        }

        while (st.Step())
        {
            var r = new string?[st.ColumnCount];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = st.GetString(i);
            }

            rows.Add(r);
        }

        return rows;
    }

    // ------------------------------------------------------------------ input

    public void RunReader(TextReader reader, bool interactive)
    {
        var buf = new StringBuilder();
        int lineNo = 0;
        int startLine = 1;
        _where = "near line";
        while (!_quit)
        {
            if (interactive)
            {
                _stdout.Write(buf.Length == 0 ? "onelite> " : "   ...> ");
                _stdout.Flush();
            }

            string? line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            lineNo++;
            if (buf.Length == 0)
            {
                string t = line.TrimStart();
                if (t.Length == 0)
                {
                    continue;
                }

                if (t[0] == '.')
                {
                    if (_echo)
                    {
                        _out.Write(line + "\n");
                    }

                    _where = "near line";
                    RunDot(t, lineNo);
                    if (_exitCode != 0 && _bail)
                    {
                        break;
                    }

                    continue;
                }

                startLine = lineNo;
            }

            buf.Append(line).Append('\n');
            if (SqlText.IsComplete(buf.ToString()))
            {
                _where = "near line";
                bool ok = ExecSql(buf.ToString(), startLine);
                buf.Clear();
                if (!ok && _bail)
                {
                    break;
                }
            }
        }

        if (!_quit && buf.ToString().AsSpan().Trim().Length > 0 && !(_exitCode != 0 && _bail))
        {
            _where = "near line";
            ExecSql(buf.ToString(), startLine);
        }
    }

    /// <summary>Runs one command-line argument: a dot-command or SQL. Returns false after an error (the shell stops there).</summary>
    public bool RunArgument(string text, int argIndex)
    {
        _argIndex = argIndex;
        _where = "in";
        string t = text.TrimStart();
        if (t.StartsWith('.'))
        {
            return RunDot(t, 0);
        }

        return ExecSql(text, 1);
    }

    private static string Ordinal(int n)
    {
        int m = n % 100;
        string suffix = m is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    private string Context(int line) => _where == "in" ? $"in {Ordinal(_argIndex)} command line argument" : $"near line {line}";

    // ------------------------------------------------------------------ executing SQL

    /// <summary>Runs every statement in <paramref name="sql"/>. Returns false after the first error.</summary>
    public bool ExecSql(string sql, int baseLine)
    {
        Connection? conn = Conn();
        if (conn is null)
        {
            return false;
        }

        if (_echo)
        {
            _out.Write(sql.TrimEnd('\n') + "\n");
        }

        int pos = 0;
        bool ok = true;
        while (true)
        {
            pos = SqlText.SkipBlank(sql, pos);
            if (pos >= sql.Length)
            {
                break;
            }

            int line = baseLine;
            for (int i = 0; i < pos; i++)
            {
                if (sql[i] == '\n')
                {
                    line++;
                }
            }

            string rest = pos == 0 ? sql : sql[pos..];
            Statement st;
            try
            {
                st = conn.Prepare(rest);
            }
            catch (OneLiteException e)
            {
                ReportParseError(e, rest, line);
                ok = false;
                break;
            }

            using (st)
            {
                if (!RunStatement(conn, st, rest, line))
                {
                    ok = false;
                    break;
                }

                string tail = st.Tail;
                pos = sql.Length - tail.Length;
            }
        }

        if (_onceActive)
        {
            EndOnce();
        }

        return ok;
    }

    private void ReportParseError(OneLiteException e, string sql, int line)
    {
        Fail();
        var sb = new StringBuilder();
        sb.Append("Parse error ").Append(Context(line)).Append(": ").Append(e.Message);
        int errorOffset = e.ErrorOffset;   // the position the reference shell shows is its sqlite3_error_offset
        if (errorOffset >= 0)
        {
            // The statement is shown on one line, cut to a window of 78 characters that keeps the error within 50 of the left edge;
            // the marker points up at the place when it is near the left edge and ends at it ("error here ---^") when it is not.
            byte[] bytes = Encoding.UTF8.GetBytes(sql);
            int off = Math.Min(errorOffset, bytes.Length);
            int charOff = Encoding.UTF8.GetString(bytes, 0, off).Length;
            string flat = FlattenLine(sql).TrimEnd();
            int start = Math.Max(0, charOff - 50);
            string window = flat[Math.Min(start, flat.Length)..];
            if (window.Length > 78)
            {
                window = window[..78];
            }

            int col = off - Encoding.UTF8.GetByteCount(flat.AsSpan(0, Math.Min(start, flat.Length)));
            sb.Append('\n').Append("  ").Append(window).Append('\n');
            if (col >= 25)
            {
                sb.Append("  ").Append(' ', col - 14).Append("error here ---^");
            }
            else
            {
                sb.Append("  ").Append(' ', col).Append("^--- error here");
            }
        }

        if (e.Suggestions.Count > 0)
        {
            sb.Append('\n').Append("  did you mean: ").Append(string.Join(", ", e.Suggestions)).Append('?');
        }

        ErrLine(sb.ToString());
    }

    private static string FlattenLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    private bool RunStatement(Connection conn, Statement st, string sql, int line)
    {
        var timerWatch = _timer ? Stopwatch.StartNew() : null;
        TimeSpan cpu0 = _timer ? Process.GetCurrentProcess().UserProcessorTime : default;
        TimeSpan sys0 = _timer ? Process.GetCurrentProcess().PrivilegedProcessorTime : default;
        if (_eqp != "off")
        {
            PrintPlan(conn, sql, st);
        }

        var names = new string[st.ColumnCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = st.ColumnName(i);
        }

        var renderer = new Renderer(_out, _st);
        renderer.Begin(names);
        try
        {
            while (st.Step())
            {
                var cells = new Cell[names.Length];
                for (int i = 0; i < cells.Length; i++)
                {
                    ColumnType t = st.ColumnType(i);
                    cells[i] = t == ColumnType.Null ? new Cell(null, t) : new Cell(st.GetTextUtf8(i).ToArray(), t);
                }

                renderer.Row(cells);
            }

            renderer.End();
        }
        catch (OneLiteException e)
        {
            try
            {
                renderer.End();
            }
            catch (OneLiteException)
            {
                // Output already failed; the statement's error is the one to report.
            }

            Fail();
            ErrLine($"Error {Context(line)}: {e.Message}" + (e.Suggestions.Count > 0 ? "\n  did you mean: " + string.Join(", ", e.Suggestions) + "?" : string.Empty));
            return false;
        }

        st.Reset();
        if (_changes && conn.ChangesCount != 0)
        {
            _out.Write($"changes: {conn.ChangesCount}   total_changes: {conn.TotalChangesCount}\n");
        }

        if (timerWatch is not null)
        {
            Process p = Process.GetCurrentProcess();
            double user = (p.UserProcessorTime - cpu0).TotalSeconds;
            double sys = (p.PrivilegedProcessorTime - sys0).TotalSeconds;
            _out.Write(string.Create(CultureInfo.InvariantCulture, $"Run Time: real {timerWatch.Elapsed.TotalSeconds:F6} user {user:F6} sys {sys:F6}\n"));
        }

        return true;
    }

    private void PrintPlan(Connection conn, string sql, Statement st)
    {
        if (st.IsReadOnly || sql.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase))
        {
            QueryPlan? plan = st.Plan;
            if (plan is not null)
            {
                _out.Write("QUERY PLAN\n");
                foreach (string l in plan.Render().TrimEnd('\n').Split('\n'))
                {
                    _out.Write("  " + l + "\n");
                }
            }
        }

        _ = conn;
    }

    // ------------------------------------------------------------------ output redirection

    private void SetOutput(string? file, bool once)
    {
        if (!ReferenceEquals(_out, _stdout))
        {
            _out.Dispose();
            _out = _stdout;
        }

        _outputName = "stdout";
        _onceActive = false;
        if (file is null || file == "stdout")
        {
            return;
        }

        try
        {
            _out = new ByteOut(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrLine($"Error: cannot write to \"{file}\"");
            Fail();
            return;
        }

        _outputName = file;
        _onceActive = once;
    }

    private void EndOnce()
    {
        _onceActive = false;
        if (!ReferenceEquals(_out, _stdout))
        {
            _out.Dispose();
            _out = _stdout;
        }

        _outputName = "stdout";
    }
}
