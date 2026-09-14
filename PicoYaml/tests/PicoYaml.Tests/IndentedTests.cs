namespace PicoYaml.Tests;

/// <summary>
/// `YamlOptions.Indented` must indent sequence items under their key and still
/// round-trip; nested mappings are already indented by the writer.
/// </summary>
public class IndentedTests
{
    [Test]
    public async Task Indented_IndentsSequenceItems()
    {
        var dto = new IndentedYamlDto { Name = "root", Tags = ["a", "b"] };
        var text = YamlSerializer.Serialize(dto, new YamlOptions { Indented = true });
        await Assert.That(text).Contains("\n  - a");
    }

    [Test]
    public async Task Indented_RoundTrips()
    {
        var dto = new IndentedYamlDto { Name = "root", Tags = ["a", "b"] };
        var text = YamlSerializer.Serialize(dto, new YamlOptions { Indented = true });
        var back = YamlSerializer.Deserialize<IndentedYamlDto>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Tags).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Default_StaysCompact()
    {
        var dto = new IndentedYamlDto { Name = "root", Tags = ["a", "b"] };
        var text = YamlSerializer.Serialize(dto);
        await Assert.That(text).Contains("\n- a");
        await Assert.That(text).DoesNotContain("\n  - a");
    }
}

internal sealed class IndentedYamlDto
{
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}
