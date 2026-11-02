using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace RecursiveAI.OneLite.Bench;

/// <summary>Micro-benchmarks. The engine and app comparison rows against native SQLite land here as the engine does.</summary>
internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>A stand-in so the harness has something to run.</summary>
[MemoryDiagnoser]
public class PlaceholderBenchmarks
{
    private readonly string name = LibraryInfo.Name;

    /// <summary>Reads a field.</summary>
    [Benchmark]
    public int NameLength() => name.Length;
}
