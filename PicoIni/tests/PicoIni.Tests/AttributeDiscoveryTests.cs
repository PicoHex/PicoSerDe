namespace PicoIni.Tests;

[PicoIniSerializable]
public class IniOnlyDto
{
    public string Key { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class AttributeDiscoveryTests
{
    [Test]
    public async Task IniOnlyDto_RoundTrip()
    {
        var dto = new IniOnlyDto { Key = "test", Value = 42 };
        var ini = IniSerializer.Serialize(dto);
        await Assert.That(ini).Contains("Key");
        await Assert.That(ini).Contains("test");

        var result = IniSerializer.Deserialize<IniOnlyDto>(Encoding.UTF8.GetBytes(ini));
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Key).IsEqualTo("test");
        await Assert.That(result.Value).IsEqualTo(42);
    }
}
