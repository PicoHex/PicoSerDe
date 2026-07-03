using PicoSerDe.Core;

namespace PicoToml.Tests;

public ref struct TomlPoint
{
    public int X,
        Y;
}

public class TomlSimple
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class TomlRefStructTests
{
    [Test]
    public async Task SourceGen_Generates_Serializer_For_RefStruct()
    {
        var v = new TomlPoint { X = 10, Y = 20 };
        var toml = TomlSerializer.Serialize(v);
        await Assert.That(toml).Contains("10");
        await Assert.That(toml).Contains("20");
    }

    [Test]
    public async Task RegularType_Still_Works()
    {
        var m = new TomlSimple { Name = "Test", Value = 42 };
        var toml = TomlSerializer.Serialize(m);
        await Assert.That(toml).Contains("Test");
        await Assert.That(toml).Contains("42");
    }
}
