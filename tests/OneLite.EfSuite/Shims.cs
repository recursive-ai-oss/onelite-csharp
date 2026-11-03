// Glue between EF Core's SQLite test sources and the released packages. Nothing here is copied from the library.

using System.Runtime.CompilerServices;
using RecursiveAI.OneLite.SQLitePCLRaw;

namespace SQLitePCL
{
    /// <summary>The suite starts its provider with <c>Batteries_V2.Init()</c>; here that selects OneLite, which is the whole point of the run.</summary>
    public static class Batteries_V2
    {
        public static void Init() => OneLiteBatteries.Init();
    }
}

namespace Microsoft.EntityFrameworkCore
{
    internal static class OneLiteModuleInit
    {
        [ModuleInitializer]
        internal static void Run() => OneLiteBatteries.Init();
    }
}
