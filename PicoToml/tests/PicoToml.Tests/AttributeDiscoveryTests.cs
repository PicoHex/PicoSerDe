namespace PicoToml.Tests;

[PicoTomlSerializable]
public class TomlAttrDto
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public class AttributeDiscoveryTests
{
    [Test]
    public async Task TomlAttrDto_RoundTrip()
    {
        var dto = new TomlAttrDto { Name = "test", Count = 7 };
        var toml = TomlSerializer.Serialize(dto);
        await Assert.That(toml).Contains("Name");
        await Assert.That(toml).Contains("test");

        var result = TomlSerializer.Deserialize<TomlAttrDto>(Encoding.UTF8.GetBytes(toml));
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("test");
        await Assert.That(result.Count).IsEqualTo(7);
    }
}
