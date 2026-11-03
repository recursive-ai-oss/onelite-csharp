using SQLitePCL;

namespace RecursiveAI.OneLite.SQLitePCLRaw;

/// <summary>
/// The one call that points SQLitePCLRaw, and so <c>Microsoft.Data.Sqlite</c> and Entity Framework Core, at OneLite.
/// </summary>
public static class OneLiteBatteries
{
    private static readonly OneLiteSqlite3Provider s_provider = new();

    /// <summary>
    /// Installs <see cref="OneLiteSqlite3Provider"/> as SQLitePCLRaw's provider. Call it once, before the first connection is opened, in
    /// place of <c>SQLitePCL.Batteries.Init()</c> or <c>Batteries_V2.Init()</c>. Calling it again is harmless: the same provider is installed.
    /// </summary>
    public static void Init() => raw.SetProvider(s_provider);

    /// <summary>
    /// Whether a connection opened through the adapter starts with <c>PRAGMA foreign_keys=ON</c>. Default true, which is what the native
    /// library bundled with SQLitePCLRaw does (it is built with <c>SQLITE_DEFAULT_FOREIGN_KEYS=1</c>) and what <c>Microsoft.Data.Sqlite</c> and
    /// EF Core assume. Set it to false before opening connections to get SQLite's own default (off). An explicit <c>PRAGMA foreign_keys</c> or
    /// the <c>Foreign Keys</c> connection-string key still wins.
    /// </summary>
    public static bool ForeignKeysByDefault { get; set; } = true;
}
