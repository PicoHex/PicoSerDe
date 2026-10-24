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
