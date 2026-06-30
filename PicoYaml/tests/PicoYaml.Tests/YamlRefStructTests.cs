using PicoSerDe.Core;

namespace PicoYaml.Tests;

public ref struct YamlPoint
{
    public int X,
        Y;
}

public class YamlSimple
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class YamlRefStructTests
{
    [Test]
    public async Task SourceGen_Generates_Serializer_For_RefStruct()
    {
        var v = new YamlPoint { X = 10, Y = 20 };
        var yaml = YamlSerializer.Serialize(v);
        await Assert.That(yaml).Contains("10");
        await Assert.That(yaml).Contains("20");
    }

    [Test]
    public async Task RegularType_Still_Works()
    {
        var m = new YamlSimple { Name = "Test", Value = 42 };
        var yaml = YamlSerializer.Serialize(m);
        await Assert.That(yaml).Contains("Test");
        await Assert.That(yaml).Contains("42");
    }
}
