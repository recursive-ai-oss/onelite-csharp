using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

internal static class Program
{
    private const string Usage = """
        Usage: onelite [OPTIONS] [FILENAME [SQL...]]
        FILENAME is the name of a database file (SQLite file format). A new database is created
        if the file does not previously exist. Defaults to :memory:.
        OPTIONS include:
           --                   treat no subsequent arguments as options
           -ascii -box -column -csv -html -json -line -list -markdown -quote -table -tabs
                                set the output mode
           -bail                stop after hitting an error
           -batch               force batch I/O
           -cmd COMMAND         run "COMMAND" before reading stdin
           -echo                print inputs before execution
           -header / -noheader  turn headers on or off
           -help                show this message
           -init FILENAME       read and run FILENAME before the other input
           -interactive         force interactive I/O
           -key KEY             passphrase of an encrypted (SQLCipher-format) database
           -newline SEP         set output row separator. Default: '\n'
           -nullvalue TEXT      set text string for NULL values. Default ''
           -readonly            open the database read-only
           -separator SEP       set output column separator. Default: '|'
           -version             show the version
        """;

    private static int Main(string[] args) =>
        Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError(), !Console.IsInputRedirected);

    /// <summary>Runs the shell on the given streams (what <c>Main</c> does with the console); returns the exit code.</summary>
    internal static int Run(string[] args, Stream stdinStream, Stream stdoutStream, Stream stderrStream, bool inputIsTerminal)
    {
        using var shell = new Shell(stdoutStream, stderrStream);
        var commands = new List<(string Text, int Index)>();
        var pre = new List<(string Text, int Index, bool IsFile)>();
        string? file = null;
        bool interactive = false, batch = false, noMoreOptions = false;
        int index = 0;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                index = i + 1;
                string arg = args[i];
                if (noMoreOptions || arg.Length < 2 || arg[0] != '-')
                {
                    if (file is null)
                    {
                        file = arg;
                    }
                    else
                    {
                        commands.Add((arg, index));
                    }

                    continue;
                }

                string opt = arg.TrimStart('-').ToLowerInvariant();
                switch (opt)
                {
                    case "":
                        noMoreOptions = true;
                        break;
                    case "help":
                    case "h":
                    case "?":
                        shell.Print(Usage + "\n");
                        return 0;
                    case "version":
                        shell.Print($"{VersionText} (OneLite; SQLite-compatible, behaviour of SQLite 3.53.4)\n");
                        return 0;
                    case "bail":
                        shell.Bail = true;
                        break;
                    case "batch":
                        batch = true;
                        break;
                    case "interactive":
                        interactive = true;
                        break;
                    case "echo":
                        shell.Echo = true;
                        break;
                    case "readonly":
                        shell.ReadOnly = true;
                        break;
                    case "header":
                    case "headers":
                        shell.Config.Headers = true;
                        break;
                    case "noheader":
                    case "noheaders":
                        shell.Config.Headers = false;
                        break;
                    case "list":
                        shell.SetModeFromOption(OutputMode.List);
                        break;
                    case "csv":
                        shell.SetModeFromOption(OutputMode.Csv);
                        break;
                    case "tabs":
                        shell.SetModeFromOption(OutputMode.Tabs);
                        break;
                    case "ascii":
                        shell.SetModeFromOption(OutputMode.Ascii);
                        break;
                    case "line":
                        shell.SetModeFromOption(OutputMode.Line);
                        break;
                    case "json":
                        shell.SetModeFromOption(OutputMode.Json);
                        break;
                    case "column":
                        shell.SetModeFromOption(OutputMode.Column);
                        break;
                    case "box":
                        shell.SetModeFromOption(OutputMode.Box);
                        break;
                    case "table":
                        shell.SetModeFromOption(OutputMode.Table);
                        break;
                    case "markdown":
                        shell.SetModeFromOption(OutputMode.Markdown);
                        break;
                    case "html":
                        shell.SetModeFromOption(OutputMode.Html);
                        break;
                    case "quote":
                        shell.SetModeFromOption(OutputMode.Quote);
                        break;
                    case "separator" when i + 1 < args.Length:
                        shell.Config.ColSep = Unescape(args[++i]);
                        break;
                    case "newline" when i + 1 < args.Length:
                        shell.Config.RowSep = Unescape(args[++i]);
                        break;
                    case "nullvalue" when i + 1 < args.Length:
                        shell.Config.NullValue = args[++i];
                        break;
                    case "key" when i + 1 < args.Length:
                        shell.Key = args[++i];
                        break;
                    case "cmd" when i + 1 < args.Length:
                        pre.Add((args[i + 1], i + 2, false));
                        i++;
                        break;
                    case "init" when i + 1 < args.Length:
                        pre.Add((args[i + 1], i + 2, true));
                        i++;
                        break;
                    case "escape" when i + 1 < args.Length:
                        shell.Config.Escape = args[++i].ToLowerInvariant() switch { "symbol" => EscapeMode.Symbol, "off" => EscapeMode.Off, _ => EscapeMode.Ascii };
                        break;
                    case "noinit":
                    case "nofollow":
                    case "stats":
                    case "safe":
                    case "unsafe-testing":
                    case "ifexists":
                    case "no-rowid-in-view":
                        break;
                    default:
                        shell.ErrLine($"onelite: Error: unknown option: {arg}\nUse -help for a list of options.");
                        return 1;
                }
            }

            if (file is not null)
            {
                shell.DbPath = file;
            }

            foreach ((string text, int idx, bool isFile) in pre)
            {
                if (isFile)
                {
                    shell.RunDot(".read " + QuoteArg(text), 0);
                }
                else if (!shell.RunArgument(text, idx))
                {
                    return 1;
                }

                if (shell.Quit)
                {
                    return shell.ExitCode;
                }
            }

            if (commands.Count > 0)
            {
                foreach ((string text, int idx) in commands)
                {
                    if (!shell.RunArgument(text, idx) || shell.Quit)
                    {
                        break;
                    }
                }

                return shell.ExitCode;
            }

            bool tty = inputIsTerminal && !batch || interactive;
            if (tty)
            {
                shell.Print($"OneLite {VersionText}\nEnter \".help\" for usage hints.\n");
                if (shell.DbPath == ":memory:")
                {
                    shell.Print("Connected to a transient in-memory database.\nUse \".open FILENAME\" to reopen on a persistent database.\n");
                }
            }

            using var stdin = new StreamReader(stdinStream, new UTF8Encoding(false));
            shell.RunReader(stdin, tty);
            return shell.ExitCode;
        }
        catch (OneLiteException e)
        {
            shell.ErrLine("Error: " + e.Message);
            return 1;
        }
    }

    private static string VersionText
    {
        get
        {
            string v = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : "0";
            int plus = v.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? v : v[..plus];
        }
    }

    private static string QuoteArg(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Unescape(string s) => s.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal);
}
