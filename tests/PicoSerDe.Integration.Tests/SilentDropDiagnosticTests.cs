using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoIni.Gen;
using PicoJetson.Gen;
using PicoMsgPack.Gen;
using PicoToml.Gen;
using PicoYaml.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Two extraction paths used to drop a member with a bare <c>continue</c> and no
/// diagnostic at all — the only remaining silent data loss in the generator:
///
/// <list type="bullet">
///   <item>an INI member whose element is an object (<c>List&lt;Dto&gt;</c>);</item>
///   <item>any member whose element is an extended collection kind
///         (<c>HashSet</c>, <c>Queue</c>, <c>Stack</c>, <c>LinkedList</c>,
///         <c>ImmutableArray</c>, <c>Memory</c>, <c>ReadOnlyMemory</c>) — in
///         <b>every</b> format, JSON included.</item>
/// </list>
///
/// Both now report <c>PICOSERDE004</c> so the drop is visible in the build log.
/// These tests drive the generator in-proc because the diagnostic id (not the
/// runtime behaviour) is the contract being pinned.
/// </summary>
public class SilentDropDiagnosticTests
{
    private const string Id = "PICOSERDE004";

    private const string PlainRow = """
        public class PlainRow
        {
            public string Name { get; set; } = "";
        }
        """;

    [Test]
    public async Task Ini_ObjectListMember_ReportsDiagnostic()
    {
        var source = $$"""
            using PicoIni;

            {{PlainRow}}

            [PicoIniSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<PlainRow> Rows { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunIni(source);
        await Assert
            .That(ids)
            .Contains(Id)
            .Because("INI cannot express an object list, so the drop must be reported");
        await Assert.That(generated).DoesNotContain("Rows").Because("the member is not emitted");
    }

    [Test]
    public async Task Json_ExtendedCollectionElement_ReportsDiagnostic()
    {
        var source = """
            using PicoJetson;

            [PicoJsonSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<System.Collections.Generic.HashSet<int>> Tags { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunJson(source);
        await Assert
            .That(ids)
            .Contains(Id)
            .Because("a HashSet *element* is not emitted yet, so the drop must be reported");
        await Assert.That(generated).DoesNotContain("Tags").Because("the member is not emitted");
    }

    [Test]
    public async Task MsgPack_ExtendedCollectionElement_ReportsDiagnostic()
    {
        var source = """
            using PicoMsgPack;

            [PicoMsgPackSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<int>> Tags { get; set; } = new();
            }
            """;

        var (ids, generated, _) = Run(new MsgPackSerializerGenerator(), source);
        await Assert
            .That(ids)
            .Contains(Id)
            .Because("MsgPack collected the same skip list but never surfaced it");
        await Assert.That(generated).DoesNotContain("Tags");
    }

    /// <summary>
    /// Control: the diagnostic must stay specific to unrepresentable shapes, otherwise
    /// this change would turn every ordinary object list into a build warning.
    /// </summary>
    [Test]
    public async Task Json_ObjectListMember_ReportsNoDiagnostic()
    {
        var source = $$"""
            using PicoJetson;

            {{PlainRow}}

            [PicoJsonSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<PlainRow> Rows { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunJson(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("Rows").Because("JSON emits object lists");
    }

    /// <summary>
    /// Control: a flat scalar list in INI is representable (<c>K = [1, 2]</c>), so the
    /// new INI branch must not fire for it.
    /// </summary>
    [Test]
    public async Task Ini_FlatScalarListMember_ReportsNoDiagnostic()
    {
        var source = """
            using PicoIni;

            [PicoIniSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public System.Collections.Generic.List<int> Nums { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunIni(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).Contains("Nums").Because("INI emits flat scalar lists");
    }

    // ── drivers ────────────────────────────────────────────────────────────────────────
    // One shape for all five formats: (diagnostic ids, generated text, diagnostic
    // messages). The messages are here for Task 7, which must tell the ⛔ wording from
    // the debt wording without driving the generator itself.

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunIni(
        string source
    ) => Run(new IniSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunJson(
        string source
    ) => Run(new JsonSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunMsgPack(
        string source
    ) => Run(new MsgPackSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunYaml(
        string source
    ) => Run(new YamlSerializerGenerator(), source);

    internal static (string[] DiagnosticIds, string Generated, string[] Messages) RunToml(
        string source
    ) => Run(new TomlSerializerGenerator(), source);

    private static (string[] DiagnosticIds, string Generated, string[] Messages) Run(
        IIncrementalGenerator generator,
        string source
    )
    {
        var compilation = CSharpCompilation.Create(
            "SilentDropDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(generator);
        var result = driver.RunGenerators(compilation).GetRunResult();
        var ids = result.Diagnostics.Select(d => d.Id).ToArray();
        var messages = result.Diagnostics.Select(d => d.GetMessage()).ToArray();
        var generated = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        return (ids, generated, messages);
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
            AssemblyPath(typeof(PicoIni.IniSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoJetson.JsonSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoMsgPack.MsgPackSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoYaml.YamlSerializer).Assembly)
        );
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoToml.TomlSerializer).Assembly)
        );
    }
}
