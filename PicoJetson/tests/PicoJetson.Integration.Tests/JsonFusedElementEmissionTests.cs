using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;

namespace PicoJetson.Tests;

// ── Models whose element type is `int`: the only element kind T15 fuses (spec §3.3 marks
//    `TryReadInt32` as JSON's single override; the other three fused reads are one-line
//    forwards, so emitting them would buy nothing but a golden diff). ──

public class IntElementListModel
{
    public List<int> Items { get; set; } = new();
}

public class IntElementArrayModel
{
    public int[] Items { get; set; } = [];
}

public class NullableIntElementListModel
{
    public List<int?> Items { get; set; } = new();
}

public class IntElementNestedListModel
{
    public List<List<int>> Rows { get; set; } = new();
}

public class IntElementDictListModel
{
    public Dictionary<string, List<int>> Map { get; set; } = new();
}

/// <summary>
/// Task T15 — the generator side of the fused read (plan §5 T15, spec §3.4.1 <b>E-3</b>, spec §3.4.3).
/// <para>
/// <b>Two halves, both needed.</b> The <b>emission guards</b> prove the fused shape is what the emitter
/// actually writes, and that the artefact still names the concrete reader (never a token-reader
/// interface). The <b>behaviour locks</b> prove the fused loop answers exactly what the old
/// <c>while (reader.Read() &amp;&amp; reader.TokenType != TokenType.ArrayEnd)</c> loop answered — including
/// the paths that only exist when a fused read <i>fails</i>: the end token, a chunk boundary where
/// <c>Read()</c> produced no token at all, a JSON <c>null</c> element, a number that does not fit
/// <see cref="int"/> (the old shape widens to <c>long</c> and narrows with <c>checked</c>), and plain
/// type mismatch.
/// </para>
/// <para>
/// <b>Why the behaviour locks cannot be red on their own.</b> The task's behaviour is unchanged by
/// design, so every one of them is green before the emitter is touched (plan §3.5). The red this task
/// must be able to show comes from the emission itself: make the emitted fused call read the wrong token
/// and these locks go red. That is the negative control reported with the task, and
/// <see cref="EmittedInt32ElementLoop_UsesTheFusedRead"/> is the assertion that is red <i>first</i>
/// (before the emitter change it pins a call that does not exist yet).
/// </para>
/// </summary>
public class JsonFusedElementEmissionTests
{
    // ══════════════════════════════════════════════════════════════════════════════════════
    //  A. emission guards (spec §3.4.3) — independent of what the runtime does
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Spec §3.4.3: the emitted text must name neither interface. Checked on the checked-in JSON
    /// baseline <b>and</b> on the emitter source, so a drift is caught even in the window before the
    /// baseline is regenerated (same two-surface discipline as T10's MessagePack guard).
    /// </summary>
    [Test]
    public async Task JsonBaseline_AndEmitter_NeverMentionATokenReaderInterface()
    {
        // Assembled from two halves so that a repo-wide grep for the banned names does not trip over
        // this file: the assertion is about artefacts and emitter source, not about the test itself.
        string[] banned = ["IToken" + "Reader", "IFusedToken" + "Reader"];

        var files = BaselineFiles();
        await Assert.That(files.Length).IsGreaterThanOrEqualTo(20);

        var offenders = files
            .Where(file => banned.Any(name => File.ReadAllText(file).Contains(name)))
            .Select(Path.GetFileName)
            .ToArray();
        await Assert.That(offenders).IsEmpty();

        var emitter = File.ReadAllText(
            Path.Combine(
                RepoRoot(),
                "PicoJetson",
                "src",
                "PicoJetson.Gen",
                "JsonSerializerGenerator.cs"
            )
        );
        var hits = banned.Where(emitter.Contains).ToArray();
        await Assert.That(hits).IsEmpty();
    }

    /// <summary>The concrete-reader advantage (spec §1.3, §3.4.3): every emitted file opens a <c>JsonReader</c>.</summary>
    [Test]
    public async Task JsonBaseline_UsesTheConcreteJsonReader()
    {
        var files = BaselineFiles();
        await Assert.That(files.Length).IsGreaterThanOrEqualTo(20);

        var withoutConcreteReader = files
            .Where(file => !File.ReadAllText(file).Contains("JsonReader"))
            .Select(Path.GetFileName)
            .ToArray();
        await Assert.That(withoutConcreteReader).IsEmpty();
    }

    /// <summary>
    /// <b>The E-3 emission, pinned as text.</b> Every <c>int</c> element is read by one fused call on the
    /// concrete reader, and the old two-step shape (<c>Read()</c> in the loop condition, then
    /// <c>TryGetInt32</c> in the body) is gone from the <c>int</c>-element sites.
    /// <para>
    /// This is the assertion that is <b>red before the emitter change</b> (assertion red, not compile
    /// red): the checked-in baseline has zero <c>TryReadInt32</c> calls, so the test names what the task
    /// is supposed to produce before it exists. The <i>absence</i> of the two-step body is pinned by
    /// <see cref="EmittedIntElementLoops_HaveNoTwoStepShapeLeft"/> instead of by a grep here, because the
    /// baseline also holds a dictionary value read (<c>Dictionary&lt;string,int&gt;</c>, which the plan
    /// keeps on the two-step path) that legitimately still calls <c>TryGetInt32</c>.
    /// </para>
    /// </summary>
    [Test]
    public async Task EmittedInt32ElementLoop_UsesTheFusedRead()
    {
        var files = BaselineFiles();
        await Assert.That(files.Length).IsGreaterThanOrEqualTo(20);

        var withFusedCall = files.Count(file =>
            File.ReadAllText(file).Contains("reader.TryReadInt32(out var ")
        );
        await Assert.That(withFusedCall).IsGreaterThanOrEqualTo(5);
    }

    /// <summary>
    /// The fused shape is emitted for <b>every</b> scalar element position the generator has — the
    /// property collection, the file-level <c>int[]</c>/<c>List&lt;int&gt;</c> entry point, the
    /// constructor-parameter collection, the extended collection (<c>HashSet&lt;int&gt;</c>) and a
    /// dictionary value that is a collection — and no <c>int</c> element body is left in the two-step
    /// shape. Asserted on the generator's real output (the probe baseline holds POCO properties only, so
    /// three of those five positions are unreachable from it).
    /// <para>
    /// <b>What is deliberately still two-step</b>: a nested list's <i>outer</i> loop (its element is a
    /// collection, not a scalar), the dictionary <c>PropertyName</c> loop, the polymorphic
    /// <c>object</c> walker and the streaming <i>top-level array</i> delegate — the last one because its
    /// loop answers a truncated element with a rewind-and-retry instead of an exit, which is a different
    /// failure contract from <b>E-2</b>'s.
    /// </para>
    /// </summary>
    [Test]
    public async Task EmittedIntElementLoops_HaveNoTwoStepShapeLeft()
    {
        var flat = ElementLoopBlocks(
            """
            using System.Collections.Generic;
            using PicoJetson;

            namespace MyApp
            {
                public class FlatInts
                {
                    public List<int> IntList { get; set; } = new();
                    public int[] IntArray { get; set; } = [];
                    public List<int?> NullableInts { get; set; } = new();
                    public HashSet<int> IntSet { get; set; } = new();
                    public Dictionary<string, List<int>> IntMap { get; set; } = new();
                }

                public class CtorInts(List<int> items)
                {
                    public List<int> Items { get; } = items;
                }

                public static class Probe
                {
                    public static object Run()
                    {
                        var flat = JsonSerializer.Deserialize<MyApp.FlatInts>("{}"u8);
                        var ctor = JsonSerializer.Deserialize<MyApp.CtorInts>("{}"u8);
                        var array = JsonSerializer.Deserialize<int[]>("[1]"u8);
                        var list = JsonSerializer.Deserialize<List<int>>("[1]"u8);
                        return (flat, ctor, array, list);
                    }
                }
            }
            """
        );

        var text = string.Join(Environment.NewLine, flat);
        await Assert.That(flat.Length).IsGreaterThanOrEqualTo(4);

        // One fused call per int element position, on the concrete reader.
        await Assert.That(text).Contains("reader.TryReadInt32(out var __elementValue)");
        await Assert.That(text).Contains("reader.TryReadInt32(out var __ev)");

        // …and not one int element body left on the two-step path.
        await Assert.That(text).DoesNotContain("TryGetInt32(out var __");
        await Assert
            .That(text)
            .DoesNotContain("while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)");
    }

    /// <summary>
    /// A nested list keeps its <i>outer</i> loop (the element is a collection) and fuses its innermost
    /// one — the scope line the plan draws at "嵌套容器/多态先不动".
    /// </summary>
    [Test]
    public async Task EmittedNestedIntList_FusesOnlyTheInnermostLoop()
    {
        var trees = ElementLoopBlocks(
            """
            using System.Collections.Generic;
            using PicoJetson;

            namespace MyApp
            {
                public class NestedInts
                {
                    public List<List<int>> Rows { get; set; } = new();
                }

                public static class Probe
                {
                    public static object Run() =>
                        JsonSerializer.Deserialize<MyApp.NestedInts>("{}"u8);
                }
            }
            """
        );

        var text = string.Join(Environment.NewLine, trees);
        await Assert.That(text).Contains("reader.TryReadInt32(out var __elementValue)");
        // The outer loop is still the end-token loop: exactly one left in this model.
        var outerLoops =
            text.Split("while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)").Length
            - 1;
        await Assert.That(outerLoops).IsEqualTo(1);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  B. behaviour locks — what the fused loop must answer
    // ══════════════════════════════════════════════════════════════════════════════════════

    [Test]
    [Arguments("""{"Items":[1,42,-7]}""", new[] { 1, 42, -7 })]
    [Arguments("""{"Items":[]}""", new int[0])]
    [Arguments("""{"Items":[-2147483648,2147483647,0]}""", new[] { int.MinValue, int.MaxValue, 0 })]
    [Arguments("""{ "Items" : [ 1 , 2 ] }""", new[] { 1, 2 })]
    public async Task ListOfIntProperty_ReadsTheValues(string json, int[] expected)
    {
        var result = JsonSerializer.Deserialize<IntElementListModel>(Encoding.UTF8.GetBytes(json));
        await Assert.That(result!.Items).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("""{"Items":[1,42,-7]}""", new[] { 1, 42, -7 })]
    [Arguments("""{"Items":[]}""", new int[0])]
    public async Task ArrayOfIntProperty_ReadsTheValues(string json, int[] expected)
    {
        var result = JsonSerializer.Deserialize<IntElementArrayModel>(Encoding.UTF8.GetBytes(json));
        await Assert.That(result!.Items).IsEquivalentTo(expected);
    }

    [Test]
    public async Task NullableIntElements_KeepNulls()
    {
        var result = JsonSerializer.Deserialize<NullableIntElementListModel>(
            """{"Items":[1,null,3]}"""u8.ToArray()
        );
        await Assert.That(result!.Items).IsEquivalentTo(new int?[] { 1, null, 3 });
    }

    [Test]
    public async Task NestedIntList_ReadsEveryLevel()
    {
        var result = JsonSerializer.Deserialize<IntElementNestedListModel>(
            """{"Rows":[[1,2],[3]]}"""u8.ToArray()
        );
        await Assert.That(result!.Rows.Count).IsEqualTo(2);
        await Assert.That(result.Rows[0]).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(result.Rows[1]).IsEquivalentTo(new[] { 3 });
    }

    [Test]
    public async Task DictionaryOfIntList_ReadsEveryValue()
    {
        var result = JsonSerializer.Deserialize<IntElementDictListModel>(
            """{"Map":{"a":[1,2],"b":[]}"""u8.ToArray()
        );
        await Assert.That(result!.Map["a"]).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(result.Map["b"]).IsEmpty();
    }

    [Test]
    public async Task TopLevelIntArray_AndList_ReadTheValues()
    {
        var array = JsonSerializer.Deserialize<int[]>("[1,42,-7]"u8.ToArray());
        await Assert.That(array).IsEquivalentTo(new[] { 1, 42, -7 });

        var list = JsonSerializer.Deserialize<List<int>>("[1,42,-7]"u8.ToArray());
        await Assert.That(list).IsEquivalentTo(new[] { 1, 42, -7 });
    }

    /// <summary>
    /// The end-of-data path of the fused read: <c>Read()</c> produced <b>no token at all</b>, so the fused
    /// read answers <c>false</c> with the previous element still current. The old <c>while</c> loop broke
    /// out on exactly that <c>false</c> — so a truncated collection yields the elements that were there,
    /// and it must not re-read the stale token (which would duplicate the last element) nor throw.
    /// </summary>
    [Test]
    [Arguments("{\"Items\":[1,2")]
    [Arguments("{\"Items\":[1,2 ")]
    [Arguments("{\"Items\":[")]
    public async Task TruncatedCollection_YieldsTheElementsThatWereThere(string json)
    {
        var result = JsonSerializer.Deserialize<IntElementListModel>(Encoding.UTF8.GetBytes(json));
        var expected = json.Contains("1,2") ? new[] { 1, 2 } : [];
        await Assert.That(result!.Items).IsEquivalentTo(expected);
    }

    /// <summary>The same end-of-data path on the top-level array entry point.</summary>
    [Test]
    [Arguments("[1,2", new[] { 1, 2 })]
    [Arguments("[", new int[0])]
    public async Task TruncatedTopLevelArray_YieldsTheElementsThatWereThere(
        string json,
        int[] expected
    )
    {
        var result = JsonSerializer.Deserialize<int[]>(Encoding.UTF8.GetBytes(json));
        await Assert.That(result).IsEquivalentTo(expected);
    }

    /// <summary>
    /// A JSON <c>null</c> where a number belongs is a format error, with the offset the old loop reported
    /// (the position right after the token the read consumed).
    /// </summary>
    [Test]
    [Arguments("""{"Items":[1,null]}""", 16)]
    [Arguments("""{"Items":[1,"x"]}""", 15)]
    [Arguments("""{"Items":[1,2.5]}""", 15)]
    [Arguments("""{"Items":[1,true]}""", 16)]
    public async Task NonIntegerElement_ThrowsTheSameFormatError(string json, int offset)
    {
        var ex = Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<IntElementListModel>(Encoding.UTF8.GetBytes(json))
        );
        await Assert.That(ex!.Message).Contains("Expected an integer element at offset ");
        await Assert.That(ex.Message).Contains($"at offset {offset}");
    }

    /// <summary>
    /// A number that fits <c>long</c> but not <c>int</c> keeps the old shape's answer: the element body
    /// widens to <c>long</c> and narrows with <c>checked</c>, so <see cref="OverflowException"/> — not a
    /// format error. The fused read must not collapse the two.
    /// </summary>
    [Test]
    [Arguments("""{"Items":[3000000000]}""")]
    [Arguments("""{"Items":[1,-3000000000]}""")]
    public async Task OutOfInt32RangeElement_ThrowsOverflow(string json)
    {
        Assert.Throws<OverflowException>(() =>
            JsonSerializer.Deserialize<IntElementListModel>(Encoding.UTF8.GetBytes(json))
        );
        Assert.Throws<OverflowException>(() =>
            JsonSerializer.Deserialize<int[]>(Encoding.UTF8.GetBytes(json[9..]))
        );
    }

    /// <summary>A trailing comma is accepted exactly when the option asks for it.</summary>
    [Test]
    public async Task TrailingCommaElement_FollowsTheOption()
    {
        var options = new JsonOptions { AllowTrailingCommas = true };
        var result = JsonSerializer.Deserialize<IntElementListModel>(
            """{"Items":[1,2,]}"""u8.ToArray(),
            options
        );
        await Assert.That(result!.Items).IsEquivalentTo(new[] { 1, 2 });

        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<IntElementListModel>("""{"Items":[1,2,]}"""u8.ToArray())
        );
    }

    /// <summary>
    /// The streaming path (the shape every class deserializer actually runs): the fused loop must report
    /// a chunk boundary the same way the <c>while</c> loop did, so the property is rewound and re-read
    /// once the rest of the payload arrives.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(7)]
    [Arguments(64)]
    public async Task ChunkedStream_ReadsTheSameValues(int chunkSize)
    {
        var json = Encoding.UTF8.GetBytes("""{"Items":[1,-2,300000,-2147483648]}""");
        using var stream = new ChunkyStream(json, chunkSize);
        var result = await JsonSerializer.DeserializeFromStreamAsync<IntElementListModel>(stream);
        await Assert.That(result!.Items).IsEquivalentTo(new[] { 1, -2, 300000, int.MinValue });
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ChunkedStream_TopLevelIntArray_ReadsTheSameValues(int chunkSize)
    {
        var json = Encoding.UTF8.GetBytes("[1,-2,300000]");
        using var stream = new ChunkyStream(json, chunkSize);
        var result = await JsonSerializer.DeserializeFromStreamAsync<int[]>(stream);
        await Assert.That(result).IsEquivalentTo(new[] { 1, -2, 300000 });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Feeds at most <paramref name="chunkSize"/> bytes per read, to force chunk boundaries.</summary>
    sealed class ChunkyStream(byte[] data, int chunkSize) : Stream
    {
        int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _pos);
            if (n <= 0)
                return 0;
            data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Every emitted class block except the <b>streaming top-level array</b> delegate, which keeps the
    /// two-step element read on purpose (see <see cref="EmittedIntElementLoops_HaveNoTwoStepShapeLeft"/>).
    /// A POCO's element loops live in its <c>{Type}Streaming</c> class — <c>Deserialize</c> delegates
    /// there — so "streaming" cannot be the exclusion criterion; the array delegate is recognised by the
    /// roll-back bookkeeping only it emits (<c>int __before = __list.Count;</c>).
    /// </summary>
    static string[] ElementLoopBlocks(string source) =>
        EmitFor(source)
            .SelectMany(text =>
                text.Split("file static class ", StringSplitOptions.RemoveEmptyEntries)
            )
            .Where(block => !block.Contains("int __before = __list.Count;"))
            .ToArray();

    /// <summary>Runs the real JSON generator and returns the emitted source text of every tree.</summary>
    static string[] EmitFor(string source)
    {
        var compilation = CSharpCompilation.Create(
            "JsonEmissionAsm",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(new JsonSerializerGenerator());
        var run = driver.RunGenerators(compilation).GetRunResult();

        // A compilation that does not compile cannot prove anything about the emitted text: the
        // generator degrades unresolved members with PICOSERDE001 instead of failing.
        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                "The harness compilation must be clean:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, errors)
            );

        return run.GeneratedTrees.Select(tree => tree.GetText().ToString()).ToArray();
    }

    static IEnumerable<MetadataReference> PlatformReferences()
    {
        // Every platform assembly, not a hand-picked five: the generator resolves each member's full
        // type (List<int>, HashSet<int>, Dictionary<string, List<int>>), and a reference set that is
        // merely "enough to parse" silently degrades those members.
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in tpa.Split(Path.PathSeparator))
            yield return MetadataReference.CreateFromFile(path);
        yield return MetadataReference.CreateFromFile(
            Path.Combine(AppContext.BaseDirectory, "PicoJetson.dll")
        );
        yield return MetadataReference.CreateFromFile(
            Path.Combine(AppContext.BaseDirectory, "PicoSerDe.Core.dll")
        );
    }

    static string[] BaselineFiles()
    {
        var directory = Path.Combine(
            RepoRoot(),
            "tests",
            "FormatCapabilities.Probe",
            "generated-baseline",
            "PicoJetson.Gen"
        );
        if (!Directory.Exists(directory))
            return [];
        return Directory.GetFiles(directory, "*.g.cs", SearchOption.AllDirectories);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PicoSerDe.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find the repo root.");
    }
}
