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
}
