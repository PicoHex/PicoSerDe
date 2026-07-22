namespace PicoSerDe.Integration.Tests;

public class AnonTypeSerializationTests
{
    [Test]
    public async Task Json_NestedAnonType_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Outer = "root", Inner = new { X = 1, Y = 2 } }
        );
        await Assert.That(json).Contains("\"Outer\":\"root\"");
        await Assert.That(json).Contains("\"X\":1");
        await Assert.That(json).Contains("\"Y\":2");
    }

    [Test]
    public async Task Json_AnonTypeWithArray_Serializes()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Name = "test", Items = new[] { 1, 2, 3 } }
        );
        await Assert.That(json).Contains("\"Items\":[1,2,3]");
    }

    [Test]
    public async Task Json_DefaultIgnoreCondition_WhenWritingNull()
    {
        var json = PicoJetson.JsonSerializer.Serialize(
            new { Name = (string?)null, Value = 42 },
            new PicoJetson.JsonOptions
            {
                DefaultIgnoreCondition = PicoJetson.JsonIgnoreCondition.WhenWritingNull,
            }
        );
        await Assert.That(json).DoesNotContain("Name");
        await Assert.That(json).Contains("\"Value\":42");
    }

    [Test]
    public async Task Json_MaxDepth_Exceeded_Throws()
    {
        var innerMost = new { X = 1 };
        var inner = new { A = innerMost };
        var outer = new { B = inner };
        Assert.Throws<FormatException>(() =>
            PicoJetson.JsonSerializer.Serialize(outer, new PicoJetson.JsonOptions { MaxDepth = 2 })
        );
    }

    [Test]
    public async Task MsgPack_NestedAnonType_Serializes()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new { Outer = "root", Inner = new { X = 1 } }
        );
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Ini_NestedAnonType_Serializes()
    {
        var ini = IniSerializer.Serialize(new { Outer = "root", Inner = new { X = 1 } });
        await Assert.That(ini).Contains("Outer");
        await Assert.That(ini).Contains("X");
    }

    [Test]
    public async Task Toml_NestedAnonType_Serializes()
    {
        var toml = TomlSerializer.Serialize(new { Outer = "root", Inner = new { X = 1 } });
        await Assert.That(toml).Contains("Outer");
        await Assert.That(toml).Contains("X");
    }

    [Test]
    public async Task Yaml_NestedAnonType_Serializes()
    {
        var yaml = YamlSerializer.Serialize(new { Outer = "root", Inner = new { X = 1 } });
        await Assert.That(yaml).Contains("Outer");
        await Assert.That(yaml).Contains("X");
    }

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
