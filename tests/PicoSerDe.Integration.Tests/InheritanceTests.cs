namespace PicoSerDe.Integration.Tests;

public class BaseDto
{
    public int A { get; set; } = 1;
}

public class MiddleDto : BaseDto
{
    public int B { get; set; } = 2;
}

public class DerivedDto : MiddleDto
{
    public int C { get; set; } = 3;
}

// Same-name shadowing: the derived member hides the base one. Exercises the
// de-duplication branch of the inherited-member merge (C-01.c/C-01.d).
public class ShadowBase
{
    public int Value { get; set; } = 1;
}

public class ShadowDerived : ShadowBase
{
    public new int Value { get; set; } = 2;
}

// Position fixture: the shadowed member sits first on the base and last on the
// derived type, so the merged order reveals which position is kept.
public class ShadowOrderBase
{
    public int Value { get; set; } = 1;
    public int A { get; set; } = 10;
}

public class ShadowOrderDerived : ShadowOrderBase
{
    public int B { get; set; } = 20;
    public new int Value { get; set; } = 2;
}

public class InheritanceTests
{
    [Test]
    public async Task Json_DerivedType_IncludesBaseMembers()
    {
        var json = JsonSerializer.Serialize(new DerivedDto());
        await Assert.That(json).Contains("\"A\":1");
        await Assert.That(json).Contains("\"B\":2");
        await Assert.That(json).Contains("\"C\":3");
    }

    [Test]
    public async Task Json_DerivedType_RoundTripsBaseMembers()
    {
        var back = JsonSerializer.Deserialize<DerivedDto>(
            Encoding.UTF8.GetBytes("{\"A\":10,\"B\":20,\"C\":30}")
        );
        await Assert.That(back.A).IsEqualTo(10);
        await Assert.That(back.B).IsEqualTo(20);
        await Assert.That(back.C).IsEqualTo(30);
    }

    [Test]
    public async Task Json_ShadowedMember_AppearsOnce_WithDerivedValue()
    {
        var json = JsonSerializer.Serialize(new ShadowDerived());
        await Assert.That(json).IsEqualTo("{\"Value\":2}");
    }

    [Test]
    public async Task MsgPack_ShadowedMember_RoundTripsDerivedValue()
    {
        var back = MsgPackSerializer.Deserialize<ShadowDerived>(
            MsgPackSerializer.SerializeToUtf8Bytes(new ShadowDerived())
        );
        await Assert.That(back.Value).IsEqualTo(2);
    }

    [Test]
    public async Task Json_ShadowedMember_KeepsBaseDeclarationPosition()
    {
        // Merge order is most-base first and a same-name collision replaces in
        // place, so the shadowed member keeps the base declaration slot
        // (`Value` first) instead of moving to its derived position (last).
        var json = JsonSerializer.Serialize(new ShadowOrderDerived());
        await Assert.That(json).IsEqualTo("{\"Value\":2,\"A\":10,\"B\":20}");
    }

    [Test]
    public async Task Toml_Yaml_Ini_MsgPack_IncludeBaseMembers()
    {
        await Assert.That(TomlSerializer.Serialize(new DerivedDto())).Contains("A = 1");
        await Assert.That(YamlSerializer.Serialize(new DerivedDto())).Contains("A: 1");
        await Assert.That(IniSerializer.Serialize(new DerivedDto())).Contains("A = 1");
        await Assert
            .That(
                MsgPackSerializer
                    .Deserialize<DerivedDto>(
                        MsgPackSerializer.SerializeToUtf8Bytes(new DerivedDto())
                    )
                    .A
            )
            .IsEqualTo(1);
    }
}
