namespace PicoSerDe.Integration.Tests;

public record RvRecordPlain(int Id, string Name);

public record RvRecordList(string Name, List<int> Nums);

public record RvRecordObjectArray(string Name, RvLeaf[] Items);

/// <summary>
/// Records (primary constructors) must work in every format, not only JSON:
/// MsgPack/TOML/YAML/INI previously required a format-specific constructor
/// attribute and emitted `new T()` + init-only assignments (CS7036/CS8852).
/// </summary>
public class RecordSupportTests
{
    [Test]
    public async Task PlainRecord_AllFormats()
    {
        var v = new RvRecordPlain(1, "a");

        var json = JsonSerializer.Deserialize<RvRecordPlain>(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(v))
        );
        await Assert.That(json!.Id).IsEqualTo(1);

        var mp = MsgPackSerializer.Deserialize<RvRecordPlain>(
            MsgPackSerializer.SerializeToUtf8Bytes(v)
        );
        await Assert.That(mp!.Id).IsEqualTo(1);

        var toml = TomlSerializer.Deserialize<RvRecordPlain>(
            System.Text.Encoding.UTF8.GetBytes(TomlSerializer.Serialize(v))
        );
        await Assert.That(toml!.Id).IsEqualTo(1);

        var yaml = YamlSerializer.Deserialize<RvRecordPlain>(
            System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(v))
        );
        await Assert.That(yaml!.Id).IsEqualTo(1);

        var ini = IniSerializer.Deserialize<RvRecordPlain>(
            System.Text.Encoding.UTF8.GetBytes(IniSerializer.Serialize(v))
        );
        await Assert.That(ini!.Id).IsEqualTo(1);
    }

    [Test]
    public async Task RecordWithListMember_AllFormats()
    {
        var v = new RvRecordList("b", new() { 1, 2 });

        var json = JsonSerializer.Deserialize<RvRecordList>(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(v))
        );
        await Assert.That(json!.Nums.Count).IsEqualTo(2);

        var mp = MsgPackSerializer.Deserialize<RvRecordList>(
            MsgPackSerializer.SerializeToUtf8Bytes(v)
        );
        await Assert.That(mp!.Nums.Count).IsEqualTo(2);

        var toml = TomlSerializer.Deserialize<RvRecordList>(
            System.Text.Encoding.UTF8.GetBytes(TomlSerializer.Serialize(v))
        );
        await Assert.That(toml!.Nums.Count).IsEqualTo(2);

        var yaml = YamlSerializer.Deserialize<RvRecordList>(
            System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(v))
        );
        await Assert.That(yaml!.Nums.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RecordWithObjectArray_JsonMsgPackTomlYaml()
    {
        var v = new RvRecordObjectArray("n", new[] { new RvLeaf { Name = "a" } });

        var json = JsonSerializer.Deserialize<RvRecordObjectArray>(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(v))
        );
        await Assert.That(json!.Items.Length).IsEqualTo(1);

        var mp = MsgPackSerializer.Deserialize<RvRecordObjectArray>(
            MsgPackSerializer.SerializeToUtf8Bytes(v)
        );
        await Assert.That(mp!.Items.Length).IsEqualTo(1);

        var toml = TomlSerializer.Deserialize<RvRecordObjectArray>(
            System.Text.Encoding.UTF8.GetBytes(TomlSerializer.Serialize(v))
        );
        await Assert.That(toml!.Items.Length).IsEqualTo(1);

        var yaml = YamlSerializer.Deserialize<RvRecordObjectArray>(
            System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(v))
        );
        await Assert.That(yaml!.Items.Length).IsEqualTo(1);
    }

    [Test]
    public async Task RecordWithListMember_Streaming()
    {
        var v = new RvRecordList("b", new() { 1, 2 });
        var json = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(v));
        using var stream = new SkChunkedStream(json, 3);
        var back = await JsonSerializer.DeserializeFromStreamAsync<RvRecordList>(stream);
        await Assert.That(back!.Nums.Count).IsEqualTo(2);
    }
}
