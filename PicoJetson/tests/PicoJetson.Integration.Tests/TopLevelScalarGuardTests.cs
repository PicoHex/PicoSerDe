using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

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
            PlatformReferences(),
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

    private static string AssemblyPath(System.Reflection.Assembly assembly) =>
        Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll");

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
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(JsonSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.SerializerExtensions).Assembly)
        );
    }
}
