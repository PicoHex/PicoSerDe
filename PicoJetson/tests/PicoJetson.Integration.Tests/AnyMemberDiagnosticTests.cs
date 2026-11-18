using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;
using PicoSerDe.TestSupport;

namespace PicoJetson.Tests;

// The `object?` MEMBER fix has two halves that a runtime test cannot see:
//   1. the member switches now REUSE the any-value chain (AnyMemberTests pins the wire),
//   2. a kind that reaches `default:` no longer disappears in silence — the generated code throws
//      and PICOSERDE006 says so at build time.
// This file pins the second half: the id exists, it does NOT fire on the kinds the switches do
// emit (a false positive would be a warning on working code), and the predicate behind it agrees
// with the emitters on every kind the JSON format can resolve.

/// <summary>
/// One member per kind the JSON member switches claim to render. Every one of them must generate
/// WITHOUT PICOSERDE006 — if a kind loses its arm, either the predicate notices (diagnostic) or the
/// emitted code throws, and both fail this test.
/// </summary>
public class EveryKindOnceDto
{
    public enum Shade
    {
        Light,
        Dark,
    }

    public string Str { get; set; } = "";
    public int I32 { get; set; }
    public long I64 { get; set; }
    public short I16 { get; set; }
    public ushort U16 { get; set; }
    public sbyte SB { get; set; }
    public byte B { get; set; }
    public uint U32 { get; set; }
    public ulong U64 { get; set; }
    public float F32 { get; set; }
    public double F64 { get; set; }
    public bool Flag { get; set; }
    public DateTime When { get; set; }
    public Guid Id { get; set; }
    public decimal Money { get; set; }
    public DateOnly Day { get; set; }
    public TimeOnly Clock { get; set; }
    public TimeSpan Span { get; set; }
    public Shade Colour { get; set; }
    public char Glyph { get; set; }
    public DateTimeOffset Offset { get; set; }
    public Uri? Link { get; set; }
    public Version? Ver { get; set; }
    public Half H { get; set; }
    public System.Numerics.BigInteger Big { get; set; }
    public Int128 I128 { get; set; }
    public UInt128 U128 { get; set; }
    public nint NativeInt { get; set; }
    public object? Anything { get; set; }
    public List<int> Numbers { get; set; } = new();
    public HashSet<string> Tags { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
    public Dictionary<string, object?> Bag { get; set; } = new();
    public EveryKindOnceNested Nested { get; set; } = new();
}

public class EveryKindOnceNested
{
    public string Name { get; set; } = "";
}

public sealed record EverythingCtorOnceDto(object? Payload);

public class AnyMemberDiagnosticTests
{
    private const string Id = "PICOSERDE006";

    [Test]
    public async Task ObjectMemberAndCtorParam_RaiseNoMetricDiagnostic()
    {
        // The two shapes this fix adds must be clean: a diagnostic on them would be a warning on
        // code that now works.
        // A CALL SITE is what makes the generator look at a type (GenInfrastructure.IsCandidate):
        // there is no [JsonSerializable] attribute in this pipeline.
        var source = """
            using PicoJetson;

            public sealed record Payload(object? Result);

            public class Holder
            {
                public object? Anything { get; set; }
                public Payload? Nested { get; set; }
            }

            public static class Probe
            {
                public static string Write(Holder v) => JsonSerializer.Serialize(v);
                public static Holder? Read(byte[] b) => JsonSerializer.Deserialize<Holder>(b);
            }
            """;

        var (ids, generated) = Run(source);
        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).DoesNotContain("PICOSERDE006");
        await Assert.That(generated).Contains("__PicoAnyDictHelper.SerializeValue");
        await Assert.That(generated).Contains("__PicoAnyDictHelper.DeserializeValue");
    }

    [Test]
    public async Task EveryKindTheSwitchesEmit_RaisesNoDiagnostic()
    {
        var (ids, generated) = Run(
            """
            using PicoJetson;

            public sealed record EverythingCtorOnceDto(object? Payload);

            public class EverythingDto
            {
                public string Str { get; set; } = "";
                public int I32 { get; set; }
                public long I64 { get; set; }
                public short I16 { get; set; }
                public ushort U16 { get; set; }
                public sbyte SB { get; set; }
                public byte B { get; set; }
                public uint U32 { get; set; }
                public ulong U64 { get; set; }
                public float F32 { get; set; }
                public double F64 { get; set; }
                public bool Flag { get; set; }
                public System.DateTime When { get; set; }
                public System.Guid Id { get; set; }
                public decimal Money { get; set; }
                public System.DateOnly Day { get; set; }
                public System.TimeOnly Clock { get; set; }
                public System.TimeSpan Span { get; set; }
                public Shade Colour { get; set; }
                public char Glyph { get; set; }
                public System.DateTimeOffset Offset { get; set; }
                public System.Uri? Link { get; set; }
                public System.Version? Ver { get; set; }
                public System.Half H { get; set; }
                public System.Numerics.BigInteger Big { get; set; }
                public System.Int128 I128 { get; set; }
                public System.UInt128 U128 { get; set; }
                public System.IntPtr NativeInt { get; set; }
                public object? Anything { get; set; }
                public System.Collections.Generic.List<int> Numbers { get; set; } = new();
                public System.Collections.Generic.HashSet<string> Tags { get; set; } = new();
                public System.Collections.Generic.Dictionary<string, int> Counts { get; set; } = new();
                public System.Collections.Generic.Dictionary<string, object?> Bag { get; set; } = new();
                public Nested Inner { get; set; } = new();
            }

            public enum Shade { Light, Dark }

            public class Nested
            {
                public string Name { get; set; } = "";
            }

            public static class Probe
            {
                public static string Write(EverythingDto v) => JsonSerializer.Serialize(v);
                public static EverythingDto? Read(byte[] b) => JsonSerializer.Deserialize<EverythingDto>(b);
                public static string WriteCtor(EverythingCtorOnceDto v) => JsonSerializer.Serialize(v);
            }
            """
        );

        await Assert.That(ids).DoesNotContain(Id);
        await Assert.That(generated).DoesNotContain("PICOSERDE006");
    }

    /// <summary>
    /// The predicate behind the diagnostic, asked directly. It must answer "yes" for every kind the
    /// member switches have an arm for (a "no" here is a false-positive warning on working code) and
    /// "no" for a kind nobody renders (that is the whole point of the id).
    /// </summary>
    [Test]
    public async Task Predicate_answers_for_the_member_kind_vocabulary()
    {
        string[] emitted =
        [
            "string",
            "int32",
            "int64",
            "int16",
            "uint16",
            "sbyte",
            "byte",
            "uint32",
            "uint64",
            "float32",
            "float64",
            "boolean",
            "datetime",
            "guid",
            "decimal",
            "dateonly",
            "timeonly",
            "timespan",
            "enum",
            "datetimeoffset",
            "char",
            "uri",
            "version",
            "half",
            "biginteger",
            "int128",
            "uint128",
            "nint",
            "object",
            "dict",
            "any",
        ];

        var missing = emitted
            .Where(kind => !JsonSerializerGenerator.MemberKindHasEmitter(kind))
            .ToArray();
        await Assert
            .That(missing)
            .IsEmpty()
            .Because("these kinds all have a member arm, so PICOSERDE006 must stay silent on them");

        // Every collection kind answers through CollectionKinds.IsSequence, not a copied list.
        var collections = PicoSerDe
            .Gen.CollectionKinds.Names.Where(kind =>
                !JsonSerializerGenerator.MemberKindHasEmitter(kind)
            )
            .ToArray();
        await Assert.That(collections).IsEmpty();

        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("no-such-kind")).IsFalse();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter(null)).IsFalse();
    }

    /// <summary>
    /// The runtime half of "never silent": a boxed runtime type the any-value chain has no form for
    /// used to be written as <c>value.ToString()</c> — it came back as a String and nothing said so.
    /// </summary>
    [Test]
    public async Task BoxedRuntimeTypeWithoutAForm_Throws_InsteadOfBeingStringified()
    {
        Exception? thrown = null;
        try
        {
            JsonSerializer.Serialize(new ObjectMemberRecord(new Uri("https://example.test/x")));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert
            .That(thrown!.Message)
            .Contains("has no JSON form in the any-value chain")
            .Because("silent stringification is the defect; the message must say what to do");
    }

    private static (string[] Ids, string Generated) Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "AnyMemberDiagAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            RoslynProbe.AllPlatformReferences(
                typeof(JsonSerializer).Assembly,
                typeof(PicoSerDe.Core.SerializerExtensions).Assembly
            ),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        var run = driver.RunGenerators(compilation).GetRunResult();
        var ids = run.Diagnostics.Select(d => d.Id).ToArray();
        var generated = string.Join(
            "\n",
            run.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
        return (ids, generated);
    }
}
