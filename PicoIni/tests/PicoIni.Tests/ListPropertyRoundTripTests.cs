namespace PicoIni.Tests;

/// <summary>
/// Review follow-up: nested list properties must compile and round-trip for
/// every element kind. Object-element lists are not representable in INI and
/// are dropped instead of emitting non-compiling code.
/// </summary>
public class ListPropertyRoundTripTests
{
    [Test]
    public async Task IntList_RoundTrips()
    {
        var dto = new IniIntListDto { Numbers = [1, 42, -7] };
        var text = IniSerializer.Serialize(dto);
        await Assert.That(text).Contains("Numbers = 1,42,-7");

        var back = IniSerializer.Deserialize<IniIntListDto>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Numbers).IsEquivalentTo([1, 42, -7]);
    }

    [Test]
    public async Task StringList_WithEscapedComma_RoundTrips()
    {
        var dto = new IniStringListDto { Tags = ["a,b", "c\\d"] };
        var text = IniSerializer.Serialize(dto);

        var back = IniSerializer.Deserialize<IniStringListDto>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Tags).IsEquivalentTo(["a,b", "c\\d"]);
    }

    [Test]
    public async Task ObjectListProperty_IsIgnoredWithoutCompileBreak()
    {
        var dto = new IniObjectListDto { Name = "x", Items = [new IniListItem { X = 1 }] };
        var text = IniSerializer.Serialize(dto);
        await Assert.That(text).Contains("Name = x");

        var back = IniSerializer.Deserialize<IniObjectListDto>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Name).IsEqualTo("x");
    }
}

internal sealed class IniIntListDto
{
    public List<int> Numbers { get; set; } = [];
}

internal sealed class IniStringListDto
{
    public List<string> Tags { get; set; } = [];
}

internal sealed class IniObjectListDto
{
    public string Name { get; set; } = "";
    public List<IniListItem> Items { get; set; } = [];
}

internal sealed class IniListItem
{
    public int X { get; set; }
}
