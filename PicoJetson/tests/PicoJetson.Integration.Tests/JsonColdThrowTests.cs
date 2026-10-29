using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

// ── json-gen3 #2: the cold-path messages leave the deserializer's method body ──
//
// Every failure inside a deserializer used to build its message inline:
//
//     throw new System.FormatException($"Expected String value for property \"Name\" at offset {reader.BytesConsumed}");
//
// The interpolated string allocates its handler on the CALLER's stack frame, and the caller is the
// document-sized method (a LargeFlat deserializer carries 32 of these). They now call one
// [MethodImpl(NoInlining)] static helper instead, so the frame stays small and the message is built
// only when the throw actually happens.
//
// The exception TYPE and the MESSAGE TEXT must not move by one character — that is the whole risk of
// this change, so it is guarded by a frozen table of real messages (including their offsets), not by
// a "contains" check:
//   * ColdThrowShapeTests   — the generated file must call the helper and must not carry an inline
//                             `{reader.BytesConsumed}` interpolation in a deserializer body any more.
//   * ColdThrowMessageTests — the frozen messages below are the pre-change text, byte for byte
//                             (captured from the build before the helper existed).

/// <summary>One member per throw shape: string, int32, int64, bool, double, DateTime, Guid, decimal,
/// enum, collection element, dictionary value, plus the object-level and document-level messages.</summary>
public class G3ColdModel
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public long Big { get; set; }
    public bool Flag { get; set; }
    public double Ratio { get; set; }
    public DateTime When { get; set; }
    public Guid Id { get; set; }
    public decimal Price { get; set; }
    public DayOfWeek Day { get; set; }
    public List<int> Items { get; set; } = new();
    public Dictionary<string, string> Map { get; set; } = new();
}

public class ColdThrowShapeTests
{
    [Test]
    public async Task DeserializerCallsTheColdHelper_InsteadOfInterpolating()
    {
        var files = RunGenerator(ColdShapeSource);
        var streaming = files.Single(f => f.Contains("DeserializeStreaming(ref JsonReader"));

        // the helper is emitted once, marked NoInlining, and it is what builds the message
        await Assert.That(streaming).Contains("MethodImplOptions.NoInlining");
        await Assert.That(streaming).Contains("throw __PicoJsonCold.Err(");

        // …and no deserializer body interpolates the reader's offset into a throw any more
        await Assert
            .That(streaming)
            .DoesNotContain(
                "throw new System.FormatException($\"Expected String value for property"
            );
        await Assert.That(streaming).DoesNotContain("at offset {reader.BytesConsumed}");

        // the two message shapes that also interpolate a VALUE still go through the helper
        await Assert.That(streaming).Contains("\" but found \" + reader.TokenType");
        await Assert.That(streaming).Contains("Encoding.UTF8.GetString(propNameSpan)");
    }

    /// <summary>
    /// The helper must never be inlined back into the caller: inlining it would rebuild the very
    /// stack frame this change removes. NoInlining is asserted as text because the generated code is
    /// the artifact under test.
    /// </summary>
    [Test]
    public async Task ColdHelperIsNoInlining()
    {
        var files = RunGenerator(ColdShapeSource);
        var withHelper = files.Single(f => f.Contains("__PicoJsonCold"));
        var helperAt = withHelper.IndexOf(
            "file static class __PicoJsonCold",
            StringComparison.Ordinal
        );
        await Assert.That(helperAt).IsGreaterThan(0);
        var helper = withHelper[helperAt..];
        await Assert.That(helper).Contains("MethodImplOptions.NoInlining");
        await Assert.That(helper).Contains("internal static System.FormatException Err(");
        // both overloads: with and without a constant suffix
        await Assert.That(helper).Contains("string suffix)");
    }

    private static string[] RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "ColdThrowAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        return driver
            .RunGenerators(compilation)
            .GetRunResult()
            .Results.SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString())
            .ToArray();
    }

    private const string ColdShapeSource = """
        using System.Collections.Generic;
        using PicoJetson;

        public class ColdShape
        {
            public string Name { get; set; } = "";
            public int Count { get; set; }
            public List<int> Items { get; set; } = new();
        }

        public static class ColdShapeProbe
        {
            public static ColdShape? Run(byte[] data) => JsonSerializer.Deserialize<ColdShape>(data);
        }
        """;

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
            Path.Combine(
                AppContext.BaseDirectory,
                typeof(JsonSerializer).Assembly.GetName().Name + ".dll"
            )
        );
        yield return MetadataReference.CreateFromFile(
            Path.Combine(
                AppContext.BaseDirectory,
                typeof(PicoSerDe.Core.SerializerExtensions).Assembly.GetName().Name + ".dll"
            )
        );
    }
}

public class ColdThrowMessageTests
{
    /// <summary>
    /// Every message a deserializer can raise for one document, with its offset, frozen. This is the
    /// pre-change text captured from the build before the cold helper existed; if a single character
    /// moves, this fails.
    /// </summary>
    private const string Expected = """
        string-value: FormatException: Expected String value for property "Name" at offset 9
        int32-value: FormatException: Expected an integer value for property "Count" at offset 12
        int64-value: FormatException: Expected a 64-bit integer value for property "Big" at offset 10
        bool-value: FormatException: Expected a boolean value for property "Flag" at offset 11
        double-value: FormatException: Expected a floating-point number value for property "Ratio" at offset 12
        datetime-value: FormatException: Expected a DateTime value for property "When" at offset 11
        guid-value: FormatException: Expected a Guid value for property "Id" at offset 12
        decimal-value: FormatException: Expected a decimal number value for property "Price" at offset 12
        enum-value: FormatException: Expected an enum value value for property "Day" at offset 10
        element-value: FormatException: Expected an integer element at offset 15
        dict-value: OK Name= Count=0 Big=0 Flag=False Items=0 Map=1
        unexpected-member: FormatException: Unexpected property 'Zzz' at offset 8
        not-an-object: FormatException: Expected a JSON object at offset 1 but found ArrayStart.
        trailing-data: FormatException: Unexpected byte 0x78 at offset 12
        empty: FormatException: Expected a JSON object at offset 0 but found None.
        trailing-value: FormatException: Unexpected data after the document at offset 13
        items-not-array: OK Name= Count=0 Big=0 Flag=False Items=0 Map=0
        map-not-object: OK Name= Count=0 Big=0 Flag=False Items=0 Map=0
        int64-fallback: OverflowException: Arithmetic operation resulted in an overflow.
        """;

    private static readonly (string Name, byte[] Doc, JsonOptions? Opts)[] Cases =
    [
        ("string-value", Bytes("""{"Name":1}"""), null),
        ("int32-value", Bytes("""{"Count":"x"}"""), null),
        ("int64-value", Bytes("""{"Big":"x"}"""), null),
        ("bool-value", Bytes("""{"Flag":"x"}"""), null),
        ("double-value", Bytes("""{"Ratio":"x"}"""), null),
        ("datetime-value", Bytes("""{"When":"x"}"""), null),
        ("guid-value", Bytes("""{"Id":"nope"}"""), null),
        ("decimal-value", Bytes("""{"Price":"x"}"""), null),
        ("enum-value", Bytes("""{"Day":"x"}"""), null),
        ("element-value", Bytes("""{"Items":[1,"x"]}"""), null),
        ("dict-value", Bytes("""{"Map":{"a":1}}"""), null),
        ("unexpected-member", Bytes("""{"Zzz":1}"""), Strict),
        ("not-an-object", Bytes("[1,2]"), null),
        ("trailing-data", Bytes("""{"Count":1} x"""), null),
        ("empty", Array.Empty<byte>(), null),
        ("trailing-value", Bytes("""{"Count":1} 7"""), null),
        ("items-not-array", Bytes("""{"Items":1}"""), null),
        ("map-not-object", Bytes("""{"Map":1}"""), null),
        ("int64-fallback", Bytes("""{"Count":3000000000}"""), null),
    ];

    private static JsonOptions Strict =>
        new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Test]
    public async Task ColdThrowMessages_AreFrozen()
    {
        var actual = string.Join("\n", Cases.Select(c => $"{c.Name}: {Outcome(c.Doc, c.Opts)}"));
        await Assert.That(actual).IsEqualTo(Expected.ReplaceLineEndings("\n"));
    }

    private static string Outcome(byte[] doc, JsonOptions? opts)
    {
        try
        {
            var v = JsonSerializer.Deserialize<G3ColdModel>(doc, opts);
            return "OK "
                + (
                    v is null
                        ? "null"
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"Name={v.Name} Count={v.Count} Big={v.Big} Flag={v.Flag} Items={v.Items.Count} Map={v.Map.Count}"
                        )
                );
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
