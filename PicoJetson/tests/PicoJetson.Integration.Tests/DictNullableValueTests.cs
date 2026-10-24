namespace PicoJetson.Tests;

// NOTE: the review brief declared these as nested classes inside the test class.
// The generator deliberately ignores nested types (GenInfrastructure.TransformType:
// "if (namedType.ContainingType is not null) return null"), so they are declared
// top-level here; the test bodies and assertions are unchanged.
public class IntMap
{
    public Dictionary<string, int?> Map { get; set; } = new();
}

public class DecimalMap
{
    public Dictionary<string, decimal?> Map { get; set; } = new();
}

public class GuidMap
{
    public Dictionary<string, Guid?> Map { get; set; } = new();
}

public class DateTimeMap
{
    public Dictionary<string, DateTime?> Map { get; set; } = new();
}

// Nested dict with a nullable value type: the outer helper delegates to the
// generated *JsonDictInner helper for Dictionary<string, int?>.
public class NestedNullableIntMap
{
    public Dictionary<string, Dictionary<string, int?>> Map { get; set; } = new();
}

public class DictNullableValueTests
{
    [Test]
    public async Task Int_NullStaysNull() =>
        await Assert
            .That(
                JsonSerializer
                    .Deserialize<IntMap>(Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}"))!
                    .Map["b"]
            )
            .IsNull();

    [Test]
    public async Task Decimal_NullStaysNull() =>
        await Assert
            .That(
                JsonSerializer
                    .Deserialize<DecimalMap>(Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}"))!
                    .Map["b"]
            )
            .IsNull();

    [Test]
    public async Task GuidAndDateTime_NullStayNull()
    {
        await Assert
            .That(
                JsonSerializer
                    .Deserialize<GuidMap>(Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}"))!
                    .Map["b"]
            )
            .IsNull();
        await Assert
            .That(
                JsonSerializer
                    .Deserialize<DateTimeMap>(Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}"))!
                    .Map["b"]
            )
            .IsNull();
    }

    // C-29 in the nested-dict path: the inner helper for Dictionary<string, int?>
    // must apply the same null guard as the flat dict path.
    [Test]
    public async Task NestedDict_NullStaysNull()
    {
        var back = JsonSerializer.Deserialize<NestedNullableIntMap>(
            Encoding.UTF8.GetBytes("{\"Map\":{\"a\":{\"b\":null,\"c\":7}}}")
        )!;

        await Assert.That(back.Map["a"]["b"]).IsNull();
        await Assert.That(back.Map["a"]["c"]).IsEqualTo(7);
    }

    [Test]
    public async Task NestedDict_RoundTrip_NullStaysNull()
    {
        var model = new NestedNullableIntMap
        {
            Map = new()
            {
                ["a"] = new() { ["b"] = null, ["c"] = 7 },
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<NestedNullableIntMap>(bytes)!;

        await Assert.That(back.Map["a"]["b"]).IsNull();
        await Assert.That(back.Map["a"]["c"]).IsEqualTo(7);
    }
}
