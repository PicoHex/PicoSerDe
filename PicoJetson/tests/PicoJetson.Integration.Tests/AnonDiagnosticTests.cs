using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

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
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: false)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return result.Diagnostics.Select(d => d.Id).ToArray();
    }

    private static IEnumerable<MetadataReference> PlatformReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            var name = Path.GetFileName(path);
            if (
                name
                is "System.Private.CoreLib.dll"
                    or "System.Runtime.dll"
                    or "System.Collections.dll"
                    or "netstandard.dll"
                    or "System.Memory.dll"
            )
                yield return MetadataReference.CreateFromFile(path);
        }
        yield return MetadataReference.CreateFromFile(typeof(JsonSerializer).Assembly.Location);
        yield return MetadataReference.CreateFromFile(
            typeof(PicoSerDe.Core.SerializerExtensions).Assembly.Location
        );
    }
}
