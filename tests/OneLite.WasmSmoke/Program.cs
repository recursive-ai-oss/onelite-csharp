using System;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Storage.Crypto;

namespace RecursiveAI.OneLite.WasmSmoke;

/// <summary>Runs the core library on the browser-wasm runtime. A headless-browser run will drive it once the browser VFS exists.</summary>
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine(LibraryInfo.Name);
        // Encryption is unsupported in the browser build: the codec says so instead of failing inside the platform.
        Console.WriteLine("encryption supported: " + PageCodec.IsSupported);
    }
}
