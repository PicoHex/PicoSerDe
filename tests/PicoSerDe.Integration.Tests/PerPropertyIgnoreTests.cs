// Cross-format contract for the per-property [PicoIgnore(Condition = ...)]
// attribute (PicoSerDe.Core).
//
// | Condition        | JSON / MsgPack (write group)      | TOML / INI / YAML (omit group) |
// |------------------|-----------------------------------|--------------------------------|
// | Always (default) | stripped (write + read)           | stripped (write + read)       |
// | WhenWritingNull  | null omitted, regardless of global| same as default (null omitted)|
// | Never            | exempt from global condition      | non-null written; null still  |
// |                  | (null written even under global   | omitted (format capability)   |
// |                  |  WhenWritingNull)                 |                               |
//
// Per-property conditions affect serialization only — deserialization still
// maps conditional properties.

using System.Text;
using PicoSerDe.Core;

namespace PicoSerDe.Integration.Tests;

// ── Models ──

[PicoSerializable]
public class PIgnNested
{
    public string Name { get; set; } = string.Empty;

    [PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

[PicoSerializable]
public class PIgnModel
{
    public string Name { get; set; } = string.Empty;

    [PicoIgnore]
    public string Secret { get; set; } = string.Empty;

    [PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [PicoIgnore(Condition = PicoIgnoreCondition.Never)]
    public string? Pinned { get; set; }

    public string? Plain { get; set; }

    public PIgnNested Nested { get; set; } = new();
}

[PicoSerializable]
[PicoDerivedType(typeof(PIgnPolyMsg), "m")]
public abstract class PIgnPolyBase { }

public class PIgnPolyMsg : PIgnPolyBase
{
    public string Content { get; set; } = string.Empty;

    [PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

// ── Tests ──

[NotInParallel("PerPropertyIgnore")]
public class PerPropertyIgnoreTests
{
    private static PIgnModel Create() =>
        new()
        {
            Name = "outer",
            Secret = "hidden",
            Note = null,
            Pinned = null,
            Plain = null,
            Nested = new PIgnNested { Name = "inner", Note = null },
        };

    // ── Always (default): stripped in every format ──

    [Test]
    public async Task AllFormats_Always_StripsProperty()
    {
        var model = Create();
        var json = Encoding.UTF8.GetString(PicoJetson.JsonSerializer.SerializeToUtf8Bytes(model));
        var yaml = PicoYaml.YamlSerializer.Serialize(model);
        var toml = PicoToml.TomlSerializer.Serialize(model);
        var ini = PicoIni.IniSerializer.Serialize(model);
        var mpMap = MessagePack.MessagePackSerializer.Deserialize<Dictionary<int, object?>>(
            PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes(model)
        );

        await Assert.That(json).DoesNotContain("Secret");
        await Assert.That(yaml).DoesNotContain("Secret");
        await Assert.That(toml).DoesNotContain("Secret");
        await Assert.That(ini).DoesNotContain("Secret");
        // MsgPack main path uses int keys (declaration order, stripped members
        // get no key): Name=0, Note=1, Pinned=2, Plain=3, Nested=4.
        await Assert.That(mpMap.Values.Contains("hidden")).IsFalse();
        await Assert.That(mpMap.Count).IsEqualTo(4); // Note skipped per-prop, Secret stripped
    }

    // ── JSON: per-property WhenWritingNull, no global option set ──

    [Test]
    public async Task Json_PerProp_WhenWritingNull_OmitsNull_WithoutGlobalOption()
    {
        var model = Create();
        var json = Encoding.UTF8.GetString(PicoJetson.JsonSerializer.SerializeToUtf8Bytes(model));
        await Assert.That(json).DoesNotContain("\"Note\"");
        // Control: unannotated nullable property is still written under default options
        await Assert.That(json).Contains("\"Plain\":null");

        model.Note = "x";
        json = Encoding.UTF8.GetString(PicoJetson.JsonSerializer.SerializeToUtf8Bytes(model));
        await Assert.That(json).Contains("\"Note\":\"x\"");
    }

    // ── JSON: per-property Never exempts from the global condition ──

    [Test]
    public async Task Json_PerProp_Never_ExemptsFromGlobalCondition()
    {
        var model = Create();
        var json = Encoding.UTF8.GetString(
            PicoJetson.JsonSerializer.SerializeToUtf8Bytes(
                model,
                new PicoJetson.JsonOptions
                {
                    DefaultIgnoreCondition = PicoJetson.JsonIgnoreCondition.WhenWritingNull,
                }
            )
        );
        await Assert.That(json).Contains("\"Pinned\":null");
        // Control: unannotated nullable property is omitted by the global condition
        await Assert.That(json).DoesNotContain("\"Plain\"");
    }

    // ── JSON: nested (inner helper) and poly emit paths ──

    [Test]
    public async Task Json_NestedPath_PerPropCondition_Applies()
    {
        var model = Create();
        var json = Encoding.UTF8.GetString(PicoJetson.JsonSerializer.SerializeToUtf8Bytes(model));
        await Assert.That(json).DoesNotContain("\"Note\"");
        await Assert.That(json).Contains("\"inner\"");
    }

    [Test]
    public async Task Json_PolyPath_PerPropCondition_Applies()
    {
        PIgnPolyBase value = new PIgnPolyMsg { Content = "hello", Note = null };
        var json = Encoding.UTF8.GetString(PicoJetson.JsonSerializer.SerializeToUtf8Bytes(value));
        await Assert.That(json).DoesNotContain("\"Note\"");
        await Assert.That(json).Contains("\"hello\"");
    }

    // ── Conditions affect writing only — deserialization still maps ──

    [Test]
    public async Task Json_ConditionalProperty_StillDeserializes()
    {
        var back = PicoJetson.JsonSerializer.Deserialize<PIgnModel>(
            "{\"Name\":\"n\",\"Note\":\"x\",\"Pinned\":\"p\"}"u8
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Note).IsEqualTo("x");
        await Assert.That(back.Pinned).IsEqualTo("p");
    }

    // ── MsgPack: write group behavior via official reader (key presence) ──

    [Test]
    public async Task MsgPack_PerProp_WhenWritingNull_SkipsKey()
    {
        var map = MessagePack.MessagePackSerializer.Deserialize<Dictionary<int, object?>>(
            PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes(Create())
        );
        // Note (key 1) skipped per-prop; Plain (key 3) written as nil by default
        await Assert.That(map.ContainsKey(1)).IsFalse();
        await Assert.That(map.ContainsKey(3)).IsTrue();
    }

    [Test]
    public async Task MsgPack_PerProp_Never_KeepsKeyUnderGlobalCondition()
    {
        PicoMsgPack.MsgPackOptions.Current = new PicoMsgPack.MsgPackOptions
        {
            DefaultIgnoreCondition = PicoMsgPack.MsgPackIgnoreCondition.WhenWritingNull,
        };
        byte[] bytes;
        try
        {
            bytes = PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes(Create());
        }
        finally
        {
            PicoMsgPack.MsgPackOptions.Current = null;
        }
        var map = MessagePack.MessagePackSerializer.Deserialize<Dictionary<int, object?>>(bytes);
        // Pinned (key 2) exempt from the global condition; Plain (key 3) skipped by it
        await Assert.That(map.ContainsKey(2)).IsTrue();
        await Assert.That(map.ContainsKey(3)).IsFalse();
    }

    // ── Omit group: Always strips; conditional non-null values still written ──

    [Test]
    public async Task OmitGroup_ConditionalNonNull_Written_NullOmitted()
    {
        var model = Create();
        model.Note = "x";
        model.Pinned = "p";

        var yaml = PicoYaml.YamlSerializer.Serialize(model);
        var toml = PicoToml.TomlSerializer.Serialize(model);
        var ini = PicoIni.IniSerializer.Serialize(model);

        foreach (var text in new[] { yaml, toml, ini })
        {
            await Assert.That(text).Contains("Note");
            await Assert.That(text).Contains("Pinned");
            await Assert.That(text).DoesNotContain("Secret");
        }

        model.Note = null;
        model.Pinned = null;
        yaml = PicoYaml.YamlSerializer.Serialize(model);
        toml = PicoToml.TomlSerializer.Serialize(model);
        ini = PicoIni.IniSerializer.Serialize(model);
        foreach (var text in new[] { yaml, toml, ini })
        {
            await Assert.That(text).DoesNotContain("Note");
            await Assert.That(text).DoesNotContain("Pinned");
        }
    }
}
