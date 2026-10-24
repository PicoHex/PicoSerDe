using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoYaml.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// F2 scope: an empty scalar sequence is fixed by <c>WriteStartSequence()</c>/
/// <c>WriteEndSequence()</c>, but <c>List&lt;List&lt;T&gt;&gt;</c> (and object-element
/// lists) inside a YAML member are still dropped at extraction time by the shared
/// <c>PICOSERDE004</c> diagnostic — that shape is F5's scope.
///
/// <c>PicoYaml.Tests.EmptySequenceTests</c> asserts the observable half (the
/// member is absent and the sibling keys survive); this test pins the diagnostic id
/// itself, which needs a generator-driven compilation. Only this project references
/// <c>PicoYaml.Gen</c> with <c>ReferenceOutputAssembly="true"</c>.
/// </summary>
public class YamlNestedListDiagnosticTests
{
    private const string Id = "PICOSERDE004";

    [Test]
    public async Task Yaml_NestedScalarListMember_ReportsDiagnostic()
    {
        const string source = """
            using PicoYaml;

            public class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<System.Collections.Generic.List<int>> Grid { get; set; } = new();
                public int Count { get; set; } = 5;
            }

            public static class Probe
            {
                public static string Run() => YamlSerializer.Serialize(new ProbeDoc());
            }
            """;

        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).Contains(Id);
        await Assert
            .That(generated)
            .DoesNotContain("Grid")
            .Because("the dropped member must not be emitted");
    }

    /// <summary>
    /// The diagnostic is specific to the unrepresentable nested shape: a plain
    /// <c>List&lt;int&gt;</c> member is generated, so F2's fix must not have moved it
    /// into the dropped set.
    /// </summary>
    [Test]
    public async Task Yaml_FlatScalarListMember_ReportsNoDiagnostic()
    {
        const string source = """
            using PicoYaml;

            public class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<int> D { get; set; } = new();
                public int Count { get; set; } = 5;
            }

            public static class Probe
            {
                public static string Run() => YamlSerializer.Serialize(new ProbeDoc());
            }
            """;

        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("WriteStartSequence()");
        await Assert.That(generated).Contains("WriteEndSequence()");
    }

    private static (string[] DiagnosticIds, string Generated) RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "YamlNestedListDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(new YamlSerializerGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        var ids = result.Diagnostics.Select(d => d.Id).ToArray();
        var generated = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        return (ids, generated);
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
            AssemblyPath(typeof(YamlSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.SerializerExtensions).Assembly)
        );
    }
}
