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
///
/// <para>
/// The same file pins <c>PICOSERDE001</c> — the id for "this member's TYPE could not
/// be resolved at all". Both extraction sites (public field, property) collected it
/// into a <c>List&lt;Diagnostic&gt;</c> that no generator ever passed, so D-6 was a
/// silent loss: the collector now travels on the <c>TypeInfo</c> value and the
/// generators report it (one diagnostic per member).
/// </para>
/// </summary>
public class SilentDropDiagnosticTests
{
    private const string Id = "PICOSERDE004";

    private const string UnresolvableTypeId = "PICOSERDE001";

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

    // ── PICOSERDE001 (D-6): an unresolvable TYPE must not vanish silently ─────────────

    /// <summary>
    /// <c>Opaque</c> has no public member, so <c>TypeKindResolver</c> resolves nothing
    /// for it: the property could never be emitted. Before Task 5 the extractor pushed
    /// a diagnostic into a list nobody passed — the member simply disappeared.
    /// </summary>
    [Test]
    public async Task Unresolvable_property_type_reports_PICOSERDE001()
    {
        const string source = """
            using PicoJetson;

            public class Opaque { }

            [PicoJsonSerializable]
            public partial class Doc
            {
                public string Name { get; set; } = "";
                public Opaque V { get; set; } = new();
            }
            """;

        var (ids, generated, messages) = RunJson(source);

        await Assert
            .That(ids)
            .Contains(UnresolvableTypeId)
            .Because("a property whose type cannot be resolved is dropped and must say so");
        await Assert
            .That(ids.Count(i => i == UnresolvableTypeId))
            .IsEqualTo(1)
            .Because("one diagnostic per member, not one per extraction pass");
        await Assert
            .That(generated)
            .DoesNotContain("public global::Opaque V")
            .Because("the member is not emitted — that is exactly why it must be reported");
        await Assert
            .That(messages.Any(m => m.Contains("Doc.V")))
            .IsTrue()
            .Because($"the message must name the member, got: {string.Join(" | ", messages)}");
    }

    /// <summary>
    /// The public-field site of the same extractor. Reached with
    /// <c>[PicoSerializable(IncludeFields = true)]</c> (or a ref struct); its
    /// unresolvable-type `continue` was the other silent point of D-6.
    /// </summary>
    [Test]
    public async Task Unresolvable_public_field_reports_PICOSERDE001()
    {
        const string source = """
            using PicoSerDe.Core;

            public class Opaque { }

            [PicoSerializable(IncludeFields = true)]
            public sealed class Doc
            {
                public string Name = "";
                public Opaque V = new();
            }
            """;

        var (ids, generated, messages) = RunJson(source);

        await Assert
            .That(ids)
            .Contains(UnresolvableTypeId)
            .Because("the public-field path drops unresolvable fields too");
        await Assert
            .That(messages.Any(m => m.Contains("Doc.V")))
            .IsTrue()
            .Because($"the field must be named, got: {string.Join(" | ", messages)}");
        await Assert.That(generated).DoesNotContain("public global::Opaque V");
    }

    /// <summary>
    /// The same type is discovered once per pipeline (usage-driven, <c>[PicoSerializable]</c>,
    /// <c>[PicoJsonSerializable]</c>, ...). Each discovery re-extracts and re-records the
    /// drop, so the reporting side must dedupe — otherwise one bad member produces one
    /// warning per pipeline (review focus "diagnostics that multiply").
    /// </summary>
    [Test]
    public async Task Member_discovered_by_two_pipelines_reports_one_diagnostic()
    {
        const string source = """
            using PicoJetson;
            using PicoSerDe.Core;

            public class Opaque { }

            [PicoSerializable]
            [PicoJsonSerializable]
            public partial class Doc
            {
                public string Name { get; set; } = "";
                public Opaque V { get; set; } = new();
            }
            """;

        var (ids, _, _) = RunJson(source);

        await Assert
            .That(ids.Count(i => i == UnresolvableTypeId))
            .IsEqualTo(1)
            .Because("two discovery pipelines, one dropped member, one diagnostic");
    }

    /// <summary>
    /// Control: a resolvable member type must not produce PICOSERDE001 — the wiring
    /// must not turn every ordinary DTO into a warning.
    /// </summary>
    [Test]
    public async Task Resolvable_member_type_reports_no_PICOSERDE001()
    {
        var source = $$"""
            using PicoJetson;

            {{PlainRow}}

            [PicoJsonSerializable]
            public partial class ProbeDoc
            {
                public string Name { get; set; } = "";
                public PlainRow Row { get; set; } = new();
            }
            """;

        var (ids, generated, _) = RunJson(source);

        await Assert.That(ids).DoesNotContain(UnresolvableTypeId);
        await Assert.That(generated).Contains("Row");
    }

    /// <summary>
    /// I-1: PICOSERDE001 has to carry the member's DECLARATION SITE. Two things depend on
    /// it, and both were lost while the diagnostic was reported with a null location: the
    /// build log can point at the member (file(line,col)) only with one, and
    /// <c>#pragma warning disable PICOSERDE001</c> can silence one member in one file only
    /// with one. Without a location the sole way out is a project-wide <c>NoWarn</c>, which
    /// also silences the next member that is dropped by a real mistake.
    /// </summary>
    [Test]
    public async Task PICOSERDE001_points_at_the_dropped_member_declaration()
    {
        const string source = """
            using PicoJetson;

            public class Opaque { }

            [PicoJsonSerializable]
            public partial class Doc
            {
                public string Name { get; set; } = "";
                public Opaque V { get; set; } = new();
            }
            """;

        var warning = RunDiagnostics(new JsonSerializerGenerator(), source)
            .Single(d => d.Id == UnresolvableTypeId);

        await Assert
            .That(warning.Location.IsInSource)
            .IsTrue()
            .Because(
                $"PICOSERDE001 must be navigable and pragma-suppressible, got location "
                    + $"'{warning.Location}' (Kind={warning.Location.Kind})"
            );

        // It also has to point at the MEMBER, not at the containing type or at line 1:
        // the pragma a reader writes around `V` only works if the location is inside that
        // region, and an IDE jump has to land on the member that was dropped.
        var lines = source.Split('\n');
        var declaredOn = Array.FindIndex(
            lines,
            l => l.Contains("Opaque V", StringComparison.Ordinal)
        );
        var span = warning.Location.GetLineSpan();

        await Assert
            .That(span.StartLinePosition.Line)
            .IsEqualTo(declaredOn)
            .Because(
                "the location must be the member's own line, not its containing type: "
                    + $"got line {span.StartLinePosition.Line}, the member is on {declaredOn}"
            );
        await Assert
            .That(lines[declaredOn][span.StartLinePosition.Character..])
            .StartsWith("V")
            .Because("the location must start at the member name");
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
        var result = RunGenerator(generator, source);
        var ids = result.Diagnostics.Select(d => d.Id).ToArray();
        var messages = result.Diagnostics.Select(d => d.GetMessage()).ToArray();
        var generated = string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        return (ids, generated, messages);
    }

    /// <summary>The same run, with the diagnostics intact: I-1 is about the Location.</summary>
    private static Diagnostic[] RunDiagnostics(IIncrementalGenerator generator, string source) =>
        RunGenerator(generator, source).Diagnostics.ToArray();

    /// <summary>The one place that builds a compilation and drives a generator.</summary>
    private static GeneratorDriverRunResult RunGenerator(
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
        return driver.RunGenerators(compilation).GetRunResult();
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
        // [PicoSerializable(IncludeFields = true)] lives in PicoSerDe.Core: without this
        // reference the attribute binds to an error type, ForAttributeWithMetadataName
        // finds nothing, and the public-field extraction path is never reached.
        yield return MetadataReference.CreateFromFile(
            AssemblyPath(typeof(PicoSerDe.Core.PicoSerializableAttribute).Assembly)
        );
    }
}
