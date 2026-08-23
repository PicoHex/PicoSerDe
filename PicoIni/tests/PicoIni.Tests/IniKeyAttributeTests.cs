namespace PicoIni.Tests;

public class IniKeyAttributeTests
{
    [Test]
    public async Task IniKey_ExposesKey()
    {
        var a = new IniKeyAttribute("k");
        await Assert.That(a.Key).IsEqualTo("k");
    }

    [Test]
    public async Task IniKey_NameIsObsoleteAlias()
    {
        var a = new IniKeyAttribute("k");
#pragma warning disable CS0618
        await Assert.That(a.Name).IsEqualTo("k");
#pragma warning restore CS0618
    }
}

public class KeyDoc
{
    [IniKey("k")]
    public int Value { get; set; }
}

public class IniKeyWireTests
{
    [Test]
    public async Task IniKey_OverridesWireKey()
    {
        var ini = IniSerializer.Serialize(new KeyDoc { Value = 1 });
        await Assert.That(ini).Contains("k = 1");
    }
}
