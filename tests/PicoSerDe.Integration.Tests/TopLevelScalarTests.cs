namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Review P1-1: top-level Nullable&lt;T&gt; / scalar targets are intentionally
/// unsupported. They must not emit broken or silently-corrupting generated
/// code — the call fails loudly with "no serializer registered" instead.
/// (Before the fix these calls did not even compile: CS0721/CS0722.)
/// </summary>
public class TopLevelScalarTests
{
    [Test]
    public async Task Json_NullableValueType_FailsLoudly()
    {
        await Assert
            .That(() => PicoJetson.JsonSerializer.Deserialize<Guid?>("null"u8))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Ini_NullableValueType_FailsLoudly()
    {
        await Assert
            .That(() => PicoIni.IniSerializer.Deserialize<Guid?>(""u8))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Toml_NullableValueType_FailsLoudly()
    {
        await Assert
            .That(() => PicoToml.TomlSerializer.Deserialize<Guid?>(""u8))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Yaml_NullableValueType_FailsLoudly()
    {
        await Assert
            .That(() => PicoYaml.YamlSerializer.Deserialize<Guid?>(""u8))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task MsgPack_NullableValueType_FailsLoudly()
    {
        await Assert
            .That(() => PicoMsgPack.MsgPackSerializer.Deserialize<Guid?>(ReadOnlySpan<byte>.Empty))
            .Throws<InvalidOperationException>();
    }
}
