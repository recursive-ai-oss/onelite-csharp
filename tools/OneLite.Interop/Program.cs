using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RecursiveAI.OneLite.Interop;
using RecursiveAI.OneLite.Oracle;

// onelite-interop
//   gen  [--out DIR] [--force] [--only SUBSTR]     build the generated corpus with the pinned sqlite3
//   list [--corpus DIR]                            manifest summary: files by category
//   run  [--corpus DIR] [--reader CMD] [--writer CMD] [--realworld DIR] [--json F] [--md F] [--filter S]
//                                                   check a reader and a writer against the corpus
//   render <db>                                    (the default reader) canonical rendering of a database, no header
//   write <db> <script>                            (the default writer) build a database from a SQL script
//   child-state ...                                (internal) the writer process the generator kills
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: interop gen | list | run | render <db> | write <db> <script>");
    return 2;
}
try
{
    switch (args[0])
    {
        case "gen": return Generator.Run(args[1..]);
        case "run": return Runner.Run(args[1..]);
        case "child-state": return StateChild.Run(args[1..]);
        case "render":
            Console.Out.Write(PinnedSqlite.RenderAll(args[1]));
            return 0;
        case "write":
            SqliteCli.ApplyScript(args[1], File.ReadAllText(args[2]));
            return 0;
        case "list":
        {
            string dir = args.Length >= 3 && args[1] == "--corpus" ? args[2] : Path.Combine(OraclePaths.CorpusRoot, "generated");
            Manifest m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;
            Console.WriteLine($"cases: {m.Cases.Count}; files: {m.Cases.Sum(c => c.Files.Count)}; bytes: {m.Cases.Sum(c => c.Files.Values.Sum(f => f.Size))}");
            var cats = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (CorpusCase c in m.Cases)
            {
                foreach (string cat in c.Categories)
                {
                    cats[cat] = cats.GetValueOrDefault(cat) + 1;
                }
            }
            foreach (KeyValuePair<string, int> kv in cats)
            {
                Console.WriteLine($"  {kv.Key}: {kv.Value}");
            }
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
