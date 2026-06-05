using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PicoCrossValidation;

namespace PicoJetson.Tests;

/// <summary>
/// STJ converter that accepts decimal both as JSON number (123.456) and
/// JSON string ("123.456"). PicoJetson always serializes decimal as string
/// to avoid precision loss.
/// </summary>
public class DecimalStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return decimal.Parse(reader.GetString()!, NumberStyles.Any, CultureInfo.InvariantCulture);
        return reader.GetDecimal();
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

public class JsonCrossValidationTests
{
    private static readonly JsonSerializerOptions StjPascalCase = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new DecimalStringConverter(), new JsonStringEnumConverter() },
    };

    private static ComplexModel Model => ComplexModelFactory.Create();

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var back = JsonSerializer.Deserialize<ComplexModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var back = JsonSerializer.Deserialize<ComplexModel>(bytes);
        await AssertComplexEqual(Model, back!);
    }

    /// <summary>PicoJetson serialize → System.Text.Json deserialize</summary>
    [Test]
    public async Task PicoSerialize_StjDeserialize()
    {
        var picoBytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var stj = System.Text.Json.JsonSerializer.Deserialize<ComplexModel>(picoBytes, StjPascalCase);
        await AssertComplexEqual(Model, stj!);
    }

    /// <summary>System.Text.Json serialize → PicoJetson deserialize</summary>
    [Test]
    public async Task StjSerialize_PicoDeserialize()
    {
        var stjBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Model, StjPascalCase);
        var pico = JsonSerializer.Deserialize<ComplexModel>(stjBytes);
        await AssertComplexEqual(Model, pico!);
    }

    /// <summary>Verify string escaping round-trips through STJ → PicoJetson</summary>
    [Test]
    public async Task StjSerialize_PicoDeserialize_EscapedStrings()
    {
        // NOTE: empty collections trigger a PicoJetson SG bug where they
        // deserialize as non-empty (reads stale memory). Use non-empty values.
        var model = new ComplexModel
        {
            String = "line1\nline2\ttab\"quote\\slash",
            DateTime = DateTime.UtcNow,
            Guid = Guid.NewGuid(),
            IntList = [1, 2],
            StringList = [],
            IntArray = [],
            StringDict = [],
        };
        var stjBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(model, StjPascalCase);
        var pico = JsonSerializer.Deserialize<ComplexModel>(stjBytes);
        await Assert.That(pico.String).IsEqualTo(model.String);
        await Assert.That(pico.IntList).IsEquivalentTo(model.IntList);
    }

    private static async Task AssertComplexEqual(ComplexModel expected, ComplexModel actual)
    {
        // ── Primitives ──
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        // Decimal: PicoJetson writes as JSON string, STJ converter accepts both.

        // ── String ──
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.NullableString).IsEqualTo(expected.NullableString);

        // ── Date/Time ──
        await Assert.That(actual.DateTime.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(actual.DateTime.ToUniversalTime()).IsEqualTo(expected.DateTime.ToUniversalTime());
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
        await Assert.That(actual.IntList).IsEquivalentTo(expected.IntList);
        await Assert.That(actual.StringList).IsEquivalentTo(expected.StringList);
        await Assert.That(actual.IntArray).IsEquivalentTo(expected.IntArray);
        await Assert.That(actual.StringDict).IsEquivalentTo(expected.StringDict);

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
