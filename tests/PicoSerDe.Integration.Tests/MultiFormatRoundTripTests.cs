// Multi-format integration tests for PicoSerDe.
// Tests the same model through all 5 serialization formats.

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Cross-format model with property types supported by all 5 PicoSerDe formats.
/// Annotated with [PicoSerializable] to trigger all format source generators.
/// </summary>
[PicoSerializable]
public class AllFormatsModel
{
    // ── Primitives ──
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public string String { get; set; } = "";
    public string? NullableString { get; set; }
    public int? NullableInt { get; set; }

    // ── Date/Time (all formats support these) ──
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public DateOnly DateOnly { get; set; }
    public TimeOnly TimeOnly { get; set; }
    public Guid Guid { get; set; }

    // ── Enum ──
    public DayOfWeek Enum { get; set; }

    // ── Collections (all formats support List<string> and Dictionary<K,V>) ──
    public int Count { get; set; }
    public List<string> StringList { get; set; } = [];

    // ── Nested object (all formats support single-level nested POCOs) ──
    public AllFormatsSub? Nested { get; set; }
}

/// <summary>Nested model used by <see cref="AllFormatsModel"/>.</summary>
[PicoSerializable]
public class AllFormatsSub
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

/// <summary>Factory for <see cref="AllFormatsModel"/>.</summary>
public static class AllFormatsModelFactory
{
    public static AllFormatsModel Create() =>
        new()
        {
            Bool = true,
            Int = 42,
            Long = 9_876_543_210L,
            Float = 3.14f,
            Double = 2.71828,
            Decimal = 123.45m,
            String = "Hello, PicoSerDe! üñîçødé ¡测试",
            NullableString = "not null",
            NullableInt = 77,
            DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
            TimeSpan = new TimeSpan(10, 30, 0),
            DateOnly = new DateOnly(2026, 6, 4),
            TimeOnly = new TimeOnly(15, 45, 30, 123),
            Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
            Enum = DayOfWeek.Wednesday,
            Count = 77,
            StringList = ["foo", "bar", "baz"],
            Nested = new AllFormatsSub { Name = "nested", Value = 99 },
        };

    /// <summary>Model with null values to test null-path serialization.</summary>
    public static AllFormatsModel CreateWithNulls() =>
        new()
        {
            Bool = false,
            Int = 0,
            Long = 0,
            Float = 0,
            Double = 0,
            Decimal = 0,
            String = "empty",
            NullableString = null,
            NullableInt = null,
            DateTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan = TimeSpan.FromHours(1),
            DateOnly = DateOnly.MinValue,
            TimeOnly = TimeOnly.MinValue,
            Guid = Guid.Parse("00000000-0000-0000-0000-000000000000"),
            Enum = DayOfWeek.Sunday,
            Count = 0,
            StringList = [],
            Nested = null,
        };
}

/// <summary>
/// Tests that a model serialized and deserialized through each format
/// preserves all data correctly (format-level round-trip consistency).
/// </summary>
public class MultiFormatRoundTripTests
{
    [Test]
    public async Task PicoJetson_RoundTrip()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<AllFormatsModel>(bytes);
        await AssertEqual(model, back!);
    }

    [Test]
    public async Task PicoIni_RoundTrip()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = IniSerializer.SerializeToUtf8Bytes(model);
        var back = IniSerializer.Deserialize<AllFormatsModel>(bytes);
        await AssertEqual(model, back!);
    }

    [Test]
    public async Task PicoToml_RoundTrip()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = TomlSerializer.SerializeToUtf8Bytes(model);
        var back = TomlSerializer.Deserialize<AllFormatsModel>(bytes);
        await AssertEqual(model, back!);
    }

    [Test]
    public async Task PicoYaml_RoundTrip()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = YamlSerializer.SerializeToUtf8Bytes(model);
        var back = YamlSerializer.Deserialize<AllFormatsModel>(bytes);
        await AssertEqual(model, back!);
    }

    [Test]
    public async Task PicoMsgPack_RoundTrip()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var back = MsgPackSerializer.Deserialize<AllFormatsModel>(bytes);
        await AssertEqual(model, back!);
    }

    /// <summary>Null-value round-trip — verifies SG null handling.</summary>
    [Test]
    public async Task AllFormats_NullRoundTrip()
    {
        var model = AllFormatsModelFactory.CreateWithNulls();

        // PicoJetson
        var jBytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var jBack = JsonSerializer.Deserialize<AllFormatsModel>(jBytes);
        await Assert.That(jBack.NullableString).IsNull();
        await Assert.That(jBack.NullableInt).IsNull();
        await Assert.That(jBack.Nested).IsNull();

        // PicoIni
        var iBytes = IniSerializer.SerializeToUtf8Bytes(model);
        var iBack = IniSerializer.Deserialize<AllFormatsModel>(iBytes);
        await Assert.That(iBack.NullableString).IsNull();
        await Assert.That(iBack.NullableInt).IsNull();
        await Assert.That(iBack.Nested).IsNull();

        // PicoToml
        var tBytes = TomlSerializer.SerializeToUtf8Bytes(model);
        var tBack = TomlSerializer.Deserialize<AllFormatsModel>(tBytes);
        await Assert.That(tBack.NullableString).IsNull();
        await Assert.That(tBack.NullableInt).IsNull();
        await Assert.That(tBack.Nested).IsNull();

        // PicoMsgPack
        var mBytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var mBack = MsgPackSerializer.Deserialize<AllFormatsModel>(mBytes);
        await Assert.That(mBack.NullableString).IsNull();
        await Assert.That(mBack.NullableInt).IsNull();
        await Assert.That(mBack.Nested).IsNull();

        // PicoYaml
        var yBytes = YamlSerializer.SerializeToUtf8Bytes(model);
        // Step 1: verify raw YAML reader handles these bytes
        var testReader = new PicoYaml.YamlReader(yBytes);
        int yr = 0;
        while (testReader.Read()) {
            yr++;
            if (yr > 500) break;
        }
        await Assert.That(yr).IsLessThan(100, "Raw YAML reader should not loop");
        // Step 2: verify full deserialization works
        var yBack = YamlSerializer.Deserialize<AllFormatsModel>(yBytes);
        await Assert.That(yBack.NullableString).IsNull();
        await Assert.That(yBack.NullableInt).IsNull();
        await Assert.That(yBack.Nested).IsNull();
    }

    private static async Task AssertEqual(AllFormatsModel expected, AllFormatsModel actual)
    {
        // ── Primitives ──
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(Math.Abs(actual.Float - expected.Float) < 0.001f).IsTrue();
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(actual.Decimal).IsEqualTo(expected.Decimal);

        // ── String ──
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.NullableString).IsEqualTo(expected.NullableString);

        // ── Date/Time ──
        await Assert.That(actual.DateTime.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert
            .That(actual.DateTime.ToUniversalTime())
            .IsEqualTo(expected.DateTime.ToUniversalTime());
        await Assert.That(actual.TimeSpan).IsEqualTo(expected.TimeSpan);
        await Assert.That(actual.DateOnly).IsEqualTo(expected.DateOnly);
        await Assert.That(actual.TimeOnly).IsEqualTo(expected.TimeOnly);

        // ── Guid ──
        await Assert.That(actual.Guid).IsEqualTo(expected.Guid);

        // ── Enum ──
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);

        // ── Nullable ──
        await Assert.That(actual.NullableInt).IsEqualTo(expected.NullableInt);

        // ── Collections ──
        await Assert.That(actual.Count).IsEqualTo(expected.Count);
        await Assert.That(actual.StringList).IsEquivalentTo(expected.StringList);

        // ── Nested ──
        if (expected.Nested is null)
        {
            await Assert.That(actual.Nested).IsNull();
        }
        else
        {
            await Assert.That(actual.Nested).IsNotNull();
            await Assert.That(actual.Nested!.Name).IsEqualTo(expected.Nested.Name);
            await Assert.That(actual.Nested.Value).IsEqualTo(expected.Nested.Value);
        }
    }
}

/// <summary>
/// Tests that [PicoSerializable] triggers all 5 format generators correctly.
/// </summary>
public class PicoSerializableCrossFormatTests
{
    [Test]
    public async Task PicoJetson_Serializes()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<AllFormatsModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Int).IsEqualTo(42);
    }

    [Test]
    public async Task PicoIni_Serializes()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = IniSerializer.SerializeToUtf8Bytes(model);
        var back = IniSerializer.Deserialize<AllFormatsModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Int).IsEqualTo(42);
    }

    [Test]
    public async Task PicoToml_Serializes()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = TomlSerializer.SerializeToUtf8Bytes(model);
        var back = TomlSerializer.Deserialize<AllFormatsModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Int).IsEqualTo(42);
    }

    [Test]
    public async Task PicoYaml_Serializes()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = YamlSerializer.SerializeToUtf8Bytes(model);
        var back = YamlSerializer.Deserialize<AllFormatsModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Int).IsEqualTo(42);
    }

    [Test]
    public async Task PicoMsgPack_Serializes()
    {
        var model = AllFormatsModelFactory.Create();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var back = MsgPackSerializer.Deserialize<AllFormatsModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Int).IsEqualTo(42);
    }
}

/// <summary>
/// Tests for JSON Lines (JSONL) serialization and deserialization.
/// </summary>
public class PicoJetsonJsonLinesTests
{
    [Test]
    public async Task JsonLines_SyncRoundTrip()
    {
        var models = Enumerable.Range(0, 5).Select(_ => AllFormatsModelFactory.Create()).ToArray();

        var jsonl = JsonSerializer.SerializeLines(models);
        var restored = JsonSerializer.DeserializeLines<AllFormatsModel>(jsonl);

        await Assert.That(restored.Length).IsEqualTo(5);
        for (int i = 0; i < 5; i++)
        {
            await Assert.That(restored[i]!.Int).IsEqualTo(models[i].Int);
            await Assert.That(restored[i]!.String).IsEqualTo(models[i].String);
            await Assert.That(restored[i]!.Guid).IsEqualTo(models[i].Guid);
        }
    }

    [Test]
    public async Task JsonLines_StreamingRoundTrip()
    {
        var models = Enumerable.Range(0, 3).Select(_ => AllFormatsModelFactory.Create()).ToArray();
        var jsonl = JsonSerializer.SerializeLines(models);

        using var stream = new MemoryStream(jsonl);
        var results = new List<AllFormatsModel?>();
        await foreach (var m in JsonSerializer.DeserializeAsyncEnumerable<AllFormatsModel>(stream))
        {
            results.Add(m);
        }

        await Assert.That(results.Count).IsEqualTo(3);
        for (int i = 0; i < 3; i++)
        {
            await Assert.That(results[i]!.Int).IsEqualTo(models[i].Int);
            await Assert.That(results[i]!.String).IsEqualTo(models[i].String);
        }
    }

    [Test]
    public async Task JsonLines_StreamingArrayMode()
    {
        var models = Enumerable.Range(0, 2).Select(_ => AllFormatsModelFactory.Create()).ToArray();

        // Build a JSON array manually
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < models.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Encoding.UTF8.GetString(
                JsonSerializer.SerializeToUtf8Bytes(models[i])));
        }
        sb.Append("]");
        var json = Encoding.UTF8.GetBytes(sb.ToString());

        using var stream = new MemoryStream(json);
        var results = new List<AllFormatsModel?>();
        await foreach (var m in JsonSerializer.DeserializeAsyncEnumerable<AllFormatsModel>(
            stream, topLevelValues: false))
        {
            results.Add(m);
        }

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0]!.Int).IsEqualTo(models[0].Int);
        await Assert.That(results[1]!.Int).IsEqualTo(models[1].Int);
    }

    [Test]
    public async Task JsonLines_SingleItem()
    {
        var model = AllFormatsModelFactory.Create();
        var jsonl = JsonSerializer.SerializeLines([model]);
        var restored = JsonSerializer.DeserializeLines<AllFormatsModel>(jsonl);

        await Assert.That(restored.Length).IsEqualTo(1);
        await Assert.That(restored[0]!.Int).IsEqualTo(42);
        await Assert.That(restored[0]!.String).IsEqualTo("Hello, PicoSerDe! üñîçødé ¡测试");
    }

    [Test]
    public async Task JsonLines_EmptyCollection()
    {
        var jsonl = JsonSerializer.SerializeLines<AllFormatsModel>(Array.Empty<AllFormatsModel>());
        var restored = JsonSerializer.DeserializeLines<AllFormatsModel>(jsonl);

        await Assert.That(jsonl.Length).IsEqualTo(0);
        await Assert.That(restored.Length).IsEqualTo(0);
    }
}
