namespace PicoSerDe.Integration.Tests;

public class AnonTypeSerializationTests
{
    [Test]
    public async Task Json_AnonType_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(json).Contains("\"Name\":\"test\"");
        await Assert.That(json).Contains("\"Value\":42");
    }

    [Test]
    public async Task MsgPack_AnonType_Serializes()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new { Name = "test", Value = 42 });
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Ini_AnonType_Serializes()
    {
        var ini = IniSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(ini).Contains("Name");
        await Assert.That(ini).Contains("test");
    }

    [Test]
    public async Task Toml_AnonType_Serializes()
    {
        var toml = TomlSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(toml).Contains("Name");
        await Assert.That(toml).Contains("test");
    }

    [Test]
    public async Task Yaml_AnonType_Serializes()
    {
        var yaml = YamlSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(yaml).Contains("Name");
        await Assert.That(yaml).Contains("test");
    }
}
