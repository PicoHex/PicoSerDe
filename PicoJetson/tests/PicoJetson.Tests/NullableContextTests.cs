// Tests that generated serializer source has proper #nullable enable directive
// to avoid CS8669 warning in consumer projects.
//
// NOTE: These model classes must be top-level (not nested inside the test class)
// for the source generator to detect them via usage-driven discovery.

namespace PicoJetson.Tests;

public class NullableRefModel
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public class InnerModel
{
    public string Value { get; set; } = "";
}

public class NullableNestedModel
{
    public string Name { get; set; } = "";
    public InnerModel? Child { get; set; }
}

public class NullableContextTests
{
    [Test]
    public async Task NullableRefType_RoundTrip_Works()
    {
        var model = new NullableRefModel { Name = "Test", Description = "Hello" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NullableRefModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Description).IsEqualTo("Hello");
    }

    [Test]
    public async Task NullableRefType_Null_RoundTrip_Works()
    {
        var model = new NullableRefModel { Name = "Test", Description = null };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NullableRefModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Description).IsNull();
    }

    [Test]
    public async Task NullableNestedType_RoundTrip_Works()
    {
        var model = new NullableNestedModel
        {
            Name = "Parent",
            Child = new InnerModel { Value = "Child" },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NullableNestedModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Parent");
        await Assert.That(result.Child).IsNotNull();
        await Assert.That(result.Child!.Value).IsEqualTo("Child");
    }

    [Test]
    public async Task NullableNestedType_Null_RoundTrip_Works()
    {
        var model = new NullableNestedModel { Name = "Parent", Child = null };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NullableNestedModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Parent");
        await Assert.That(result.Child).IsNull();
    }
}
