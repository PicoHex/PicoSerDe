using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;
using PicoSerDe.TestSupport;

namespace PicoJetson.Tests;

/// <summary>
/// Review P3-1: PICOJETSON004 (anonymous serialization needs AllowUnsafeBlocks)
/// must only fire when the compilation actually uses anonymous-type
/// serialization — it used to fire for every compilation without unsafe.
/// </summary>
public class AnonDiagnosticTests
{
    [Test]
    public async Task NoAnonymousUsage_NoDiagnostic()
    {
        const string source = """
            using PicoJetson;

            public class Plain
            {
                public int X { get; set; }
            }

            public static class Probe
            {
                public static string Run() => JsonSerializer.Serialize(new Plain());
            }
            """;

        var ids = RunGenerator(source);
        await Assert.That(ids).DoesNotContain("PICOJETSON004");
    }

    [Test]
    public async Task AnonymousUsageWithoutUnsafe_ReportsDiagnostic()
    {
        const string source = """
            using PicoJetson;

            public static class Probe
            {
                public static string Run() => JsonSerializer.Serialize(new { V = 1 });
            }
            """;

        var ids = RunGenerator(source);
        await Assert.That(ids).Contains("PICOJETSON004");
    }

    private static string[] RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "AnonDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            RoslynProbe.PlatformReferences(typeof(JsonSerializer).Assembly, typeof(PicoSerDe.Core.SerializerExtensions).Assembly),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return result.Diagnostics.Select(d => d.Id).ToArray();
    }

}
