using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

// ── json-gen3 #1: the property-name fast path keys on three bytes at once ──
//
// Before: the exact-match fast path walked a switch on the name length, then a switch on the first
// byte, then up to ten SequenceEqual compares (F01…F10 all live in the "3 bytes, 'F'" bucket):
//
//     switch (propNameSpan.Length) { case 3: switch (propNameSpan[0]) { case (byte)'F': …10 compares… } }
//
// After: one packed 24-bit key — the first three name bytes — in a single switch. The confirmation
// that the key alone does not prove equality is the *other* half of the change: three bytes only
// pin the name when the length is also three, so a three-byte candidate is guarded by
// `Length == 3` and a longer one by `SequenceEqual` over the whole name.
//
//   * NameDispatchPackedShapeTests      — the generated file must contain the packed key and must
//                                         not fall back to the per-length switch for those names.
//   * NameDispatchPackedEquivalenceTests — the shapes that a key-only (or a wrongly confirmed)
//                                         dispatch would get wrong: names that share their first
//                                         three bytes, a four-byte name whose first three bytes are
//                                         a real property, short names, non-ASCII names, and the
//                                         case-insensitive fallback that must stay reachable.

/// <summary>
/// Names that collide three ways: the same packed key for a 5-byte and a 4-byte name, a 3-byte name
/// that is the four-byte name's prefix, one- and two-byte names (the short path), and two non-ASCII
/// names sharing their first three UTF-8 bytes (C3 9C 62).
/// </summary>
public class G3PackedNames
{
    public int Alpha { get; set; }
    public int Alpine { get; set; }
    public int Alps { get; set; }
    public int Abc { get; set; }
    public int Abcd { get; set; }
    public int Al { get; set; }
    public int A { get; set; }

    [JsonPropertyName("Über")]
    public int Ueber { get; set; }

    [JsonPropertyName("Übel")]
    public int Uebel { get; set; }
}

/// <summary>Every name is exactly three bytes — the `LargeFlat` benchmark shape.</summary>
public class G3ThreeByteNames
{
    public int F01 { get; set; }
    public int F02 { get; set; }
    public int N01 { get; set; }
}

/// <summary>No name is three bytes long — the fast path must keep the per-length switch.</summary>
public class G3ShortNames
{
    public int A { get; set; }
    public int B { get; set; }
    public int Ab { get; set; }
}

public class NameDispatchPackedShapeTests
{
    [Test]
    public async Task ThreeByteNames_UseOnePackedKeySwitch()
    {
        var streaming = Streaming(ThreeByteShape);

        // one 3-byte read packed into an int, one switch on it
        await Assert
            .That(streaming)
            .Contains(
                "int __nk = (propNameSpan[0] << 16) | (propNameSpan[1] << 8) | propNameSpan[2];"
            );
        await Assert.That(streaming).Contains("switch (__nk)");
        // "F01" = 0x46,0x30,0x31
        await Assert.That(streaming).Contains("case 0x463031:");

        // and no per-length bucket for those names any more
        await Assert.That(streaming).DoesNotContain("switch (propNameSpan.Length)");

        // the guard that makes a key equal to a name: without it a four-byte name whose first three
        // bytes are "F01" would bind F01.
        await Assert.That(streaming).Contains("if (propNameSpan.Length == 3)");
    }

    [Test]
    public async Task LongNames_ConfirmWithSequenceEqual()
    {
        // "Alpha"/"Alpine"/"Alps" share the packed key 0x416C70, so the key alone cannot decide:
        // every candidate longer than three bytes must be confirmed over its whole name.
        var generated = AllGenerated(MixedShape);
        var dispatch = generated.Single(f => f.Contains("switch (__nk)"));
        await Assert.That(dispatch).Contains("case 0x416C70:");
        await Assert.That(dispatch).Contains("propNameSpan.SequenceEqual(\"Alpha\"u8)");
        await Assert.That(dispatch).Contains("propNameSpan.SequenceEqual(\"Alpine\"u8)");
        await Assert.That(dispatch).Contains("propNameSpan.SequenceEqual(\"Alps\"u8)");
        // a three-byte candidate is confirmed by its length instead
        await Assert.That(dispatch).Contains("propNameSpan.Length == 3");
    }

    [Test]
    public async Task ShortNames_KeepThePerLengthSwitch()
    {
        var streaming = Streaming(ShortShape);
        await Assert.That(streaming).Contains("switch (propNameSpan.Length)");
        await Assert.That(streaming).DoesNotContain("int __nk =");
    }

    [Test]
    public async Task CaseInsensitiveFallbackIsStillEmitted()
    {
        var streaming = Streaming(MixedShape);
        await Assert.That(streaming).Contains("TextHelpers.Eq(");
        await Assert.That(streaming).Contains("if (__mi < 0 && ");
    }

    private static string Streaming(string source) =>
        AllGenerated(source).Single(f => f.Contains("DeserializeStreaming(ref JsonReader"));

    private static string[] AllGenerated(string source)
    {
        var compilation = CSharpCompilation.Create(
            "PackedNameDispatchAsm",
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

    private const string ThreeByteShape = """
        using PicoJetson;

        public class PackedShape3
        {
            public int F01 { get; set; }
            public int F02 { get; set; }
            public int N01 { get; set; }
        }

        public static class PackedShape3Probe
        {
            public static byte[] Run() => JsonSerializer.SerializeToUtf8Bytes(new PackedShape3());
        }
        """;

    private const string ShortShape = """
        using PicoJetson;

        public class PackedShapeShort
        {
            public int A { get; set; }
            public int B { get; set; }
            public int Ab { get; set; }
        }

        public static class PackedShapeShortProbe
        {
            public static byte[] Run() => JsonSerializer.SerializeToUtf8Bytes(new PackedShapeShort());
        }
        """;

    private const string MixedShape = """
        using PicoJetson;

        public class PackedShapeMixed
        {
            public int Alpha { get; set; }
            public int Alpine { get; set; }
            public int Alps { get; set; }
            public int Abc { get; set; }
            public int A { get; set; }
        }

        public static class PackedShapeMixedProbe
        {
            public static byte[] Run() => JsonSerializer.SerializeToUtf8Bytes(new PackedShapeMixed());
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

public class NameDispatchPackedEquivalenceTests
{
    private static JsonOptions Default => new();

    private static JsonOptions CaseSensitive => new() { PropertyNameCaseInsensitive = false };

    private static JsonOptions CaseSensitiveStrict =>
        new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

    /// <summary>
    /// The document binds every member, and the values prove WHICH member each name reached: a
    /// dispatch that mapped a name to its neighbour would put 1 into Alpine instead of Alpha.
    /// </summary>
    [Test]
    public async Task NamesSharingThreeBytes_BindTheirOwnMember()
    {
        var json = """{"Alpha":1,"Alpine":2,"Alps":3,"Abc":4,"Abcd":5,"Al":6,"A":7}"""u8.ToArray();
        var v = JsonSerializer.Deserialize<G3PackedNames>(json, CaseSensitive)!;

        await Assert.That(v.Alpha).IsEqualTo(1);
        await Assert.That(v.Alpine).IsEqualTo(2);
        await Assert.That(v.Alps).IsEqualTo(3);
        await Assert.That(v.Abc).IsEqualTo(4);
        await Assert.That(v.Abcd).IsEqualTo(5);
        await Assert.That(v.Al).IsEqualTo(6);
        await Assert.That(v.A).IsEqualTo(7);
    }

    /// <summary>
    /// Every three-byte name binds its own member: a dispatch that mapped a key to a neighbouring
    /// index (or dropped a byte of the key) would put these values one property to the left.
    /// </summary>
    [Test]
    public async Task ThreeByteNames_BindTheirOwnMember()
    {
        var json = """{"F01":1,"F02":2,"N01":3}"""u8.ToArray();
        var v = JsonSerializer.Deserialize<G3ThreeByteNames>(json, CaseSensitive)!;
        await Assert.That(v.F01).IsEqualTo(1);
        await Assert.That(v.F02).IsEqualTo(2);
        await Assert.That(v.N01).IsEqualTo(3);
    }

    /// <summary>
    /// A name whose first three bytes ARE a compiled three-byte name must not bind it: `F01x` is
    /// not `F01`. This is the exact hole a key-only dispatch (key → index, no length confirmation)
    /// leaves open, and the one the `Length == 3` guard closes.
    /// </summary>
    [Test]
    public async Task FourByteNameSharingAThreeBytePrefix_DoesNotBind()
    {
        var json = """{"F01x":9,"N01":2}"""u8.ToArray();

        // UnmappedMemberHandling.Disallow turns "not bound" into a throw; a key-only dispatch would
        // instead have silently bound F01 = 9 and returned.
        await Assert
            .That(() => JsonSerializer.Deserialize<G3ThreeByteNames>(json, CaseSensitiveStrict))
            .Throws<FormatException>();

        // …and with the default (skip) policy the member simply stays at its default.
        var v = JsonSerializer.Deserialize<G3ThreeByteNames>(json, CaseSensitive)!;
        await Assert.That(v.F01).IsEqualTo(0);
        await Assert.That(v.N01).IsEqualTo(2);
    }

    [Test]
    public async Task ThreeByteNamePrefix_DoesNotBindTheLongerProperty()
    {
        // "Alp" packs to the same key as "Alpha"/"Alpine"/"Alps" but equals none of them.
        var json = """{"Alp":9,"A":1}"""u8.ToArray();
        await Assert
            .That(() => JsonSerializer.Deserialize<G3PackedNames>(json, CaseSensitiveStrict))
            .Throws<FormatException>();
    }

    [Test]
    public async Task WrongCase_StillBindsThroughTheFallback()
    {
        var json =
            """{"alpha":11,"alpine":12,"alps":13,"abc":14,"abcd":15,"al":16,"a":17}"""u8.ToArray();
        var v = JsonSerializer.Deserialize<G3PackedNames>(json, Default)!;

        await Assert.That(v.Alpha).IsEqualTo(11);
        await Assert.That(v.Alpine).IsEqualTo(12);
        await Assert.That(v.Alps).IsEqualTo(13);
        await Assert.That(v.Abc).IsEqualTo(14);
        await Assert.That(v.Abcd).IsEqualTo(15);
        await Assert.That(v.Al).IsEqualTo(16);
        await Assert.That(v.A).IsEqualTo(17);
    }

    [Test]
    public async Task WrongCase_DoesNotBind_WhenCaseSensitive()
    {
        var json = """{"alpha":11,"a":17}"""u8.ToArray();
        var v = JsonSerializer.Deserialize<G3PackedNames>(json, CaseSensitive)!;
        await Assert.That(v.Alpha).IsEqualTo(0);
        await Assert.That(v.A).IsEqualTo(0);
    }

    [Test]
    public async Task NonAsciiNamesSharingThreeBytes_BindTheirOwnMember()
    {
        // "Über"/"Übel" are C3 9C 62 65 72 and C3 9C 62 65 6C — same packed key, same length.
        var json = """{"Über":1,"Übel":2}"""u8.ToArray();
        var v = JsonSerializer.Deserialize<G3PackedNames>(json, CaseSensitive)!;
        await Assert.That(v.Ueber).IsEqualTo(1);
        await Assert.That(v.Uebel).IsEqualTo(2);
    }

    [Test]
    public async Task UnknownName_IsSkipped_AndKnownOnesSurvive()
    {
        var json = """{"Zzz":1,"Alpha":2,"A":3}"""u8.ToArray();
        var v = JsonSerializer.Deserialize<G3PackedNames>(json, Default)!;
        await Assert.That(v.Alpha).IsEqualTo(2);
        await Assert.That(v.A).IsEqualTo(3);
    }

    [Test]
    public async Task ShortOnlyType_RoundTrips()
    {
        var json = """{"A":1,"B":2,"Ab":3}"""u8.ToArray();
        var v = JsonSerializer.Deserialize<G3ShortNames>(json, CaseSensitive)!;
        await Assert.That(v.A).IsEqualTo(1);
        await Assert.That(v.B).IsEqualTo(2);
        await Assert.That(v.Ab).IsEqualTo(3);
    }
}
