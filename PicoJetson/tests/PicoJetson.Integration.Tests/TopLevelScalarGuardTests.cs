using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;
using PicoSerDe.TestSupport;

namespace PicoJetson.Tests;

/// <summary>
/// Review P1-1: top-level Nullable&lt;T&gt; / scalar usage must never make the
/// JSON generator emit non-compiling code (CS0721/CS0722 against the static
/// <c>System.Nullable</c> class).
/// </summary>
public class TopLevelScalarGuardTests
{
    [Test]
    public async Task TopLevelNullableValueType_GeneratesCompilableCode()
    {
        const string source = """
            using PicoJetson;

            public static class Probe
            {
                public static int? Run() => JsonSerializer.Deserialize<int?>("1"u8);
            }
            """;

        var compilation = CSharpCompilation.Create(
            "ProbeAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            RoslynProbe.PlatformReferences(typeof(JsonSerializer).Assembly, typeof(PicoSerDe.Core.SerializerExtensions).Assembly),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var errors = string.Join(
            "\n",
            output
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
        );
        await Assert.That(errors).IsEqualTo("");
    }

    [Test]
    public async Task TopLevelGuidNoLongerSerializesAsEmptyObject()
    {
        await Assert
            .That(() => JsonSerializer.Serialize(Guid.NewGuid()))
            .Throws<InvalidOperationException>();
    }

}
