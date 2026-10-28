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
    /// <b>The four text formats are absent by construction, not by omission.</b> JSON, TOML, YAML and INI
    /// have no count in their wire form (spec §3.3 marks their <c>HasCountedCollections</c> as a hard
    /// <c>false</c>) and they do not implement <see cref="IFusedTokenReader"/> at all yet — that is task
    /// T1b, which this task does not do. Their "false" is therefore expressed by the type system (no
    /// interface ⇒ no member to answer), and the moment T1b adds the interface their one-line
    /// <c>=> false</c> bodies bring them into this witness. Asserting <c>false</c> for a member that does
    /// not exist yet would be a test of a fiction.
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
}
