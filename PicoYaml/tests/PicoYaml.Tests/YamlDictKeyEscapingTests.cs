using PicoYaml;

namespace PicoYaml.Tests;

public class YamlKeyDictDto
{
    public Dictionary<string, int> V { get; set; } = new();
}

/// <summary>
/// R2 Task 6 — the WRITE side of a YAML mapping key.
/// <para>
/// <c>YamlWriter.WritePropertyName</c> used to write the name raw while
/// <c>WriteString</c> quoted and escaped the value, so a dictionary key carrying a
/// separator, a quote or a control character produced a document that no reader — ours
/// or PyYAML's — could read back: <c>n\nline: 2</c> is two lines, and <c>q"uote: -1</c>
/// only survived by accident. The cell (spec §3 S9 × yaml) says the format can hold such
/// a key, so the writer owes the quoted form <c>"n\nline": 2</c> and the reader owes the
/// matching unquote.
/// </para>
/// <para>
/// One model, nine keys: <c>\n</c>, <c>"</c>, <c>:</c> (the three the task names) plus
/// the neighbouring cases a quoting rule regresses most easily — a backslash, a leading
/// <c>#</c>, a leading <c>-</c>, a tab, an empty key and the plain <c>a</c>.
/// </para>
/// </summary>
public class YamlDictKeyEscapingTests
{
    private static readonly string[] TrickyKeys =
    [
        "a",
        "n\nline",
        "q\"uote",
        "c:olon",
        "back\\slash",
        "#hash",
        "- dash",
        "tab\there",
        "",
    ];

    private static Dictionary<string, int> Payload()
    {
        var map = new Dictionary<string, int>();
        for (var i = 0; i < TrickyKeys.Length; i++)
            map[TrickyKeys[i]] = i;
        return map;
    }

    [Test]
    public async Task KeysCarryingASeparatorAQuoteOrAControlCharacter_RoundTrip()
    {
        var dto = new YamlKeyDictDto { V = Payload() };

        var yaml = YamlSerializer.Serialize(dto);
        var back = YamlSerializer.Deserialize<YamlKeyDictDto>(Encoding.UTF8.GetBytes(yaml))!;

        await Assert.That(back.V.Count).IsEqualTo(TrickyKeys.Length).Because(yaml);
        foreach (var (key, value) in Payload())
            await Assert
                .That(back.V.TryGetValue(key, out var got) ? got : -1)
                .IsEqualTo(value)
                .Because(key);
    }

    /// <summary>
    /// The wire itself is the evidence: a raw newline inside a key makes the document
    /// unreadable, so no line may contain anything but the key's escaped form. Counted
    /// line by line instead of "does the text contain a newline" — the root member's own
    /// separator is legitimate, a key's newline is not.
    /// </summary>
    [Test]
    public async Task AKeyThatCarriesANewline_IsEscapedOnTheWire()
    {
        var yaml = YamlSerializer.Serialize(new YamlKeyDictDto { V = new() { ["n\nline"] = 2 } });

        var lines = yaml.Split('\n');
        // "V:" + one escaped key line + the empty tail after the final newline.
        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[1]).Contains("\"n\\nline\"");
    }

    /// <summary>
    /// A key whose text looks like a YAML literal (<c>null</c>, <c>true</c>, <c>123</c>)
    /// must stay a key, not become a tag or a number — the same rule the value side has
    /// always applied through <c>NeedsQuoting</c>.
    /// </summary>
    [Test]
    public async Task KeysThatLookLikeLiteralsOrNumbers_StayStrings()
    {
        var dto = new YamlKeyDictDto
        {
            V = new()
            {
                ["null"] = 1,
                ["true"] = 2,
                ["123"] = 3,
            },
        };

        var yaml = YamlSerializer.Serialize(dto);
        var back = YamlSerializer.Deserialize<YamlKeyDictDto>(Encoding.UTF8.GetBytes(yaml))!;

        await Assert.That(back.V.Count).IsEqualTo(3);
        await Assert.That(back.V["null"]).IsEqualTo(1);
        await Assert.That(back.V["true"]).IsEqualTo(2);
        await Assert.That(back.V["123"]).IsEqualTo(3);
    }

    /// <summary>
    /// The quoted-key path has to work through the STREAMING entry point too: it runs the
    /// sequence-mode reader, whose key scan is a second copy of the span-mode one.
    /// </summary>
    [Test]
    public async Task TrickyKeys_RoundTripThroughTheStreamingEntryPoint()
    {
        var dto = new YamlKeyDictDto { V = Payload() };
        var yaml = YamlSerializer.SerializeToUtf8Bytes(dto);

        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlKeyDictDto>(
            new MemoryStream(yaml)
        );

        await Assert.That(back.V.Count).IsEqualTo(TrickyKeys.Length);
        foreach (var (key, value) in Payload())
            await Assert.That(back.V.TryGetValue(key, out var got) ? got : -1).IsEqualTo(value);
    }
}
