namespace PicoMsgPack.Tests;

public class MsgPackWriter32BitHeaderTests
{
    [Test]
    public async Task WriteStartObject_65536Elements_UsesMap32()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartObject(70000); // > 65535
        writer.Flush();
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
        writer.Flush();
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
        writer.Flush();
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
        writer.Flush();
        // 0xDC = array16, followed by 2-byte ushort count
        await Assert
            .That(buf.WrittenSpan.ToArray())
            .IsEquivalentTo(new byte[] { 0xDC, 0x03, 0xE8 });
    }

    // ── the fixarray/array16 boundary itself ──
    // 15 is the last element count that fits the 4-bit fixarray header, 16 is the
    // first that needs array16. The tests above pin 1000 and 70000, both far from
    // that boundary, so an off-by-one comparison in WriteStartArray (e.g. <= 16)
    // keeps every one of them green: only these two can see it.

    [Test]
    public async Task WriteStartArray_15Elements_UsesFixarrayHeader_AndReadsBack15()
    {
        var written = WriteArrayOfFixints(15);

        // 0x9F = 0x90 | 15: the last count that fits a one-byte fixarray header.
        // Every element is 0..14, i.e. a one-byte positive fixint, so the total is 1 + 15.
        await Assert.That(written[0]).IsEqualTo((byte)0x9F).Because("15 elements = fixarray");
        await Assert.That(written.Length).IsEqualTo(16).Because("1 header byte + 15 fixints");

        var (form, count, elements) = ReadBack(written);
        await Assert.That(form).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(count).IsEqualTo(15).Because("the header must declare 15 elements");
        await Assert.That(elements).IsEqualTo(15).Because("15 element tokens, then ArrayEnd");
    }

    [Test]
    public async Task WriteStartArray_16Elements_UsesArray16Header_AndReadsBack16()
    {
        var written = WriteArrayOfFixints(16);

        // 0xDC 0x00 0x10 = array16, 16 elements: one past the fixarray limit.
        // 0x90 | 16 would be 0xA0, which MessagePack reads back as a fixMAP of 16 pairs.
        await Assert.That(written[0]).IsEqualTo((byte)0xDC).Because("16 elements = array16");
        await Assert.That(written[1]).IsEqualTo((byte)0x00).Because("high byte of 16");
        await Assert.That(written[2]).IsEqualTo((byte)0x10).Because("low byte of 16");
        await Assert.That(written.Length).IsEqualTo(19).Because("3 header bytes + 16 fixints");

        var (form, count, elements) = ReadBack(written);
        await Assert.That(form).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(count).IsEqualTo(16).Because("the header must declare 16 elements");
        await Assert.That(elements).IsEqualTo(16).Because("16 element tokens, then ArrayEnd");
    }

    /// <summary>An array of <paramref name="n"/> fixints 0..n-1, flattened to the written bytes.</summary>
    private static byte[] WriteArrayOfFixints(int n)
    {
        var buf = new ArrayBufferWriter<byte>(64);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartArray(n);
        for (int i = 0; i < n; i++)
            writer.WriteInt32(i);
        writer.WriteEndArray();
        writer.Flush();
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads the bytes back with the reader under test: the array-start token, the count it
    /// declares, and how many element tokens follow before the array closes. Ref structs cannot
    /// cross an await, so the reads finish here and only plain values reach the assertions.
    /// </summary>
    private static (TokenType Form, int Count, int Elements) ReadBack(byte[] payload)
    {
        TokenType form = TokenType.None;
        int count = -1;
        int elements = -1;
        using (var reader = new MsgPackReader(payload))
        {
            if (reader.Read())
            {
                form = reader.TokenType;
                count = reader.TryGetCollectionCount(out var declared) ? declared : -1;
                elements = 0;
                while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                    elements++;
            }
        }
        return (form, count, elements);
    }
}
