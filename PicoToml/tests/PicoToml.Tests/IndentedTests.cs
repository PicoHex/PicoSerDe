namespace PicoToml.Tests;

/// <summary>
/// `TomlOptions.Indented` must indent table contents and still round-trip.
/// </summary>
public class IndentedTests
{
    [Test]
    public async Task Indented_IndentsTableContents()
    {
        var dto = new IndentedTomlDto
        {
            Name = "root",
            Sec = new IndentedTomlSub { X = 1 },
        };
        var text = TomlSerializer.Serialize(dto, new TomlOptions { Indented = true });
        await Assert.That(text).Contains("\n  X = 1");
    }

    [Test]
    public async Task Indented_RoundTrips()
    {
        var dto = new IndentedTomlDto
        {
            Name = "root",
            Sec = new IndentedTomlSub { X = 1 },
        };
        var text = TomlSerializer.Serialize(dto, new TomlOptions { Indented = true });
        var back = TomlSerializer.Deserialize<IndentedTomlDto>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Sec.X).IsEqualTo(1);
    }

    [Test]
    public async Task Default_StaysCompact()
    {
        var dto = new IndentedTomlDto
        {
            Name = "root",
            Sec = new IndentedTomlSub { X = 1 },
        };
        var text = TomlSerializer.Serialize(dto);
        await Assert.That(text).Contains("\nX = 1");
        await Assert.That(text).DoesNotContain("\n  X = 1");
    }

    [Test]
    public async Task AnonymousNestedObject_EmitsTable()
    {
        var toml = TomlSerializer.Serialize(new { Outer = new { X = 1 } });
        await Assert.That(toml).Contains("[Outer]");
        await Assert.That(toml).Contains("X = 1");
    }

    [Test]
    public async Task Indented_AnonymousType_IndentsTableContents()
    {
        var toml = TomlSerializer.Serialize(
            new { Outer = new { X = 1 } },
            new TomlOptions { Indented = true }
        );
        await Assert.That(toml).Contains("\n  X = 1");
    }
}

internal sealed class IndentedTomlDto
{
    public string Name { get; set; } = "";
    public IndentedTomlSub Sec { get; set; } = new();
}

internal sealed class IndentedTomlSub
{
    public int X { get; set; }
}
