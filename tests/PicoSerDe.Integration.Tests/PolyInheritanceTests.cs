namespace PicoSerDe.Integration.Tests;

public class PolyAddress
{
    public string City { get; set; } = "";
}

[PicoSerializable]
[PicoDerivedType(typeof(PolyEmployee), "employee")]
public abstract class PolyPerson
{
    public int Id { get; set; }
}

public class PolyEmployee : PolyPerson
{
    // First declared member is a complex object: the poly dispatch chain must
    // skip it without emitting a dangling "else if".
    public PolyAddress Address { get; set; } = new();
    public string Name { get; set; } = "";
}

/// <summary>
/// Polymorphic inheritance fixtures: derived types inherit base members.
/// Guards the shared poly fixes: an if/else-if chain that may skip a complex
/// member must not emit a dangling "else if" (<c>ChainKeyword</c>), and
/// inherited properties must not reuse the base's auto field ids
/// (MsgPack IntKey renumbering after the poly merge).
/// </summary>
public class PolyInheritanceTests
{
    [Test]
    public async Task Json_DerivedWithInheritedMember_RoundTrips()
    {
        PolyPerson person = new PolyEmployee
        {
            Id = 7,
            Name = "Ada",
            Address = new PolyAddress { City = "Cambridge" },
        };

        var json = JsonSerializer.Serialize(person);
        var back = JsonSerializer.Deserialize<PolyPerson>(System.Text.Encoding.UTF8.GetBytes(json));

        await Assert.That(back).IsTypeOf<PolyEmployee>();
        var employee = (PolyEmployee)back!;
        await Assert.That(employee.Id).IsEqualTo(7);
        await Assert.That(employee.Name).IsEqualTo("Ada");
        await Assert.That(employee.Address.City).IsEqualTo("Cambridge");
    }

    [Test]
    public async Task MsgPack_DerivedWithInheritedMember_RoundTrips()
    {
        PolyPerson person = new PolyEmployee
        {
            Id = 7,
            Name = "Ada",
            Address = new PolyAddress { City = "Cambridge" },
        };

        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
        var back = MsgPackSerializer.Deserialize<PolyPerson>(bytes);

        await Assert.That(back).IsTypeOf<PolyEmployee>();
        var employee = (PolyEmployee)back!;
        // Inherited (Id) and derived (Name) members must both survive: this
        // fails when the poly merge leaves duplicate auto field ids.
        await Assert.That(employee.Id).IsEqualTo(7);
        await Assert.That(employee.Name).IsEqualTo("Ada");
        await Assert.That(employee.Address.City).IsEqualTo("Cambridge");
    }
}
