namespace PicoYaml.Tests;

// ── Models: full C# integer family ──

public class YamlUnsignedFamilyDto
{
    public uint U { get; set; }
    public short S { get; set; }
    public ushort US { get; set; }
    public byte B { get; set; }
    public sbyte SB { get; set; }
    public ulong L { get; set; }
}

/// <summary>
/// PicoYaml must support the full C# integer family (uint/ushort/short/byte/
/// sbyte/ulong) — same shared TypeKindResolver as PicoJetson.
/// </summary>
public class UnsignedIntegerTests
{
    private static bool ThrowsFormat(Action a)
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

    [Test]
    public async Task Dto_IntegerFamily_RoundTrips_Boundaries()
    {
        var dto = new YamlUnsignedFamilyDto
        {
            U = uint.MaxValue,
            S = short.MinValue,
            US = ushort.MaxValue,
            B = byte.MaxValue,
            SB = sbyte.MinValue,
            L = ulong.MaxValue,
        };

        var text = YamlSerializer.Serialize(dto);
        await Assert.That(text).Contains("U: 4294967295");
        await Assert.That(text).Contains("S: -32768");
        await Assert.That(text).Contains("US: 65535");
        await Assert.That(text).Contains("B: 255");
        await Assert.That(text).Contains("SB: -128");
        await Assert.That(text).Contains("L: 18446744073709551615");

        var back = YamlSerializer.Deserialize<YamlUnsignedFamilyDto>(
            YamlSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.U).IsEqualTo(uint.MaxValue);
        await Assert.That(back.S).IsEqualTo(short.MinValue);
        await Assert.That(back.US).IsEqualTo(ushort.MaxValue);
        await Assert.That(back.B).IsEqualTo(byte.MaxValue);
        await Assert.That(back.SB).IsEqualTo(sbyte.MinValue);
        await Assert.That(back.L).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Dto_NoIntegerPropertyIsDropped()
    {
        var dto = new YamlUnsignedFamilyDto
        {
            U = 7u,
            S = 2,
            US = 3,
            B = 4,
            SB = 5,
            L = 6UL,
        };
        var text = YamlSerializer.Serialize(dto);
        await Assert.That(text).Contains("U: 7");
        await Assert.That(text).Contains("S: 2");
        await Assert.That(text).Contains("US: 3");
        await Assert.That(text).Contains("B: 4");
        await Assert.That(text).Contains("SB: 5");
        await Assert.That(text).Contains("L: 6");
    }

    [Test]
    public async Task Dto_SmallValues_RoundTrips()
    {
        var dto = new YamlUnsignedFamilyDto
        {
            U = 1u,
            S = -1,
            US = 0,
            B = 0,
            SB = -5,
            L = 42UL,
        };
        var back = YamlSerializer.Deserialize<YamlUnsignedFamilyDto>(
            YamlSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.U).IsEqualTo(1u);
        await Assert.That(back.S).IsEqualTo((short)-1);
        await Assert.That(back.US).IsEqualTo((ushort)0);
        await Assert.That(back.B).IsEqualTo((byte)0);
        await Assert.That(back.SB).IsEqualTo((sbyte)-5);
        await Assert.That(back.L).IsEqualTo(42UL);
    }

    [Test]
    public async Task TopLevel_ListOfUInt64_RoundTrips()
    {
        var back = YamlSerializer.Deserialize<List<ulong>>(
            YamlSerializer.SerializeToUtf8Bytes(new List<ulong> { ulong.MaxValue, 7UL })
        );
        await Assert.That(back).Count().IsEqualTo(2);
        await Assert.That(back![0]).IsEqualTo(ulong.MaxValue);
        await Assert.That(back[1]).IsEqualTo(7UL);
    }

    [Test]
    public async Task Dict_OfUInt64Values_RoundTrips()
    {
        var dto = new YamlDictDto
        {
            Map = new Dictionary<string, ulong> { ["a"] = 1UL, ["b"] = ulong.MaxValue },
        };
        var back = YamlSerializer.Deserialize<YamlDictDto>(
            YamlSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Map.Count).IsEqualTo(2);
        await Assert.That(back.Map["a"]).IsEqualTo(1UL);
        await Assert.That(back.Map["b"]).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Deserialize_NegativeIntoUnsigned_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() => YamlSerializer.Deserialize<YamlUnsignedFamilyDto>("U: -1\n"u8))
            )
            .IsTrue();
        await Assert
            .That(
                ThrowsFormat(() => YamlSerializer.Deserialize<YamlUnsignedFamilyDto>("L: -1\n"u8))
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_OutOfRange_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    YamlSerializer.Deserialize<YamlUnsignedFamilyDto>("U: 4294967296\n"u8)
                )
            )
            .IsTrue();
        await Assert
            .That(
                ThrowsFormat(() =>
                    YamlSerializer.Deserialize<YamlUnsignedFamilyDto>("L: 18446744073709551616\n"u8)
                )
            )
            .IsTrue();
    }
}

public class YamlDictDto
{
    public Dictionary<string, ulong> Map { get; set; } = new();
}
