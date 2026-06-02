namespace PicoMsgPack.Tests;

public class MsgPackReaderTests
{
    [Test]
    public async Task Nil_ReturnsNull()
    {
        var data = new byte[] { 0xC0 };
        TokenType tt;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
        }
        await Assert.That(tt).IsEqualTo(TokenType.Null);
    }

    [Test]
    public async Task BooleanFalse_ReturnsBool()
    {
        var data = new byte[] { 0xC2 };
        TokenType tt;
        bool v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetBool(out v);
        }
        await Assert.That(tt).IsEqualTo(TokenType.Bool);
        await Assert.That(v).IsFalse();
    }

    [Test]
    public async Task BooleanTrue_ReturnsBool()
    {
        var data = new byte[] { 0xC3 };
        bool v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            reader.TryGetBool(out v);
        }
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task PositiveFixInt_ReturnsInt32()
    {
        var data = new byte[] { 0x2A };
        TokenType tt;
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetInt32(out v);
        }
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task NegativeFixInt_ReturnsInt32()
    {
        var data = new byte[] { 0xFF };
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(-1);
    }

    [Test]
    public async Task UInt8_ReturnsInt32()
    {
        var data = new byte[] { 0xCC, 0xFF };
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(255);
    }

    [Test]
    public async Task Int8_ReturnsInt32()
    {
        var data = new byte[] { 0xD0, 0x80 };
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(-128);
    }

    [Test]
    public async Task FixStr_ReturnsString()
    {
        var data = new byte[] { 0xA5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        TokenType tt;
        string s;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
            s = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(tt).IsEqualTo(TokenType.String);
        await Assert.That(s).IsEqualTo("hello");
    }

    [Test]
    public async Task FixArray_ReturnsArrayTokens()
    {
        var data = new byte[] { 0x93, 0x01, 0x02, 0x03 };
        TokenType t1,
            t5;
        int v1,
            v2,
            v3;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            t1 = reader.TokenType;
            reader.Read();
            reader.TryGetInt32(out v1);
            reader.Read();
            reader.TryGetInt32(out v2);
            reader.Read();
            reader.TryGetInt32(out v3);
            reader.Read();
            t5 = reader.TokenType;
        }
        await Assert.That(t1).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(v1).IsEqualTo(1);
        await Assert.That(v2).IsEqualTo(2);
        await Assert.That(v3).IsEqualTo(3);
        await Assert.That(t5).IsEqualTo(TokenType.ArrayEnd);
    }

    [Test]
    public async Task FixMap_ReturnsObjectTokens()
    {
        var data = new byte[] { 0x81, 0xA1, (byte)'a', 0x01 };
        TokenType t1,
            t2,
            t4;
        string k;
        int v;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            t1 = reader.TokenType;
            reader.Read();
            t2 = reader.TokenType;
            k = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            reader.TryGetInt32(out v);
            reader.Read();
            t4 = reader.TokenType;
        }
        await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(t2).IsEqualTo(TokenType.PropertyName);
        await Assert.That(k).IsEqualTo("a");
        await Assert.That(v).IsEqualTo(1);
        await Assert.That(t4).IsEqualTo(TokenType.ObjectEnd);
    }

    [Test]
    public async Task RoundTrip_Complex_ManuallyEncoded()
    {
        // {"name":"Alice","age":30} = fixmap(2), fixstr("name"), fixstr("Alice"), fixstr("age"), 30
        var nameBytes = Encoding.UTF8.GetBytes("name");
        var aliceBytes = Encoding.UTF8.GetBytes("Alice");
        var ageBytes = Encoding.UTF8.GetBytes("age");
        var data = new byte[
            1 + 1 + nameBytes.Length + 1 + aliceBytes.Length + 1 + ageBytes.Length + 1
        ];
        int p = 0;
        data[p++] = 0x82; // fixmap(2)
        data[p++] = (byte)(0xA0 | nameBytes.Length);
        Array.Copy(nameBytes, 0, data, p, nameBytes.Length);
        p += nameBytes.Length;
        data[p++] = (byte)(0xA0 | aliceBytes.Length);
        Array.Copy(aliceBytes, 0, data, p, aliceBytes.Length);
        p += aliceBytes.Length;
        data[p++] = (byte)(0xA0 | ageBytes.Length);
        Array.Copy(ageBytes, 0, data, p, ageBytes.Length);
        p += ageBytes.Length;
        data[p++] = 30;

        TokenType t1,
            t2,
            t4,
            t6;
        string k1,
            v1,
            k2;
        int v2;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            t1 = reader.TokenType;
            reader.Read();
            t2 = reader.TokenType;
            k1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            t4 = reader.TokenType;
            k2 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            reader.TryGetInt32(out v2);
            reader.Read();
            t6 = reader.TokenType;
        }
        await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(t2).IsEqualTo(TokenType.PropertyName);
        await Assert.That(k1).IsEqualTo("name");
        await Assert.That(v1).IsEqualTo("Alice");
        await Assert.That(t4).IsEqualTo(TokenType.PropertyName);
        await Assert.That(k2).IsEqualTo("age");
        await Assert.That(v2).IsEqualTo(30);
        await Assert.That(t6).IsEqualTo(TokenType.ObjectEnd);
    }

    // === P1 #3: maxDepth guard ===
    [Test]
    public async Task DeepNesting_ExceedsMaxDepth_ThrowsFormatException()
    {
        // Build 65 nested 1-element arrays (exceeds IntStack64's 64-element capacity)
        // Each 0x91 = fixarray(1)
        var data = new byte[65];
        for (int i = 0; i < 65; i++)
            data[i] = 0x91; // fixarray with 1 element

        var reader = new MsgPackReader(data);
        try
        {
            // Read deep enough to exceed depth 64
            for (int i = 0; i < 64; i++)
                reader.Read();
            // The 65th should throw
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    // === Code review #7: truncated input boundary checks ===

    [Test]
    public async Task TruncatedString_ThrowsFormatException()
    {
        // str8 header (0xD9) says 10 bytes, but only 3 follow
        var data = new byte[] { 0xD9, 10, (byte)'a', (byte)'b', (byte)'c' };
        var reader = new MsgPackReader(data);
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task TruncatedBinHeader_ThrowsFormatException()
    {
        // bin16 header (0xC5) needs 2 length bytes, but only 1 follows
        var data = new byte[] { 0xC5, 0x00 };
        var reader = new MsgPackReader(data);
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }
}
