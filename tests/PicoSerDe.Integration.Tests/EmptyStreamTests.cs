using PicoIni;
using PicoJetson;
using PicoToml;
using PicoYaml;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Empty-stream semantics must match the synchronous path:
/// - TOML/YAML/INI treat an empty document as an empty object (sync behavior);
/// - JSON rejects empty input with a clear FormatException.
/// </summary>
public class EmptyStreamTests
{
    [Test]
    public async Task Toml_EmptyStream_ReturnsEmptyObject()
    {
        using var s = new MemoryStream();
        var dto = await TomlSerializer.DeserializeFromStreamAsync<SkDoc>(s);
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Scalar).IsEqualTo("");
    }

    [Test]
    public async Task Yaml_EmptyStream_ReturnsEmptyObject()
    {
        using var s = new MemoryStream();
        var dto = await YamlSerializer.DeserializeFromStreamAsync<SkDoc>(s);
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Scalar).IsEqualTo("");
    }

    [Test]
    public async Task Ini_EmptyStream_ReturnsEmptyObject()
    {
        using var s = new MemoryStream();
        var dto = await IniSerializer.DeserializeFromStreamAsync<SkDoc>(s);
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Scalar).IsEqualTo("");
    }

    [Test]
    public async Task Json_EmptyStream_ThrowsFormatException_WithClearMessage()
    {
        using var s = new MemoryStream();
        Exception? error = null;
        try
        {
            await JsonSerializer.DeserializeFromStreamAsync<SkDoc>(s);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await Assert.That(error).IsTypeOf<FormatException>();
        await Assert.That(error!.Message).Contains("no value");
    }
}
