namespace PicoMsgPack.Tests;

// ── Models: full C# integer family ──

public class MpUnsignedFamilyDto
{
    public uint U { get; set; }
    public short S { get; set; }
    public ushort US { get; set; }
    public byte B { get; set; }
    public sbyte SB { get; set; }
    public ulong L { get; set; }
}

public class MpDictDto
{
    public Dictionary<string, ulong> Map { get; set; } = new();
}

public class MpSignedProxy
{
    public int U { get; set; }
}

public class MpLayoutTwin
{
    public int U { get; set; }
    public int S { get; set; }
    public int US { get; set; }
    public int B { get; set; }
    public int SB { get; set; }
    public long L { get; set; }
}

public class MpIntProxy
{
    public int S { get; set; }
}

/// <summary>
/// PicoMsgPack must support the full C# integer family (uint/ushort/short/
/// byte/sbyte/ulong) — same shared TypeKindResolver as PicoJetson.
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
        var dto = new MpUnsignedFamilyDto
        {
            U = uint.MaxValue,
            S = short.MinValue,
            US = ushort.MaxValue,
            B = byte.MaxValue,
            SB = sbyte.MinValue,
            L = ulong.MaxValue,
        };

        var back = MsgPackSerializer.Deserialize<MpUnsignedFamilyDto>(
            MsgPackSerializer.SerializeToUtf8Bytes(dto)
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
        var dto = new MpUnsignedFamilyDto
        {
            U = 7u,
            S = 2,
            US = 3,
            B = 4,
            SB = 5,
            L = 6UL,
        };
        var back = MsgPackSerializer.Deserialize<MpUnsignedFamilyDto>(
            MsgPackSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.U).IsEqualTo(7u);
        await Assert.That(back.S).IsEqualTo((short)2);
        await Assert.That(back.US).IsEqualTo((ushort)3);
        await Assert.That(back.B).IsEqualTo((byte)4);
        await Assert.That(back.SB).IsEqualTo((sbyte)5);
        await Assert.That(back.L).IsEqualTo(6UL);
    }

    [Test]
    public async Task Dto_SmallValues_RoundTrips()
    {
        var dto = new MpUnsignedFamilyDto
        {
            U = 1u,
            S = -1,
            US = 0,
            B = 0,
            SB = -5,
            L = 42UL,
        };
        var back = MsgPackSerializer.Deserialize<MpUnsignedFamilyDto>(
            MsgPackSerializer.SerializeToUtf8Bytes(dto)
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
        var back = MsgPackSerializer.Deserialize<List<ulong>>(
            MsgPackSerializer.SerializeToUtf8Bytes(new List<ulong> { ulong.MaxValue, 7UL })
        );
        await Assert.That(back).Count().IsEqualTo(2);
        await Assert.That(back![0]).IsEqualTo(ulong.MaxValue);
        await Assert.That(back[1]).IsEqualTo(7UL);
    }

    [Test]
    public async Task Dict_OfUInt64Values_RoundTrips()
    {
        var dto = new MpDictDto
        {
            Map = new Dictionary<string, ulong> { ["a"] = 1UL, ["b"] = ulong.MaxValue },
        };
        var back = MsgPackSerializer.Deserialize<MpDictDto>(
            MsgPackSerializer.SerializeToUtf8Bytes(dto)
        );
        await Assert.That(back!.Map.Count).IsEqualTo(2);
        await Assert.That(back.Map["a"]).IsEqualTo(1UL);
        await Assert.That(back.Map["b"]).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task Deserialize_NegativeIntoUnsigned_Throws()
    {
        var negativeU = MsgPackSerializer.SerializeToUtf8Bytes(new MpUnsignedFamilyDto { U = 1u });
        // hand-craft an int32 -1 value for U by serializing a signed model
        var negBytes = MsgPackSerializer.SerializeToUtf8Bytes(new MpSignedProxy { U = -1 });
        await Assert
            .That(ThrowsFormat(() => MsgPackSerializer.Deserialize<MpUnsignedFamilyDto>(negBytes)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_OutOfRangeIntoSmallType_Throws()
    {
        // 32768 (fits int32, out of short range) into short must fail loudly.
        // Source twin mirrors MpUnsignedFamilyDto's property layout so MsgPack
        // integer keys (property order) align.
        var big = MsgPackSerializer.SerializeToUtf8Bytes(
            new MpLayoutTwin
            {
                U = 0,
                S = 32768,
                US = 0,
                B = 0,
                SB = 0,
                L = 0,
            }
        );
        await Assert
            .That(ThrowsFormat(() => MsgPackSerializer.Deserialize<MpUnsignedFamilyDto>(big)))
            .IsTrue();
    }
}
