namespace PicoSerDe.Integration.Tests;

public class NullableElemObj
{
    public string Name { get; set; } = "";
}

public class NullableElemHolder
{
    public List<int?> Nums { get; set; } = new();
    public List<string?> Texts { get; set; } = new();
    public List<NullableElemObj?> Objs { get; set; } = new();
    public Dictionary<string, NullableElemObj?> Map { get; set; } = new();
}

/// <summary>
/// Nullable element types (<c>List&lt;int?&gt;</c>, <c>List&lt;string?&gt;</c>,
/// <c>List&lt;TObject?&gt;</c>) must compile and round-trip, including null
/// elements. Broken before the fix: MSgPack/JSON generated wrong element types
/// (CS1503/CS0029/CS8619) and JSON passed possibly-null elements to the
/// custom-serializer / inner-helper call (CS8604 → NRE at runtime).
/// </summary>
public class NullableElementTests
{
    private static NullableElemHolder Sample() =>
        new()
        {
            Nums = new List<int?> { 1, null, 3 },
            Texts = new List<string?> { "a", null },
            Objs = new List<NullableElemObj?>
            {
                new NullableElemObj { Name = "x" },
                null,
            },
            Map = new Dictionary<string, NullableElemObj?>
            {
                ["k"] = new NullableElemObj { Name = "m" },
                ["n"] = null,
            },
        };

    [Test]
    public async Task Json_NullableElements_RoundTrip()
    {
        var root = Sample();
        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<NullableElemHolder>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );

        await Assert.That(back!.Nums.Count).IsEqualTo(3);
        await Assert.That(back.Nums[1]).IsNull();
        await Assert.That(back.Nums[2]).IsEqualTo(3);
        await Assert.That(back.Texts[1]).IsNull();
        await Assert.That(back.Objs[1]).IsNull();
        await Assert.That(back.Objs[0]!.Name).IsEqualTo("x");
        await Assert.That(back.Map["n"]).IsNull();
        await Assert.That(back.Map["k"]!.Name).IsEqualTo("m");
    }

    [Test]
    public async Task MsgPack_NullableElements_RoundTrip()
    {
        var root = Sample();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(root);
        var back = MsgPackSerializer.Deserialize<NullableElemHolder>(bytes);

        await Assert.That(back!.Nums[1]).IsNull();
        await Assert.That(back.Texts[1]).IsNull();
        await Assert.That(back.Objs[1]).IsNull();
        await Assert.That(back.Map["n"]).IsNull();
    }

    [Test]
    [Arguments("toml")]
    [Arguments("yaml")]
    [Arguments("ini")]
    public async Task SectionFormats_DropNullableElementMembers(string format)
    {
        var root = Sample();
        var text = format switch
        {
            "toml" => TomlSerializer.Serialize(root),
            "yaml" => YamlSerializer.Serialize(root),
            _ => IniSerializer.Serialize(root),
        };
        await Assert.That(text).DoesNotContain("Nums");

        var doc = format switch
        {
            "toml" => "Texts = [\"a\"]\n"u8.ToArray(),
            "yaml" => "Texts: [a]\n"u8.ToArray(),
            _ => "Texts = a\n"u8.ToArray(),
        };
        var back = format switch
        {
            "toml" => TomlSerializer.Deserialize<NullableElemHolder>(doc),
            "yaml" => YamlSerializer.Deserialize<NullableElemHolder>(doc),
            _ => IniSerializer.Deserialize<NullableElemHolder>(doc),
        };
        await Assert.That(back).IsNotNull().Because(format);
    }
}
