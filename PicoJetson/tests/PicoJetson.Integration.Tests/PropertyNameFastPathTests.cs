using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

// ── P0-2: write-side property names, compiled at generation time ──
//
// The serializers used to re-encode every property name on every call:
//
//     var __name_Name = jw.Options?.PropertyNamingPolicy?.ConvertName("Name") ?? "Name";
//     jw.WritePropertyName(__name_Name);      // → GetMaxByteCount + stackalloc + GetBytes + escape scan
//
// P0-2 emits the compiled UTF-8 form instead and only takes the ConvertName detour when a
// naming policy is actually configured:
//
//     private static global::System.ReadOnlySpan<byte> PropName_Name => "Name"u8;
//     …
//     var __policy = jw.Options?.PropertyNamingPolicy;
//     if (__policy is null)
//         jw.WritePropertyName(PropName_Name);                                 // no transcode at all
//     else
//         jw.WritePropertyName(__policy.ConvertName("Name") ?? "Name");        // the old path, verbatim
//
// `WritePropertyName(ReadOnlySpan<byte>)` already existed and is the single exit for names:
// it still runs the escape scan (and still escapes what needs escaping), so nothing about
// JSON escaping moves. The two halves of this file are the proof that the change is
// behaviour-free:
//   * PropertyNameShapeTests        — the generated code must contain BOTH halves, so neither
//                                     the compiled constant nor the policy fallback can be
//                                     dropped by a later "cleanup".
//   * PropertyNameEquivalenceTests  — three name sources (no policy, CamelCase, a custom
//                                     policy) each produce the exact bytes the pre-P0-2 code
//                                     produced, including the `?? name` fallback for a policy
//                                     that returns null and the escaping of names that need it.

/// <summary>The `Simple POCO` benchmark shape: one string, one int.</summary>
public class P02Simple
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

/// <summary>Name sources that are not "plain ASCII without escapes".</summary>
public class P02EscapableNames
{
    [JsonPropertyName("he\"llo")]
    public int Quote { get; set; }

    [JsonPropertyName("tab\tname")]
    public int Tab { get; set; }

    [JsonPropertyName("ctl\u0001name")]
    public int Control { get; set; }

    [JsonPropertyName("Über")]
    public int Ueber { get; set; }

    [JsonPropertyName("姓名")]
    public int Name { get; set; }
}

/// <summary>A skippable property plus a normal one — the ignore-condition guard owns the name write.</summary>
public class P02Guard
{
    public string? Maybe { get; set; }
    public int Value { get; set; }
}

/// <summary>Shouty policy: proves the fallback really goes through <c>ConvertName</c>.</summary>
internal sealed class P02UpperPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => name.ToUpperInvariant();
}

/// <summary>
/// A policy that returns null. The pre-P0-2 code was
/// <c>ConvertName(name) ?? name</c>, so the compiled name was used — that fallback is part
/// of the observable behaviour and has to survive.
/// </summary>
internal sealed class P02NullPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => null!;
}

public class PropertyNameEquivalenceTests
{
    private static readonly P02Simple Simple = new() { Name = "Alice", Age = 42 };

    private static string Serialize(P02Simple value, JsonOptions? options = null) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(value, options));

    // ── 1. no policy: the compiled names ──

    [Test]
    public async Task NoPolicy_WritesTheCompiledNames()
    {
        // The exact bytes the pre-P0-2 code produced: names came from "Name"/"Age" literals.
        await Assert.That(Serialize(Simple)).IsEqualTo("""{"Name":"Alice","Age":42}""");
    }

    [Test]
    public async Task NoPolicy_IsAlsoTheDefaultOptionsInstance()
    {
        // Same document whether options are absent or an empty JsonOptions is passed.
        await Assert.That(Serialize(Simple, new JsonOptions())).IsEqualTo(Serialize(Simple));
    }

    // ── 2. CamelCase: the fallback path ──

    [Test]
    public async Task CamelCasePolicy_WritesTheConvertedNames()
    {
        var options = new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Pre-P0-2 bytes: ConvertName("Name") == "name", ConvertName("Age") == "age".
        await Assert.That(Serialize(Simple, options)).IsEqualTo("""{"name":"Alice","age":42}""");
    }

    // ── 3. a custom policy: names that exist nowhere in the generated file ──

    [Test]
    public async Task CustomPolicy_WritesTheConvertedNames()
    {
        var options = new JsonOptions { PropertyNamingPolicy = new P02UpperPolicy() };

        await Assert.That(Serialize(Simple, options)).IsEqualTo("""{"NAME":"Alice","AGE":42}""");
    }

    // ── 4. the two halves agree when the policy is the identity ──

    [Test]
    public async Task IdentityPolicy_ProducesTheSameBytesAsNoPolicy()
    {
        // PascalCase.ConvertName returns the name unchanged, so the compiled constant and the
        // ConvertName result must serialize to byte-identical documents. This is the direct
        // "fast path == fallback" comparison, at the level where it can actually be seen.
        var identity = new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.PascalCase };

        await Assert.That(Serialize(Simple, identity)).IsEqualTo(Serialize(Simple));
    }

    // ── 5. `ConvertName(name) ?? name` — a null-returning policy keeps the compiled name ──

    [Test]
    public async Task NullReturningPolicy_FallsBackToTheCompiledName()
    {
        var options = new JsonOptions { PropertyNamingPolicy = new P02NullPolicy() };

        await Assert.That(Serialize(Simple, options)).IsEqualTo(Serialize(Simple));
    }

    // ── 6. escaping still happens on the compiled-constant path ──

    [Test]
    public async Task EscapableNames_AreStillEscaped_WithoutPolicy()
    {
        var model = new P02EscapableNames
        {
            Quote = 1,
            Tab = 2,
            Control = 3,
            Ueber = 4,
            Name = 5,
        };

        // The compiled UTF-8 constant goes through the same WriteQuotedString as before:
        // `"` → `\"`, TAB → `\t`, U+0001 → `\u0001`, and non-ASCII names stay UTF-8.
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(model));
        await Assert
            .That(json)
            .IsEqualTo("""{"he\"llo":1,"tab\tname":2,"ctl\u0001name":3,"Über":4,"姓名":5}""");
    }

    [Test]
    public async Task EscapableNames_AreStillEscaped_WithPolicy()
    {
        var model = new P02EscapableNames
        {
            Quote = 1,
            Tab = 2,
            Control = 3,
            Ueber = 4,
            Name = 5,
        };

        // The shouty policy leaves these names unchanged (no ASCII letters to fold), so the
        // fallback arm must escape exactly like the fast arm does.
        var json = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(
                model,
                new JsonOptions { PropertyNamingPolicy = new P02UpperPolicy() }
            )
        );
        await Assert
            .That(json)
            .IsEqualTo("""{"HE\"LLO":1,"TAB\tNAME":2,"CTL\u0001NAME":3,"ÜBER":4,"姓名":5}""");
    }

    // ── 7. the name write sits behind the ignore-condition guard; both arms stay inside it ──

    [Test]
    public async Task SkippedProperty_WritesNoName_OnEitherArm()
    {
        var options = new JsonOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var camel = new JsonOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        await Assert
            .That(
                Encoding.UTF8.GetString(
                    JsonSerializer.SerializeToUtf8Bytes(new P02Guard { Value = 7 }, options)
                )
            )
            .IsEqualTo("""{"Value":7}""");
        await Assert
            .That(
                Encoding.UTF8.GetString(
                    JsonSerializer.SerializeToUtf8Bytes(new P02Guard { Value = 7 }, camel)
                )
            )
            .IsEqualTo("""{"value":7}""");
        await Assert
            .That(
                Encoding.UTF8.GetString(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new P02Guard { Maybe = "x", Value = 7 },
                        camel
                    )
                )
            )
            .IsEqualTo("""{"maybe":"x","value":7}""");
    }
}

/// <summary>
/// Guards the SHAPE of the generated serializer: the compiled name constant and the policy
/// fallback must both be emitted. Without this a later "the fallback is dead code" cleanup
/// would silently make every naming policy a no-op — and the equivalence tests above would
/// then be pointing at a code path that no longer exists.
/// </summary>
public class PropertyNameShapeTests
{
    [Test]
    public async Task GeneratedSerializer_HasCompiledNameConstantAndPolicyFallback()
    {
        var files = RunGenerator(ShapeSource);

        var serializer = files.Single(f =>
            f.Contains("class P02ShapeJsonSer") && f.Contains("public static void Serialize(")
        );

        // The compiled UTF-8 constant, named after the property.
        await Assert
            .That(serializer)
            .Contains(
                """private static global::System.ReadOnlySpan<byte> PropName_Name => "Name"u8;"""
            );
        await Assert
            .That(serializer)
            .Contains(
                """private static global::System.ReadOnlySpan<byte> PropName_Age => "Age"u8;"""
            );

        // The policy is read once per document, not once per property.
        await Assert.That(serializer).Contains("var __policy = jw.Options?.PropertyNamingPolicy;");

        // Fast half: the compiled span, no transcode.
        await Assert.That(serializer).Contains("if (__policy is null)");
        await Assert.That(serializer).Contains("jw.WritePropertyName(PropName_Name);");

        // Fallback half: the pre-P0-2 ConvertName call, `?? name` included.
        await Assert
            .That(serializer)
            .Contains("""jw.WritePropertyName(__policy.ConvertName("Name") ?? "Name");""");

        // The per-property string local (and therefore the per-call GetMaxByteCount/GetBytes)
        // must be gone.
        await Assert.That(serializer).DoesNotContain("var __name_");
    }

    [Test]
    public async Task GeneratedSerializer_SkipsThePolicyLocal_WhenTheTypeHasNoProperties()
    {
        var files = RunGenerator(ShapeSource);

        var serializer = files.Single(f => f.Contains("class P02ShapeEmptyJsonSer"));

        // No names to write ⇒ no name constants and no unused local (CS0219 would fire in
        // every consumer project, and generated warnings are build failures downstream).
        await Assert.That(serializer).DoesNotContain("PropName_");
        await Assert.That(serializer).DoesNotContain("__policy");
    }

    private static string[] RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "PropertyNameShapeAsm",
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

        public class P02Shape
        {
            public string Name { get; set; } = "";
            public int Age { get; set; }
        }

        public class P02ShapeEmpty { }

        public static class P02ShapeProbe
        {
            public static byte[] Run() => JsonSerializer.SerializeToUtf8Bytes(new P02Shape());

            public static byte[] RunEmpty() =>
                JsonSerializer.SerializeToUtf8Bytes(new P02ShapeEmpty());
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
