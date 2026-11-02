using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace RecursiveAI.OneLite.Storage.Vfs.Browser;

/// <summary>
/// The JavaScript half of the browser file systems (<c>onelite-opfs.js</c>, shipped as a static web asset of the package) imported through the
/// runtime's <c>[JSImport]</c>. Nothing here is reachable outside a browser.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class OpfsJs
{
    private const string Module = "onelite-opfs";

    private static Task? s_load;

    /// <summary>Loads the module once. <paramref name="moduleUrl"/> is resolved the way <c>JSHost.ImportAsync</c> resolves it, relative to the runtime's <c>dotnet.js</c>.</summary>
    internal static Task EnsureLoadedAsync(string? moduleUrl) => s_load ??= JSHost.ImportAsync(Module, moduleUrl ?? DefaultUrl);

    /// <summary>Where the package's static web asset lives relative to <c>_framework/dotnet.js</c>.</summary>
    internal const string DefaultUrl = "../_content/RecursiveAI.OneLite/onelite-opfs.js";

    [JSImport("capabilitiesText", Module)]
    internal static partial string CapabilitiesText();

    [JSImport("exists", Module)]
    internal static partial Task<bool> Exists(string path);

    [JSImport("existsNonEmpty", Module)]
    internal static partial Task<bool> ExistsNonEmpty(string path);

    [JSImport("removeFile", Module)]
    internal static partial Task RemoveFile(string path);

    [JSImport("listFiles", Module)]
    internal static partial Task<string> ListFiles(string path);

    [JSImport("wipe", Module)]
    internal static partial Task Wipe();

    // ---- main-thread style files -----------------------------------------------------------------------------------------------------------

    [JSImport("aOpen", Module)]
    internal static partial Task<int> AOpen(string path, bool create, bool exclusive);

    [JSImport("aRead", Module)]
    internal static partial Task<int> ARead(int id, [JSMarshalAs<JSType.Number>] long offset, int length);

    [JSImport("aTake", Module)]
    internal static partial void ATake(int id, [JSMarshalAs<JSType.MemoryView>] Span<byte> dst);

    [JSImport("aWrite", Module)]
    internal static partial Task AWrite(int id, [JSMarshalAs<JSType.Number>] long offset, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> data);

    [JSImport("aTruncate", Module)]
    internal static partial Task ATruncate(int id, [JSMarshalAs<JSType.Number>] long size);

    [JSImport("aSize", Module)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    internal static partial Task<long> ASize(int id);

    [JSImport("aFlush", Module)]
    internal static partial Task AFlush(int id);

    [JSImport("aClose", Module)]
    internal static partial Task AClose(int id);

    // ---- worker style files ----------------------------------------------------------------------------------------------------------------

    [JSImport("sOpen", Module)]
    internal static partial Task<int> SOpen(string path, bool create);

    [JSImport("sRead", Module)]
    internal static partial int SRead(int id, [JSMarshalAs<JSType.Number>] long offset, [JSMarshalAs<JSType.MemoryView>] Span<byte> dst);

    [JSImport("sWrite", Module)]
    internal static partial void SWrite(int id, [JSMarshalAs<JSType.Number>] long offset, [JSMarshalAs<JSType.MemoryView>] Span<byte> src);

    [JSImport("sTruncate", Module)]
    internal static partial void STruncate(int id, [JSMarshalAs<JSType.Number>] long size);

    [JSImport("sSize", Module)]
    [return: JSMarshalAs<JSType.Number>]
    internal static partial long SSize(int id);

    [JSImport("sFlush", Module)]
    internal static partial void SFlush(int id);

    [JSImport("sClose", Module)]
    internal static partial void SClose(int id);

    // ---- Web Locks -------------------------------------------------------------------------------------------------------------------------

    [JSImport("lockTry", Module)]
    internal static partial Task<int> LockTry(string name, bool exclusive);

    [JSImport("lockRelease", Module)]
    internal static partial Task LockRelease(int id);

    // ---- errors ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Maps a failure thrown by the JavaScript module (<c>OL:&lt;code&gt;:&lt;text&gt;</c>) to the SQLite extended result code of the operation that failed.</summary>
    /// <param name="e">The exception from the JavaScript call.</param>
    /// <param name="ioCode">The code for a generic failure of this operation (<c>IOERR_READ</c> and so on).</param>
    /// <param name="notFoundCode">The code when the file does not exist.</param>
    internal static VfsException Map(Exception e, int ioCode, int notFoundCode)
    {
        if (e is VfsException v)
        {
            return v;
        }

        // JavaScript's Error.toString puts "Error: " in front of the message, so the marker may not be at the start.
        string text = e.Message;
        int code = ioCode;
        int mark = text.IndexOf("OL:", StringComparison.Ordinal);
        if (mark >= 0)
        {
            int colon = text.IndexOf(':', mark + 3);
            if (colon > mark + 3 && int.TryParse(text.AsSpan(mark + 3, colon - mark - 3), out int k))
            {
                text = text[(colon + 1)..];
                code = k switch
                {
                    1 => notFoundCode,
                    2 => VfsCodes.CantOpen,
                    3 => VfsCodes.Busy,
                    5 => VfsCodes.Full,
                    6 => VfsCodes.CantOpenIsDir,
                    _ => ioCode,
                };
            }
        }

        return new VfsException(code, text, e);
    }
}

/// <summary>Loads the browser storage module. Call once before creating a browser VFS.</summary>
[SupportedOSPlatform("browser")]
public static class BrowserStorage
{
    /// <summary>
    /// Loads <c>onelite-opfs.js</c>. The default location is the static web asset of this package, <c>_content/RecursiveAI.OneLite/onelite-opfs.js</c>,
    /// reached from <c>_framework/dotnet.js</c>; pass <paramref name="moduleUrl"/> when the asset is served from somewhere else.
    /// </summary>
    /// <param name="moduleUrl">Where the module is, as <c>JSHost.ImportAsync</c> reads it; null for the default.</param>
    /// <returns>A task that completes when the module is loaded.</returns>
    public static Task LoadAsync(string? moduleUrl = null) => OpfsJs.EnsureLoadedAsync(moduleUrl);

    /// <summary>What this context offers, as <c>name=value</c> words: <c>opfs</c>, <c>syncAccessHandle</c>, <c>webLocks</c>, <c>worker</c>, <c>crossOriginIsolated</c>. Needs <see cref="LoadAsync"/> first.</summary>
    /// <returns>The words, separated by spaces.</returns>
    public static string Capabilities() => OpfsJs.CapabilitiesText();

    /// <summary>Removes every file in the origin's private file system. For tests; there is no undo.</summary>
    /// <returns>A task that completes when the file system is empty.</returns>
    public static Task WipeAsync() => OpfsJs.Wipe();

    /// <summary>Lists the files in an OPFS directory as <c>name:size</c> lines. For tests and tools.</summary>
    /// <param name="directory">The directory path; empty for the root.</param>
    /// <returns>The lines, sorted, separated by newlines.</returns>
    public static Task<string> ListAsync(string directory = "") => OpfsJs.ListFiles(directory);
}
