using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs.Browser;

// This program runs only in a browser, like the file systems it tests.
[assembly: SupportedOSPlatform("browser")]

namespace RecursiveAI.OneLite.BrowserTests;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("OneLite browser tests");
    }
}

/// <summary>What the page and worker scripts call. Each call returns a short text result; a failure comes back as text starting with FAIL.</summary>
[SupportedOSPlatform("browser")]
internal static partial class Exports
{
    private static bool s_registered;

    private static void Register()
    {
        if (s_registered)
        {
            return;
        }

        s_registered = true;
        VfsTests.Register();
        DbTests.Register();
        TransferTests.Register();
    }

    [JSExport]
    internal static async Task<string> Init(string moduleUrl)
    {
        await BrowserStorage.LoadAsync(string.IsNullOrEmpty(moduleUrl) ? null : moduleUrl).ConfigureAwait(false);
        Register();
        return BrowserStorage.Capabilities();
    }

    /// <summary>The names of the tests that apply in this context, one per line.</summary>
    [JSExport]
    internal static string List(string context)
    {
        var sb = new StringBuilder();
        foreach (TestCase t in Registry.All)
        {
            if (t.Context == "any" || t.Context == context)
            {
                sb.Append(t.Name).Append('\n');
            }
        }

        return sb.ToString();
    }

    [JSExport]
    internal static async Task<string> Run(string name)
    {
        foreach (TestCase t in Registry.All)
        {
            if (t.Name == name)
            {
                try
                {
                    await t.Body().ConfigureAwait(false);
                    return "ok";
                }
                catch (Exception e)
                {
                    return "FAIL " + e.GetType().Name + ": " + e.Message;
                }
            }
        }

        return "FAIL no such test";
    }
}
