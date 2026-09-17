namespace PicoSerDe.Integration.Tests;

public class RvDictValueList
{
    public string Name { get; set; } = "";
    public Dictionary<string, List<int>> Map { get; set; } = new();
}

/// <summary>
/// Dictionary values that are collections are not representable by the
/// generated dict emitters (previously non-compiling in JSON/TOML/YAML and
/// silently lossy in MsgPack); they are dropped with PICOSERDE004 instead.
/// </summary>
public class DictValueCollectionTests
{
    private static RvDictValueList Sample() =>
        new()
        {
            Name = "n",
            Map = new()
            {
                ["k"] = new() { 1, 2 },
            },
        };

    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    [Arguments("ini")]
    public async Task DictValueList_IsDropped_WithDiagnostic(string format)
    {
        var v = Sample();
        var text = format switch
        {
            "json" => JsonSerializer.Serialize(v),
            "msgpack" => Convert.ToBase64String(MsgPackSerializer.SerializeToUtf8Bytes(v)),
            "toml" => TomlSerializer.Serialize(v),
            "yaml" => YamlSerializer.Serialize(v),
            _ => IniSerializer.Serialize(v),
        };
        await Assert.That(text).DoesNotContain("k").Because(format);

        var back = format switch
        {
            "json" => JsonSerializer.Deserialize<RvDictValueList>(
                System.Text.Encoding.UTF8.GetBytes(text)
            ),
            "msgpack" => MsgPackSerializer.Deserialize<RvDictValueList>(
                Convert.FromBase64String(text)
            ),
            "toml" => TomlSerializer.Deserialize<RvDictValueList>(
                System.Text.Encoding.UTF8.GetBytes(text)
            ),
            "yaml" => YamlSerializer.Deserialize<RvDictValueList>(
                System.Text.Encoding.UTF8.GetBytes(text)
            ),
            _ => IniSerializer.Deserialize<RvDictValueList>(
                System.Text.Encoding.UTF8.GetBytes(text)
            ),
        };
        await Assert.That(back!.Name).IsEqualTo("n").Because(format);
        await Assert.That(back.Map.Count).IsEqualTo(0).Because(format);
    }
}
