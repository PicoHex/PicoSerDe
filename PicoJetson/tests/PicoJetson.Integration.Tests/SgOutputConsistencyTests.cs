namespace PicoJetson.Integration.Tests;

// ── SG output consistency tests ──
// These verify that the source generator produces internally consistent code —
// e.g., if a streaming delegate is registered, the corresponding class must exist.
//
// Without these tests, bugs like the poly-streaming CS0103 can only be caught at
// compile time (if the SG generates a dead reference) or runtime (if someone
// actually calls DeserializeFromStreamAsync).

// Models for SG consistency testing
public class SgConsistencyModel
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

[PicoSerializable]
[PicoDerivedType(typeof(SgConsistencyChild), "child")]
public abstract class SgConsistencyBase { }

public class SgConsistencyChild : SgConsistencyBase
{
    public string Data { get; set; } = string.Empty;
}

public class SgConsistencyImmutable
{
    public string Key { get; }
    public int Version { get; }

    [JsonConstructor]
    public SgConsistencyImmutable(string key, int version)
    {
        Key = key;
        Version = version;
    }
}

public class SgOutputConsistencyTests
{
    // ── Streaming delegate registration consistency ──
    // If HasStreamingDelegate<T> returns true, DeserializeFromStreamAsync<T>
    // must work (not throw due to missing class). Conversely, if it returns
    // false, DeserializeFromStreamAsync must throw InvalidOperationException
    // rather than a compiler-level CS0103.

    [Test]
    public async Task RegularType_HasStreamingDelegate_And_StreamingWorks()
    {
        var hasDelegate = JsonSerializer.HasStreamingDelegate<SgConsistencyModel>();
        await Assert.That(hasDelegate).IsTrue();

        var json = """{"Name":"ok","Value":1}"""u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<SgConsistencyModel>(stream);
        await Assert.That(result.Name).IsEqualTo("ok");
    }

    [Test]
    public async Task PolyBase_HasStreamingDelegate_And_StreamingWorks()
    {
        var hasDelegate = JsonSerializer.HasStreamingDelegate<SgConsistencyBase>();
        await Assert.That(hasDelegate).IsTrue();

        var json = """{"$type":"child","Data":"x"}"""u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<SgConsistencyBase>(stream);
        await Assert.That(result).IsTypeOf<SgConsistencyChild>();
        await Assert.That(((SgConsistencyChild)result).Data).IsEqualTo("x");
    }

    [Test]
    public async Task ImmutableType_HasStreamingDelegate_And_StreamingWorks()
    {
        var hasDelegate = JsonSerializer.HasStreamingDelegate<SgConsistencyImmutable>();
        await Assert.That(hasDelegate).IsTrue();

        var json = """{"Key":"k","Version":3}"""u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<SgConsistencyImmutable>(
            stream
        );
        await Assert.That(result.Key).IsEqualTo("k");
        await Assert.That(result.Version).IsEqualTo(3);
    }

    [Test]
    public async Task TopLevelArray_HasStreamingDelegate_And_StreamingWorks()
    {
        var hasDelegate = JsonSerializer.HasStreamingDelegate<string[]>();
        await Assert.That(hasDelegate).IsTrue();

        var json = "[\"a\",\"b\"]"u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<string[]>(stream);
        await Assert.That(result).HasCount().EqualTo(2);
        await Assert.That(result[0]).IsEqualTo("a");
    }

    [Test]
    public async Task SerializeThenDeserialize_AllTypeVariants_RoundTrip()
    {
        // Regular
        {
            var original = new SgConsistencyModel { Name = "r", Value = 1 };
            var json = JsonSerializer.SerializeToUtf8Bytes(original);
            var result = JsonSerializer.Deserialize<SgConsistencyModel>(json);
            await Assert.That(result!.Name).IsEqualTo("r");
        }

        // Poly
        {
            SgConsistencyBase original = new SgConsistencyChild { Data = "d" };
            var json = JsonSerializer.SerializeToUtf8Bytes(original);
            var result = JsonSerializer.Deserialize<SgConsistencyBase>(json);
            await Assert.That(result).IsTypeOf<SgConsistencyChild>();
        }

        // Immutable
        {
            var original = new SgConsistencyImmutable("k", 5);
            var json = JsonSerializer.SerializeToUtf8Bytes(original);
            var result = JsonSerializer.Deserialize<SgConsistencyImmutable>(json);
            await Assert.That(result!.Key).IsEqualTo("k");
        }

        // Array
        {
            var arr = new[] { "x", "y" };
            var json = JsonSerializer.SerializeToUtf8Bytes(arr);
            var result = JsonSerializer.Deserialize<string[]>(json);
            await Assert.That(result!).HasCount().EqualTo(2);
        }
    }
}
