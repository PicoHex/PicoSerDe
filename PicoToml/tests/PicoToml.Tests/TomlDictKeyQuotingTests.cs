using System.Text;

namespace PicoToml.Tests;

public class KeyQuotingDictDto
{
    public Dictionary<string, int> V { get; set; } = new();
}

/// <summary>
/// R2 Task 6 / spec D-9b — the TOML key-quoting CHARACTER SET.
/// <para>
/// <c>TomlWriter.NeedsKeyQuoting</c> used to be a blacklist of six characters
/// (<c>space \t = [ ] . # "</c>). A key carrying a newline, a carriage return, another
/// control character or a backslash was therefore written RAW, and a raw newline ends the
/// key/value line — the document it produced could not be read back by our own reader
/// (and is not valid TOML for anyone else either). D-9's hang fix is what exposed it:
/// <c>toml|S9</c> went from TIMEOUT to "read fails on the boundary key".
/// </para>
/// <para>
/// The rule is now TOML 1.0's own: a bare key is <c>[A-Za-z0-9_-]+</c>. Everything else is
/// quoted and escaped through the same basic-string writer the values use, so the set can
/// no longer be missing a character.
/// </para>
/// </summary>
public class TomlDictKeyQuotingTests
{
    private static readonly string[] TrickyKeys =
    [
        "a",
        "plain-key_1",
        "n\nline",
        "r\rreturn",
        "tab\there",
        "back\\slash",
        "q\"uote",
        "ctl\u0001char",
        "sp ace",
        "do.t",
        "eq=ual",
        "ha#sh",
    ];

    private static Dictionary<string, int> Payload()
    {
        var map = new Dictionary<string, int>();
        for (var i = 0; i < TrickyKeys.Length; i++)
            map[TrickyKeys[i]] = i;
        return map;
    }

    [Test]
    public async Task KeysOutsideTheBareKeySet_RoundTrip()
    {
        var dto = new KeyQuotingDictDto { V = Payload() };

        var text = TomlSerializer.Serialize(dto);
        var back = TomlSerializer.Deserialize<KeyQuotingDictDto>(Encoding.UTF8.GetBytes(text))!;

        await Assert.That(back.V.Count).IsEqualTo(TrickyKeys.Length);
        foreach (var (key, value) in Payload())
            await Assert.That(back.V.TryGetValue(key, out var got) ? got : -1).IsEqualTo(value);
    }

    /// <summary>
    /// The wire is the evidence D-9b was about: the boundary key's newline must be an
    /// ESCAPE, never a line break. Counted per line — a raw newline in a key would make
    /// the document one line longer than its key/value pairs.
    /// </summary>
    [Test]
    public async Task AKeyThatCarriesANewline_IsEscapedOnTheWire()
    {
        var text = TomlSerializer.Serialize(
            new KeyQuotingDictDto { V = new() { ["n\nline"] = 2 } }
        );

        var lines = text.Split('\n');
        // "[V]" + one key/value line + the empty tail after the final newline.
        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[1]).Contains("\"n\\nline\"");
    }

    /// <summary>
    /// A hand-written TOML document with quoted keys must read the same way the writer
    /// writes them (<c>'…'</c> literal keys included: the reader already understood them,
    /// which is what makes the quoted form the right canonical form here).
    /// </summary>
    [Test]
    public async Task QuotedKeysFromAHandWrittenDocument_AreRead()
    {
        const string text = "[V]\n\"n\\nline\" = 2\n'lit.key' = 3\nplain = 4\n";

        var back = TomlSerializer.Deserialize<KeyQuotingDictDto>(Encoding.UTF8.GetBytes(text))!;

        await Assert.That(back.V.Count).IsEqualTo(3);
        await Assert.That(back.V["n\nline"]).IsEqualTo(2);
        await Assert.That(back.V["lit.key"]).IsEqualTo(3);
        await Assert.That(back.V["plain"]).IsEqualTo(4);
    }

    /// <summary>Same document, streamed: the sequence-mode key scan is a second copy.</summary>
    [Test]
    public async Task TrickyKeys_RoundTripThroughTheStreamingEntryPoint()
    {
        var dto = new KeyQuotingDictDto { V = Payload() };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(dto);

        var back = await TomlSerializer.DeserializeFromStreamAsync<KeyQuotingDictDto>(
            new MemoryStream(bytes)
        );

        await Assert.That(back.V.Count).IsEqualTo(TrickyKeys.Length);
        foreach (var (key, value) in Payload())
            await Assert.That(back.V.TryGetValue(key, out var got) ? got : -1).IsEqualTo(value);
    }
}
