using System.Text;

namespace PicoSerDe.Integration.Tests;

public class NestedPlain
{
    public string Name { get; set; } = "";
}

public class NestedListPlain
{
    public string Name { get; set; } = "";
    public List<List<NestedPlain>> Rows { get; set; } = new();
}

public class NestedListNode
{
    public string Name { get; set; } = "";
    public List<List<NestedListNode>> Rows { get; set; } = new();
}

/// <summary>
/// Nested lists of objects (<c>List&lt;List&lt;TObject&gt;&gt;</c>) must generate
/// compilable code and round-trip. Broken before the fix: the object element's
/// inner helper was never emitted (CS0234) and the inner list was constructed
/// with the wrong element type (CS1503).
/// </summary>
public class NestedListOfObjectsTests
{
    [Test]
    public async Task Json_NestedListOfPlainObjects_RoundTrips()
    {
        var root = new NestedListPlain
        {
            Name = "root",
            Rows = new List<List<NestedPlain>>
            {
                new()
                {
                    new NestedPlain { Name = "a" },
                    new NestedPlain { Name = "b" },
                },
                new() { new NestedPlain { Name = "c" } },
            },
        };

        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<NestedListPlain>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Rows.Count).IsEqualTo(2);
        await Assert.That(back.Rows[0][1].Name).IsEqualTo("b");
        await Assert.That(back.Rows[1][0].Name).IsEqualTo("c");
    }

    [Test]
    public async Task Json_NestedListOfRecursiveObjects_RoundTrips()
    {
        var root = new NestedListNode
        {
            Name = "root",
            Rows = new List<List<NestedListNode>>
            {
                new() { new NestedListNode { Name = "child" } },
            },
        };

        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<NestedListNode>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Rows[0][0].Name).IsEqualTo("child");
    }

    [Test]
    public async Task MsgPack_NestedListOfPlainObjects_RoundTrips()
    {
        var root = new NestedListPlain
        {
            Name = "root",
            Rows = new List<List<NestedPlain>> { new() { new NestedPlain { Name = "a" } } },
        };

        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(root);
        var back = MsgPackSerializer.Deserialize<NestedListPlain>(bytes);

        await Assert.That(back!.Rows[0][0].Name).IsEqualTo("a");
    }

    [Test]
    [Arguments("toml")]
    [Arguments("yaml")]
    [Arguments("ini")]
    public async Task SectionFormats_DropNestedListMember(string format)
    {
        var root = new NestedListPlain
        {
            Name = "root",
            Rows = new List<List<NestedPlain>> { new() { new NestedPlain { Name = "a" } } },
        };

        // Serializing must not throw and must not emit the unsupported member.
        var text = format switch
        {
            "toml" => TomlSerializer.Serialize(root),
            "yaml" => YamlSerializer.Serialize(root),
            _ => IniSerializer.Serialize(root),
        };
        await Assert.That(text).DoesNotContain("Rows");

        // Deserializing a document without the member yields an empty collection.
        var doc = format switch
        {
            "toml" => "Name = \"root\"\n"u8.ToArray(),
            "yaml" => "Name: root\n"u8.ToArray(),
            _ => "Name = root\n"u8.ToArray(),
        };
        var back = format switch
        {
            "toml" => TomlSerializer.Deserialize<NestedListPlain>(doc),
            "yaml" => YamlSerializer.Deserialize<NestedListPlain>(doc),
            _ => IniSerializer.Deserialize<NestedListPlain>(doc),
        };
        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Rows.Count).IsEqualTo(0).Because(format);
    }

    [Test]
    public async Task Json_Streaming_NestedListOfPlainObjects()
    {
        var root = new NestedListPlain
        {
            Name = "root",
            Rows = new List<List<NestedPlain>> { new() { new NestedPlain { Name = "a" } } },
        };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(root));

        foreach (var chunk in new[] { 1, 3, 8 })
        {
            using var stream = new SkChunkedStream(json, chunk);
            var back = await JsonSerializer.DeserializeFromStreamAsync<NestedListPlain>(
                stream,
                new PicoJetson.JsonOptions(),
                default
            );
            await Assert.That(back!.Rows[0][0].Name).IsEqualTo("a").Because($"chunk={chunk}");
        }
    }
}
