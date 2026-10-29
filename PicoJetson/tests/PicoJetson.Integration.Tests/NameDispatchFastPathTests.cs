using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

// ── P0-1: read-side property-name dispatch ──
//
// The deserializers used to bind a property name with an O(n) chain of
// case-insensitive byte comparisons (`TextHelpers.Eq(…, !caseInsensitive)`), i.e. O(n²)
// comparisons per object. P0-1 puts an exact-match fast path in front:
//
//     int __mi = -1;
//     switch (propNameSpan.Length)
//     {
//         case 4:
//             if (propNameSpan.SequenceEqual("Name"u8))
//                 __mi = 1;
//             break;
//     }
//     if (__mi < 0 && (reader.Options?.PropertyNameCaseInsensitive ?? true))
//     {
//         if (TextHelpers.Eq(propNameSpan, "Name"u8))      // ← the old chain, kept
//             __mi = 1;
//     }
//     if (__mi == 1) { … }
//
// The two halves of this file are the proof that the change is behaviour-free:
//   * NameDispatchShapeTests     — the generated file must contain BOTH halves, so the
//                                  fallback cannot be deleted by a later "cleanup".
//   * NameDispatchEquivalenceTests — the same document parsed through the fast path and
//                                  through the fallback path must produce the same object,
//                                  wrong-case documents must still bind by default, and
//                                  PropertyNameCaseInsensitive = false must still not bind
//                                  them (the fast path must not have loosened matching).
//
// The models mirror the two benchmark shapes the fix was measured on: `TenInts`
// (10 one-character int properties) and `LargeFlat` (30 properties: strings, ints,
// bools, doubles).

/// <summary>Ten one-character int properties — the `TenInts` benchmark shape.</summary>
public class P01TenInts
{
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public int D { get; set; }
    public int E { get; set; }
    public int F { get; set; }
    public int G { get; set; }
    public int H { get; set; }
    public int I { get; set; }
    public int J { get; set; }
}

/// <summary>30 properties — the `LargeFlat50` benchmark shape (same member kinds).</summary>
public class P01LargeFlat
{
    public string F01 { get; set; } = "";
    public string F02 { get; set; } = "";
    public string F03 { get; set; } = "";
    public string F04 { get; set; } = "";
    public string F05 { get; set; } = "";
    public string F06 { get; set; } = "";
    public string F07 { get; set; } = "";
    public string F08 { get; set; } = "";
    public string F09 { get; set; } = "";
    public string F10 { get; set; } = "";
    public int N01 { get; set; }
    public int N02 { get; set; }
    public int N03 { get; set; }
    public int N04 { get; set; }
    public int N05 { get; set; }
    public int N06 { get; set; }
    public int N07 { get; set; }
    public int N08 { get; set; }
    public int N09 { get; set; }
    public int N10 { get; set; }
    public bool B01 { get; set; }
    public bool B02 { get; set; }
    public bool B03 { get; set; }
    public bool B04 { get; set; }
    public bool B05 { get; set; }
    public double D01 { get; set; }
    public double D02 { get; set; }
    public double D03 { get; set; }
    public double D04 { get; set; }
    public double D05 { get; set; }
}

/// <summary>
/// Two JSON names that differ only by case. This is the one shape where an exact match
/// must NOT outrank an earlier case-insensitive one, so the generator keeps the old chain
/// verbatim for it (see <c>HasCaseInsensitiveNameCollision</c>).
/// </summary>
public class P01CaseOnlyNames
{
    [JsonPropertyName("id")]
    public int First { get; set; }

    [JsonPropertyName("ID")]
    public int Second { get; set; }
}

/// <summary>
/// Non-ASCII compiled names: their first UTF-8 byte is a lead byte (0xC3…), not the low
/// byte of the first char, so the first-byte bucket has to come from the ENCODING — keyed
/// on the char it would never match and case-sensitive binding would break for them.
/// </summary>
public class P01NonAsciiNames
{
    [JsonPropertyName("Über")]
    public int Ueber { get; set; }

    // Same UTF-8 length as "Über" (five bytes) and the same 0xC3 lead byte, so both land
    // in one length bucket that has to be split on that lead byte.
    [JsonPropertyName("Ähnl")]
    public int Aehnl { get; set; }

    [JsonPropertyName("姓名")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Id")]
    public int Id { get; set; }
}

/// <summary>
/// A polymorphic pair: its dispatch has two more exact-compare/case-insensitive-fallback
/// sites — the discriminator value and the discriminator property name.
/// </summary>
[PicoDerivedType(typeof(P01Dog), "dog")]
public abstract class P01Animal { }

public class P01Dog : P01Animal
{
    public string Name { get; set; } = "";
}

public class NameDispatchEquivalenceTests
{
    private static readonly byte[] TenIntsExact = Encoding.UTF8.GetBytes(
        """{"A":1,"B":2,"C":3,"D":4,"E":5,"F":6,"G":7,"H":8,"I":9,"J":10}"""
    );

    // Every name differs from the compiled one by case only: no member can be resolved by
    // the exact fast path, so the whole object is bound by the fallback chain.
    private static readonly byte[] TenIntsWrongCase = Encoding.UTF8.GetBytes(
        """{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6,"g":7,"h":8,"i":9,"j":10}"""
    );

    private static readonly byte[] LargeFlatExact = Encoding.UTF8.GetBytes(
        """
        {"F01":"v01","F02":"v02","F03":"v03","F04":"v04","F05":"v05","F06":"v06","F07":"v07","F08":"v08","F09":"v09","F10":"v10",
         "N01":1,"N02":2,"N03":3,"N04":4,"N05":5,"N06":6,"N07":7,"N08":8,"N09":9,"N10":10,
         "B01":true,"B02":false,"B03":true,"B04":false,"B05":true,
         "D01":1.5,"D02":2.5,"D03":3.5,"D04":4.5,"D05":5.5}
        """
    );

    private static readonly byte[] LargeFlatWrongCase = Encoding.UTF8.GetBytes(
        """
        {"f01":"v01","f02":"v02","f03":"v03","f04":"v04","f05":"v05","f06":"v06","f07":"v07","f08":"v08","f09":"v09","f10":"v10",
         "n01":1,"n02":2,"n03":3,"n04":4,"n05":5,"n06":6,"n07":7,"n08":8,"n09":9,"n10":10,
         "b01":true,"b02":false,"b03":true,"b04":false,"b05":true,
         "d01":1.5,"d02":2.5,"d03":3.5,"d04":4.5,"d05":5.5}
        """
    );

    // Half the names exactly as compiled (fast path) and half differently cased (fallback):
    // one object where both halves of the dispatch resolve members.
    private static readonly byte[] LargeFlatMixedCase = Encoding.UTF8.GetBytes(
        """
        {"F01":"v01","F02":"v02","F03":"v03","F04":"v04","F05":"v05","F06":"v06","F07":"v07","F08":"v08","F09":"v09","F10":"v10",
         "n01":1,"n02":2,"n03":3,"n04":4,"n05":5,"n06":6,"n07":7,"n08":8,"n09":9,"n10":10,
         "B01":true,"B02":false,"B03":true,"B04":false,"B05":true,
         "d01":1.5,"d02":2.5,"d03":3.5,"d04":4.5,"d05":5.5}
        """
    );

    /// <summary>Case-insensitivity ON — the default. Fast path + fallback are both live.</summary>
    private static JsonOptions Default => new();

    /// <summary>Case-sensitivity ON — the fast path is the only path.</summary>
    private static JsonOptions CaseSensitive => new() { PropertyNameCaseInsensitive = false };

    /// <summary>Case-sensitive and strict: an unmatched name must throw, not be skipped.</summary>
    private static JsonOptions CaseSensitiveStrict =>
        new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

    // ── TenInts ──

    [Test]
    public async Task TenInts_ExactCase_FastPathAndFallbackAgree()
    {
        var expected = TenInts(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

        // Fast path: every name equals its compiled name, so nothing reaches the fallback.
        var fast = JsonSerializer.Deserialize<P01TenInts>(TenIntsExact, Default);

        // Fallback only: every name differs by case, so the exact switch misses them all
        // and the pre-P0-1 case-insensitive chain binds the members.
        var fallback = JsonSerializer.Deserialize<P01TenInts>(TenIntsWrongCase, Default);

        // Case-sensitive option with an exact document: the fast path alone must be enough.
        var caseSensitive = JsonSerializer.Deserialize<P01TenInts>(TenIntsExact, CaseSensitive);

        await AssertWireIdentical(expected, fast!);
        await AssertWireIdentical(expected, fallback!);
        await AssertWireIdentical(expected, caseSensitive!);
        await AssertWireIdentical(fast!, fallback!);
    }

    [Test]
    public async Task TenInts_WrongCase_StillBinds_ByDefault()
    {
        var bound = JsonSerializer.Deserialize<P01TenInts>(TenIntsWrongCase, Default)!;
        await Assert.That(bound.J).IsEqualTo(10);
        await Assert.That(bound.A).IsEqualTo(1);
    }

    [Test]
    public async Task TenInts_WrongCase_DoesNotBind_WhenCaseSensitive()
    {
        // Skip (the default unmapped-member policy): the members keep their defaults,
        // which is exactly what the old chain did once case-insensitivity was off.
        var bound = JsonSerializer.Deserialize<P01TenInts>(TenIntsWrongCase, CaseSensitive)!;
        await AssertWireIdentical(new P01TenInts(), bound);
    }

    [Test]
    public async Task TenInts_WrongCase_Throws_WhenCaseSensitiveAndStrict()
    {
        await Assert
            .That(() =>
                JsonSerializer.Deserialize<P01TenInts>(TenIntsWrongCase, CaseSensitiveStrict)
            )
            .Throws<FormatException>();
    }

    // ── LargeFlat (30 properties) ──

    [Test]
    public async Task LargeFlat_ExactCase_FastPathAndFallbackAgree()
    {
        var expected = LargeFlat();

        var fast = JsonSerializer.Deserialize<P01LargeFlat>(LargeFlatExact, Default);
        var fallback = JsonSerializer.Deserialize<P01LargeFlat>(LargeFlatWrongCase, Default);
        var caseSensitive = JsonSerializer.Deserialize<P01LargeFlat>(LargeFlatExact, CaseSensitive);

        await AssertWireIdentical(expected, fast!);
        await AssertWireIdentical(expected, fallback!);
        await AssertWireIdentical(expected, caseSensitive!);
        await AssertWireIdentical(fast!, fallback!);
    }

    [Test]
    public async Task LargeFlat_MixedCase_BindsEveryMember()
    {
        // Half the object resolves through the exact switch, half through the fallback.
        var mixed = JsonSerializer.Deserialize<P01LargeFlat>(LargeFlatMixedCase, Default);
        await AssertWireIdentical(LargeFlat(), mixed!);
    }

    [Test]
    public async Task LargeFlat_WrongCase_DoesNotBind_WhenCaseSensitive()
    {
        var bound = JsonSerializer.Deserialize<P01LargeFlat>(LargeFlatWrongCase, CaseSensitive)!;
        await AssertWireIdentical(new P01LargeFlat(), bound);
    }

    [Test]
    public async Task LargeFlat_WrongCase_Throws_WhenCaseSensitiveAndStrict()
    {
        await Assert
            .That(() =>
                JsonSerializer.Deserialize<P01LargeFlat>(LargeFlatWrongCase, CaseSensitiveStrict)
            )
            .Throws<FormatException>();
    }

    // ── The one shape that keeps the old chain: names differing only by case ──

    [Test]
    public async Task CaseOnlyNames_KeepDeclarationOrderPrecedence()
    {
        // "ID" matches the first property case-insensitively ("id"), so the OLD chain set
        // First — and it must still do so, even though "ID" equals the second property's
        // compiled name exactly. The generator emits no fast path for this type.
        var bound = JsonSerializer.Deserialize<P01CaseOnlyNames>(
            """{"ID":2}"""u8,
            new JsonOptions()
        )!;
        await Assert.That(bound.First).IsEqualTo(2);
        await Assert.That(bound.Second).IsEqualTo(0);
    }

    // ── Non-ASCII compiled names (the first-byte bucket is a UTF-8 lead byte) ──

    [Test]
    public async Task NonAsciiNames_BindExactly_WhenCaseSensitive()
    {
        var json = Encoding.UTF8.GetBytes("""{"Über":1,"Ähnl":2,"姓名":"Z","Id":3}""");
        var bound = JsonSerializer.Deserialize<P01NonAsciiNames>(json, CaseSensitive)!;
        await Assert.That(bound.Ueber).IsEqualTo(1);
        await Assert.That(bound.Aehnl).IsEqualTo(2);
        await Assert.That(bound.Name).IsEqualTo("Z");
        await Assert.That(bound.Id).IsEqualTo(3);
    }

    [Test]
    public async Task NonAsciiNames_StillBindCaseInsensitively()
    {
        // The ASCII fold applies to the letters around the non-ASCII ones: "ÜBER" matches
        // "Über" through the fallback chain (ÜBER's Ü is unchanged by the fold).
        var json = Encoding.UTF8.GetBytes("""{"ÜBER":1,"姓名":"Z","id":3}""");
        var bound = JsonSerializer.Deserialize<P01NonAsciiNames>(json, Default)!;
        await Assert.That(bound.Ueber).IsEqualTo(1);
        await Assert.That(bound.Id).IsEqualTo(3);
    }

    // ── Polymorphic dispatch (discriminator value / property name) ──

    [Test]
    public async Task Poly_DiscriminatorValue_StillMatchesCaseInsensitively()
    {
        // Discriminators are matched case-insensitively today (Eq's third argument is
        // `false`), so the exact compare in front of it must keep the fold as its fallback.
        var json = Encoding.UTF8.GetBytes("""{"$type":"DOG","Name":"Rex"}""");
        var animal = JsonSerializer.Deserialize<P01Animal>(json, Default);
        await Assert.That(animal).IsTypeOf<P01Dog>();
        await Assert.That(((P01Dog)animal!).Name).IsEqualTo("Rex");
    }

    [Test]
    public async Task Poly_DiscriminatorPropertyName_StillMatchesCaseInsensitively()
    {
        var json = Encoding.UTF8.GetBytes("""{"$TYPE":"dog","Name":"Rex"}""");
        var animal = JsonSerializer.Deserialize<P01Animal>(json, Default);
        await Assert.That(animal).IsTypeOf<P01Dog>();
    }

    [Test]
    public async Task Poly_MemberNameInsideBranch_StillMatchesCaseInsensitively()
    {
        var json = Encoding.UTF8.GetBytes("""{"$type":"dog","name":"Rex"}""");
        var animal = JsonSerializer.Deserialize<P01Animal>(json, Default);
        await Assert.That(animal).IsTypeOf<P01Dog>();
        await Assert.That(((P01Dog)animal!).Name).IsEqualTo("Rex");
    }

    // ── helpers ──

    private static P01TenInts TenInts(
        int a,
        int b,
        int c,
        int d,
        int e,
        int f,
        int g,
        int h,
        int i,
        int j
    ) =>
        new()
        {
            A = a,
            B = b,
            C = c,
            D = d,
            E = e,
            F = f,
            G = g,
            H = h,
            I = i,
            J = j,
        };

    private static P01LargeFlat LargeFlat() =>
        new()
        {
            F01 = "v01",
            F02 = "v02",
            F03 = "v03",
            F04 = "v04",
            F05 = "v05",
            F06 = "v06",
            F07 = "v07",
            F08 = "v08",
            F09 = "v09",
            F10 = "v10",
            N01 = 1,
            N02 = 2,
            N03 = 3,
            N04 = 4,
            N05 = 5,
            N06 = 6,
            N07 = 7,
            N08 = 8,
            N09 = 9,
            N10 = 10,
            B01 = true,
            B02 = false,
            B03 = true,
            B04 = false,
            B05 = true,
            D01 = 1.5,
            D02 = 2.5,
            D03 = 3.5,
            D04 = 4.5,
            D05 = 5.5,
        };

    /// <summary>
    /// Compares two instances by their serialized wire form, so a single unbound member
    /// shows up as a difference (the deserializers must not be assumed to be right).
    /// </summary>
    private static async Task AssertWireIdentical<T>(T actual, T expected)
    {
        var actualWire = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(actual!));
        var expectedWire = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(expected!));
        await Assert.That(actualWire).IsEqualTo(expectedWire);
    }
}

/// <summary>
/// Guards the SHAPE of the generated dispatch, not its behaviour: the fast path and the
/// case-insensitive fallback must both be emitted, in the same file. Without this, a later
/// "the fallback is dead code" cleanup would silently drop case-insensitive matching for
/// every differently-cased document — and only the equivalence tests above would notice,
/// which is exactly one commit too late.
/// </summary>
public class NameDispatchShapeTests
{
    [Test]
    public async Task GeneratedDispatch_HasFastPathAndCaseInsensitiveFallback()
    {
        var files = RunGenerator(ShapeSource);

        // The streaming deserializer (every Deserialize<T>(span) goes through it).
        var streaming = files.Single(f => f.Contains("DeserializeStreaming(ref JsonReader"));
        await AssertFallbackIsKept(streaming, "propNameSpan");

        // The nested-object helper (`{Type}JsonInner.Deserialize(ref reader)`).
        var inner = files.Single(f =>
            f.Contains("JsonInner") && f.Contains("var __n = reader.GetStringRaw();")
        );
        await AssertFallbackIsKept(inner, "__n");
    }

    private static async Task AssertFallbackIsKept(string generatedFile, string nameVar)
    {
        // Fast path: a length switch dispatching exact span compares.
        await Assert.That(generatedFile).Contains($"switch ({nameVar}.Length)");
        await Assert.That(generatedFile).Contains($"{nameVar}.SequenceEqual(");

        // Fallback: the pre-P0-1 chain, still guarded by the case-insensitivity option — now the
        // once-per-document local json-gen3 #4 hoists above the property loop.
        await Assert.That(generatedFile).Contains("TextHelpers.Eq(");
        await Assert
            .That(generatedFile)
            .Contains("bool __ci = reader.Options?.PropertyNameCaseInsensitive ?? true;");
        await Assert.That(generatedFile).Contains("if (__mi < 0 && __ci)");
    }

    private static string[] RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "NameDispatchShapeAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return result
            .Results.SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString())
            .ToArray();
    }

    private const string ShapeSource = """
        using PicoJetson;

        public class NameShapeInner
        {
            public int Value { get; set; }
            public string Label { get; set; } = "";
        }

        public class NameShapeOuter
        {
            public NameShapeInner Inner { get; set; } = new();
            public int Count { get; set; }
        }

        [PicoSerializable]
        [PicoPolymorphic(TypeDiscriminatorPropertyName = "kind")]
        [PicoDerivedType(typeof(NameShapeDog), "dog")]
        public abstract class NameShapeAnimal { }

        public class NameShapeDog : NameShapeAnimal
        {
            public string Name { get; set; } = "";
        }

        public static class NameShapeProbe
        {
            public static byte[] Run() => JsonSerializer.SerializeToUtf8Bytes(new NameShapeOuter());

            public static byte[] RunPoly() =>
                JsonSerializer.SerializeToUtf8Bytes<NameShapeAnimal>(new NameShapeDog());
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
