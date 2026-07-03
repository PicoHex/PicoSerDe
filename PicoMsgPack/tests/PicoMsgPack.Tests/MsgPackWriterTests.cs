namespace PicoMsgPack.Tests;

public class MsgPackWriterTests
{
    [Test]
    public async Task WriteNil_WritesC0()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteNull();
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xC0 });
    }

    [Test]
    public async Task WriteBool_WritesC2C3()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xC2, 0xC3 });
    }

    [Test]
    public async Task WriteInt32_Zero_UsesFixInt()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt32(0);
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0x00 });
    }

    [Test]
    public async Task WriteInt32_42_UsesFixInt()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt32(42);
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0x2A });
    }

    [Test]
    public async Task WriteInt32_255_UsesUInt8()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteInt32(255);
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(new byte[] { 0xCC, 0xFF });
    }

    [Test]
    public async Task WriteString_Hello_WritesFixStr()
    {
        var buf = new ArrayBufferWriter<byte>(16);
        var writer = new MsgPackWriter(buf);
        writer.WriteString("hello"u8);
        var expected = new byte[] { 0xA5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        await Assert.That(buf.WrittenSpan.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task WriteStartObject_EndObject_WritesFixMap()
    {
        var buf = new ArrayBufferWriter<byte>(64);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartObject(2);
        writer.WritePropertyName("name"u8);
        writer.WriteString("Alice"u8);
        writer.WritePropertyName("age"u8);
        writer.WriteInt32(30);
        writer.WriteEndObject();

        // Verify by reading back
        var data = buf.WrittenSpan.ToArray();
        TokenType t1;
        string k1,
            v1,
            k2;
        int age;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            t1 = reader.TokenType;
            reader.Read();
            k1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            k2 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            reader.TryGetInt32(out age);
        }
        await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(k1).IsEqualTo("name");
        await Assert.That(v1).IsEqualTo("Alice");
        await Assert.That(k2).IsEqualTo("age");
        await Assert.That(age).IsEqualTo(30);
    }

    [Test]
    public async Task RoundTrip_Person_Manual()
    {
        var buf = new ArrayBufferWriter<byte>(64);
        var writer = new MsgPackWriter(buf);
        writer.WriteStartObject(2);
        writer.WritePropertyName("name"u8);
        writer.WriteString("Bob"u8);
        writer.WritePropertyName("age"u8);
        writer.WriteInt32(25);
        writer.WriteEndObject();

        var data = buf.WrittenSpan.ToArray();
        var reader = new MsgPackReader(data);
        string name = string.Empty;
        int age = 0;
        reader.Read(); // ObjectStart
        reader.Read(); /* "name" */
        reader.Read();
        name = Encoding.UTF8.GetString(reader.GetStringRaw()); // "Bob"
        reader.Read(); /* "age" */
        reader.Read();
        reader.TryGetInt32(out age);
        await Assert.That(name).IsEqualTo("Bob");
        await Assert.That(age).IsEqualTo(25);
    }

    [Test]
    public async Task WriteExtension_RoundTrip()
    {
        var buf = new ArrayBufferWriter<byte>(64);
        var writer = new MsgPackWriter(buf);
        var data = new byte[] { 0x01, 0x02, 0x03 };
        writer.WriteExtension(42, data);

        var bytes = buf.WrittenSpan.ToArray();
        TokenType tt;
        bool ok;
        byte tag;
        byte[] extData;
        {
            var reader = new MsgPackReader(bytes);
            reader.Read();
            tt = reader.TokenType;
            ok = reader.TryGetExtension(out tag, out var extSpan);
            extData = extSpan.ToArray();
        }
        await Assert.That(tt).IsEqualTo(TokenType.Extension);
        await Assert.That(ok).IsTrue();
        await Assert.That(tag).IsEqualTo((byte)42);
        await Assert.That(extData).IsEquivalentTo(data);
    }

    // === Code review #8: str32/bin32 support for large strings ===

    [Test]
    public async Task WriteString_ExceedsUInt16_UsesStr32()
    {
        var buf = new ArrayBufferWriter<byte>(70000);
        var writer = new MsgPackWriter(buf);
        // Create a string of 65536 'a' bytes — exceeds ushort max
        var large = new byte[65536];
        Array.Fill<byte>(large, (byte)'a');
        writer.WriteString(large);

        var result = buf.WrittenSpan.ToArray();
        // str32 header: 0xDB + 4-byte big-endian length
        await Assert.That(result[0]).IsEqualTo((byte)0xDB);
        var len = (uint)(result[1] << 24 | result[2] << 16 | result[3] << 8 | result[4]);
        await Assert.That(len).IsEqualTo(65536u);
        await Assert.That(result[5]).IsEqualTo((byte)'a');
        await Assert.That(result[^1]).IsEqualTo((byte)'a');
    }

    [Test]
    public async Task WriteBytes_ExceedsUInt16_UsesBin32()
    {
        var buf = new ArrayBufferWriter<byte>(70000);
        var writer = new MsgPackWriter(buf);
        var large = new byte[65536];
        Array.Fill<byte>(large, (byte)0xFF);
        writer.WriteBytes(large);

        var result = buf.WrittenSpan.ToArray();
        // bin32 header: 0xC6 + 4-byte big-endian length
        await Assert.That(result[0]).IsEqualTo((byte)0xC6);
        var len = (uint)(result[1] << 24 | result[2] << 16 | result[3] << 8 | result[4]);
        await Assert.That(len).IsEqualTo(65536u);
    }
}
