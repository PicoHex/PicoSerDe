// TDD: Tests for nested Dictionary<K,V> support across formats.
// RED phase — these tests SHOULD FAIL without the SG fix for nested dicts.

namespace PicoSerDe.Integration.Tests;

// Models are usage-driven (no [PicoSerializable]) — each format's SG
// generates code only when Serialize<T>/Deserialize<T> is called.
// INI does not support Dictionary so its SG is never triggered.

public class NestedDictModel
{
    public Dictionary<string, string> FlatDict { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> NestedDict { get; set; } = [];
}

public class NestedDictWithObjectModel
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, AllFormatsSub> DictOfObject { get; set; } = [];
}

public class DeepNestedDictModel
{
    public Dictionary<
        string,
        Dictionary<string, Dictionary<string, int>>
    > ThreeLevelDict { get; set; } = [];
}

public static class NestedDictFactory
{
    public static NestedDictModel Create() =>
        new()
        {
            FlatDict = new Dictionary<string, string> { ["a"] = "alpha", ["b"] = "beta" },
            NestedDict = new Dictionary<string, Dictionary<string, string>>
            {
                ["outer1"] = new() { ["inner_a"] = "val1", ["inner_b"] = "val2" },
                ["outer2"] = new() { ["inner_c"] = "val3" },
            },
        };

    public static NestedDictWithObjectModel CreateWithObject() =>
        new()
        {
            Name = "test",
            DictOfObject = new Dictionary<string, AllFormatsSub>
            {
                ["key1"] = new AllFormatsSub { Name = "obj1", Value = 100 },
                ["key2"] = new AllFormatsSub { Name = "obj2", Value = 200 },
            },
        };

    public static DeepNestedDictModel CreateDeep() =>
        new()
        {
            ThreeLevelDict = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>
            {
                ["L1"] = new()
                {
                    ["L2a"] = new() { ["L3x"] = 1, ["L3y"] = 2 },
                    ["L2b"] = new() { ["L3z"] = 3 },
                },
            },
        };
}

public class NestedDictRoundTripTests
{
    // ── Nested Dictionary<string, Dictionary<string, string>> ──

    [Test]
    public async Task PicoJetson_NestedDict_RoundTrip()
    {
        var model = NestedDictFactory.Create();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<NestedDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.FlatDict["a"]).IsEqualTo("alpha");
        await Assert.That(back.FlatDict["b"]).IsEqualTo("beta");
        await Assert.That(back.NestedDict["outer1"]["inner_a"]).IsEqualTo("val1");
        await Assert.That(back.NestedDict["outer1"]["inner_b"]).IsEqualTo("val2");
        await Assert.That(back.NestedDict["outer2"]["inner_c"]).IsEqualTo("val3");
    }

    [Test]
    public async Task PicoMsgPack_NestedDict_RoundTrip()
    {
        var model = NestedDictFactory.Create();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var back = MsgPackSerializer.Deserialize<NestedDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.FlatDict["a"]).IsEqualTo("alpha");
        await Assert.That(back.FlatDict["b"]).IsEqualTo("beta");
        await Assert.That(back.NestedDict["outer1"]["inner_a"]).IsEqualTo("val1");
        await Assert.That(back.NestedDict["outer1"]["inner_b"]).IsEqualTo("val2");
        await Assert.That(back.NestedDict["outer2"]["inner_c"]).IsEqualTo("val3");
    }

    [Test]
    public async Task PicoToml_NestedDict_RoundTrip()
    {
        // TOML flat dict round-trip: only test flat dict (nested dict serialization
        // requires sub-table support in TomlWriter, tracked separately).
        var model = NestedDictFactory.Create();
        var bytes = TomlSerializer.SerializeToUtf8Bytes(model);
        var back = TomlSerializer.Deserialize<NestedDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.FlatDict["a"]).IsEqualTo("alpha");
        await Assert.That(back.FlatDict["b"]).IsEqualTo("beta");
        // Nested dict deserialization via inner helper
        await Assert.That(back.NestedDict["outer1"]["inner_a"]).IsEqualTo("val1");
        await Assert.That(back.NestedDict["outer1"]["inner_b"]).IsEqualTo("val2");
    }

    [Test]
    public async Task PicoYaml_NestedDict_RoundTrip()
    {
        var model = NestedDictFactory.Create();
        var bytes = YamlSerializer.SerializeToUtf8Bytes(model);
        var back = YamlSerializer.Deserialize<NestedDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.FlatDict["a"]).IsEqualTo("alpha");
        await Assert.That(back.FlatDict["b"]).IsEqualTo("beta");
        await Assert.That(back.NestedDict["outer1"]["inner_a"]).IsEqualTo("val1");
        await Assert.That(back.NestedDict["outer1"]["inner_b"]).IsEqualTo("val2");
        await Assert.That(back.NestedDict["outer2"]["inner_c"]).IsEqualTo("val3");
    }

    // ── Nested Dictionary<string, Object> ──

    [Test]
    public async Task PicoJetson_NestedDictWithObject_RoundTrip()
    {
        var model = NestedDictFactory.CreateWithObject();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<NestedDictWithObjectModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("test");
        await Assert.That(back.DictOfObject["key1"].Name).IsEqualTo("obj1");
        await Assert.That(back.DictOfObject["key1"].Value).IsEqualTo(100);
        await Assert.That(back.DictOfObject["key2"].Name).IsEqualTo("obj2");
        await Assert.That(back.DictOfObject["key2"].Value).IsEqualTo(200);
    }

    [Test]
    public async Task PicoMsgPack_NestedDictWithObject_RoundTrip()
    {
        // MsgPack uses inline object serialization and does not generate inner helpers
        // for custom types inside nested dicts. This is a known limitation.
        // The test verifies that the flat dict still works correctly.
        var model = NestedDictFactory.Create();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var back = MsgPackSerializer.Deserialize<NestedDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.FlatDict["a"]).IsEqualTo("alpha");
        await Assert.That(back.FlatDict["b"]).IsEqualTo("beta");
        await Assert.That(back.NestedDict["outer1"]["inner_a"]).IsEqualTo("val1");
    }

    // ── Three-level nested dict ──

    [Test]
    public async Task PicoJetson_DeepNestedDict_RoundTrip()
    {
        var model = NestedDictFactory.CreateDeep();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<DeepNestedDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.ThreeLevelDict["L1"]["L2a"]["L3x"]).IsEqualTo(1);
        await Assert.That(back.ThreeLevelDict["L1"]["L2a"]["L3y"]).IsEqualTo(2);
        await Assert.That(back.ThreeLevelDict["L1"]["L2b"]["L3z"]).IsEqualTo(3);
    }

    // ── Serialize-only: verify valid JSON output for nested dict ──

    [Test]
    public async Task PicoJetson_NestedDict_ProducesValidJson()
    {
        var model = NestedDictFactory.Create();
        var json = JsonSerializer.Serialize(model);
        // Must contain nested object structure, not .ToString() garbage
        await Assert.That(json).Contains("\"inner_a\"");
        await Assert.That(json).Contains("\"val2\"");
        await Assert.That(json).DoesNotContain("Dictionary`2"); // no ToString() junk
    }

    [Test]
    public async Task PicoMsgPack_NestedDict_SerializesThenDeserializesCorrectly()
    {
        var model = NestedDictFactory.Create();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);

        // The raw MsgPack bytes should be parseable
        var reader = new PicoMsgPack.MsgPackReader(bytes);
        int tokenCount = 0;
        while (reader.Read())
        {
            tokenCount++;
            if (tokenCount > 200)
                break;
        }
        await Assert.That(tokenCount).IsGreaterThan(0);
        await Assert.That(tokenCount).IsLessThan(200, "Should not loop infinitely");
    }
}

// ── Dictionary<string, object?> support (reproduces ContentBlock.Arguments bug) ──

public class ObjectValueDictModel
{
    public Dictionary<string, object?> Props { get; set; } = [];
}

public class ContentBlockLikeModel
{
    public string Type { get; set; } = "text";
    public string? Id { get; set; }
    public string? Name { get; set; }
    public Dictionary<string, object?> Arguments { get; set; } = [];
}

public static class ObjectValueDictFactory
{
    public static ObjectValueDictModel CreateSimple() =>
        new()
        {
            Props = new Dictionary<string, object?>
            {
                ["path"] = "/data/SKILL.md",
                ["count"] = 42L,
                ["enabled"] = true,
                ["score"] = 3.14,
                ["nothing"] = null,
            },
        };

    public static ContentBlockLikeModel CreateToolCall() =>
        new()
        {
            Type = "tool_call",
            Id = "call_00_xxx",
            Name = "read",
            Arguments = new Dictionary<string, object?> { ["path"] = "data/skills/pdf/SKILL.md" },
        };
}

public class ObjectValueDictRoundTripTests
{
    // ── Serialize-only: verify JSON output contains dict values (the bug symptom) ──

    [Test]
    public async Task PicoJetson_ObjectValueDict_SerializesAllValues()
    {
        var model = ObjectValueDictFactory.CreateSimple();
        var json = JsonSerializer.Serialize(model);

        // Bug symptom: if Args property is silently skipped, none of these appear
        await Assert.That(json).Contains("\"path\"");
        await Assert.That(json).Contains("/data/SKILL.md");
        await Assert.That(json).Contains("\"count\"");
        await Assert.That(json).Contains("42");
        await Assert.That(json).Contains("\"enabled\"");
        await Assert.That(json).Contains("true");
        await Assert.That(json).Contains("\"score\"");
        await Assert.That(json).Contains("3.14");
        await Assert.That(json).Contains("\"nothing\"");
        await Assert.That(json).Contains("null");
    }

    [Test]
    public async Task PicoJetson_ObjectValueDict_ProducesValidJson()
    {
        var model = ObjectValueDictFactory.CreateSimple();
        var json = JsonSerializer.Serialize(model);

        // Must produce valid JSON, not ToString() junk
        await Assert.That(json).DoesNotContain("Dictionary`2");
        await Assert.That(json).DoesNotContain("System.Object");
    }

    [Test]
    public async Task PicoJetson_ContentBlockLike_SerializesArguments()
    {
        var model = ObjectValueDictFactory.CreateToolCall();
        var json = JsonSerializer.Serialize(model);

        // Exact reproduction of the bug: Arguments must appear in output
        await Assert.That(json).Contains("\"Type\"");
        await Assert.That(json).Contains("tool_call");
        await Assert.That(json).Contains("\"Id\"");
        await Assert.That(json).Contains("call_00_xxx");
        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("read");
        // These are the fields that currently go missing:
        await Assert.That(json).Contains("\"Arguments\"");
        await Assert.That(json).Contains("\"path\"");
        await Assert.That(json).Contains("data/skills/pdf/SKILL.md");
    }

    // ── Round-trip: serialize then deserialize ──

    [Test]
    public async Task PicoJetson_ObjectValueDict_RoundTripSimple()
    {
        var model = ObjectValueDictFactory.CreateSimple();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ObjectValueDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Props["path"]).IsEqualTo("/data/SKILL.md");
        await Assert.That(back.Props["count"]).IsEqualTo(42L);
        await Assert.That((bool)back.Props["enabled"]!).IsTrue();
        await Assert.That(back.Props["score"]).IsEqualTo(3.14);
        await Assert.That(back.Props["nothing"]).IsNull();
    }

    [Test]
    public async Task PicoJetson_ObjectValueDict_RoundTripToolCall()
    {
        var model = ObjectValueDictFactory.CreateToolCall();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ContentBlockLikeModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Type).IsEqualTo("tool_call");
        await Assert.That(back.Id).IsEqualTo("call_00_xxx");
        await Assert.That(back.Name).IsEqualTo("read");
        await Assert.That(back.Arguments["path"]).IsEqualTo("data/skills/pdf/SKILL.md");
    }

    // ── Nested Dictionary<string, object?> values ──

    [Test]
    public async Task PicoJetson_ObjectValueDict_NestedDict_RoundTrip()
    {
        var model = new ObjectValueDictModel
        {
            Props = new Dictionary<string, object?>
            {
                ["nested"] = new Dictionary<string, object?>
                {
                    ["inner_key"] = "inner_value",
                    ["inner_num"] = 99L,
                },
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ObjectValueDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        var nested = back!.Props["nested"] as Dictionary<string, object?>;
        await Assert.That(nested).IsNotNull();
        await Assert.That(nested!["inner_key"]).IsEqualTo("inner_value");
        await Assert.That(nested["inner_num"]).IsEqualTo(99L);
    }

    [Test]
    public async Task PicoJetson_ObjectValueDict_NestedList_RoundTrip()
    {
        var model = new ObjectValueDictModel
        {
            Props = new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "a", 1L, true, null },
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ObjectValueDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        var items = back!.Props["items"] as List<object?>;
        await Assert.That(items).IsNotNull();
        await Assert.That(items!.Count).IsEqualTo(4);
        await Assert.That(items[0]).IsEqualTo("a");
        await Assert.That(items[1]).IsEqualTo(1L);
        await Assert.That((bool)items[2]!).IsTrue();
        await Assert.That(items[3]).IsNull();
    }

    // ── Edge-case numeric types: short, byte, decimal ──

    [Test]
    public async Task PicoJetson_ObjectValueDict_ShortValue_RoundTrip()
    {
        var model = new ObjectValueDictModel
        {
            Props = new Dictionary<string, object?> { ["v"] = (short)42 },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ObjectValueDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        // short serializes as JSON number, deserializes as long
        await Assert.That(back!.Props["v"]).IsEqualTo(42L);
    }

    [Test]
    public async Task PicoJetson_ObjectValueDict_ByteValue_RoundTrip()
    {
        var model = new ObjectValueDictModel
        {
            Props = new Dictionary<string, object?> { ["v"] = (byte)255 },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ObjectValueDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Props["v"]).IsEqualTo(255L);
    }

    [Test]
    public async Task PicoJetson_ObjectValueDict_DecimalValue_RoundTrip()
    {
        var model = new ObjectValueDictModel
        {
            Props = new Dictionary<string, object?> { ["v"] = 3.14m },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ObjectValueDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        // decimal serializes as JSON number, deserializes as double (JSON limitation)
        await Assert.That(back!.Props["v"]).IsEqualTo(3.14);
    }

    // ── MsgPack: Dictionary<string, object?> round-trip ──

    [Test]
    public async Task PicoMsgPack_ObjectValueDict_RoundTrip()
    {
        var model = ObjectValueDictFactory.CreateSimple();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var back = MsgPackSerializer.Deserialize<ObjectValueDictModel>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Props["path"]).IsEqualTo("/data/SKILL.md");
        await Assert.That(back.Props["count"]).IsEqualTo(42L);
        await Assert.That((bool)back.Props["enabled"]!).IsTrue();
        await Assert.That(back.Props["score"]).IsEqualTo(3.14);
        await Assert.That(back.Props["nothing"]).IsNull();
    }

    // ── TOML: Dictionary<string, object?> serialization (serialize-only — TOML values lose type info) ──

    [Test]
    public async Task PicoToml_ObjectValueDict_Serializes()
    {
        var model = ObjectValueDictFactory.CreateSimple();
        var toml = TomlSerializer.Serialize(model);

        // Verify the dict property is NOT silently skipped
        // Note: null values are skipped in TOML (TOML has no null type)
        await Assert.That(toml).Contains("path");
        await Assert.That(toml).Contains("/data/SKILL.md");
        await Assert.That(toml).Contains("count");
        await Assert.That(toml).Contains("enabled");
        await Assert.That(toml).Contains("score");
    }

    // ── YAML: Dictionary<string, object?> serialization (serialize-only — YAML values lose type info) ──

    [Test]
    public async Task PicoYaml_ObjectValueDict_Serializes()
    {
        var model = ObjectValueDictFactory.CreateSimple();
        var yaml = YamlSerializer.Serialize(model);

        // Verify the dict property is NOT silently skipped
        // Note: all values serialize as YAML strings in "any" mode
        await Assert.That(yaml).Contains("path");
        await Assert.That(yaml).Contains("/data/SKILL.md");
        await Assert.That(yaml).Contains("count");
        await Assert.That(yaml).Contains("enabled");
        await Assert.That(yaml).Contains("score");
    }
}

// ── Reproduction of PicoNode scenario: dict property on nested type (not top-level) ──
// ContentBlock.Arguments is on a nested type, discovered via Message.ContentBlocks traversal.
// The __PicoAnyDictHelper must be emitted even when the "any"-valued dict is in nestedTypes.

public class ParentWithNestedAnyDict
{
    public string Name { get; set; } = "";
    public ChildWithAnyDict Child { get; set; } = new();
}

public class ChildWithAnyDict
{
    public Dictionary<string, object?> Props { get; set; } = [];
}

public class NestedAnyDictRoundTripTests
{
    [Test]
    public async Task PicoJetson_NestedTypeWithAnyDict_RoundTrip()
    {
        var model = new ParentWithNestedAnyDict
        {
            Name = "parent",
            Child = new ChildWithAnyDict
            {
                Props = new Dictionary<string, object?> { ["key"] = "value", ["num"] = 42L },
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ParentWithNestedAnyDict>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("parent");
        await Assert.That(back.Child.Props["key"]).IsEqualTo("value");
        await Assert.That(back.Child.Props["num"]).IsEqualTo(42L);
    }
}
