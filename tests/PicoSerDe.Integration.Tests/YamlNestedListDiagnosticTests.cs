using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoYaml.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// F5 scope (closed): <c>List&lt;List&lt;T&gt;&gt;</c> (and object-element lists) inside a
/// YAML member used to be dropped at extraction time by the shared <c>PICOSERDE004</c>
/// diagnostic. Nested sequences are generated now, so the diagnostic must not fire
/// for them and the member must reach the generated serializer.
///
/// <c>PicoYaml.Tests.NestedSeqTests</c> asserts the observable half (the member is
/// written, read back, and its siblings survive); this test pins the diagnostic id
/// itself, which needs a generator-driven compilation. Only this project references
/// <c>PicoYaml.Gen</c> with <c>ReferenceOutputAssembly="true"</c>.
/// </summary>
public class YamlNestedListDiagnosticTests
{
    private const string Id = "PICOSERDE004";

    [Test]
    public async Task Yaml_NestedScalarListMember_ReportsNoDiagnostic()
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
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("Grid").Because("the member must be emitted");
        await Assert
            .That(generated)
            .Contains("WriteEmptySequenceItem")
            .Because("a nested sequence is written as '- ' block lines");
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

    /// <summary>
    /// F5 review I-3: the innermost element's nullability decides representability. Before
    /// the fix only the outermost element was inspected, so <c>List&lt;List&lt;int?&gt;&gt;</c> slipped
    /// through and the generated <c>WriteSequenceItem(int?)</c> did not compile (CS1503) —
    /// a hard build break where the pre-F5 contract was PICOSERDE004 + dropped member.
    /// Each shape below must report the diagnostic and leave no code behind.
    /// </summary>
    [Test]
    [Arguments("System.Collections.Generic.List<System.Collections.Generic.List<int?>>")]
    [Arguments("System.Collections.Generic.List<int?[]>")]
    [Arguments("System.Collections.Generic.List<System.Collections.Generic.List<ProbeEnum?>>")]
    [Arguments("System.Collections.Generic.List<System.Collections.Generic.List<ProbeObj?>>")]
    [Arguments(
        "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int?>>"
    )]
    [Arguments(
        "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int?[]>>"
    )]
    public async Task Yaml_NestedNullableElementMember_IsDroppedWithTheDiagnostic(string shape)
    {
        var source =
            """
                using PicoYaml;

                public enum ProbeEnum { A, B }

                public class ProbeObj { public string Name { get; set; } = ""; }

                public class ProbeDoc
                {
                    public string Name { get; set; } = "";
                    public
                """
            + "\n"
            + shape
            + " Member { get; set; } = new();\n}\n\n"
            + "public static class Probe { public static string Run() => YamlSerializer.Serialize(new ProbeDoc()); }";

        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).Contains(Id).Because("shape=" + shape + " ids=" + ids.Length);
        await Assert
            .That(generated)
            .DoesNotContain("Member")
            .Because("the unrepresentable member must not be emitted (shape=" + shape + ")");
        await Assert.That(generated).Contains("Name").Because("the sibling member must survive");
    }

    /// <summary>
    /// F5 fix round 2 (A): `List&lt;Wrapper&lt;int?&gt;&gt;` is a list of OBJECTS. Round 1 walked the
    /// element chain through every single-type-argument generic, so the innermost element was
    /// computed as `int?` and the member was dropped with PICOSERDE004 — even though the
    /// element is an object whose own `int?` member is representable. Only a collection
    /// element may be descended into.
    /// </summary>
    [Test]
    public async Task Yaml_ListOfPlainGenericObject_ReportsNoDiagnostic()
    {
        const string source = """
            using PicoYaml;

            public class ProbeWrap<T>
            {
                public string Name { get; set; } = "";
                public T? Value { get; set; }
            }

            public class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<ProbeWrap<int?>> Items { get; set; } = new();
                public int Count { get; set; } = 5;
            }

            public static class Probe
            {
                public static string Run() => YamlSerializer.Serialize(new ProbeDoc());
            }
            """;

        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("Items").Because("the member must be emitted");
    }

    /// <summary>
    /// The over-drop fix must not re-open the genuinely unrepresentable shape: an object
    /// element is kept, a nested nullable sequence is still dropped — in the same document.
    /// </summary>
    [Test]
    public async Task Yaml_ObjectElementKept_NestedNullableElementStillDropped()
    {
        const string source = """
            using PicoYaml;

            public class ProbeWrap<T>
            {
                public string Name { get; set; } = "";
                public T? Value { get; set; }
            }

            public class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<ProbeWrap<int?>> Items { get; set; } = new();
                public System.Collections.Generic.List<System.Collections.Generic.List<int?>> Grid { get; set; } = new();
            }

            public static class Probe
            {
                public static string Run() => YamlSerializer.Serialize(new ProbeDoc());
            }
            """;

        var (ids, generated) = RunGenerator(source);
        await Assert.That(ids).Contains(Id).Because("the nested nullable sequence stays dropped");
        await Assert.That(generated).Contains("Items").Because("the object list must be emitted");
        await Assert.That(generated).DoesNotContain("Grid").Because("Grid must stay dropped");
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
