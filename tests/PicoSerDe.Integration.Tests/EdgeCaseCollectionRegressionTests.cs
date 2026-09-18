namespace PicoSerDe.Integration.Tests;

// ── review probes (batch A) ──

public class RvScalarArrElem
{
    public List<string[]> Tags { get; set; } = new();
    public List<int[]> Nums { get; set; } = new();
}

public class RvMpArrElem
{
    public List<RvLeaf[]> Blocks { get; set; } = new();
}

public class RvDictArrValue
{
    public string Name { get; set; } = "";
    public Dictionary<string, RvLeaf[]> Items { get; set; } = new();
}

public class RvNestedNullable
{
    public string Name { get; set; } = "";
    public List<InnerHolder> Items { get; set; } = new();
}

public class InnerHolder
{
    public List<List<int?>> Grid { get; set; } = new();
}

public class RvExtendedNullable
{
    public HashSet<int?> Set { get; set; } = new();
    public Queue<string?> Queue { get; set; } = new();
}

public class RvArrWrapper
{
    public string Name { get; set; } = "";
    public RvLeaf[] Items { get; set; } = Array.Empty<RvLeaf>();
}

public class RvNestedNullableObj
{
    public List<List<RvLeaf?>> Grid { get; set; } = new();
    public List<List<string?>> Names { get; set; } = new();
}

public class RvDeepArrays
{
    public RvLeaf[][][] Cube { get; set; } = Array.Empty<RvLeaf[][]>();
}

public record RvRecArrayField(string Name, RvLeaf[] Items);

public class RvCtorArrHolder
{
    public RvLeaf[] Items { get; set; } = Array.Empty<RvLeaf>();
}

public class RvNestedArrHolder
{
    public string Name { get; set; } = "";
    public InnerArrHolder Inner { get; set; } = new();
}

public class InnerArrHolder
{
    public RvLeaf[] Items { get; set; } = Array.Empty<RvLeaf>();
}

public class EdgeCaseCollectionRegressionTests
{
    [Test]
    public async Task P3_ScalarArrayElements_RoundTrip_Json()
    {
        var v = new RvScalarArrElem
        {
            Tags = new() { new[] { "a", "b" } },
            Nums = new() { new[] { 1, 2 } },
        };
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvScalarArrElem>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Tags[0][1]).IsEqualTo("b").Because("json=" + json);
        await Assert.That(back.Nums[0][1]).IsEqualTo(2);
    }

    [Test]
    public async Task P2_Json_Streaming_ArrayElement()
    {
        var v = new RvMpArrElem { Blocks = new() { new[] { new RvLeaf { Name = "a" } } } };
        var json = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(v));
        foreach (var chunk in new[] { 1, 7, 64 })
        {
            using var stream = new SkChunkedStream(json, chunk);
            var back = await JsonSerializer.DeserializeFromStreamAsync<RvMpArrElem>(stream);
            await Assert.That(back!.Blocks[0][0].Name).IsEqualTo("a").Because($"chunk={chunk}");
        }
    }

    [Test]
    public async Task P6_MsgPack_ArrayElementAndArrayOfArrays()
    {
        var elem = new RvMpArrElem { Blocks = new() { new[] { new RvLeaf { Name = "a" } } } };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(elem);
        var back = MsgPackSerializer.Deserialize<RvMpArrElem>(bytes);
        await Assert.That(back!.Blocks.Count).IsEqualTo(1).Because($"len={bytes.Length}");
        await Assert.That(back.Blocks[0][0].Name).IsEqualTo("a");
    }

    [Test]
    public async Task P7_DictArrayValue_IsDropped()
    {
        var v = new RvDictArrValue { Name = "n" };
        var json = JsonSerializer.Serialize(v);
        await Assert.That(json).DoesNotContain("Items");
        var back = JsonSerializer.Deserialize<RvDictArrValue>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Name).IsEqualTo("n");
    }

    [Test]
    public async Task P8_NestedNullableInNestedObjectMember()
    {
        var v = new RvNestedNullable
        {
            Name = "n",
            Items = new()
            {
                new InnerHolder
                {
                    Grid = new()
                    {
                        new() { 1, null },
                    },
                },
            },
        };
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvNestedNullable>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Items[0].Grid[0][1]).IsNull().Because("json=" + json);
        var mp = MsgPackSerializer.SerializeToUtf8Bytes(v);
        var mpBack = MsgPackSerializer.Deserialize<RvNestedNullable>(mp);
        await Assert.That(mpBack!.Items[0].Grid[0][1]).IsNull();
    }

    [Test]
    public async Task P9_ExtendedCollectionNullableElements()
    {
        var v = new RvExtendedNullable
        {
            Set = new HashSet<int?> { 1, null },
            Queue = new Queue<string?>(new[] { "a", null }),
        };
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvExtendedNullable>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Set.Contains(null)).IsTrue().Because("json=" + json);
        await Assert.That(back.Queue.Contains(null)).IsTrue();
    }

    [Test]
    public async Task P1_NestedNullableObjectsAndStrings()
    {
        var v = new RvNestedNullableObj
        {
            Grid = new()
            {
                new()
                {
                    new RvLeaf { Name = "a" },
                    null,
                },
            },
            Names = new()
            {
                new() { "x", null },
            },
        };
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvNestedNullableObj>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Grid[0][0]!.Name).IsEqualTo("a").Because("json=" + json);
        await Assert.That(back.Grid[0][1]).IsNull();
        await Assert.That(back.Names[0][1]).IsNull();
    }

    [Test]
    public async Task P1b_MsgPack_NestedNullableObjects()
    {
        var v = new RvNestedNullableObj
        {
            Grid = new()
            {
                new()
                {
                    new RvLeaf { Name = "a" },
                    null,
                },
            },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(v);
        var back = MsgPackSerializer.Deserialize<RvNestedNullableObj>(bytes);
        await Assert.That(back!.Grid[0][0]!.Name).IsEqualTo("a");
        await Assert.That(back.Grid[0][1]).IsNull();
    }

    [Test]
    public async Task P1c_Json_Streaming_NestedNullable()
    {
        var v = new RvNestedNullableObj
        {
            Grid = new()
            {
                new()
                {
                    new RvLeaf { Name = "a" },
                    null,
                },
            },
        };
        var json = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(v));
        foreach (var chunk in new[] { 1, 11, 64 })
        {
            using var stream = new SkChunkedStream(json, chunk);
            var back = await JsonSerializer.DeserializeFromStreamAsync<RvNestedNullableObj>(stream);
            await Assert.That(back!.Grid[0][1]).IsNull().Because($"chunk={chunk}");
        }
    }

    [Test]
    public async Task P5_RecordWithObjectArray()
    {
        var v = new RvRecArrayField("n", new[] { new RvLeaf { Name = "a" } });
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvRecArrayField>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.Items.Length).IsEqualTo(1).Because("json=" + json);
    }

    [Test]
    public async Task ObjectArrayInsideNestedMember_JsonMsgPackToml_YamlThrowsLoudly()
    {
        var v = new RvNestedArrHolder
        {
            Name = "n",
            Inner = new InnerArrHolder { Items = new[] { new RvLeaf { Name = "a" } } },
        };
        var json = JsonSerializer.Serialize(v);
        await Assert
            .That(
                JsonSerializer
                    .Deserialize<RvNestedArrHolder>(System.Text.Encoding.UTF8.GetBytes(json))!
                    .Inner.Items.Length
            )
            .IsEqualTo(1);
        var mp = MsgPackSerializer.SerializeToUtf8Bytes(v);
        await Assert
            .That(MsgPackSerializer.Deserialize<RvNestedArrHolder>(mp)!.Inner.Items.Length)
            .IsEqualTo(1);

        // YAML drops the outer member with PICOSERDE004 instead of silently
        // returning an empty list (its inner helper cannot read this shape).
        var yaml = YamlSerializer.Serialize(v);
        await Assert.That(yaml).DoesNotContain("Items").Because("yaml=" + yaml);
        var yamlBack = YamlSerializer.Deserialize<RvNestedArrHolder>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );
        await Assert.That(yamlBack!.Name).IsEqualTo("n");

        // TOML documents one nesting level: the deep member fails loudly (write or read).
        Exception? tomlError = null;
        try
        {
            var toml = TomlSerializer.Serialize(v);
            TomlSerializer.Deserialize<RvNestedArrHolder>(System.Text.Encoding.UTF8.GetBytes(toml));
        }
        catch (Exception ex)
        {
            tomlError = ex;
        }
        await Assert.That(tomlError).IsTypeOf<NotSupportedException>();
    }

    [Test]
    public async Task P4_Toml_Yaml_Streaming_TypeWithNestedArrayMember()
    {
        var v = new RvArrWrapper
        {
            Name = "n",
            Items = new[]
            {
                new RvLeaf { Name = "a" },
                new RvLeaf { Name = "b" },
            },
        };
        var toml = System.Text.Encoding.UTF8.GetBytes(TomlSerializer.Serialize(v));
        foreach (var chunk in new[] { 1, 9, 128 })
        {
            using var stream = new SkChunkedStream(toml, chunk);
            var back = await TomlSerializer.DeserializeFromStreamAsync<RvArrWrapper>(stream);
            await Assert.That(back!.Items.Length).IsEqualTo(2).Because($"toml chunk={chunk}");
        }
        var yaml = System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(v));
        foreach (var chunk in new[] { 1, 9, 128 })
        {
            using var stream = new SkChunkedStream(yaml, chunk);
            var back = await YamlSerializer.DeserializeFromStreamAsync<RvArrWrapper>(stream);
            await Assert.That(back!.Items.Length).IsEqualTo(2).Because($"yaml chunk={chunk}");
        }
    }
}
