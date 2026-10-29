using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

// ── json-gen3 #4: the case-insensitivity option is read once per document, not once per property ──
//
// The exact-match fast path misses for every name that differs by case, and the fallback that then
// binds it used to re-read the option for every property of the document:
//
//     while (true)                                   // once per property
//     {
//         …
//         if (__mi < 0 && (reader.Options?.PropertyNameCaseInsensitive ?? true)) { … }
//     }
//
// `Options` is a reader-wide setting, so hoisting it above the loop is behaviour-free — but only if
// the DEFAULT stays `true` (a null Options object must still mean case-insensitive: that is the
// repository's iron rule) and only if the fallback stays reachable.
//   * CaseInsensitiveHoistShapeTests      — the local must be a `bool __ci = … ?? true` declared
//                                           before the loop, and the guard must read the local.
//   * CaseInsensitiveHoistBehaviourTests  — the default still binds `{"name":…}` to `Name`, and
//                                           PropertyNameCaseInsensitive = false still refuses to.

/// <summary>Five properties — enough for the per-property read to be visible, few enough to read.</summary>
public class G3HoistModel
{
    public int Alpha { get; set; }
    public int Beta { get; set; }
    public int Gamma { get; set; }
    public int Delta { get; set; }
    public int Epsilon { get; set; }
}

public class CaseInsensitiveHoistShapeTests
{
    [Test]
    public async Task OptionIsReadOnce_BeforeThePropertyLoop()
    {
        var streaming = Streaming(HoistShapeSource);

        var read = streaming.IndexOf(
            "bool __ci = reader.Options?.PropertyNameCaseInsensitive ?? true;",
            StringComparison.Ordinal
        );
        await Assert.That(read).IsGreaterThan(0);

        // declared before the property loop, and the loop's fallback guard reads the local
        var loop = streaming.IndexOf("while (true)", StringComparison.Ordinal);
        await Assert.That(loop).IsGreaterThan(0);
        await Assert.That(read).IsLessThan(loop);
        await Assert.That(streaming).Contains("if (__mi < 0 && __ci)");

        // the inline expression must not survive anywhere else in the streaming method
        var occurrences = CountOccurrences(
            streaming,
            "reader.Options?.PropertyNameCaseInsensitive ?? true"
        );
        await Assert.That(occurrences).IsEqualTo(1);
    }

    /// <summary>
    /// The fallback is entered by a differently-cased name, so the "read once" local is only correct
    /// while it still means "case-insensitive". This is the shape half of that; the behaviour half is
    /// <see cref="CaseInsensitiveHoistBehaviourTests"/>.
    /// </summary>
    [Test]
    public async Task DefaultStaysTrue()
    {
        var options = new JsonOptions();
        await Assert.That(options.PropertyNameCaseInsensitive).IsTrue();
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    private static string Streaming(string source)
    {
        var compilation = CSharpCompilation.Create(
            "CaseInsensitiveHoistAsm",
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
            .Single(f => f.Contains("DeserializeStreaming(ref JsonReader"));
    }

    private const string HoistShapeSource = """
        using PicoJetson;

        public class HoistShape
        {
            public int Alpha { get; set; }
            public int Beta { get; set; }
            public int Gamma { get; set; }
            public int Delta { get; set; }
            public int Epsilon { get; set; }
        }

        public static class HoistShapeProbe
        {
            public static HoistShape? Run(byte[] data) => JsonSerializer.Deserialize<HoistShape>(data);
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

public class CaseInsensitiveHoistBehaviourTests
{
    [Test]
    public async Task WrongCase_StillBinds_ByDefault()
    {
        var v = JsonSerializer.Deserialize<G3HoistModel>(
            """{"alpha":1,"beta":2,"gamma":3,"delta":4,"epsilon":5}"""u8.ToArray()
        )!;
        await Assert.That(v.Alpha).IsEqualTo(1);
        await Assert.That(v.Beta).IsEqualTo(2);
        await Assert.That(v.Gamma).IsEqualTo(3);
        await Assert.That(v.Delta).IsEqualTo(4);
        await Assert.That(v.Epsilon).IsEqualTo(5);
    }

    [Test]
    public async Task WrongCase_StillBinds_WithAnExplicitDefaultOptionsObject()
    {
        var v = JsonSerializer.Deserialize<G3HoistModel>(
            """{"ALPHA":1,"BETA":2}"""u8.ToArray(),
            new JsonOptions()
        )!;
        await Assert.That(v.Alpha).IsEqualTo(1);
        await Assert.That(v.Beta).IsEqualTo(2);
    }

    [Test]
    public async Task WrongCase_DoesNotBind_WhenCaseSensitive()
    {
        var v = JsonSerializer.Deserialize<G3HoistModel>(
            """{"alpha":1,"beta":2}"""u8.ToArray(),
            new JsonOptions { PropertyNameCaseInsensitive = false }
        )!;
        await Assert.That(v.Alpha).IsEqualTo(0);
        await Assert.That(v.Beta).IsEqualTo(0);
    }

    /// <summary>Exact names keep binding case-sensitively — the fast path does not depend on the option.</summary>
    [Test]
    public async Task ExactCase_Binds_WhenCaseSensitive()
    {
        var v = JsonSerializer.Deserialize<G3HoistModel>(
            """{"Alpha":1,"Beta":2}"""u8.ToArray(),
            new JsonOptions { PropertyNameCaseInsensitive = false }
        )!;
        await Assert.That(v.Alpha).IsEqualTo(1);
        await Assert.That(v.Beta).IsEqualTo(2);
    }
}
