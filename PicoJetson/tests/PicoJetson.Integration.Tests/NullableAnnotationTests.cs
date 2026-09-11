namespace PicoJetson.Tests;

// Regression models for the reference-nullability annotation loss: generated code
// used to write declaration-position types without "?" (from
// SymbolDisplayFormat.FullyQualifiedFormat), which breaks nullable-enabled
// consumers that promote CS8619/CS8620 to errors (reported: PicoAgent.Domain,
// ContentBlock.Arguments). This project promotes both, so a regression fails the BUILD.

public class AnnotationListDto
{
    public List<string?> Items { get; set; } = new();
}

public class AnnotationDictDto
{
    public Dictionary<string, object?> Arguments { get; set; } = new();
    public Dictionary<string, string?> Names { get; set; } = new();
}

public class AnnotationExtendedCollectionDto
{
    public HashSet<string?> Set { get; set; } = new();
}

public record AnnotationRecordDto(Dictionary<string, object?> Arguments, List<string?> Items);

public class NullableAnnotationTests
{
    [Test]
    public async Task ListOfNullable_RoundTrips_WithNullElement()
    {
        var dto = new AnnotationListDto { Items = { "a", null } };
        var back = JsonSerializer.Deserialize<AnnotationListDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Items).Count().IsEqualTo(2);
        await Assert.That(back.Items[0]).IsEqualTo("a");
        await Assert.That(back.Items[1]).IsNull();
    }

    [Test]
    public async Task DictOfNullableValues_RoundTrips()
    {
        var dto = new AnnotationDictDto
        {
            Arguments = { ["s"] = "x", ["nil"] = null },
            Names = { ["k"] = null },
        };
        var back = JsonSerializer.Deserialize<AnnotationDictDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Arguments["s"]).IsEqualTo("x");
        await Assert.That(back.Arguments["nil"]).IsNull();
        await Assert.That(back.Names["k"]).IsNull();
    }

    [Test]
    public async Task ExtendedCollectionOfNullable_RoundTrips()
    {
        var dto = new AnnotationExtendedCollectionDto { Set = { "a", null } };
        var back = JsonSerializer.Deserialize<AnnotationExtendedCollectionDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Set.Count).IsEqualTo(2);
        await Assert.That(back.Set.Contains(null)).IsTrue();
    }

    [Test]
    public async Task RecordCtor_AnnotatedTypes_RoundTrip()
    {
        var dto = new AnnotationRecordDto(
            new Dictionary<string, object?> { ["k"] = null },
            new List<string?> { "v", null }
        );
        var back = JsonSerializer.Deserialize<AnnotationRecordDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Arguments["k"]).IsNull();
        await Assert.That(back.Items[1]).IsNull();
    }
}
