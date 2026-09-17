namespace PicoSerDe.Integration.Tests;

public class PolyListAddr
{
    public string City { get; set; } = "";
}

[PicoSerializable]
[PicoDerivedType(typeof(PolyListEmp), "emp")]
public abstract class PolyListBase { }

public class PolyListEmp : PolyListBase
{
    public string Name { get; set; } = "";
    public List<PolyListAddr> Addresses { get; set; } = new();
    public Dictionary<string, PolyListAddr> Map { get; set; } = new();
}

public class PolyCollectionMemberTests
{
    private static PolyListBase Sample() =>
        new PolyListEmp
        {
            Name = "n",
            Addresses = new List<PolyListAddr> { new PolyListAddr { City = "C1" } },
            Map = new Dictionary<string, PolyListAddr> { ["k"] = new PolyListAddr { City = "C2" } },
        };

    [Test]
    public async Task Toml_PolyListMember()
    {
        var v = Sample();
        var text = TomlSerializer.Serialize(v);
        var back = TomlSerializer.Deserialize<PolyListBase>(
            System.Text.Encoding.UTF8.GetBytes(text)
        );
        var e = (PolyListEmp)back!;
        await Assert.That(e.Addresses.Count).IsEqualTo(1).Because("toml=" + text);
        await Assert.That(e.Addresses[0].City).IsEqualTo("C1");
        await Assert.That(e.Map["k"].City).IsEqualTo("C2");
    }

    [Test]
    public async Task Yaml_PolyListMember()
    {
        var v = Sample();
        var text = YamlSerializer.Serialize(v);
        var back = YamlSerializer.Deserialize<PolyListBase>(
            System.Text.Encoding.UTF8.GetBytes(text)
        );
        var e = (PolyListEmp)back!;
        await Assert.That(e.Addresses.Count).IsEqualTo(1).Because("yaml=" + text);
        await Assert.That(e.Addresses[0].City).IsEqualTo("C1");
        await Assert.That(e.Map["k"].City).IsEqualTo("C2");
    }

    [Test]
    public async Task Json_PolyCollectionMembers()
    {
        var v = Sample();
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<PolyListBase>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        var e = (PolyListEmp)back!;
        await Assert.That(e.Addresses[0].City).IsEqualTo("C1").Because("json=" + json);
        await Assert.That(e.Map["k"].City).IsEqualTo("C2");
    }

    [Test]
    public async Task MsgPack_PolyListMember()
    {
        var v = Sample();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(v);
        var back = MsgPackSerializer.Deserialize<PolyListBase>(bytes);
        var e = (PolyListEmp)back!;
        await Assert.That(e.Addresses.Count).IsEqualTo(1);
        await Assert.That(e.Addresses[0].City).IsEqualTo("C1");
        await Assert.That(e.Map["k"].City).IsEqualTo("C2");
    }
}
