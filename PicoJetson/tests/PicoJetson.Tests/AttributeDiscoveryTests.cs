using System.Buffers;

namespace PicoJetson.Tests;

/// <summary>Type discovered via [PicoSerializable] (all formats).</summary>
[PicoSerializable]
public class AllFormatsDto
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

/// <summary>Type discovered via [PicoJsonSerializable] (JSON only).</summary>
[PicoJsonSerializable]
public class JsonOnlyDto
{
    public string Label { get; set; } = "";
}

/// <summary>Indirect discovery via [PicoJsonSerializable(typeof(T))].</summary>
[PicoJsonSerializable(typeof(JsonIndirectDto))]
public class JsonConfig { }

public class JsonIndirectDto
{
    public string Data { get; set; } = "";
}

/// <summary>Type discovered via [GenerateSerializer(typeof(T))].</summary>
[GenerateSerializer(typeof(GenSerializerRefDto))]
class GenSerializerConfig { }

public class GenSerializerRefDto
{
    public string Token { get; set; } = "";
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
