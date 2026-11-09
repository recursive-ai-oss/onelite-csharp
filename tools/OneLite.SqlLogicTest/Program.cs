using System;
using System.Globalization;
using System.IO;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.SqlLogicTest;

// sqllogictest runner
//   run    --corpus DIR --out DIR [--engine onelite-rw|sqlite|onelite] [--shard i/n] [--filter GLOB[,GLOB]] [--max-minutes N] [--force] [--scratch DIR]
//   report --out DIR [--corpus DIR] [--known FILE]
//   check-printf [COUNT]   compare the %.3f renderer with the pinned library's printf
// See docs/sqllogictest.md.
if (args.Length >= 1 && args[0] == "check-printf")
{
    return PrintfCheck.Run(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 100000, 1, args.Length > 2 ? args[2] : null) == 0 ? 0 : 1;
}
if (args.Length == 0 || args[0] is not ("run" or "report"))
{
    Console.Error.WriteLine("usage: sqllogictest run --corpus DIR --out DIR [options] | report --out DIR [--corpus DIR] [--known FILE]");
    return 2;
}

string? corpus = null, outDir = null, scratch = null, known = null, engineName = "onelite-rw";
int shard = 1, shards = 1;
double maxMinutes = 0;
bool force = false;
var filters = new System.Collections.Generic.List<string>();
try
{
    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--corpus": corpus = args[++i]; break;
            case "--out": outDir = args[++i]; break;
            case "--scratch": scratch = args[++i]; break;
            case "--known": known = args[++i]; break;
            case "--engine": engineName = args[++i]; break;
            case "--force": force = true; break;
            case "--max-minutes": maxMinutes = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--filter": filters.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
            case "--shard":
            {
                string[] p = args[++i].Split('/');
                shard = int.Parse(p[0], CultureInfo.InvariantCulture);
                shards = int.Parse(p[1], CultureInfo.InvariantCulture);
                if (shards < 1 || shard < 1 || shard > shards)
                {
                    throw new ArgumentException("--shard i/n needs 1 <= i <= n");
                }
                break;
            }
            default: throw new ArgumentException("unknown option " + args[i]);
        }
    }
    if (outDir is null)
    {
        throw new ArgumentException("--out is required");
    }
    if (args[0] == "report")
    {
        return Sweep.Report(outDir, corpus, known);
    }
    corpus ??= Path.Combine(OraclePaths.CorpusRoot, "sqllogictest");
    var opts = new Sweep.Options
    {
        Corpus = corpus,
        Out = outDir,
        Scratch = scratch ?? Path.Combine(outDir, "scratch"),
        Engine = engineName switch
        {
            "sqlite" => new SqliteNativeEngine(),
            "onelite" => new OneLiteEngine(),
            "onelite-rw" => new OneLiteWriteEngine(),
            _ => throw new ArgumentException("--engine must be onelite-rw, sqlite or onelite"),
        },
        Shard = shard,
        Shards = shards,
        MaxMinutes = maxMinutes,
        Force = force,
    };
    opts.Filters.AddRange(filters);
    return Sweep.Run(opts);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
