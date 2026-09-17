namespace PicoSerDe.Integration.Tests;

[PicoSerializable]
[PicoDerivedType(typeof(ConcBaseLeaf), "leaf")]
public class ConcBasePerson
{
    public int Id { get; set; }
}

public class ConcBaseLeaf : ConcBasePerson
{
    public string Name { get; set; } = "";
}

/// <summary>
/// A concrete polymorphic base can be instantiated: serializing a base-typed
/// instance used to emit malformed output (<c>{"$type":}</c> in JSON,
/// <c>$type = ""</c> in TOML). The generator now synthesizes a discriminator
/// (the base type name) so the base instance round-trips as the base type.
/// </summary>
public class PolyConcreteBaseTests
{
    private static ConcBasePerson BaseInstance() => new ConcBasePerson { Id = 7 };

    [Test]
    public async Task Json_ConcreteBaseInstance_RoundTrips()
    {
        var json = JsonSerializer.Serialize(BaseInstance());
        await Assert.That(json).Contains("ConcBasePerson");
        var back = JsonSerializer.Deserialize<ConcBasePerson>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Id).IsEqualTo(7);
        await Assert.That(back).IsNotTypeOf<ConcBaseLeaf>();
    }

    [Test]
    public async Task MsgPack_ConcreteBaseInstance_RoundTrips()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(BaseInstance());
        var back = MsgPackSerializer.Deserialize<ConcBasePerson>(bytes);
        await Assert.That(back!.Id).IsEqualTo(7);
    }

    [Test]
    public async Task Toml_ConcreteBaseInstance_RoundTrips()
    {
        var toml = TomlSerializer.Serialize(BaseInstance());
        await Assert.That(toml).Contains("ConcBasePerson");
        var back = TomlSerializer.Deserialize<ConcBasePerson>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );
        await Assert.That(back!.Id).IsEqualTo(7);
    }

    [Test]
    public async Task Yaml_ConcreteBaseInstance_RoundTrips()
    {
        var yaml = YamlSerializer.Serialize(BaseInstance());
        await Assert.That(yaml).Contains("ConcBasePerson");
        var back = YamlSerializer.Deserialize<ConcBasePerson>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );
        await Assert.That(back!.Id).IsEqualTo(7);
    }

    [Test]
    public async Task Ini_ConcreteBaseInstance_RoundTrips()
    {
        var ini = IniSerializer.Serialize(BaseInstance());
        var back = IniSerializer.Deserialize<ConcBasePerson>(
            System.Text.Encoding.UTF8.GetBytes(ini)
        );
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task DerivedInstance_StillDispatchesToDerived()
    {
        ConcBasePerson value = new ConcBaseLeaf { Id = 1, Name = "n" };
        var json = JsonSerializer.Serialize(value);
        var back = JsonSerializer.Deserialize<ConcBasePerson>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back).IsTypeOf<ConcBaseLeaf>();
        await Assert.That(((ConcBaseLeaf)back!).Name).IsEqualTo("n");
    }
}
