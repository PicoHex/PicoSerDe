namespace PicoMsgPack.Tests;

public class UInt16SignTests
{
    [Test]
    public async Task UInt16_0xFFFF_ReadsAs65535()
    {
        // 0xCD = uint16, 0xFF 0xFF = 65535
        var data = new byte[] { 0xCD, 0xFF, 0xFF };
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        // BUG: reads as ReadInt16BigEndian → -1
        await Assert.That(v).IsEqualTo(65535);
    }

    [Test]
    public async Task UInt16_0xCD_TokenTypeIsUInt16()
    {
        var data = new byte[] { 0xCD, 0x00, 0x01 };
        TokenType tt;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
        }
        // BUG: currently tagged as TokenType.Int32
        await Assert.That(tt).IsEqualTo(TokenType.UInt16);
    }

    [Test]
    public async Task UInt16_0x8000_ReadsAs32768()
    {
        // 0x8000 = 32768, which exceeds Int16 max (32767)
        var data = new byte[] { 0xCD, 0x80, 0x00 };
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(32768);
    }

    [Test]
    public async Task UInt16_0_MinValue()
    {
        var data = new byte[] { 0xCD, 0x00, 0x00 };
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(0);
    }
}
