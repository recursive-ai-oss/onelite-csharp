using System;
using System.IO;
using System.Text;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.SqlRun;

// Runs a SQL script (stdin or a file) against a database file and prints rows the way the sqlite3 shell's list mode does, so that
// the output can be compared with the shell's. usage: SqlRun <database> [--page-size N] [--utf16le|--utf16be] [--auto-vacuum] [--journal wal|delete|...] [--sync normal|full|off] [--async] [--yield N] [--script file]
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: SqlRun <database> [options] < script.sql");
            return 2;
        }

        string path = args[0];
        int pageSize = 4096;
        TextEncoding enc = TextEncoding.Utf8;
        bool av = false;
        bool incr = false;
        JournalMode jm = JournalMode.Delete;
        SynchronousMode sync = SynchronousMode.Full;
        bool useAsync = false;
        bool fk = false;
        bool header = false;
        int yieldEvery = 0;
        string? script = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--page-size": pageSize = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--utf16le": enc = TextEncoding.Utf16Le; break;
                case "--utf16be": enc = TextEncoding.Utf16Be; break;
                case "--auto-vacuum": av = true; break;
                case "--incremental": av = true; incr = true; break;
                case "--journal": jm = Enum.Parse<JournalMode>(args[++i], true); break;
                case "--sync": sync = Enum.Parse<SynchronousMode>(args[++i], true); break;
                case "--async": useAsync = true; break;
                case "--fk": fk = true; break;
                case "--header": header = true; break;
                case "--yield": yieldEvery = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--script": script = File.ReadAllText(args[++i]); break;
                default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
            }
        }

        script ??= Console.In.ReadToEnd();
        var opts = new DatabaseOptions { PageSize = pageSize, Encoding = enc, AutoVacuum = av, IncrementalVacuum = incr, JournalMode = jm, Synchronous = sync, ForeignKeys = fk, YieldEvery = yieldEvery };
        using Database db = Database.Open(path, opts);
        using Connection conn = db.Connect();
        var o = new StringBuilder();
        string rest = script;
        int rc = 0;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            Statement st;
            try
            {
                st = conn.Prepare(rest);
            }
            catch (OneLiteException e)
            {
                o.Append("Error: ").Append(e.Message).Append(" [").Append(e.ExtendedCode).Append("] ").Append(e.Detail).Append((char)10);
                rc = 1;

                // A statement that does not compile has no tail; the script goes on after its first semicolon (as the shell does).
                int semi = rest.IndexOf(';', StringComparison.Ordinal);
                rest = semi < 0 ? string.Empty : rest[(semi + 1)..];
                continue;
            }

            rest = st.Tail;
            try
            {
                if (useAsync)
                {
                    bool first = true;
                    while (st.StepAsync().AsTask().GetAwaiter().GetResult())
                    {
                        if (header && first)
                        {
                            Header(st, o);
                        }

                        first = false;
                        Row(st, o);
                    }
                }
                else
                {
                    bool first = true;
                    while (st.Step())
                    {
                        if (header && first)
                        {
                            Header(st, o);
                        }

                        first = false;
                        Row(st, o);
                    }
                }
            }
            catch (OneLiteException e)
            {
                o.Append("Error: ").Append(e.Message).Append(" [").Append(e.ExtendedCode).Append("] ").Append(e.Detail).Append((char)10);
                rc = 1;
            }

            st.Dispose();
        }

        Console.Out.Write(o.ToString());
        return rc;
    }

    private static void Header(Statement st, StringBuilder o)
    {
        for (int i = 0; i < st.ColumnCount; i++)
        {
            o.Append(i > 0 ? "|" : string.Empty).Append(st.ColumnName(i));
        }

        o.Append('\n');
    }

    private static void Row(Statement st, StringBuilder o)
    {
        for (int i = 0; i < st.ColumnCount; i++)
        {
            if (i > 0)
            {
                o.Append('|');
            }

            if (!st.IsNull(i))
            {
                o.Append(st.GetString(i));
            }
        }

        o.Append('\n');
    }
}
