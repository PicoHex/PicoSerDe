namespace PicoSerDe.Integration.Tests;

public class RvArrayHolder
{
    public string Name { get; set; } = "";
    public RvLeaf[] Items { get; set; } = Array.Empty<RvLeaf>();
}

public class RvArrayElementHolder
{
    public List<RvLeaf[]> Blocks { get; set; } = new();
}

public class RvArrayOfArraysHolder
{
    public RvLeaf[][] Matrix { get; set; } = Array.Empty<RvLeaf[]>();
}

/// <summary>
/// Field-level object arrays (RV-03): <c>TObject[]</c> previously generated
/// non-compiling code in TOML/YAML (List-only APIs on arrays) and array
/// elements (<c>List&lt;TObject[]&gt;</c>, <c>TObject[][]</c>) failed in JSON.
/// Arrays are now supported in JSON/MsgPack/TOML/YAML; INI keeps dropping
/// object collections because its format is flat. TOML arrays of tables have
/// no chunk-resume strategy for arrays, so their streaming delegate is not
/// registered (DeserializeFromStreamAsync fails loudly instead of silently
/// returning partial data).
/// </summary>
public class ArraySupportTests
{
    private static RvArrayHolder Sample() =>
        new()
        {
            Name = "n",
            Items = new[]
            {
                new RvLeaf { Name = "a" },
                new RvLeaf { Name = "b" },
            },
        };

    [Test]
    public async Task ObjectArrayField_RoundTrips_JsonMsgPackTomlYaml()
    {
        var v = Sample();

        var json = JsonSerializer.Serialize(v);
        var jb = JsonSerializer.Deserialize<RvArrayHolder>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(jb!.Items.Length).IsEqualTo(2);
        await Assert.That(jb.Items[1].Name).IsEqualTo("b");

        var mp = MsgPackSerializer.SerializeToUtf8Bytes(v);
        var mb = MsgPackSerializer.Deserialize<RvArrayHolder>(mp);
        await Assert.That(mb!.Items.Length).IsEqualTo(2);
        await Assert.That(mb.Items[1].Name).IsEqualTo("b");

        var toml = TomlSerializer.Serialize(v);
        await Assert.That(toml).Contains("Items");
        var tb = TomlSerializer.Deserialize<RvArrayHolder>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );
        await Assert.That(tb!.Items.Length).IsEqualTo(2);
        await Assert.That(tb.Items[1].Name).IsEqualTo("b");

        var yaml = YamlSerializer.Serialize(v);
        var yb = YamlSerializer.Deserialize<RvArrayHolder>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );
        await Assert.That(yb!.Items.Length).IsEqualTo(2);
        await Assert.That(yb.Items[1].Name).IsEqualTo("b");
    }

    [Test]
    public async Task ArrayElementAndArrayOfArrays_RoundTrip_Json()
    {
        var elem = new RvArrayElementHolder
        {
            Blocks = new() { new[] { new RvLeaf { Name = "a" } } },
        };
        var elemJson = JsonSerializer.Serialize(elem);
        var elemBack = JsonSerializer.Deserialize<RvArrayElementHolder>(
            System.Text.Encoding.UTF8.GetBytes(elemJson)
        );
        await Assert.That(elemBack!.Blocks.Count).IsEqualTo(1);
        await Assert.That(elemBack.Blocks[0][0].Name).IsEqualTo("a");

        var matrix = new RvArrayOfArraysHolder
        {
            Matrix = new[] { new[] { new RvLeaf { Name = "m" } } },
        };
        var matrixJson = JsonSerializer.Serialize(matrix);
        var matrixBack = JsonSerializer.Deserialize<RvArrayOfArraysHolder>(
            System.Text.Encoding.UTF8.GetBytes(matrixJson)
        );
        await Assert.That(matrixBack!.Matrix[0][0].Name).IsEqualTo("m");
    }

    [Test]
    public async Task Toml_ObjectArray_Streaming_UsesBufferingFallback()
    {
        var v = Sample();
        var text = TomlSerializer.Serialize(v);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

        // Arrays of tables have no chunk-resume strategy, so the streaming
        // delegate is not registered: TomlSerializer buffers the payload and
        // uses the always-correct sync path (documented non-incremental case).
        var back = await TomlSerializer.DeserializeFromStreamAsync<RvArrayHolder>(stream);

        await Assert.That(back!.Items.Length).IsEqualTo(2);
        await Assert.That(back.Items[1].Name).IsEqualTo("b");
    }

    [Test]
    public async Task Ini_ObjectArray_IsDropped()
    {
        var v = Sample();
        var ini = IniSerializer.Serialize(v);
        await Assert.That(ini).DoesNotContain("Items");
        var back = IniSerializer.Deserialize<RvArrayHolder>(
            System.Text.Encoding.UTF8.GetBytes(ini)
        );
        await Assert.That(back!.Name).IsEqualTo("n");
        await Assert.That(back.Items.Length).IsEqualTo(0);
    }
}
