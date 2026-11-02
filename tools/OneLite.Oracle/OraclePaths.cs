using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RecursiveAI.OneLite.Oracle;

/// <summary>Where the pinned oracle binaries live. The fetch scripts in tools/scripts install exactly this layout.</summary>
public static class OraclePaths
{
    /// <summary>The SQLite release every oracle run is checked against.</summary>
    public const string PinnedVersion = "3.53.4";

    /// <summary>Install root: <c>$ONELITE_HOME</c> or <c>~/onelite</c>.</summary>
    public static string Root
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("ONELITE_HOME");
            return !string.IsNullOrEmpty(env) ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onelite");
        }
    }

    /// <summary>The <c>sqlite3</c> command-line shell (override with <c>$ONELITE_SQLITE3</c>).</summary>
    public static string Cli
        => Environment.GetEnvironmentVariable("ONELITE_SQLITE3") is { Length: > 0 } e
            ? e : Path.Combine(Root, "oracles", "sqlite-" + PinnedVersion, "sqlite3");

    /// <summary>The native library of the same release (override with <c>$ONELITE_NATIVE_SQLITE</c>).</summary>
    public static string NativeLibrary
        => Environment.GetEnvironmentVariable("ONELITE_NATIVE_SQLITE") is { Length: > 0 } e
            ? e : Path.Combine(Root, "oracles", "native-" + PinnedVersion,
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libe_sqlite3.dylib" : "libe_sqlite3.so");

    /// <summary>Where generated and fetched corpora live.</summary>
    public static string CorpusRoot => Path.Combine(Root, "corpus");
}
