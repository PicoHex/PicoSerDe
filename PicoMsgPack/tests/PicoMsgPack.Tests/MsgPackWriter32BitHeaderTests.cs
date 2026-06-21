namespace PicoMsgPack.Tests;

public class MsgPackWriter32BitHeaderTests
{
    [Test]
    public async Task WriteStartObject_65536Elements_UsesMap32()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartObject(70000); // > 65535
        // 0xDF = map32, followed by 4-byte uint32 count (70000 = 0x00011170)
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xDF, 0x00, 0x01, 0x11, 0x70 });
    }

    [Test]
    public async Task WriteStartArray_65536Elements_UsesArray32()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartArray(70000); // > 65535
        // 0xDD = array32, followed by 4-byte uint32 count
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xDD, 0x00, 0x01, 0x11, 0x70 });
    }

    [Test]
    public async Task WriteStartObject_16To65535_UsesMap16()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartObject(1000);
        // 0xDE = map16, followed by 2-byte ushort count (1000 = 0x03E8)
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xDE, 0x03, 0xE8 });
    }

    [Test]
    public async Task WriteStartArray_16To65535_UsesArray16()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartArray(1000);
        // 0xDC = array16, followed by 2-byte ushort count
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xDC, 0x03, 0xE8 });
    }
}
