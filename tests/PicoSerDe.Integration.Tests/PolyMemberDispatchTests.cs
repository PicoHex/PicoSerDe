namespace PicoSerDe.Integration.Tests;

// H5: a polymorphic base reached as a MEMBER of a non-polymorphic type. JSON has always dispatched
// here (its member emitter calls the type's inner helper, which IS the polymorphic one); TOML, YAML
// and MsgPack inlined the member as if it were the BASE type — no discriminator, no derived members —
// and read it back as a base instance. The ELEMENT position of the same DTO is the control: it
// already dispatched in msgpack (WriteSerElem went through the polymorphic helper) and must keep
// doing so.
[PicoSerializable]
[PicoDerivedType(typeof(PolyMemberCat), "cat")]
public class PolyMemberBase
{
    public int Id { get; set; }
}

public class PolyMemberCat : PolyMemberBase
{
    public int Lives { get; set; }
}

public class PolyMemberHolder
{
    public PolyMemberBase Member { get; set; } = null!;

    public List<PolyMemberBase> Items { get; set; } = new();
}

// The same shape with an ABSTRACT base — what a real hierarchy looks like. Before this fix the
// member position emitted `new <abstract>()` for toml/yaml/msgpack (CS0144), so this DTO did not even
// compile; it is here so a fix that only works for a CONCRETE base cannot pass.
[PicoSerializable]
[PicoDerivedType(typeof(PolyAbsLeaf), "leaf")]
public abstract class PolyAbsBase
{
    public int Id { get; set; }
}

public class PolyAbsLeaf : PolyAbsBase
{
    public string Name { get; set; } = "";
}

public class PolyAbsHolder
{
    public PolyAbsBase Member { get; set; } = null!;
}

// A polymorphic member whose DERIVED type carries a nested object: the member-position reader must
// know which child sections are its own (`[Member.Child]`) and which end it, and in yaml which
// `ObjectEnd` is its own rather than a nested mapping's. Both were measured bugs.
[PicoSerializable]
[PicoDerivedType(typeof(PolyWithChildCat), "cat")]
public class PolyWithChildBase
{
    public int Id { get; set; }
}

public class PolyWithChildCat : PolyWithChildBase
{
    // Complex member FIRST, on purpose (the shape that produced a dangling `else if` once).
    public PolyChild Child { get; set; } = new();
    public string Name { get; set; } = "";
}

public class PolyChild
{
    public string City { get; set; } = "";
}

public class PolyWithChildHolder
{
    public PolyWithChildBase Member { get; set; } = null!;
}

public class PolyMemberDispatchTests
{
    private static PolyWithChildHolder ChildSample() =>
        new()
        {
            Member = new PolyWithChildCat
            {
                Id = 7,
                Child = new PolyChild { City = "Cambridge" },
                Name = "Ada",
            },
        };

    private static PolyAbsHolder AbsSample() => new() { Member = new PolyAbsLeaf { Id = 5, Name = "n" } };

    private static PolyMemberHolder Sample() =>
        new()
        {
            Member = new PolyMemberCat { Id = 3, Lives = 9 },
            Items = new List<PolyMemberBase> { new PolyMemberCat { Id = 4, Lives = 7 } },
        };

    // ── the member position, per format: the DISCRIMINATOR and the DERIVED member must be on the
    //    wire, and the value must come back as its derived type ──

    [Test]
    public async Task Json_PolyMember_CarriesDiscriminatorAndDerivedMembers()
    {
        var json = JsonSerializer.Serialize(Sample());

        await Assert.That(json).Contains("\"cat\"").Because("json=" + json);
        await Assert.That(json).Contains("\"Lives\":9").Because("json=" + json);

        var back = JsonSerializer.Deserialize<PolyMemberHolder>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Member).IsTypeOf<PolyMemberCat>().Because("json=" + json);
        await Assert.That(((PolyMemberCat)back.Member).Lives).IsEqualTo(9).Because("json=" + json);
    }

    [Test]
    public async Task MsgPack_PolyMember_CarriesDiscriminatorAndDerivedMembers()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(Sample());
        var hex = Convert.ToHexString(bytes);

        // "cat" and "Lives" must be ON THE WIRE, not merely reconstructible from a base instance.
        await Assert.That(hex).Contains("636174").Because("msgpack=" + hex);
        await Assert.That(hex).Contains("4C69766573").Because("msgpack=" + hex);

        var back = MsgPackSerializer.Deserialize<PolyMemberHolder>(bytes);
        await Assert.That(back!.Member).IsTypeOf<PolyMemberCat>().Because("msgpack=" + hex);
        await Assert.That(((PolyMemberCat)back.Member).Lives).IsEqualTo(9).Because("msgpack=" + hex);
    }

    [Test]
    public async Task Toml_PolyMember_CarriesDiscriminatorAndDerivedMembers()
    {
        var toml = TomlSerializer.Serialize(Sample());

        await Assert.That(toml).Contains("cat").Because("toml=" + toml);
        await Assert.That(toml).Contains("Lives").Because("toml=" + toml);

        var back = TomlSerializer.Deserialize<PolyMemberHolder>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );
        await Assert.That(back!.Member).IsTypeOf<PolyMemberCat>().Because("toml=" + toml);
        await Assert.That(((PolyMemberCat)back.Member).Lives).IsEqualTo(9).Because("toml=" + toml);
    }

    [Test]
    public async Task Yaml_PolyMember_CarriesDiscriminatorAndDerivedMembers()
    {
        var yaml = YamlSerializer.Serialize(Sample());

        await Assert.That(yaml).Contains("cat").Because("yaml=" + yaml);
        await Assert.That(yaml).Contains("Lives").Because("yaml=" + yaml);

        var back = YamlSerializer.Deserialize<PolyMemberHolder>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );
        await Assert.That(back!.Member).IsTypeOf<PolyMemberCat>().Because("yaml=" + yaml);
        await Assert.That(((PolyMemberCat)back.Member).Lives).IsEqualTo(9).Because("yaml=" + yaml);
    }

    // ── the same member shape in the ELEMENT position: the control that must not regress ──

    [Test]
    public async Task MsgPack_PolyListElement_StillDispatches()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(Sample());
        var back = MsgPackSerializer.Deserialize<PolyMemberHolder>(bytes);

        await Assert.That(back!.Items.Count).IsEqualTo(1);
        await Assert.That(back.Items[0]).IsTypeOf<PolyMemberCat>();
        await Assert.That(((PolyMemberCat)back.Items[0]).Lives).IsEqualTo(7);
    }

    [Test]
    public async Task Toml_PolyListElement_StillDispatches()
    {
        var toml = TomlSerializer.Serialize(Sample());
        var back = TomlSerializer.Deserialize<PolyMemberHolder>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );

        await Assert.That(back!.Items.Count).IsEqualTo(1).Because("toml=" + toml);
        await Assert.That(back.Items[0]).IsTypeOf<PolyMemberCat>().Because("toml=" + toml);
        await Assert.That(((PolyMemberCat)back.Items[0]).Lives).IsEqualTo(7).Because("toml=" + toml);
    }

    [Test]
    public async Task Yaml_PolyListElement_StillDispatches()
    {
        var yaml = YamlSerializer.Serialize(Sample());
        var back = YamlSerializer.Deserialize<PolyMemberHolder>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );

        await Assert.That(back!.Items.Count).IsEqualTo(1).Because("yaml=" + yaml);
        await Assert.That(back.Items[0]).IsTypeOf<PolyMemberCat>().Because("yaml=" + yaml);
        await Assert.That(((PolyMemberCat)back.Items[0]).Lives).IsEqualTo(7).Because("yaml=" + yaml);
    }

    // ── an ABSTRACT base in the member position: compiles and round-trips in every format ──

    [Test]
    public async Task Json_AbstractPolyMember_RoundTrips()
    {
        var json = JsonSerializer.Serialize(AbsSample());
        var back = JsonSerializer.Deserialize<PolyAbsHolder>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );

        await Assert.That(back!.Member).IsTypeOf<PolyAbsLeaf>().Because("json=" + json);
        await Assert.That(((PolyAbsLeaf)back.Member).Name).IsEqualTo("n").Because("json=" + json);
    }

    [Test]
    public async Task MsgPack_AbstractPolyMember_RoundTrips()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(AbsSample());
        var hex = Convert.ToHexString(bytes);
        var back = MsgPackSerializer.Deserialize<PolyAbsHolder>(bytes);

        await Assert.That(back!.Member).IsTypeOf<PolyAbsLeaf>().Because("msgpack=" + hex);
        await Assert.That(((PolyAbsLeaf)back.Member).Name).IsEqualTo("n").Because("msgpack=" + hex);
    }

    [Test]
    public async Task Toml_AbstractPolyMember_RoundTrips()
    {
        var toml = TomlSerializer.Serialize(AbsSample());
        var back = TomlSerializer.Deserialize<PolyAbsHolder>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );

        await Assert.That(back!.Member).IsTypeOf<PolyAbsLeaf>().Because("toml=" + toml);
        await Assert.That(((PolyAbsLeaf)back.Member).Name).IsEqualTo("n").Because("toml=" + toml);
    }

    // ── a nested object inside the polymorphic member: the child section must stay its own ──

    [Test]
    public async Task Toml_PolyMember_WithNestedObject_RoundTrips()
    {
        var toml = TomlSerializer.Serialize(ChildSample());
        var back = TomlSerializer.Deserialize<PolyWithChildHolder>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );

        await Assert.That(back!.Member).IsTypeOf<PolyWithChildCat>().Because("toml=" + toml);
        var cat = (PolyWithChildCat)back.Member;
        await Assert.That(cat.Id).IsEqualTo(7).Because("toml=" + toml);
        await Assert.That(cat.Name).IsEqualTo("Ada").Because("toml=" + toml);
        await Assert.That(cat.Child.City).IsEqualTo("Cambridge").Because("toml=" + toml);
    }

    [Test]
    public async Task Yaml_PolyMember_WithNestedObject_RoundTrips()
    {
        var yaml = YamlSerializer.Serialize(ChildSample());
        var back = YamlSerializer.Deserialize<PolyWithChildHolder>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );

        await Assert.That(back!.Member).IsTypeOf<PolyWithChildCat>().Because("yaml=" + yaml);
        var cat = (PolyWithChildCat)back.Member;
        await Assert.That(cat.Id).IsEqualTo(7).Because("yaml=" + yaml);
        await Assert.That(cat.Name).IsEqualTo("Ada").Because("yaml=" + yaml);
        await Assert.That(cat.Child.City).IsEqualTo("Cambridge").Because("yaml=" + yaml);
    }

    [Test]
    public async Task MsgPack_PolyMember_WithNestedObject_RoundTrips()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(ChildSample());
        var hex = Convert.ToHexString(bytes);
        var back = MsgPackSerializer.Deserialize<PolyWithChildHolder>(bytes);

        await Assert.That(back!.Member).IsTypeOf<PolyWithChildCat>().Because("msgpack=" + hex);
        var cat = (PolyWithChildCat)back.Member;
        await Assert.That(cat.Id).IsEqualTo(7).Because("msgpack=" + hex);
        await Assert.That(cat.Name).IsEqualTo("Ada").Because("msgpack=" + hex);
        await Assert.That(cat.Child.City).IsEqualTo("Cambridge").Because("msgpack=" + hex);
    }

    [Test]
    public async Task Yaml_AbstractPolyMember_RoundTrips()
    {
        var yaml = YamlSerializer.Serialize(AbsSample());
        var back = YamlSerializer.Deserialize<PolyAbsHolder>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );

        await Assert.That(back!.Member).IsTypeOf<PolyAbsLeaf>().Because("yaml=" + yaml);
        await Assert.That(((PolyAbsLeaf)back.Member).Name).IsEqualTo("n").Because("yaml=" + yaml);
    }
}
