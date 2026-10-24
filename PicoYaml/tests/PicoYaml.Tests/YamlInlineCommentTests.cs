// TDD: inline comments must be stripped; a document-final empty nested object must not spin.
namespace PicoYaml.Tests;

public class YamlInlineTextDto
{
    public string Name { get; set; } = "";
}

public class YamlInlineNumDto
{
    public int Count { get; set; }
}

public class YamlInlineInner
{
    public string City { get; set; } = "";
}

public class YamlInlineNestedDto
{
    public YamlInlineInner Nested { get; set; } = new();
    public int Count { get; set; }
}

public class YamlInlineCommentTests
{
    [Test]
    public async Task InlineComment_OnString_IsStripped() =>
        await Assert
            .That(
                YamlSerializer
                    .Deserialize<YamlInlineTextDto>(Encoding.UTF8.GetBytes("Name: v # note"))!
                    .Name
            )
            .IsEqualTo("v");

    [Test]
    public async Task InlineComment_OnInt_IsStripped() =>
        await Assert
            .That(
                YamlSerializer
                    .Deserialize<YamlInlineNumDto>(Encoding.UTF8.GetBytes("Count: 5 # note"))!
                    .Count
            )
            .IsEqualTo(5);

    [Test]
    public async Task EmptyNestedObjectAtEnd_DoesNotSpin()
    {
        var v = YamlSerializer.Deserialize<YamlInlineNestedDto>(Encoding.UTF8.GetBytes("Nested:"));
        await Assert.That(v!.Nested.City).IsEqualTo("");
    }

    [Test]
    public async Task NestedBlockWithSibling_KeepsBoth()
    {
        var v = YamlSerializer.Deserialize<YamlInlineNestedDto>(
            Encoding.UTF8.GetBytes("Nested:\n  City: X\nCount: 3")
        );
        await Assert.That(v!.Nested.City).IsEqualTo("X");
        await Assert.That(v.Count).IsEqualTo(3);
    }
}
