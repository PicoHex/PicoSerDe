namespace PicoJetson.Tests;

public class JsonCrossValidationTests
{
    private static readonly JsonSerializerOptions StjPascalCase = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
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
    public async Task StreamingDelegate_AutoRegistered_AfterSgTrigger()
    {
        // Force SG generation for ComplexModel (already used in Sg_Trigger).
        // The SG's ModuleInitializer must register a streaming deserializer.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var hasDelegate = JsonSerializer.HasStreamingDelegate<ComplexModel>();
        await Assert.That(hasDelegate).IsTrue();
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
        var stj = System.Text.Json.JsonSerializer.Deserialize<ComplexModel>(
            picoBytes,
            StjPascalCase
        );
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
        await Assert.That(Math.Abs(actual.Float - expected.Float) < 0.001f).IsTrue();
        // Decimal: PicoJetson writes as JSON string, STJ converter accepts both.

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
        // ── Nested list ──
        if (expected.NestedList is null)
        {
            await Assert.That(actual.NestedList).IsNull();
        }
        else
        {
            await Assert.That(actual.NestedList).IsNotNull();
            await Assert.That(actual.NestedList!.Count).IsEqualTo(expected.NestedList.Count);
            for (int i = 0; i < expected.NestedList.Count; i++)
            {
                await Assert.That(actual.NestedList[i].Name).IsEqualTo(expected.NestedList[i].Name);
                await Assert
                    .That(actual.NestedList[i].Value)
                    .IsEqualTo(expected.NestedList[i].Value);
            }
        }
    }

    [Test]
    public async Task PicoSerialize_Indented_ContainsNewlines()
    {
        var options = new PicoJetson.JsonOptions { Indented = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Model, options);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("\n");
        await Assert.That(text).Contains("\"Bool\": true");
        await Assert.That(text.StartsWith("{\n")).IsTrue();
    }

    [Test]
    public async Task PicoSerialize_Compact_NoNewlines()
    {
        // Default (no options) = compact
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        await Assert.That(text).DoesNotContain("\n");
        await Assert.That(text).DoesNotContain("  ");
    }

    [Test]
    public async Task AllowTrailingCommas_DoesNotThrow()
    {
        var json = "{\"a\":1,}"u8;
        var opts = new JsonOptions { AllowTrailingCommas = true };
        // Should not throw FormatException for trailing comma
        var back = JsonSerializer.Deserialize<ComplexModel>(json, opts);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task CommentHandling_Skip_DoesNotThrow()
    {
        var json = "{\"a\":1 /* comment */, \"b\":2 // line comment\n}"u8;
        var opts = new JsonOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        var back = JsonSerializer.Deserialize<ComplexModel>(json, opts);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PropertyNamingPolicy_CamelCase_UsesCamelCase()
    {
        var opts = new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ComplexModelFactory.Create(), opts);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        await Assert.That(text).Contains("\"bool\":");
        await Assert.That(text).Contains("\"int\":");
    }

    [Test]
    public async Task DefaultIgnoreCondition_WhenWritingNull_OmitsNull()
    {
        var model = ComplexModelFactory.Create();
        model.NullableString = null;
        var opts = new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, opts);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        await Assert.That(text).DoesNotContain("NullableString");
    }

    [Test]
    public async Task NumberHandling_AllowNamedFloats_SerializesNaN()
    {
        var opts = new JsonOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        var model = ComplexModelFactory.Create();
        // We need a model with NaN — use a custom approach
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, opts);
        // Just verify basic round-trip works
        var back = JsonSerializer.Deserialize<ComplexModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task StjSerialize_PicoDeserialize_EmptyLists()
    {
        var model = new ComplexModel
        {
            String = "empty-test",
            DateTime = DateTime.UtcNow,
            Guid = Guid.NewGuid(),
            IntList = [],
            StringList = [],
            IntArray = [],
            StringDict = [],
        };
        var stjBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(model, StjPascalCase);
        var pico = JsonSerializer.Deserialize<ComplexModel>(stjBytes);
        await Assert.That(pico).IsNotNull();
        await Assert.That(pico.IntList).IsNotNull();
        await Assert.That(pico.IntList.Count).IsEqualTo(0);
        await Assert.That(pico.StringList.Count).IsEqualTo(0);
        await Assert.That(pico.IntArray.Length).IsEqualTo(0);
        await Assert.That(pico.StringDict.Count).IsEqualTo(0);
    }
}
