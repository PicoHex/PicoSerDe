// Tests that generated serializer source has proper #nullable enable directive
// to avoid CS8669 warning in consumer projects.
//
// NOTE: These model classes must be top-level (not nested inside the test class)
// for the source generator to detect them via usage-driven discovery.

namespace PicoJetson.Tests;

public class NullableRefModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class InnerModel
{
    public string Value { get; set; } = string.Empty;
}

public class NullableNestedModel
{
    public string Name { get; set; } = string.Empty;
    public InnerModel? Child { get; set; }
}

// ── CS8625 repro: non-nullable object property deserialized from JSON null ──

public class NonNullNestedOwner
{
    public string Name { get; set; } = string.Empty;
    public NonNullNestedChild Child { get; set; } = new();
}

public class NonNullNestedChild
{
    public string Value { get; set; } = "default";
}

// ── CS8604 repro: nullable string array with null elements ──

public class NullableStringArrayModel
{
    public string?[] Items { get; set; } = [];
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

    // ── CS8625: non-nullable object property must keep default when JSON has null ──

    [Test]
    public async Task NonNullNestedObject_NullJson_KeepsDefault()
    {
        var json = """{"Name":"owner","Child":null}"""u8;
        var result = JsonSerializer.Deserialize<NonNullNestedOwner>(json.ToArray());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("owner");
        // Child should NOT be null — it should keep the default new() value
        await Assert.That(result.Child).IsNotNull();
        await Assert.That(result.Child!.Value).IsEqualTo("default");
    }

    [Test]
    public async Task NonNullNestedObject_WithValue_RoundTrip()
    {
        var model = new NonNullNestedOwner
        {
            Name = "owner",
            Child = new NonNullNestedChild { Value = "custom" },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NonNullNestedOwner>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("owner");
        await Assert.That(result.Child).IsNotNull();
        await Assert.That(result.Child!.Value).IsEqualTo("custom");
    }

    // ── CS8604: nullable string array serialization must not throw ──

    [Test]
    public async Task NullableStringArray_Serialize_HandlesNullElements()
    {
        var model = new NullableStringArrayModel { Items = ["hello", null, "world"] };

        // Must not throw ArgumentNullException from Encoding.UTF8.GetBytes(null)
        var json = JsonSerializer.Serialize(model);
        await Assert.That(json).IsNotNull();
        await Assert.That(json).Contains("hello");
        await Assert.That(json).Contains("null");
        await Assert.That(json).Contains("world");
    }

    [Test]
    public async Task NullableStringArray_RoundTrip_PreservesNulls()
    {
        var model = new NullableStringArrayModel { Items = ["a", null, "b"] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NullableStringArrayModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Items).IsNotNull();
        await Assert.That(result.Items).Count().IsEqualTo(3);
        await Assert.That(result.Items[0]).IsEqualTo("a");
        await Assert.That(result.Items[1]).IsNull();
        await Assert.That(result.Items[2]).IsEqualTo("b");
    }
}
