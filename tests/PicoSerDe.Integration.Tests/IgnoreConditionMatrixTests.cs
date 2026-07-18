// Cross-format semantic contract for DefaultIgnoreCondition.
//
// | Format             | Default (Never)             | WhenWritingNull |
// |--------------------|-----------------------------|-----------------|
// | JSON               | null written ('null')       | omitted         |
// | MsgPack            | null written (nil)          | omitted         |
// | TOML / INI         | omitted (no null literal)   | omitted         |
// | YAML               | omitted (reader has no null-literal support yet;
// |                    |  writing 'key:' would read back as default and
// |                    |  break round-trip fidelity) | omitted         |
//
// Any generator change that breaks this matrix must fail here.

namespace PicoSerDe.Integration.Tests;

// ── Shared model ──

[PicoSerializable]
public class IgnMatrixNested
{
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
}

[PicoSerializable]
public class IgnMatrixModel
{
    public string Name { get; set; } = string.Empty;
    public string? NullNote { get; set; }
    public int? NullCount { get; set; }
    public IgnMatrixNested Nested { get; set; } = new();
}

public static class IgnMatrixFactory
{
    public static IgnMatrixModel CreateWithNulls() =>
        new()
        {
            Name = "outer",
            NullNote = null,
            NullCount = null,
            Nested = new IgnMatrixNested { Name = "inner", Note = null },
        };
}

// ── Matrix tests ──

[NotInParallel("IgnoreConditionMatrix")]
public class IgnoreConditionMatrixTests
{
    // ── Default (Never): null-capable formats write nulls ──

    [Test]
    public async Task Json_Default_WritesNulls()
    {
        var json = Encoding.UTF8.GetString(
            PicoJetson.JsonSerializer.SerializeToUtf8Bytes(IgnMatrixFactory.CreateWithNulls())
        );
        await Assert.That(json).Contains("\"NullNote\":null");
        await Assert.That(json).Contains("\"NullCount\":null");
        await Assert.That(json).Contains("\"Note\":null");
    }

    [Test]
    public async Task Yaml_Default_OmitsNulls_AndRoundTrips()
    {
        var yaml = PicoYaml.YamlSerializer.Serialize(IgnMatrixFactory.CreateWithNulls());
        await Assert.That(yaml).DoesNotContain("NullNote");
        await Assert.That(yaml).DoesNotContain("NullCount");
        await Assert.That(yaml).Contains("inner");

        var back = PicoYaml.YamlSerializer.Deserialize<IgnMatrixModel>(
            Encoding.UTF8.GetBytes(yaml)
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.NullNote).IsNull();
        await Assert.That(back.NullCount).IsNull();
    }

    [Test]
    public async Task MsgPack_Default_RoundTripsNulls()
    {
        var bytes = PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes(
            IgnMatrixFactory.CreateWithNulls()
        );
        var back = PicoMsgPack.MsgPackSerializer.Deserialize<IgnMatrixModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.NullNote).IsNull();
        await Assert.That(back.NullCount).IsNull();
        await Assert.That(back.Nested.Note).IsNull();
    }

    // ── Default (Never): formats without a null literal omit nulls ──

    [Test]
    public async Task Toml_Default_OmitsNulls()
    {
        var toml = PicoToml.TomlSerializer.Serialize(IgnMatrixFactory.CreateWithNulls());
        await Assert.That(toml).DoesNotContain("NullNote");
        await Assert.That(toml).DoesNotContain("NullCount");
        await Assert.That(toml).DoesNotContain("Note");
        await Assert.That(toml).Contains("inner");
    }

    [Test]
    public async Task Ini_Default_OmitsNulls()
    {
        var ini = PicoIni.IniSerializer.Serialize(IgnMatrixFactory.CreateWithNulls());
        await Assert.That(ini).DoesNotContain("NullNote");
        await Assert.That(ini).DoesNotContain("NullCount");
        await Assert.That(ini).DoesNotContain("Note");
        await Assert.That(ini).Contains("inner");
    }

    // ── WhenWritingNull: every format omits nulls, including nested members ──

    [Test]
    public async Task Json_WhenWritingNull_OmitsNulls()
    {
        var json = Encoding.UTF8.GetString(
            PicoJetson.JsonSerializer.SerializeToUtf8Bytes(
                IgnMatrixFactory.CreateWithNulls(),
                new PicoJetson.JsonOptions
                {
                    DefaultIgnoreCondition = PicoJetson.JsonIgnoreCondition.WhenWritingNull,
                }
            )
        );
        await Assert.That(json).DoesNotContain("NullNote");
        await Assert.That(json).DoesNotContain("NullCount");
        await Assert.That(json).DoesNotContain("Note");
        await Assert.That(json).Contains("inner");
    }

    [Test]
    public async Task Yaml_WhenWritingNull_OmitsNulls()
    {
        PicoYaml.YamlOptions.Current = new PicoYaml.YamlOptions
        {
            DefaultIgnoreCondition = PicoYaml.YamlIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var yaml = PicoYaml.YamlSerializer.Serialize(IgnMatrixFactory.CreateWithNulls());
            await Assert.That(yaml).DoesNotContain("NullNote");
            await Assert.That(yaml).DoesNotContain("NullCount");
            await Assert.That(yaml).DoesNotContain("Note");
            await Assert.That(yaml).Contains("inner");
        }
        finally
        {
            PicoYaml.YamlOptions.Current = null;
        }
    }

    [Test]
    public async Task Toml_WhenWritingNull_OmitsNulls()
    {
        PicoToml.TomlOptions.Current = new PicoToml.TomlOptions
        {
            DefaultIgnoreCondition = PicoToml.TomlIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var toml = PicoToml.TomlSerializer.Serialize(IgnMatrixFactory.CreateWithNulls());
            await Assert.That(toml).DoesNotContain("NullNote");
            await Assert.That(toml).DoesNotContain("NullCount");
            await Assert.That(toml).DoesNotContain("Note");
        }
        finally
        {
            PicoToml.TomlOptions.Current = null;
        }
    }

    [Test]
    public async Task Ini_WhenWritingNull_OmitsNulls()
    {
        PicoIni.IniOptions.Current = new PicoIni.IniOptions
        {
            DefaultIgnoreCondition = PicoIni.IniIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var ini = PicoIni.IniSerializer.Serialize(IgnMatrixFactory.CreateWithNulls());
            await Assert.That(ini).DoesNotContain("NullNote");
            await Assert.That(ini).DoesNotContain("NullCount");
            await Assert.That(ini).DoesNotContain("Note");
        }
        finally
        {
            PicoIni.IniOptions.Current = null;
        }
    }

    [Test]
    public async Task MsgPack_WhenWritingNull_SkipsAndRoundTrips()
    {
        var model = IgnMatrixFactory.CreateWithNulls();
        var full = PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes(model);
        PicoMsgPack.MsgPackOptions.Current = new PicoMsgPack.MsgPackOptions
        {
            DefaultIgnoreCondition = PicoMsgPack.MsgPackIgnoreCondition.WhenWritingNull,
        };
        byte[] skipped;
        try
        {
            skipped = PicoMsgPack.MsgPackSerializer.SerializeToUtf8Bytes(model);
        }
        finally
        {
            PicoMsgPack.MsgPackOptions.Current = null;
        }
        await Assert.That(skipped.Length).IsLessThan(full.Length);
        var back = PicoMsgPack.MsgPackSerializer.Deserialize<IgnMatrixModel>(skipped);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("outer");
        await Assert.That(back.NullNote).IsNull();
        await Assert.That(back.Nested.Name).IsEqualTo("inner");
    }
}
