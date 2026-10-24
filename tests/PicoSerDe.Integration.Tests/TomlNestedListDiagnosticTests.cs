using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoToml;
using PicoToml.Gen;

namespace PicoSerDe.Integration.Tests;

// ── Runtime fixtures ──────────────────────────────────────────────────────

public class TomlNestedDropLeaf
{
    public string Name { get; set; } = "";
}

/// <summary>
/// F6 boundary: a nested <em>primitive</em> array is representable in TOML (an inline
/// array of inline arrays) and is generated now; a nested <em>object</em> array is not
/// (there is no array-table of array-tables), so it must still be dropped with
/// PICOSERDE004 and leave the document otherwise intact.
/// </summary>
public class TomlNestedDropDoc
{
    public string K { get; set; } = "";
    public List<List<TomlNestedDropLeaf>> Grid { get; set; } = new();
    public int Count { get; set; }
}

/// <summary>
/// F6: <c>List&lt;List&lt;int&gt;&gt;</c> used to be dropped by the shared PICOSERDE004
/// diagnostic for TOML even though TOML expresses it natively. This pins the
/// diagnostic itself (needs a generator-driven compilation), plus the remaining
/// boundary: the object-leaf chain stays unrepresentable.
/// </summary>
public class TomlNestedListDiagnosticTests
{
    private const string Id = "PICOSERDE004";

    [Test]
    public async Task Toml_NestedScalarListMember_ReportsNoDiagnostic()
    {
        const string source = """
            using PicoToml;

            public class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<System.Collections.Generic.List<int>> Grid { get; set; } = new();
                public int Count { get; set; } = 5;
            }

            public static class Probe
            {
                public static string Run() => TomlSerializer.Serialize(new ProbeDoc());
            }
            """;

        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("Grid").Because("the member must be emitted");
        await Assert
            .That(generated)
            .Contains("tw.WriteStartArray(\"\"u8)")
            .Because("the inner level is an anonymous inline array");
    }

    [Test]
    public async Task Toml_NestedObjectListMember_IsDroppedWithTheDiagnostic()
    {
        const string source = """
            using PicoToml;

            public class ProbeLeaf { public string Name { get; set; } = ""; }

            public class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<System.Collections.Generic.List<ProbeLeaf>> Rows { get; set; } = new();
                public int Count { get; set; } = 5;
            }

            public static class Probe
            {
                public static string Run() => TomlSerializer.Serialize(new ProbeDoc());
            }
            """;

        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).Contains(Id).Because("array tables cannot nest");
        await Assert.That(generated).DoesNotContain("Rows").Because("the member must be dropped");
        await Assert.That(generated).Contains("Count").Because("the sibling must survive");
    }

    /// <summary>
    /// A null inner array has no TOML representation at all (there is no null, and
    /// skipping it would silently change the element count), so a chain with a
    /// nullable collection level must stay dropped instead of emitting generated code
    /// that dereferences a possibly-null inner collection.
    /// </summary>
    [Test]
    [Arguments("System.Collections.Generic.List<System.Collections.Generic.List<int>?>")]
    [Arguments("System.Collections.Generic.List<int[]?>")]
    public async Task Toml_NestedNullableCollectionLevel_IsDroppedWithTheDiagnostic(string shape)
    {
        var source = """
            using PicoToml;

            public class ProbeDoc
            {
                public string Name { get; set; } = "";
                public __SHAPE__ Member { get; set; } = new();
            }

            public static class Probe { public static string Run() => TomlSerializer.Serialize(new ProbeDoc()); }
            """.Replace("__SHAPE__", shape);
        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).Contains(Id).Because("shape=" + shape);
        await Assert.That(generated).DoesNotContain("Member").Because("shape=" + shape);
        await Assert.That(generated).Contains("Name").Because("the sibling must survive");
    }

    /// <summary>
    /// The runtime half of the boundary above: the dropped member is invisible on the
    /// wire and leaves an empty collection behind, while its siblings round-trip.
    /// </summary>
    [Test]
    public async Task Toml_NestedObjectListMember_IsDroppedAtRuntime()
    {
        var doc = new TomlNestedDropDoc
        {
            K = "k",
            Count = 7,
            Grid = new List<List<TomlNestedDropLeaf>> { new() { new() { Name = "a" } } },
        };

        var toml = TomlSerializer.Serialize(doc);

        await Assert.That(toml).Contains("K = \"k\"");
        await Assert.That(toml).Contains("Count = 7");
        await Assert.That(toml).DoesNotContain("Grid");
        await Assert.That(toml).DoesNotContain("[[Grid");

        var back = TomlSerializer.Deserialize<TomlNestedDropDoc>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.K).IsEqualTo("k");
        await Assert.That(back.Count).IsEqualTo(7);
        await Assert.That(back.Grid.Count).IsEqualTo(0);
    }

    private static (string[] DiagnosticIds, string Generated) RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TomlNestedListDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(new TomlSerializerGenerator());
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
            AssemblyPath(typeof(TomlSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.SerializerExtensions).Assembly)
        );
    }
}
