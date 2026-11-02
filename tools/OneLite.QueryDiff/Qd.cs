using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.QueryDiff;

/// <summary>
/// Switches for the OneLite side, read from the environment so every command honours them: ONELITE_QD_YIELD=N forces a suspend point at
/// every N-th page access, ONELITE_QD_ASYNC=1 steps with StepAsync instead of Step. ONELITE_QD_ASYNC=cold goes further (the differential tool only):
/// the database is opened through a backend with only asynchronous calls whose every call suspends (<see cref="ColdAsyncVfs"/>), with a six-page cache so
/// nearly every read misses, a forced suspend point every third page access unless ONELITE_QD_YIELD says otherwise, and every statement is
/// prepared with PrepareAsync, stepped with StepAsync and closed with DisposeAsync. A statement that needs a synchronous call (an integrity check,
/// VACUUM, a journal mode change, a checkpoint, a full-text 3/4 table) is refused with MISUSE by design; the tool reruns that one on the sync driver and counts it.
/// </summary>
internal static class Qd
{
    private static readonly string s_mode = Environment.GetEnvironmentVariable("ONELITE_QD_ASYNC") ?? string.Empty;

    private static readonly int s_yield = int.TryParse(Environment.GetEnvironmentVariable("ONELITE_QD_YIELD"), out int n) ? n : 0;

    internal static readonly bool Async = s_mode is "1" or "cold";

    internal static readonly bool Cold = s_mode == "cold";

    internal static readonly ColdAsyncVfs ColdVfs = new();

    internal static readonly DatabaseOptions Options = Cold
        ? new DatabaseOptions { Vfs = ColdVfs, CacheSize = 6, YieldEvery = s_yield > 0 ? s_yield : 3 }
        : new DatabaseOptions { YieldEvery = s_yield };

    private static long s_fallbacks;

    /// <summary>Statements the cold mode reran on the sync driver because they need a synchronous backend.</summary>
    internal static long Fallbacks => Interlocked.Read(ref s_fallbacks);

    internal static void CountFallback() => Interlocked.Increment(ref s_fallbacks);

    internal static bool Step(Statement st) => Async ? st.StepAsync().AsTask().GetAwaiter().GetResult() : st.Step();

    internal static Statement Prepare(Connection conn, string sql, bool cold) => cold ? conn.PrepareAsync(sql).AsTask().GetAwaiter().GetResult() : conn.Prepare(sql);

    internal static void Close(Statement st, bool cold)
    {
        if (cold)
        {
            st.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else
        {
            st.Dispose();
        }
    }

    /// <summary>True for the error a synchronous-only operation raises on a backend with only asynchronous calls.</summary>
    internal static bool NeedsBlocking(OneLiteException e) => e.Code == ResultCode.Misuse && e.Detail.Contains("requires the async driver", StringComparison.Ordinal);
}
