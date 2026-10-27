using System.Buffers.Binary;

namespace PicoMsgPack.Tests;

public class WriteInt64EncodingTests
{
    [Test]
    public async Task WriteInt64_0_UsesPositiveFixInt()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(0);
        writer.FlushToBuffer();
        // 0x00 = positive fixint (1 byte)
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0x00 });
    }

    [Test]
    public async Task WriteInt64_127_UsesPositiveFixInt()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(127);
        writer.FlushToBuffer();
        // 0x7F = positive fixint (1 byte)
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0x7F });
    }

    [Test]
    public async Task WriteInt64_128_UsesUInt8()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(128);
        writer.FlushToBuffer();
        // BUG: currently writes 0xD3 + 8 bytes = 9 bytes
        // Should write 0xCC 0x80 = 2 bytes (uint8)
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xCC, 0x80 });
    }

    [Test]
    public async Task WriteInt64_255_UsesUInt8()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(255);
        writer.FlushToBuffer();
        // 0xCC 0xFF = uint8 (2 bytes)
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xCC, 0xFF });
    }

    [Test]
    public async Task WriteInt64_Neg1_UsesNegativeFixInt()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(-1);
        writer.FlushToBuffer();
        // 0xFF = negative fixint (1 byte)
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xFF });
    }

    [Test]
    public async Task WriteInt64_Neg33_UsesInt8()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(-33);
        writer.FlushToBuffer();
        // BUG: currently writes 0xD3 + 8 bytes
        // Should write 0xD0 0xDF = 2 bytes (int8)
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xD0, 0xDF });
    }

    [Test]
    public async Task WriteInt64_Neg128_UsesInt8()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(-128);
        writer.FlushToBuffer();
        // 0xD0 0x80 = int8 (2 bytes)
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xD0, 0x80 });
    }

    [Test]
    public async Task WriteInt64_256_UsesUInt16()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(256);
        writer.FlushToBuffer();
        // 0xCD 0x01 0x00 = uint16 (3 bytes)
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xCD, 0x01, 0x00 });
    }

    [Test]
    public async Task WriteInt64_65535_UsesUInt16()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(65535);
        writer.FlushToBuffer();
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xCD, 0xFF, 0xFF });
    }

    [Test]
    public async Task WriteInt64_Minus129_UsesInt16()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(-129);
        writer.FlushToBuffer();
        // 0xD1 0xFF 0x7F = int16 (3 bytes)
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xD1, 0xFF, 0x7F });
    }

    [Test]
    public async Task WriteInt64_65536_UsesUInt32()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(65536);
        writer.FlushToBuffer();
        // 0xCE 0x00 0x01 0x00 0x00 = uint32 (5 bytes)
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xCE, 0x00, 0x01, 0x00, 0x00 });
    }

    [Test]
    public async Task WriteInt64_Minus32769_UsesInt32()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(-32769);
        writer.FlushToBuffer();
        // 0xD2 0xFF 0xFF 0x7F 0xFF = int32 (5 bytes)
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xD2, 0xFF, 0xFF, 0x7F, 0xFF });
    }

    [Test]
    public async Task WriteInt64_4294967295_UsesUInt32()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(4294967295); // uint.MaxValue
        writer.FlushToBuffer();
        // 0xCE 0xFF 0xFF 0xFF 0xFF = uint32 (5 bytes)
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xCE, 0xFF, 0xFF, 0xFF, 0xFF });
    }

    [Test]
    public async Task WriteInt64_4294967296_UsesInt64()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt64(4294967296); // uint.MaxValue + 1
        writer.FlushToBuffer();
        // 0xD3 + 8 bytes = int64 (9 bytes)
        var expected = new byte[9];
        expected[0] = 0xD3;
        BinaryPrimitives.WriteInt64BigEndian(expected.AsSpan(1), 4294967296);
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(expected);
    }
}
