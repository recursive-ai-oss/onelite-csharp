namespace RecursiveAI.OneLite.QueryDiff;

/// <summary>
/// Switches for the OneLite side, read from the environment so every command honours them: ONELITE_QD_YIELD=N forces a suspend point at
/// every N-th page access, ONELITE_QD_ASYNC=1 steps with StepAsync instead of Step.
/// </summary>
internal static class Qd
{
    internal static readonly DatabaseOptions Options = new()
    {
        YieldEvery = int.TryParse(Environment.GetEnvironmentVariable("ONELITE_QD_YIELD"), out int n) ? n : 0,
    };

    internal static readonly bool Async = Environment.GetEnvironmentVariable("ONELITE_QD_ASYNC") == "1";

    internal static bool Step(Statement st) => Async ? st.StepAsync().AsTask().GetAwaiter().GetResult() : st.Step();
}
