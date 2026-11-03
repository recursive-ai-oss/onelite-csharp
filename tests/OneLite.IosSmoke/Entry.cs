namespace RecursiveAI.OneLite.IosSmoke;

/// <summary>Runs the smoke checks once and ends the process, printing a line the launching script looks for.</summary>
internal static class Entry
{
    private static int Main()
    {
        int rc = RecursiveAI.OneLite.AotSmoke.Program.Main();
        Console.WriteLine(rc == 0 ? "ONELITE-IOS-SMOKE: OK" : "ONELITE-IOS-SMOKE: FAILED");
        return rc;
    }
}
