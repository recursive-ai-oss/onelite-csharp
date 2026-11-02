using System;

namespace RecursiveAI.OneLite.ForensicsTool;

/// <summary>
/// Checks for the forensic reader, run against the pinned shell:
///   nonmod [--ro] [--pass-only] PATH...   a full pass over database files; proves nothing changed
///   calibrate [--work DIR] [--seed N] [--json FILE]   carving recall and precision against known deletions
///   walsnap [--work DIR] [--seed N]       log snapshots against a copy of the files after every commit
///   coexist [--work DIR] [--seconds N]    reads beside a live shell holding an exclusive lock, and beside a committing writer
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: nonmod | calibrate | walsnap | coexist");
            return 2;
        }

        return args[0] switch
        {
            "nonmod" => NonMod.Run(args[1..]),
            "calibrate" => Calibrate.Run(args[1..]),
            "walsnap" => WalSnap.Run(args[1..]),
            "coexist" => Coexist.Run(args[1..]),
            "info" => Info.Run(args[1..]),
            _ => 2,
        };
    }
}
