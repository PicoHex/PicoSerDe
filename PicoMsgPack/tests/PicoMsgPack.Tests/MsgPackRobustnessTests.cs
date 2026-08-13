namespace PicoMsgPack.Tests;

/// <summary>
/// C5/H6/M6 regression tests: interop integer encodings, sign handling,
/// partial-token streaming, and writer depth protection.
/// </summary>
public class MsgPackRobustnessTests
{
    // ── C5: uint-encoded integers from standard MessagePack encoders ──

    [Test]
    public async Task TryGetInt32_UInt8Token_ReadsUnsignedValue()
    {
        // 0xCC 0x9B = uint8 155
        var data = new byte[] { 0xCC, 0x9B };
        int v;
        bool ok;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(155);
    }

    [Test]
    public async Task TryGetInt32_UInt16Token_ReadsUnsignedValue()
    {
        // 0xCD 0x01 0x00 = uint16 256
        var data = new byte[] { 0xCD, 0x01, 0x00 };
        int v;
        bool ok;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(256);
    }

    [Test]
    public async Task TryGetInt32_UInt32TokenWithinRange_ReadsUnsignedValue()
    {
        // 0xCE 0x00 0x00 0x01 0x00 = uint32 256
        var data = new byte[] { 0xCE, 0x00, 0x00, 0x01, 0x00 };
        int v;
        bool ok;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(256);
    }

    [Test]
    public async Task TryGetInt32_UInt32TokenBeyondIntRange_ReturnsFalse()
    {
        // 0xCE 0xFF 0xFF 0xFF 0xFF = uint32 4294967295
        var data = new byte[] { 0xCE, 0xFF, 0xFF, 0xFF, 0xFF };
        int v;
        bool ok;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryReadInt32Array_InteropUIntEncodings_DecodeCorrectly()
    {
        // [155 (uint8), 256 (uint16), 65536 (uint32)]
        var data = new byte[] { 0x93, 0xCC, 0x9B, 0xCD, 0x01, 0x00, 0xCE, 0x00, 0x01, 0x00, 0x00 };
        var buf = new int[3];
        int n;
        using (var reader = new MsgPackReader(data))
        {
            n = reader.TryReadInt32Array(buf);
        }
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(buf[0]).IsEqualTo(155);
        await Assert.That(buf[1]).IsEqualTo(256);
        await Assert.That(buf[2]).IsEqualTo(65536);
    }

    [Test]
    public async Task TryReadInt32Array_UInt32BeyondIntRange_BailsOut()
    {
        // [4294967295 (uint32)]
        var data = new byte[] { 0x91, 0xCE, 0xFF, 0xFF, 0xFF, 0xFF };
        var buf = new int[1];
        int n;
        using (var reader = new MsgPackReader(data))
        {
            n = reader.TryReadInt32Array(buf);
        }
        await Assert.That(n).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadInt32Array_FloatElement_BailsOut()
    {
        // [1.5 (float64)]
        var data = new byte[] { 0x91, 0xCB, 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var buf = new int[1];
        int n;
        using (var reader = new MsgPackReader(data))
        {
            n = reader.TryReadInt32Array(buf);
        }
        await Assert.That(n).IsEqualTo(0);
    }

    // ── H6: partial tokens in streaming mode signal NeedMoreData ──

    [Test]
    public async Task Read_SequenceMode_PartialString_SignalsNeedMoreData()
    {
        // str8 header declares 10 bytes, only 3 present
        var data = new byte[] { 0xD9, 10, (byte)'a', (byte)'b', (byte)'c' };
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new MsgPackReader(seq, isFinalBlock: false);
        var ok = reader.Read();
        var needsMore = reader.NeedsMoreData;
        await Assert.That(ok).IsFalse();
        await Assert.That(needsMore).IsTrue();
    }

    [Test]
    public async Task Read_SequenceMode_CompleteString_ReadsToken()
    {
        var data = new byte[] { 0xD9, 3, (byte)'a', (byte)'b', (byte)'c' };
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new MsgPackReader(seq, isFinalBlock: false);
        var ok = reader.Read();
        var tt = reader.TokenType;
        await Assert.That(ok).IsTrue();
        await Assert.That(tt).IsEqualTo(TokenType.String);
    }

    [Test]
    public async Task Read_SequenceMode_PartialInt32_SignalsNeedMoreData()
    {
        // int32 header needs 4 payload bytes, only 2 present
        var data = new byte[] { 0xD2, 0x00, 0x01 };
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new MsgPackReader(seq, isFinalBlock: false);
        var ok = reader.Read();
        var needsMore = reader.NeedsMoreData;
        await Assert.That(ok).IsFalse();
        await Assert.That(needsMore).IsTrue();
    }

    [Test]
    public async Task Read_FinalBlock_PartialString_Throws()
    {
        var data = new byte[] { 0xD9, 10, (byte)'a' };
        var reader = new MsgPackReader(data, isFinalBlock: true);
        var threw = false;
        try
        {
            reader.Read();
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    // ── M6: writer depth protection ──

    [Test]
    public async Task Writer_NestedBeyondMaxDepth_ThrowsFormatException()
    {
        var writer = new ArrayBufferWriter<byte>();
        var mw = new MsgPackWriter(writer);
        var threw = false;
        try
        {
            for (int i = 0; i < 70; i++)
                mw.WriteStartArray(1);
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }
}
