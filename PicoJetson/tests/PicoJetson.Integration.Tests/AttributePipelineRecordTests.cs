using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;
using PicoSerDe.TestSupport;

namespace PicoJetson.Tests;

// ── Fixtures: discovered through the ATTRIBUTE pipeline only ──────────────────────────────────
//
// The [GenerateSerializer(typeof(T))] declaration below is the ONLY thing that asks for PosRecord's
// serializer: no call site in this file names it in the pipeline's eyes… except the round-trip test
// further down, which is the point. Before the fix, a project that declared nothing but these two
// lines did not COMPILE (see the in-proc guard at the bottom of this file for the isolated proof and
// GenInfrastructure.ExpandOneType for the cause): the attribute pipeline emitted `new PosRecord()` +
// `result.A = …` for a positional record, i.e. CS7036 + one CS8852 per member.

/// <summary>Attribute-only discovery of a positional record (the regression fixture).</summary>
[GenerateSerializer(typeof(PosRecord))]
internal sealed class PosRecordConfig { }

/// <summary>Positional record: no parameterless constructor, init-only properties.</summary>
public sealed record PosRecord(string A, int B);

/// <summary>Attribute-only discovery of a class with a public parameterless constructor.</summary>
[GenerateSerializer(typeof(PlainAttributeDto))]
internal sealed class PlainAttributeDtoConfig { }

/// <summary>Reverse guard: the `new()` + property-assignment path must keep working.</summary>
public class PlainAttributeDto
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// `[GenerateSerializer(typeof(T))]` on a POSITIONAL RECORD: the attribute pipeline must construct it
/// through its primary constructor, exactly like the call-site pipeline
/// (`JsonSerializer.Deserialize&lt;MatrixRecord&gt;`) already did.
/// </summary>
public class AttributePipelineRecordTests
{
    [Test]
    public async Task PositionalRecord_ViaAttribute_RoundTrips()
    {
        var original = new PosRecord("a", 7);

        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<PosRecord>(json);

        await Assert.That(json.Length).IsGreaterThan(0);
        await Assert.That(result).IsEqualTo(original);
        await Assert.That(result!.A).IsEqualTo("a");
        await Assert.That(result.B).IsEqualTo(7);
    }

    /// <summary>
    /// The wire shape is the property shape, not the parameter order: positional parameters land on
    /// properties of the same name (declared `A`/`B`, written `A`/`B`) — the mapping the primary
    /// constructor's parameters have to keep agreeing with.
    /// </summary>
    [Test]
    public async Task PositionalRecord_ViaAttribute_WritesPropertyNames()
    {
        var json = JsonSerializer.Serialize(new PosRecord("a", 7));

        await Assert.That(json).IsEqualTo("""{"A":"a","B":7}""");
    }

    /// <summary>Reverse guard: a class with a public parameterless ctor keeps the `new()` path.</summary>
    [Test]
    public async Task PlainClass_ViaAttribute_StillRoundTrips()
    {
        var original = new PlainAttributeDto { Label = "plain", Count = 3 };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<PlainAttributeDto>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Label).IsEqualTo("plain");
        await Assert.That(result.Count).IsEqualTo(3);
    }

    // ── Compile-time guards (drive the generator in-proc) ────────────────────────────────────
    //
    // These two give what a project build cannot: a source in which the attribute is the ONLY thing
    // naming the type (so the call-site pipeline cannot mask the attribute pipeline), and a source
    // whose type can be constructed NEITHER way.

    [Test]
    public async Task PositionalRecord_ViaAttribute_GeneratesCompilableCode()
    {
        const string source = """
            using PicoSerDe.Core;

            public sealed record Pos(string A, int B);

            [GenerateSerializer(typeof(Pos))]
            internal sealed class Config { }
            """;

        var (errors, ids) = Run(source);

        await Assert
            .That(errors.Replace("\r\n", "\n"))
            .IsEqualTo("")
            .Because(
                "the attribute pipeline used to emit `new Pos()` + `result.A = …` for this record "
                    + "(CS7036, CS8852); the call-site pipeline handled the same type correctly"
            );
        await Assert.That(ids).DoesNotContain("PICOSERDE007");
    }

    /// <summary>
    /// Neither a primary constructor nor a public parameterless one: no `new T()` and no constructor
    /// call can be emitted, so the generator must REPORT the drop (PICOSERDE007, Warning,
    /// Location.None) and emit nothing that fails to compile.
    /// </summary>
    [Test]
    public async Task UnconstructibleType_ViaAttribute_IsReportedNotEmitted()
    {
        const string source = """
            using PicoSerDe.Core;

            public class NoWayIn
            {
                public NoWayIn(int only) => Only = only;
                public int Only { get; }
            }

            [GenerateSerializer(typeof(NoWayIn))]
            internal sealed class Config { }
            """;

        var (errors, ids) = Run(source);

        await Assert.That(errors.Replace("\r\n", "\n")).IsEqualTo("");
        await Assert.That(ids).Contains("PICOSERDE007");
    }

    /// <summary>Runs the JSON generator in-proc: the compilation errors over the generated code, and
    /// the ids of the diagnostics the generator itself reported.</summary>
    private static (string Errors, string[] Ids) Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "AttributePipelineAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            RoslynProbe.PlatformReferences(
                typeof(JsonSerializer).Assembly,
                typeof(PicoSerDe.Core.SerializerExtensions).Assembly
            ),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var errors = string.Join(
            "\n",
            output
                .GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
        );
        return (errors, diagnostics.Select(d => d.Id).ToArray());
    }
}
