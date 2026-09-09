namespace PicoJetson.Tests;

// ── Models: full C# integer family ──

public class UnsignedFamilyDto
{
    public uint U { get; set; }
    public short S { get; set; }
    public ushort US { get; set; }
    public byte B { get; set; }
    public sbyte SB { get; set; }
    public ulong L { get; set; }
}

public class UnsignedNestedDto
{
    public string Name { get; set; } = "";
    public ulong Id { get; set; }
    public List<uint> Counts { get; set; } = new();
    public Dictionary<uint, ulong> Map { get; set; } = new();
    public List<List<ulong>> Matrix { get; set; } = new();
}

/// <summary>
/// Regression: PicoAgent reported PicoJetson does not support ulong.
/// Root cause: TypeKindResolver mapped only int/long/float/double — the entire
/// non-core integer family (uint/ushort/short/byte/sbyte/ulong) resolved to
/// null and was silently dropped (or crashed the SG for top-level arrays).
/// </summary>
public class UnsignedIntegerTests
{
    private static (
        TokenType TokenType,
        bool TryInt32Ok,
        bool TryInt64Ok,
        bool TryUInt64Ok,
        ulong UInt64Val
    ) ReadNumber(ReadOnlySpan<byte> json)
    {
        var reader = new JsonReader(json);
        reader.Read();
        var tt = reader.TokenType;
        var tryInt32Ok = reader.TryGetInt32(out _);
        var tryInt64Ok = reader.TryGetInt64(out _);
        var tryUInt64Ok = reader.TryGetUInt64(out var u64);
        return (tt, tryInt32Ok, tryInt64Ok, tryUInt64Ok, u64);
    }

    [Test]
    public async Task Reader_UInt64Max_ReturnsUInt64Token()
    {
        var (tt, ok32, ok64, okU64, v64) = ReadNumber("18446744073709551615"u8);
        await Assert.That(tt).IsEqualTo(TokenType.UInt64);
        await Assert.That(ok32).IsFalse();
        await Assert.That(ok64).IsFalse();
        await Assert.That(okU64).IsTrue();
        await Assert.That(v64).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Reader_LongMaxPlus1_ReturnsUInt64Token()
    {
        var (tt, ok32, ok64, okU64, v64) = ReadNumber("9223372036854775808"u8);
        await Assert.That(tt).IsEqualTo(TokenType.UInt64);
        await Assert.That(ok64).IsFalse();
        await Assert.That(okU64).IsTrue();
        await Assert.That(v64).IsEqualTo(9223372036854775808UL);
    }

    [Test]
    public async Task Reader_NegativeOne_IntoUnsigned_Rejected()
    {
        var (tt, ok32, ok64, okU64, _) = ReadNumber("-1"u8);
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
        await Assert.That(ok32).IsTrue();
        await Assert.That(ok64).IsTrue();
        await Assert.That(okU64).IsFalse();
    }

    [Test]
    public async Task Dto_IntegerFamily_RoundTrips_Boundaries()
    {
        var dto = new UnsignedFamilyDto
        {
            U = uint.MaxValue,
            S = short.MinValue,
            US = ushort.MaxValue,
            B = byte.MaxValue,
            SB = sbyte.MinValue,
            L = ulong.MaxValue,
        };

        var json = JsonSerializer.Serialize(dto);
        await Assert
            .That(json)
            .IsEqualTo(
                "{\"U\":4294967295,\"S\":-32768,\"US\":65535,\"B\":255,\"SB\":-128,\"L\":18446744073709551615}"
            );

        var back = JsonSerializer.Deserialize<UnsignedFamilyDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.U).IsEqualTo(uint.MaxValue);
        await Assert.That(back.S).IsEqualTo(short.MinValue);
        await Assert.That(back.US).IsEqualTo(ushort.MaxValue);
        await Assert.That(back.B).IsEqualTo(byte.MaxValue);
        await Assert.That(back.SB).IsEqualTo(sbyte.MinValue);
        await Assert.That(back.L).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Dto_IntegerFamily_NegativeSmall_RoundTrips()
    {
        var dto = new UnsignedFamilyDto
        {
            U = 1u,
            S = -1,
            US = 0,
            B = 0,
            SB = -5,
            L = 42UL,
        };

        var back = JsonSerializer.Deserialize<UnsignedFamilyDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.U).IsEqualTo(1u);
        await Assert.That(back.S).IsEqualTo((short)-1);
        await Assert.That(back.US).IsEqualTo((ushort)0);
        await Assert.That(back.B).IsEqualTo((byte)0);
        await Assert.That(back.SB).IsEqualTo((sbyte)-5);
        await Assert.That(back.L).IsEqualTo(42UL);
    }

    [Test]
    public async Task Dto_NoIntegerPropertyIsDropped()
    {
        // Regression for silent drop: every property must survive the round trip.
        var dto = new UnsignedFamilyDto
        {
            U = 7u,
            S = 2,
            US = 3,
            B = 4,
            SB = 5,
            L = 6UL,
        };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).IsEqualTo("{\"U\":7,\"S\":2,\"US\":3,\"B\":4,\"SB\":5,\"L\":6}");
    }

    [Test]
    public async Task TopLevel_UInt64Array_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new ulong[] { 1UL, ulong.MaxValue });
        await Assert.That(json).IsEqualTo("[1,18446744073709551615]");
        var back = JsonSerializer.Deserialize<ulong[]>(
            JsonSerializer.SerializeToUtf8Bytes(new ulong[] { 1UL, ulong.MaxValue })
        );
        await Assert.That(back).Count().IsEqualTo(2);
        await Assert.That(back![0]).IsEqualTo(1UL);
        await Assert.That(back[1]).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task TopLevel_ListOfUInt64_RoundTrips()
    {
        var back = JsonSerializer.Deserialize<List<ulong>>(
            JsonSerializer.SerializeToUtf8Bytes(new List<ulong> { ulong.MaxValue, 7UL })
        );
        await Assert.That(back).Count().IsEqualTo(2);
        await Assert.That(back![0]).IsEqualTo(ulong.MaxValue);
        await Assert.That(back[1]).IsEqualTo(7UL);
    }

    [Test]
    public async Task TopLevel_ByteArray_SerializesAsNumberArray()
    {
        var json = JsonSerializer.Serialize(new byte[] { 1, 2, 255 });
        await Assert.That(json).IsEqualTo("[1,2,255]");
        var back = JsonSerializer.Deserialize<byte[]>(
            JsonSerializer.SerializeToUtf8Bytes(new byte[] { 1, 2, 255 })
        );
        await Assert.That(back).Count().IsEqualTo(3);
        await Assert.That(back![0]).IsEqualTo((byte)1);
        await Assert.That(back[1]).IsEqualTo((byte)2);
        await Assert.That(back[2]).IsEqualTo((byte)255);
    }

    [Test]
    public async Task Nested_ListAndDictionaryWithUnsignedKeys_RoundTrips()
    {
        var dto = new UnsignedNestedDto
        {
            Name = "n",
            Id = ulong.MaxValue,
            Counts = new List<uint> { uint.MaxValue, 1u },
            Map = new Dictionary<uint, ulong> { [3u] = 4UL, [uint.MaxValue] = ulong.MaxValue },
            Matrix = new List<List<ulong>>
            {
                new() { 1UL, ulong.MaxValue },
            },
        };

        var back = JsonSerializer.Deserialize<UnsignedNestedDto>(
            JsonSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Name).IsEqualTo("n");
        await Assert.That(back.Id).IsEqualTo(ulong.MaxValue);
        await Assert.That(back.Counts).Count().IsEqualTo(2);
        await Assert.That(back.Counts[0]).IsEqualTo(uint.MaxValue);
        await Assert.That(back.Counts[1]).IsEqualTo(1u);
        await Assert.That(back.Map.Count).IsEqualTo(2);
        await Assert.That(back.Map[3u]).IsEqualTo(4UL);
        await Assert.That(back.Map[uint.MaxValue]).IsEqualTo(ulong.MaxValue);
        await Assert.That(back.Matrix).Count().IsEqualTo(1);
        await Assert.That(back.Matrix[0]).Count().IsEqualTo(2);
        await Assert.That(back.Matrix[0][0]).IsEqualTo(1UL);
        await Assert.That(back.Matrix[0][1]).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task AnyValue_UInt64BeyondInt64_ThrowsOnRead()
    {
        // User-approved semantics: the object? (any-value) path keeps
        // long/double behavior — values beyond long.MaxValue fail loudly.
        var dto = new AnyDictDto
        {
            Map = new Dictionary<string, object?> { ["big"] = 18446744073709551615UL },
        };
        var json = JsonSerializer.Serialize(dto);
        await Assert.That(json).Contains("18446744073709551615");
        await Assert
            .That(
                _Throws(() =>
                    JsonSerializer.Deserialize<AnyDictDto>(JsonSerializer.SerializeToUtf8Bytes(dto))
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task PicoDocument_TryGetUInt64_ReadsLargeUnsigned()
    {
        var doc = PicoDocument.Parse("{\"id\":18446744073709551615}"u8.ToArray());
        await Assert.That(doc.RootElement["id"].TryGetUInt64(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Deserialize_Streaming_UInt64Dto_RoundTrips()
    {
        using var stream = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(
                new UnsignedFamilyDto
                {
                    U = 9u,
                    S = 1,
                    US = 2,
                    B = 3,
                    SB = 4,
                    L = ulong.MaxValue,
                }
            )
        );
        var back = await JsonSerializer.DeserializeFromStreamAsync<UnsignedFamilyDto>(stream);
        await Assert.That(back!.L).IsEqualTo(ulong.MaxValue);
        await Assert.That(back.U).IsEqualTo(9u);
    }

    [Test]
    public async Task Deserialize_NegativeIntoUnsigned_Throws()
    {
        await Assert
            .That(_Throws(() => JsonSerializer.Deserialize<UnsignedFamilyDto>("{\"U\":-1}"u8)))
            .IsTrue();
        await Assert
            .That(_Throws(() => JsonSerializer.Deserialize<UnsignedFamilyDto>("{\"L\":-1}"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_OutOfRange_Throws()
    {
        // uint target with value > uint.MaxValue
        await Assert
            .That(
                _Throws(() => JsonSerializer.Deserialize<UnsignedFamilyDto>("{\"U\":4294967296}"u8))
            )
            .IsTrue();
        // ulong target with value > ulong.MaxValue
        await Assert
            .That(
                _Throws(() =>
                    JsonSerializer.Deserialize<UnsignedFamilyDto>("{\"L\":18446744073709551616}"u8)
                )
            )
            .IsTrue();
        // short target with value out of short range
        await Assert
            .That(_Throws(() => JsonSerializer.Deserialize<UnsignedFamilyDto>("{\"S\":32768}"u8)))
            .IsTrue();
        // long target must still reject values beyond long.MaxValue
        await Assert
            .That(
                _Throws(() =>
                    JsonSerializer.Deserialize<LongSignedDto>("{\"V\":18446744073709551615}"u8)
                )
            )
            .IsTrue();
    }

    private static bool _Throws(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (Exception ex)
            when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return true;
        }
    }
}

public class LongSignedDto
{
    public long V { get; set; }
}

public class AnyDictDto
{
    public Dictionary<string, object?> Map { get; set; } = new();
}
