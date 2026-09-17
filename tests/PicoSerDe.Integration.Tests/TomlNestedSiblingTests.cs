using System.Text;
using PicoToml;

namespace PicoSerDe.Integration.Tests;

public class NestedSiblingFirst
{
    public string Name { get; set; } = "";
}

public class NestedSiblingSecond
{
    public int Count { get; set; }
}

public class NestedSiblingDoc
{
    public NestedSiblingFirst? First { get; set; }
    public NestedSiblingSecond? Second { get; set; }
}

/// <summary>
/// TOML is section-based and emits no ObjectEnd token: an inner helper must stop
/// at the next section header instead of consuming the rest of the document.
/// Regression guard for sibling nested objects (the second one used to be lost).
/// </summary>
public class TomlNestedSiblingTests
{
    private static NestedSiblingDoc MakeDoc() =>
        new()
        {
            First = new NestedSiblingFirst { Name = "one" },
            Second = new NestedSiblingSecond { Count = 42 },
        };

    [Test]
    public async Task Sync_TwoNestedSiblings_RoundTrip()
    {
        var doc = MakeDoc();
        var toml = TomlSerializer.Serialize(doc);
        var back = TomlSerializer.Deserialize<NestedSiblingDoc>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back!.First?.Name).IsEqualTo("one");
        await Assert.That(back.Second?.Count).IsEqualTo(42);
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(4096)]
    public async Task Stream_TwoNestedSiblings_RoundTrip(int chunk)
    {
        var doc = MakeDoc();
        var toml = TomlSerializer.Serialize(doc);
        using var s = new SkChunkedStream(Encoding.UTF8.GetBytes(toml), chunk);
        var back = await TomlSerializer.DeserializeFromStreamAsync<NestedSiblingDoc>(s);

        await Assert.That(back!.First?.Name).IsEqualTo("one");
        await Assert.That(back.Second?.Count).IsEqualTo(42);
    }
}
