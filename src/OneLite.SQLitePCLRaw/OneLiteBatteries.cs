using SQLitePCL;

namespace RecursiveAI.OneLite.SQLitePCLRaw;

/// <summary>
/// The one call that points SQLitePCLRaw, and so <c>Microsoft.Data.Sqlite</c> and Entity Framework Core, at OneLite.
/// </summary>
public static class OneLiteBatteries
{
    private static readonly object s_gate = new();
    private static bool s_installed;

    /// <summary>
    /// Installs <see cref="OneLiteSqlite3Provider"/> as SQLitePCLRaw's provider. Call it once, before the first connection is opened, in
    /// place of <c>SQLitePCL.Batteries.Init()</c> or <c>Batteries_V2.Init()</c>. Calling it again is harmless.
    /// </summary>
    public static void Init()
    {
        lock (s_gate)
        {
            if (s_installed)
            {
                return;
            }

            raw.SetProvider(new OneLiteSqlite3Provider());
            s_installed = true;
        }
    }
}
