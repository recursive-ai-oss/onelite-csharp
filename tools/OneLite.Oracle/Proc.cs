using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RecursiveAI.OneLite.Oracle;

/// <summary>Runs child processes, with captured output. Used for the <c>sqlite3</c> shell and for the tools' own writer processes.</summary>
public static class Proc
{
    /// <summary>The outcome of a finished process.</summary>
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>Runs <paramref name="exe"/> to completion with <paramref name="stdin"/> on its standard input.</summary>
    public static Result Run(string exe, IEnumerable<string> args, string? stdin = null, string? workingDir = null,
        IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        using Process p = Start(exe, args, redirectIn: true, workingDir, env);
        Task<string> outT = p.StandardOutput.ReadToEndAsync();
        Task<string> errT = p.StandardError.ReadToEndAsync();
        if (!string.IsNullOrEmpty(stdin))
        {
            p.StandardInput.Write(stdin);
        }
        p.StandardInput.Close();
        if (!p.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
        {
            p.Kill();   // only the process this call started
            p.WaitForExit();
            throw new TimeoutException("process timed out: " + exe);
        }
        return new Result(p.ExitCode, outT.GetAwaiter().GetResult(), errT.GetAwaiter().GetResult());
    }

    /// <summary>Starts a process the caller will manage (for example to kill it). Standard output and error are redirected.</summary>
    public static Process Start(string exe, IEnumerable<string> args, bool redirectIn = false, string? workingDir = null,
        IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectIn,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (redirectIn)
        {
            psi.StandardInputEncoding = new UTF8Encoding(false);
        }
        if (workingDir != null)
        {
            psi.WorkingDirectory = workingDir;
        }
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        if (env != null)
        {
            foreach (KeyValuePair<string, string> kv in env)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
    }

    /// <summary>
    /// The command that re-runs the current tool with <paramref name="args"/>: the apphost when there is one, otherwise
    /// <c>dotnet &lt;assembly&gt;</c>. Lets a tool spawn copies of itself as writer processes.
    /// </summary>
    public static (string Exe, List<string> Args) Self(IEnumerable<string> args)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var list = new List<string>();
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
        {
            list.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        }
        list.AddRange(args);
        return (exe, list);
    }
}

/// <summary>The <c>sqlite3</c> command-line shell of the pinned release (cli).</summary>
public static class SqliteCli
{
    /// <summary>
    /// Runs the shell on a database with commands on standard input. <c>-init</c> points at an empty file so a
    /// user's <c>~/.sqliterc</c> cannot change the output; <c>-batch</c> keeps it free of prompts.
    /// </summary>
    public static Proc.Result Run(string? dbPath, string input, params string[] extraArgs)
    {
        string empty = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var args = new List<string> { "-batch", "-init", empty };
        args.AddRange(extraArgs);
        if (dbPath != null)
        {
            args.Add(dbPath);
        }
        return Proc.Run(OraclePaths.Cli, args, input);
    }

    /// <summary>
    /// <c>.dump</c> of a database (cli: "Convert the database into a single UTF-8 text file"). Opening a database with a hot
    /// journal or a WAL recovers it, so callers dump a copy.
    /// </summary>
    public static string Dump(string dbPath)
    {
        Proc.Result r = Run(dbPath, ".dump\n");
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException("sqlite3 .dump failed (" + r.ExitCode + "): " + r.Stderr);
        }
        return r.Stdout;
    }

    /// <summary>Runs one query in the shell and returns its rows as CSV fields (for values that contain no quotes or newlines).</summary>
    public static List<string[]> QueryCsv(string dbPath, string sql)
    {
        Proc.Result r = Run(dbPath, sql + "\n", "-csv");
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException("sqlite3 query failed (" + r.ExitCode + "): " + r.Stderr);
        }
        return [.. r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(','))];
    }

    /// <summary>Applies a SQL script to a database file with the shell (creating it).</summary>
    public static void ApplyScript(string dbPath, string sql)
    {
        Proc.Result r = Run(dbPath, sql, "-bail");
        if (r.ExitCode != 0 || r.Stderr.Length > 0)
        {
            throw new InvalidOperationException("sqlite3 script failed (" + r.ExitCode + "): " + r.Stderr);
        }
    }
}
