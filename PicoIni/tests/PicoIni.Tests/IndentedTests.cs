namespace PicoIni.Tests;

/// <summary>
/// `IniOptions.Indented` must indent section contents and still round-trip.
/// </summary>
public class IndentedTests
{
    [Test]
    public async Task Indented_IndentsSectionContents()
    {
        var dto = new IndentedIniDto
        {
            Name = "root",
            Sec = new IndentedIniSub { X = 1 },
        };
        var text = IniSerializer.Serialize(dto, new IniOptions { Indented = true });
        await Assert.That(text).Contains("\n  X = 1");
    }

    [Test]
    public async Task Indented_RoundTrips()
    {
        var dto = new IndentedIniDto
        {
            Name = "root",
            Sec = new IndentedIniSub { X = 1 },
        };
        var text = IniSerializer.Serialize(dto, new IniOptions { Indented = true });
        var back = IniSerializer.Deserialize<IndentedIniDto>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Sec.X).IsEqualTo(1);
    }

    [Test]
    public async Task Default_StaysCompact()
    {
        var dto = new IndentedIniDto
        {
            Name = "root",
            Sec = new IndentedIniSub { X = 1 },
        };
        var text = IniSerializer.Serialize(dto);
        await Assert.That(text).Contains("\nX = 1");
        await Assert.That(text).DoesNotContain("\n  X = 1");
    }

    [Test]
    public async Task AnonymousNestedObject_EmitsSection()
    {
        var ini = IniSerializer.Serialize(new { Outer = new { X = 1 } });
        await Assert.That(ini).Contains("[Outer]");
        await Assert.That(ini).Contains("X = 1");
    }

    [Test]
    public async Task Indented_AnonymousType_IndentsSectionContents()
    {
        var ini = IniSerializer.Serialize(
            new { Outer = new { X = 1 } },
            new IniOptions { Indented = true }
        );
        await Assert.That(ini).Contains("\n  X = 1");
    }
}

internal sealed class IndentedIniDto
{
    public string Name { get; set; } = "";
    public IndentedIniSub Sec { get; set; } = new();
}

internal sealed class IndentedIniSub
{
    public int X { get; set; }
}
