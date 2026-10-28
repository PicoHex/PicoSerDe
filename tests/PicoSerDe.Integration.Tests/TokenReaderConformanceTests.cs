namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Conformance tests for the shared ITokenReader contract across all 5 formats.
/// NOTE: ref structs cannot be boxed to ITokenReader — the generic Run helpers
/// (allows ref struct) are mandatory. Token SHAPES differ per format (JSON and
/// MsgPack emit ObjectStart/PropertyName/value; INI/TOML/YAML emit
/// PropertyName-then-value without a container token), so the shared assertions
/// cover the contract (readable, depth-balanced, numeric accessors) and the
/// shape assertions are format-specific.
/// </summary>
public class TokenReaderConformanceTests
{
    [Test]
    [Arguments("json", "{ \"a\": 1 }")]
    [Arguments("ini", "a=1\n")]
    [Arguments("toml", "a = 1\n")]
    [Arguments("yaml", "a: 1\n")]
    public async Task Reader_ReadsTokens_AndDepthReturnsToZero(string format, string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        bool ok = format switch
        {
            "json" => Run(new JsonReader(bytes)),
            "ini" => Run(new IniReader(bytes)),
            "toml" => Run(new TomlReader(bytes)),
            "yaml" => Run(new YamlReader(bytes)),
            _ => false,
        };
        await Assert.That(ok).IsTrue();
    }

    // Sync helper returning bool: ref structs cannot cross into async methods,
    // and TUnit assertions only execute when awaited — the checks run here
    // synchronously and the single awaited assertion reports the result.
    private static bool Run<TReader>(TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        int tokens = 0;
        while (reader.Read())
            tokens++;
        return tokens > 0 && reader.Depth == 0;
    }

    [Test]
    public async Task Json_ProducesObjectStartPropertyNameValue()
    {
        var reader = new JsonReader("{ \"a\": 1 }"u8);
        await Assert.That(RunShape(reader)).IsTrue();
    }

    private static bool RunShape<TReader>(TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        return reader.Read()
            && reader.TokenType == TokenType.ObjectStart
            && reader.Read()
            && reader.TokenType == TokenType.PropertyName
            && reader.Read()
            && reader.TryGetInt32(out var v)
            && v == 1;
    }

    [Test]
    public async Task MsgPack_ProducesObjectStartPropertyNameValue()
    {
        byte[] data = [0x81, 0xA1, (byte)'a', 0x01];
        await Assert.That(RunShape(new MsgPackReader(data))).IsTrue();
    }

    [Test]
    public async Task Ini_ProducesPropertyNameThenString()
    {
        await Assert.That(RunIniShape(new IniReader("a=1\n"u8))).IsTrue();
    }

    private static bool RunIniShape<TReader>(TReader reader)
        where TReader : ITokenReader, allows ref struct
    {
        return reader.Read()
            && reader.TokenType == TokenType.PropertyName
            && reader.Read()
            && reader.TokenType == TokenType.String;
    }

    /// <summary>
    /// Task T5 (fused token read, spec §5.3 last row + §3.3): <b>MsgPack declares counted collections</b>,
    /// and the declaration is reachable through the contract — a generic consumer constrained to
    /// <see cref="IFusedTokenReader"/> (the constraint generated code and the conformance tests use) sees
    /// <c>true</c> and can read a count without advancing.
    /// <para>
    /// <b>The four text formats are absent from this test by construction, not by omission.</b> JSON, TOML,
    /// YAML and INI have no count in their wire form (spec §3.3 marks their <c>HasCountedCollections</c> as a
    /// hard <c>false</c>); task T1b then added the interface, and their one-line <c>=> false</c> bodies are
    /// held to one contract in
    /// <see cref="FusedReader_AnswersTheCountedCollectionContract"/> — the five-format witness below.
    /// </para>
    /// </summary>
    [Test]
    public async Task MsgPack_DeclaresCountedCollections_ThroughTheContract()
    {
        byte[] data = [0x82, 0x00, 0x01, 0x01, 0x02]; // fixmap(2)
        await Assert.That(CountWitness(new MsgPackReader(data))).IsEqualTo("");
    }

    // Sync helper: ref structs cannot cross into async methods, and TUnit assertions only execute when
    // awaited — the checks run here and the single awaited assertion reports the result.
    private static string CountWitness<TReader>(TReader reader)
        where TReader : IFusedTokenReader, allows ref struct
    {
        var problems = new List<string>();
        if (!reader.HasCountedCollections)
            problems.Add(
                "HasCountedCollections: MessagePack carries the count in every array/map header"
            );

        if (!reader.Read() || reader.TokenType != TokenType.ObjectStart)
            return "the payload must open a map";

        bool ok = reader.TryGetCollectionCount(out int count);
        if (!ok || count != 2)
            problems.Add($"TryGetCollectionCount: expected (True, 2), got ({ok}, {count})");

        return string.Join("; ", problems);
    }

    /// <summary>
    /// Task T1b (fused token read, spec §6 S0/S4): <b>one contract, five formats</b>. Every reader answers
    /// <see cref="IFusedTokenReader"/> through the contract — not through the concrete type — and the
    /// counted-collection pair must stay self-consistent:
    /// <list type="bullet">
    /// <item>MessagePack declares <see langword="true"/> and answers a count right after <b>every</b> start
    /// token it produced (spec §3.1: a reader answering <see langword="true"/> MUST answer
    /// <see cref="IFusedTokenReader.TryGetCollectionCount"/> right after every
    /// <see cref="TokenType.ArrayStart"/>/<see cref="TokenType.ObjectStart"/>), and the count is never
    /// negative.</item>
    /// <item>JSON / TOML / YAML / INI declare <see langword="false"/> and answer <see langword="false"/> for
    /// <b>every</b> token — before the first one, during the walk, and after the last — which is spec §3.1's
    /// "when it is false, TryGetCollectionCount MUST return false for every token". Their wire form carries no
    /// count (spec §3.3), so a lone <see langword="true"/> would be a lie.</item>
    /// <item>Both kinds report <c>count = 0</c> whenever they answer <see langword="false"/> — the contract
    /// pairs <see langword="false"/> with 0, so a caller can never read a stale count.</item>
    /// </list>
    /// The declaration and the answer are asserted together on purpose: this is the pair spec §3.1 keeps two
    /// members for, and a drift between them is precisely what a single-member contract could not catch.
    /// </summary>
    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    [Arguments("ini")]
    public async Task FusedReader_AnswersTheCountedCollectionContract(string format)
    {
        var problems = format switch
        {
            // A map whose only member is an array: two collections to ask about.
            "json" => CountedContract(new JsonReader("""{"a":[1,2]} """u8), counted: false),
            // fixmap(1) { 0: fixarray(2) [1, 2] } — MessagePack carries both counts in the header.
            "msgpack" => CountedContract(
                new MsgPackReader([0x81, 0x00, 0x92, 0x01, 0x02]),
                counted: true
            ),
            "toml" => CountedContract(new TomlReader("a = [1, 2]\n"u8), counted: false),
            "yaml" => CountedContract(new YamlReader("a: 1\n"u8), counted: false),
            "ini" => CountedContract(new IniReader("a=1\n"u8), counted: false),
            _ => $"unknown format '{format}'",
        };
        await Assert.That(problems).IsEqualTo("");
    }

    // Sync helper: ref structs cannot cross into async methods, and TUnit assertions only execute when
    // awaited — the checks run here and the single awaited assertion reports the result.
    private static string CountedContract<TReader>(TReader reader, bool counted)
        where TReader : IFusedTokenReader, allows ref struct
    {
        var problems = new List<string>();
        if (reader.HasCountedCollections != counted)
            problems.Add(
                $"HasCountedCollections: expected {counted}, got {reader.HasCountedCollections}"
            );

        // No collection token was produced yet ⇒ nothing to count, whichever way the format answers.
        bool before = reader.TryGetCollectionCount(out int beforeCount);
        if (before || beforeCount != 0)
            problems.Add(
                $"TryGetCollectionCount before the first token: expected (False, 0), got ({before}, {beforeCount})"
            );

        int starts = 0;
        while (reader.Read())
        {
            bool ok = reader.TryGetCollectionCount(out int count);
            if (!ok && count != 0)
                problems.Add(
                    $"TryGetCollectionCount answered false with count {count}; the contract pairs false with 0"
                );

            if (!counted)
            {
                if (ok)
                    problems.Add(
                        $"TryGetCollectionCount answered ({ok}, {count}) for a format whose wire form carries no count"
                    );
                continue;
            }

            if (reader.TokenType is TokenType.ArrayStart or TokenType.ObjectStart)
            {
                starts++;
                if (!ok)
                    problems.Add(
                        $"TryGetCollectionCount must answer right after {reader.TokenType} in a counted format"
                    );
                if (count < 0)
                    problems.Add($"TryGetCollectionCount returned a negative count {count}");
            }
        }

        bool after = reader.TryGetCollectionCount(out int afterCount);
        if (after || afterCount != 0)
            problems.Add(
                $"TryGetCollectionCount after the walk: expected (False, 0), got ({after}, {afterCount})"
            );

        if (counted && starts == 0)
            problems.Add("the payload must open at least one collection");

        return string.Join("; ", problems);
    }
}
