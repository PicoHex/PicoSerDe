using System.Buffers;

namespace PicoJetson.Tests;

/// <summary>Type discovered via [PicoSerializable] (all formats).</summary>
[PicoSerializable]
public class AllFormatsDto
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

/// <summary>Type discovered via [PicoJsonSerializable] (JSON only).</summary>
[PicoJsonSerializable]
public class JsonOnlyDto
{
    public string Label { get; set; } = string.Empty;
}

/// <summary>Indirect discovery via [PicoJsonSerializable(typeof(T))].</summary>
[PicoJsonSerializable(typeof(JsonIndirectDto))]
public class JsonConfig { }

public class JsonIndirectDto
{
    public string Data { get; set; } = string.Empty;
}

/// <summary>Type discovered via [GenerateSerializer(typeof(T))].</summary>
[GenerateSerializer(typeof(GenSerializerRefDto))]
class GenSerializerConfig { }

public class GenSerializerRefDto
{
    public string Token { get; set; } = string.Empty;
}

/// <summary>Tests for attribute-driven source generation.</summary>
public class AttributeDiscoveryTests
{
    [Test]
    public async Task AllFormatsDto_RoundTrip()
    {
        var dto = new AllFormatsDto { Name = "all", Value = 42 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var result = JsonSerializer.Deserialize<AllFormatsDto>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("all");
        await Assert.That(result.Value).IsEqualTo(42);
    }

    [Test]
    public async Task JsonOnlyDto_RoundTrip()
    {
        var dto = new JsonOnlyDto { Label = "json-only" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var result = JsonSerializer.Deserialize<JsonOnlyDto>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Label).IsEqualTo("json-only");
    }

    [Test]
    public async Task JsonIndirectDto_RoundTrip()
    {
        var dto = new JsonIndirectDto { Data = "indirect" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var result = JsonSerializer.Deserialize<JsonIndirectDto>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Data).IsEqualTo("indirect");
    }

    [Test]
    public async Task GenerateSerializerRefDto_RoundTrip()
    {
        var dto = new GenSerializerRefDto { Token = "from-generate-serializer" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var result = JsonSerializer.Deserialize<GenSerializerRefDto>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Token).IsEqualTo("from-generate-serializer");
    }

    [Test]
    public async Task AllFormatsDto_JsonOutput()
    {
        var dto = new AllFormatsDto { Name = "check", Value = 99 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("\"Value\"");
        await Assert.That(json).Contains("check");
        await Assert.That(json).Contains("99");
    }
}

public class ContentTypeTests
{
    [Test]
    public async Task ContentType_Value()
    {
        await Assert.That(PicoJetson.JsonSerializer.ContentType).IsEqualTo("application/json");
    }
}

// Required member tests disabled — `required` keyword adds CS9035 codegen complexity
// that requires format-specific deserializer changes beyond the current scope.
// PropertyInfo.IsRequired is tracked for all formats; runtime validation TBD.

public class FieldDto
{
    public string Name = "default"; // field, not property
}

public class IncludeFieldsTests
{
    [Test]
    public async Task Fields_NotSerialized_ByDefault()
    {
        var dto = new FieldDto { Name = "test" };
        var json = JsonSerializer.SerializeToUtf8Bytes(dto);
        var result = System.Text.Encoding.UTF8.GetString(json);
        await Assert.That(result).DoesNotContain("Name");
    }
}

public class IncludeFieldsDto
{
    public string Name = "default"; // field
    public int Count; // field
    public string Label { get; set; } = string.Empty; // property (still works)
}

public class IncludeFieldsTests2
{
    [Test]
    public async Task Fields_Excluded_ByDefault()
    {
        var json = JsonSerializer.Serialize(new IncludeFieldsDto { Name = "test" });
        // "Label" is a property, should be serialized
        await Assert.That(json).Contains("Label");
        // "Name" and "Count" are fields, should NOT be serialized by default
        await Assert.That(json).DoesNotContain("Name");
        await Assert.That(json).DoesNotContain("Count");
    }
}

public class RequiredCreateDto
{
    public required string Name { get; set; }
    public int Value { get; set; }
}

public class RequiredCreateTests
{
    [Test]
    public async Task RequiredDto_Deserialize_Succeeds()
    {
        var json = """{"Name":"test","Value":42}"""u8;
        var result = JsonSerializer.Deserialize<RequiredCreateDto>(json);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("test");
        await Assert.That(result.Value).IsEqualTo(42);
    }
}

[PicoSerializable(IncludeFields = true)]
public class FieldDto2
{
    public string Label = "default"; // field
}

public class IncludeFieldsTriggerTests
{
    [Test]
    public async Task PicoSerializable_IncludeFields_SerializesFields()
    {
        var dto = new FieldDto2 { Label = "works" };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("Label");
        await Assert.That(json).Contains("works");
    }
}
