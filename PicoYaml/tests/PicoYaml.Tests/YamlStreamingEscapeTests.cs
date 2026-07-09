using PicoYaml;

namespace PicoYaml.Tests;

public class YamlStreamEscapeDto
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Regression tests for YAML streaming (DeserializeFromStreamAsync).
/// Follow-up: sequence-mode escape in streaming path.
/// </summary>
public class YamlStreamingEscapeTests
{
    [Test]
    public async Task Streaming_SimpleScalar_RoundTrips()
    {
        var dto = new YamlStreamEscapeDto { Value = "hello" };
        var yaml = YamlSerializer.Serialize(dto);
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(yaml));

        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlStreamEscapeDto>(stream);

        await Assert.That(back!.Value).IsEqualTo("hello");
    }

    [Test]
    public async Task Streaming_ScalarWithEmbeddedQuote_RoundTrips()
    {
        var dto = new YamlStreamEscapeDto { Value = "a\"b" };
        var yaml = YamlSerializer.Serialize(dto);
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(yaml));

        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlStreamEscapeDto>(stream);

        await Assert.That(back!.Value).IsEqualTo("a\"b");
    }
}
