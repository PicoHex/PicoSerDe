using PicoToml;

namespace PicoToml.Tests;

public class TomlStreamingEscapeDto
{
    public string Value { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Regression tests for TOML streaming (DeserializeFromStreamAsync).
/// Follow-up #1: streaming returned null for root-level scalar DTOs —
/// the first Read() returned false, causing the SG streaming deserializer
/// to return Success with result=null.
/// Follow-up #2: sequence-mode string reads in ReadSeq used raw " scanning
/// without honouring  \", causing truncation of strings containing quotes.
/// </summary>
public class TomlStreamingEscapeTests
{
    [Test]
    public async Task Streaming_SimpleRootScalar_RoundTrips()
    {
        var dto = new TomlStreamingEscapeDto { Value = "hello" };
        var toml = TomlSerializer.Serialize(dto);
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(toml));

        var back = await TomlSerializer.DeserializeFromStreamAsync<TomlStreamingEscapeDto>(stream);

        await Assert.That(back!.Value).IsEqualTo("hello");
    }

    [Test]
    public async Task Streaming_RootScalarWithEmbeddedQuote_RoundTrips()
    {
        var dto = new TomlStreamingEscapeDto { Value = "a\"b\\c" };
        var toml = TomlSerializer.Serialize(dto);
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(toml));

        var back = await TomlSerializer.DeserializeFromStreamAsync<TomlStreamingEscapeDto>(stream);

        await Assert.That(back!.Value).IsEqualTo("a\"b\\c");
    }
}
