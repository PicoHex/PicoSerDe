using PicoYaml;

namespace PicoYaml.Tests;

public class YamlEscapeScalarDto
{
    public string Value { get; set; } = string.Empty;
}

public class YamlEscapeSeqDto
{
    public List<string> Tags { get; set; } = new();
}

public class YamlEscapeDictDto
{
    public Dictionary<string, string> Map { get; set; } = new();
}

/// <summary>
/// Regression tests for PicoYaml string escaping.
/// Bug #2: WriteSequenceItem wrote a quoted string with raw (unescaped) content,
///   so a sequence element containing " produced invalid YAML.
/// Bug #3: YamlReader's double-quoted string scan did not honour the \" escape,
///   so a mapping value containing " was truncated on round-trip.
/// </summary>
public class YamlEscapeTests
{
    [Test]
    public async Task MappingValueWithEmbeddedQuote_RoundTrips()
    {
        var dto = new YamlEscapeScalarDto { Value = "a\"b" };
        var yaml = YamlSerializer.Serialize(dto);

        var back = YamlSerializer.Deserialize<YamlEscapeScalarDto>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );

        await Assert.That(back!.Value).IsEqualTo("a\"b");
    }

    [Test]
    public async Task MappingValueWithBackslash_RoundTrips()
    {
        var dto = new YamlEscapeScalarDto { Value = "a\\b" };
        var yaml = YamlSerializer.Serialize(dto);

        var back = YamlSerializer.Deserialize<YamlEscapeScalarDto>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );

        await Assert.That(back!.Value).IsEqualTo("a\\b");
    }

    [Test]
    public async Task SequenceItemWithEmbeddedQuote_RoundTrips()
    {
        var dto = new YamlEscapeSeqDto { Tags = ["x\"y", "normal"] };
        var yaml = YamlSerializer.Serialize(dto);

        var back = YamlSerializer.Deserialize<YamlEscapeSeqDto>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );

        await Assert.That(back!.Tags.Count).IsEqualTo(2);
        await Assert.That(back.Tags[0]).IsEqualTo("x\"y");
        await Assert.That(back.Tags[1]).IsEqualTo("normal");
    }

    [Test]
    public async Task DictValueWithEmbeddedQuote_RoundTrips()
    {
        var dto = new YamlEscapeDictDto { Map = new() { ["k"] = "v\"1" } };
        var yaml = YamlSerializer.Serialize(dto);

        var back = YamlSerializer.Deserialize<YamlEscapeDictDto>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );

        await Assert.That(back!.Map["k"]).IsEqualTo("v\"1");
    }

    [Test]
    public async Task ReadsUnicodeEscapeU0041_AsA()
    {
        var yaml = "Value: \"\\u0041X\""u8.ToArray();
        var back = YamlSerializer.Deserialize<YamlEscapeScalarDto>(yaml);
        await Assert.That(back!.Value).IsEqualTo("AX");
    }

    [Test]
    public async Task ReadsUnicodeEscapeULarge_AsEmoji()
    {
        var yaml = System.Text.Encoding.UTF8.GetBytes("Value: \"\\U0001F600X\"");
        var back = YamlSerializer.Deserialize<YamlEscapeScalarDto>(yaml);
        await Assert.That(back!.Value).IsEqualTo("😀X");
    }

    [Test]
    public async Task ReadsHexEscapeX41_AsA()
    {
        var yaml = "Value: \"\\x41X\""u8.ToArray();
        var back = YamlSerializer.Deserialize<YamlEscapeScalarDto>(yaml);
        await Assert.That(back!.Value).IsEqualTo("AX");
    }

    [Test]
    public async Task ReadsBellBackspaceEscapes()
    {
        var yaml = "Value: \"\\a\\bX\""u8.ToArray();
        var back = YamlSerializer.Deserialize<YamlEscapeScalarDto>(yaml);
        await Assert.That(back!.Value).IsEqualTo("\a\bX");
    }

    [Test]
    public async Task SingleQuotedStringWithDoubledQuote_Unescapes()
    {
        var yaml = "Value: 'don''t'"u8.ToArray();
        var back = YamlSerializer.Deserialize<YamlEscapeScalarDto>(yaml);
        await Assert.That(back!.Value).IsEqualTo("don't");
    }
}
