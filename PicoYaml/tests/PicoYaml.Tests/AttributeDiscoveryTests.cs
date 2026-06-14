namespace PicoYaml.Tests;

[PicoYamlSerializable]
public class YamlAttrDto
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public class AttributeDiscoveryTests
{
    [Test]
    public async Task YamlAttrDto_RoundTrip()
    {
        var dto = new YamlAttrDto { Name = "yaml-attr", Count = 42 };
        var yaml = YamlSerializer.Serialize(dto);
        await Assert.That(yaml).Contains("Name");
        await Assert.That(yaml).Contains("yaml-attr");

        var result = YamlSerializer.Deserialize<YamlAttrDto>(
            Encoding.UTF8.GetBytes(yaml));
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("yaml-attr");
        await Assert.That(result.Count).IsEqualTo(42);
    }
}
