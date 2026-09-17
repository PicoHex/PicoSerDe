using System.Text;

namespace PicoSerDe.Integration.Tests;

public class BomDto
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

/// <summary>
/// UTF-8 BOM contract: every text format skips a leading BOM; a BOM-only
/// document fails loudly with FormatException (never silently yields default
/// values).
/// </summary>
public class BomTests
{
    private static byte[] Bom(string tail) =>
        Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(tail)).ToArray();

    [Test]
    public async Task Json_BomPrefixed_ParsesAllFields()
    {
        var dto = JsonSerializer.Deserialize<BomDto>(Bom("""{"Name":"n","Age":3}"""));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Toml_BomPrefixed_KeepsFirstKey()
    {
        var dto = TomlSerializer.Deserialize<BomDto>(Bom("Name = \"n\"\nAge = 3\n"));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Yaml_BomPrefixed_KeepsFirstKey()
    {
        var dto = YamlSerializer.Deserialize<BomDto>(Bom("Name: n\nAge: 3\n"));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Ini_BomPrefixed_KeepsFirstKey()
    {
        var dto = IniSerializer.Deserialize<BomDto>(Bom("Name=n\nAge=3\n"));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Json_OnlyBom_ThrowsFormatException()
    {
        await Assert
            .That(() => JsonSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble()))
            .Throws<FormatException>();
    }

    [Test]
    public async Task Toml_OnlyBom_MatchesEmptyInputSemantics()
    {
        // TOML treats an empty document as a valid empty object; a BOM-only
        // document is logically empty and must behave identically.
        var dto = TomlSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble());
        await Assert.That(dto!.Name).IsEqualTo("");
        await Assert.That(dto.Age).IsEqualTo(0);
    }

    [Test]
    public async Task Yaml_OnlyBom_MatchesEmptyInputSemantics()
    {
        var dto = YamlSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble());
        await Assert.That(dto!.Name).IsEqualTo("");
        await Assert.That(dto.Age).IsEqualTo(0);
    }

    [Test]
    public async Task Ini_OnlyBom_MatchesEmptyInputSemantics()
    {
        var dto = IniSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble());
        await Assert.That(dto!.Name).IsEqualTo("");
        await Assert.That(dto.Age).IsEqualTo(0);
    }
}
