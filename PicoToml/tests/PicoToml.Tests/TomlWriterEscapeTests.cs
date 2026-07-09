using PicoToml;

namespace PicoToml.Tests;

public class EscapeDto
{
    public string Value { get; set; } = string.Empty;
}

public class EscapeListDto
{
    public List<string> Tags { get; set; } = new();
}

public class EscapeDictDto
{
    public Dictionary<string, string> Map { get; set; } = new();
}

/// <summary>
/// Regression tests for TomlWriter string-value escaping (confirmed bug:
/// WriteKeyValue wrote raw UTF-8 bytes between quotes without escaping
/// " and \, causing round-trip truncation at the first embedded quote).
/// </summary>
public class TomlWriterEscapeTests
{
    [Test]
    public async Task ScalarStringWithEmbeddedQuote_RoundTrips()
    {
        var dto = new EscapeDto { Value = "a\"b" };
        var toml = TomlSerializer.Serialize(dto);

        var back = TomlSerializer.Deserialize<EscapeDto>(System.Text.Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.Value).IsEqualTo("a\"b");
    }

    [Test]
    public async Task ScalarStringWithBackslash_RoundTrips()
    {
        var dto = new EscapeDto { Value = "a\\b" };
        var toml = TomlSerializer.Serialize(dto);

        // Conformance: backslash must be escaped to \\ in the wire format
        await Assert.That(toml).Contains("\\\\");

        var back = TomlSerializer.Deserialize<EscapeDto>(System.Text.Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.Value).IsEqualTo("a\\b");
    }

    [Test]
    public async Task ListStringWithEmbeddedQuote_RoundTrips()
    {
        var dto = new EscapeListDto { Tags = ["x\"y", "normal"] };
        var toml = TomlSerializer.Serialize(dto);

        var back = TomlSerializer.Deserialize<EscapeListDto>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );

        await Assert.That(back!.Tags.Count).IsEqualTo(2);
        await Assert.That(back.Tags[0]).IsEqualTo("x\"y");
        await Assert.That(back.Tags[1]).IsEqualTo("normal");
    }

    [Test]
    public async Task DictStringWithEmbeddedQuote_RoundTrips()
    {
        var dto = new EscapeDictDto { Map = new() { ["k\"1"] = "v\"1" } };
        var toml = TomlSerializer.Serialize(dto);

        var back = TomlSerializer.Deserialize<EscapeDictDto>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );

        await Assert.That(back!.Map["k\"1"]).IsEqualTo("v\"1");
    }

    [Test]
    public async Task ScalarStringWithNewline_RoundTrips()
    {
        var dto = new EscapeDto { Value = "line1\nline2" };
        var toml = TomlSerializer.Serialize(dto);

        // Conformance: newline must be escaped to \n (not emitted as a raw newline byte)
        await Assert.That(toml).Contains("\\n");

        var back = TomlSerializer.Deserialize<EscapeDto>(System.Text.Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.Value).IsEqualTo("line1\nline2");
    }

    [Test]
    public async Task ScalarStringWithTab_RoundTrips()
    {
        var dto = new EscapeDto { Value = "a\tb" };
        var toml = TomlSerializer.Serialize(dto);

        // Conformance: tab must be escaped to \t (not emitted as a raw tab byte)
        await Assert.That(toml).Contains("\\t");

        var back = TomlSerializer.Deserialize<EscapeDto>(System.Text.Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.Value).IsEqualTo("a\tb");
    }

    [Test]
    public async Task ScalarStringWithControlChar_RoundTrips()
    {
        var dto = new EscapeDto { Value = "a\u0001b" };
        var toml = TomlSerializer.Serialize(dto);

        // Conformance: control char must be escaped as \u00XX, not emitted raw
        await Assert.That(toml).Contains("\\u0001");

        var back = TomlSerializer.Deserialize<EscapeDto>(System.Text.Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.Value).IsEqualTo("a\u0001b");
    }

    [Test]
    public async Task ReadsUnicodeEscapeU0041_AsA()
    {
        var toml = "Value = \"\\u0041X\""u8.ToArray();
        var back = TomlSerializer.Deserialize<EscapeDto>(toml);
        await Assert.That(back!.Value).IsEqualTo("AX");
    }

    [Test]
    public async Task ReadsUnicodeEscapeULarge_AsEmoji()
    {
        var toml = System.Text.Encoding.UTF8.GetBytes("Value = \"\\U0001F600X\"");
        var back = TomlSerializer.Deserialize<EscapeDto>(toml);
        await Assert.That(back!.Value).IsEqualTo("😀X");
    }
}
