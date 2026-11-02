using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>The pinned <c>sqlite3</c> shell, used only as a black-box peer process for lock interoperability. Found through <c>ONELITE_SQLITE3</c> or the default oracle folder; tests skip when it is absent.</summary>
internal static class SqliteOracle
{
    public static string? Path
    {
        get
        {
            string? p = Environment.GetEnvironmentVariable("ONELITE_SQLITE3");
            if (string.IsNullOrEmpty(p))
            {
                p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onelite", "oracles", "sqlite-3.53.4", "sqlite3");
            }

            return File.Exists(p) ? p : null;
        }
    }

    public static string Require()
    {
        string? p = Path;
        Assert.SkipUnless(p != null, "the pinned sqlite3 is not installed (set ONELITE_SQLITE3)");
        return p!;
    }

    /// <summary>Runs one batch of SQL in a fresh process and returns exit code and the combined output.</summary>
    public static (int Exit, string Output) Run(string db, string sql, int timeoutMs = 15000)
    {
        using Process p = Start(db, sql);
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (sb) { sb.AppendLine(e.Data); } } };
        p.BeginOutputReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(true);
            throw new TimeoutException("sqlite3 did not finish: " + sb);
        }

        p.WaitForExit();
        lock (sb)
        {
            return (p.ExitCode, sb.ToString());
        }
    }

    private static Process Start(string db, string? sql)
    {
        // stderr is folded into stdout by the shell so the order of messages is kept.
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = sql == null,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$0\" \"$@\" 2>&1");
        psi.ArgumentList.Add(Require());
        psi.ArgumentList.Add(db);
        if (sql != null)
        {
            psi.ArgumentList.Add(sql);
        }

        return Process.Start(psi)!;
    }

    public static bool IsBusy(string output) => output.Contains("database is locked", StringComparison.Ordinal);

    /// <summary>A long-lived <c>sqlite3</c> that executes the statements we feed it, so it can hold a lock between our checks.</summary>
    public sealed class Session : IDisposable
    {
        private const string Marker = "@@done@@";
        private readonly Process _p;
        private readonly BlockingCollection<string> _lines = [];

        public Session(string db)
        {
            _p = Start(db, null);
            _p.OutputDataReceived += (_, e) => { if (e.Data != null) { _lines.Add(e.Data); } };
            _p.BeginOutputReadLine();
            Run(".timeout 0");
        }

        /// <summary>Sends statements and waits for them to finish; returns what the shell printed.</summary>
        public string Run(string sql, int timeoutMs = 15000)
        {
            Send(sql);
            return Wait(timeoutMs);
        }

        /// <summary>Sends statements without waiting (for a statement that blocks on purpose).</summary>
        public void Send(string sql)
        {
            _p.StandardInput.WriteLine(sql);
            _p.StandardInput.WriteLine(".print " + Marker);
            _p.StandardInput.Flush();
        }

        public string Wait(int timeoutMs = 15000)
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (!_lines.TryTake(out string? line, timeoutMs))
                {
                    throw new TimeoutException("sqlite3 session did not answer: " + sb);
                }

                if (line == Marker)
                {
                    return sb.ToString();
                }

                sb.AppendLine(line);
            }
        }

        public void Dispose()
        {
            try
            {
                _p.StandardInput.WriteLine(".quit");
                _p.StandardInput.Close();
                if (!_p.WaitForExit(5000))
                {
                    _p.Kill(true);
                }
            }
            catch (IOException)
            {
            }

            _p.Dispose();
            _lines.Dispose();
        }
    }
}

/// <summary>Collects the outcome table the lock-interop tests print; written to <c>ONELITE_LOCK_TABLE</c> when set.</summary>
internal static class OutcomeTable
{
    private static readonly List<string> Rows = [];

    public static void Add(string row)
    {
        lock (Rows)
        {
            Rows.Add(row);
            string? file = Environment.GetEnvironmentVariable("ONELITE_LOCK_TABLE");
            if (!string.IsNullOrEmpty(file))
            {
                File.AppendAllText(file, row + "\n");
            }
        }
    }
}
