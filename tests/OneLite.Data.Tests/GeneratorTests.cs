using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RecursiveAI.OneLite.Generators;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>The mapping generator's diagnostics name the type and the member, and its output compiles.</summary>
public sealed class GeneratorTests
{
    private static (ImmutableArray<Diagnostic> Diagnostics, Compilation Output) Run(string source)
    {
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = trusted.Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "System.Collections.dll" or "netstandard.dll" or "System.Linq.dll" or "System.Threading.Tasks.dll")
            .Select(p => MetadataReference.CreateFromFile(p)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(OneLiteRowAttribute).Assembly.Location));
        CSharpCompilation comp = CSharpCompilation.Create("g", [CSharpSyntaxTree.ParseText(source)], refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MappingGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(comp, out Compilation output, out ImmutableArray<Diagnostic> diags);
        return (diags, output);
    }

    [Fact]
    public void UnsupportedPropertyTypeIsReportedWithTypeAndMember()
    {
        (ImmutableArray<Diagnostic> d, _) = Run("""
            using RecursiveAI.OneLite;
            using System.Collections.Generic;
            namespace N { [OneLiteRow] public class Bad { public long Id { get; set; } public List<int> Tags { get; set; } = new(); } }
            """);
        Diagnostic one = Assert.Single(d, x => x.Id == "ONELITE001");
        string msg = one.GetMessage();
        Assert.Contains("Bad.Tags", msg, StringComparison.Ordinal);
        Assert.Contains("List<int>", msg, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, one.Severity);
    }

    [Fact]
    public void PrivateTypeAndMissingConstructorAreReported()
    {
        (ImmutableArray<Diagnostic> d, _) = Run("""
            using RecursiveAI.OneLite;
            namespace N { public class Outer { [OneLiteRow] private class Hidden { public long Id { get; set; } } } }
            """);
        Assert.Contains(d, x => x.Id == "ONELITE003" && x.GetMessage().Contains("Hidden", StringComparison.Ordinal));

        (ImmutableArray<Diagnostic> d2, _) = Run("""
            using RecursiveAI.OneLite;
            namespace N { [OneLiteRow] public class NoCtor { private NoCtor() { } public long Id { get; set; } } }
            """);
        Assert.Contains(d2, x => x.Id == "ONELITE002" && x.GetMessage().Contains("NoCtor", StringComparison.Ordinal));
    }

    [Fact]
    public void GoodTypesGenerateCompilableMappers()
    {
        (ImmutableArray<Diagnostic> d, Compilation output) = Run("""
            using RecursiveAI.OneLite;
            namespace N { [OneLiteRow] public record R(long Id, string? Name, int N = 3); [OneLiteRow] public struct S { public long Id { get; set; } public double? V { get; set; } } }
            """);
        Assert.DoesNotContain(d, x => x.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(TestContext.Current.CancellationToken), x => x.Severity == DiagnosticSeverity.Error);
        Assert.Contains(output.SyntaxTrees, t => t.FilePath.EndsWith("OneLiteRowMappers.g.cs", StringComparison.Ordinal));
    }
}
