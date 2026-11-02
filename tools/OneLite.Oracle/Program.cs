using System;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;

// onelite-oracle: drives the pinned sqlite3 (shell and native library).
//   info                       print the oracle header (versions, compile options)
//   dump   <db>                .dump of a database (of a copy: opening recovers journals and WAL)
//   check  <db>                PRAGMA integrity_check (on a copy)
//   render <db>                canonical rendering of the whole database (pragmas, schema, every table)
//   run    <db|:memory:> [-e SQL | -f FILE]   run a script and print the canonical result of every statement (stdin if neither)
//
// Every command starts with the header lines ("# ...") so a saved output says which oracle produced it.
return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        Console.Error.WriteLine("usage: oracle info | dump <db> | check <db> | render <db> | run <db> [-e SQL | -f FILE]");
        return 2;
    }
    try
    {
        Console.Out.Write(PinnedSqlite.Header());
        switch (args[0])
        {
            case "info":
                return 0;
            case "dump":
                return WithCopy(args[1], c => { Console.Out.Write(SqliteCli.Dump(c)); return 0; });
            case "check":
                return WithCopy(args[1], c =>
                {
                    bool ok = PinnedSqlite.IntegrityOk(c, out string detail);
                    Console.Out.WriteLine(detail);
                    return ok ? 0 : 1;
                });
            case "render":
                return WithCopy(args[1], c => { Console.Out.Write(PinnedSqlite.RenderAll(c)); return 0; });
            case "run":
            {
                string sql = args.Length >= 4 && args[2] == "-e" ? args[3]
                    : args.Length >= 4 && args[2] == "-f" ? File.ReadAllText(args[3])
                    : Console.In.ReadToEnd();
                using SqliteDb db = SqliteDb.Open(args[1]);
                var sb = new StringBuilder();
                db.RenderScript(sql, sb);
                Console.Out.Write(sb.ToString());
                return 0;
            }
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }
    catch (Exception e) when (e is SqliteException or InvalidOperationException or IOException)
    {
        Console.Error.WriteLine("error: " + e.Message);
        return 1;
    }
}

static int WithCopy(string db, Func<string, int> action)
{
    string dir = Path.Combine(Path.GetTempPath(), "onelite-oracle-" + Environment.ProcessId);
    string copy = Path.Combine(dir, "copy.db");
    try
    {
        PinnedSqlite.CopyFileSet(db, copy);
        return action(copy);
    }
    finally
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
