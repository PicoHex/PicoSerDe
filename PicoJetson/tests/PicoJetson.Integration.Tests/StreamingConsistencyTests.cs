using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PicoJetson.Tests;

// ── Deserialize vs DeserializeFromStreamAsync consistency tests ──
// Regression guard for the PicoJetson bloat report: the two deserialization
// paths used to generate two independent property dispatch chains, which
// drifted (UnmappedMemberHandling.Disallow was missing in the streaming
// path; top-level null behaved differently). Contract: same JSON + same
// options ⇒ same result OR same exception type, on both paths.

public class ConsistencyDto
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Value { get; set; }
}

/// <summary>10-property DTO — subject of the generated-size / thin-wrapper guard.</summary>
public class ConsistencyBigDto
{
    public int A1 { get; set; }
    public int A2 { get; set; }
    public int A3 { get; set; }
    public int A4 { get; set; }
    public int A5 { get; set; }
    public string B1 { get; set; } = "";
    public string B2 { get; set; } = "";
    public string B3 { get; set; } = "";
    public double C { get; set; }
    public bool D { get; set; }
}

public struct ConsistencyStruct
{
    public int Id { get; set; }
    public string Label { get; set; }
}

public class StreamingConsistencyTests
{
    private static async Task<bool> ThrowsFormat(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private static async ValueTask<ConsistencyDto?> DeserializeFromStream(
        byte[] json,
        JsonOptions? opts
    )
    {
        using var ms = new MemoryStream(json);
        return await JsonSerializer.DeserializeFromStreamAsync<ConsistencyDto>(ms, opts);
    }

    // ── Problem 2 from the report: UnmappedMemberHandling drift ──

    [Test]
    public async Task UnmappedMemberHandling_Disallow_UnknownProperty_ThrowsInBothPaths()
    {
        var json = """{"Name":"x","Count":1,"Value":2.5,"Extra":9}"""u8.ToArray();
        var opts = new JsonOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

        var plainThrew = await ThrowsFormat(() =>
            Task.FromResult(JsonSerializer.Deserialize<ConsistencyDto>(json, opts))
        );
        var streamingThrew = await ThrowsFormat(async () =>
            await DeserializeFromStream(json, opts)
        );

        await Assert.That(plainThrew).IsTrue();
        await Assert.That(streamingThrew).IsTrue();
    }

    [Test]
    public async Task UnknownProperty_DefaultSkip_SucceedsInBothPaths()
    {
        var json = """{"Name":"x","Count":1,"Value":2.5,"Extra":9}"""u8.ToArray();

        var plain = JsonSerializer.Deserialize<ConsistencyDto>(json);
        var streamed = await DeserializeFromStream(json, null);

        await Assert.That(plain!.Name).IsEqualTo("x");
        await Assert.That(streamed!.Name).IsEqualTo("x");
        await Assert.That(streamed.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(16)]
    public async Task ChunkedStream_Disallow_UnknownProperty_Throws(int chunkSize)
    {
        var json = """{"Name":"x","Count":1,"Value":2.5,"Extra":9}"""u8.ToArray();
        var opts = new JsonOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        using var stream = new StreamingOptionsTests.ChunkedStream(
            new MemoryStream(json),
            chunkSize
        );

        var threw = await ThrowsFormat(async () =>
            await JsonSerializer.DeserializeFromStreamAsync<ConsistencyDto>(stream, opts)
        );
        await Assert.That(threw).IsTrue();
    }

    // ── Top-level null drift ──

    [Test]
    public async Task TopLevelNull_ReferenceType_ReturnsNullInBothPaths()
    {
        var json = "null"u8.ToArray();

        var plain = JsonSerializer.Deserialize<ConsistencyDto>(json);
        var streamed = await DeserializeFromStream(json, null);

        await Assert.That(plain).IsNull();
        await Assert.That(streamed).IsNull();
    }

    [Test]
    public async Task TopLevelNull_ValueType_Throws_And_StructStreamingIsNotRegistered()
    {
        var json = "null"u8.ToArray();

        // Structs keep the direct deserialization path: the shared
        // StreamingFunc<T> delegate cannot express Nullable<T> parameters,
        // so struct DTOs have no streaming delegate (documented boundary).
        var plainThrew = await ThrowsFormat(() =>
            Task.FromResult(JsonSerializer.Deserialize<ConsistencyStruct>(json))
        );
        await Assert.That(plainThrew).IsTrue();
        await Assert.That(JsonSerializer.HasStreamingDelegate<ConsistencyStruct>()).IsFalse();
    }

    // ── Sanity: valid object parity + empty input parity ──

    [Test]
    public async Task ValidObject_BothPaths_ProduceSameValues()
    {
        var json = """{"Name":"hello","Count":42,"Value":1.5}"""u8.ToArray();

        var plain = JsonSerializer.Deserialize<ConsistencyDto>(json);
        var streamed = await DeserializeFromStream(json, null);

        await Assert.That(streamed!.Name).IsEqualTo(plain!.Name);
        await Assert.That(streamed.Count).IsEqualTo(plain.Count);
        await Assert.That(streamed.Value).IsEqualTo(plain.Value);
    }

    [Test]
    public async Task EmptyInput_BothPaths_ThrowFormatException()
    {
        var plainThrew = await ThrowsFormat(() =>
            Task.FromResult(JsonSerializer.Deserialize<ConsistencyDto>([]))
        );
        var streamingThrew = await ThrowsFormat(async () => await DeserializeFromStream([], null));

        await Assert.That(plainThrew).IsTrue();
        await Assert.That(streamingThrew).IsTrue();
    }

    // ── Problem 1 from the report: duplicated dispatch chain ──
    // Deserialize must be a thin wrapper over the streaming delegate — the
    // single property dispatch chain lives in DeserializeStreaming only.

    [Test]
    public async Task GeneratedCode_Deserialize_IsThinWrapper_OverStreaming()
    {
        // Root the type through the real API first — generation is usage-driven.
        var dto = new ConsistencyBigDto
        {
            A1 = 1,
            A2 = 2,
            A3 = 3,
            A4 = 4,
            A5 = 5,
            B1 = "a",
            B2 = "b",
            B3 = "c",
            C = 1.5,
            D = true,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        var roundTrip = JsonSerializer.Deserialize<ConsistencyBigDto>(bytes);
        await Assert.That(roundTrip!.A5).IsEqualTo(5);

        // File-local generated classes are hidden from Assembly.GetTypes() —
        // read the assembly metadata directly (System.Reflection.Metadata,
        // part of the BCL) and measure the IL size of the generated methods.
        // Contract: Deserialize is a thin wrapper over DeserializeStreaming;
        // the single property dispatch chain lives in the streaming method.
        var asm = typeof(ConsistencyBigDto).Assembly;
        using var fs = File.OpenRead(asm.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var md = pe.GetMetadataReader();

        var deserIl = GetMethodIlSize(md, pe, "__ConsistencyBigDtoJsonDeserializer", "Deserialize");
        var streamingIl = GetMethodIlSize(
            md,
            pe,
            "__ConsistencyBigDtoStreaming",
            "DeserializeStreaming"
        );

        // The 10-prop dispatch chain cannot fit in a thin wrapper's budget.
        await Assert.That(deserIl).IsLessThan(1024);
        // The chain must live in the streaming method (not duplicated).
        await Assert.That(streamingIl).IsGreaterThan(deserIl);
    }

    private static int GetMethodIlSize(
        System.Reflection.Metadata.MetadataReader md,
        System.Reflection.PortableExecutable.PEReader pe,
        string typeNameSuffix,
        string methodName
    )
    {
        foreach (var td in md.TypeDefinitions)
        {
            var tdi = md.GetTypeDefinition(td);
            if (!md.GetString(tdi.Name).EndsWith(typeNameSuffix))
                continue;
            foreach (var mh in tdi.GetMethods())
            {
                var mdi = md.GetMethodDefinition(mh);
                if (md.GetString(mdi.Name) != methodName)
                    continue;
                var rva = mdi.RelativeVirtualAddress;
                var body = pe.GetSectionData(rva).GetContent();
                if (body.Length == 0)
                    continue;
                var header = body[0];
                // Tiny header: bits 7-2 = code size; Fat header: code size is
                // the uint32 at offset 4 (ECMA-335 II.25.4.2).
                return (header & 0x3) == 0x2
                    ? header >> 2
                    : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                        body.AsSpan(4, 4)
                    );
            }
        }
        throw new InvalidOperationException(
            $"Generated type '{typeNameSuffix}' with method '{methodName}' not found"
        );
    }
}
