namespace PicoMsgPack.Tests;

public class MsgPackReaderSequenceTests
{
    // Helper: create ReadOnlySequence from byte arrays
    private static ReadOnlySequence<byte> MakeSeq(params byte[][] segments)
    {
        if (segments.Length == 1)
            return new ReadOnlySequence<byte>(segments[0]);

        var first = new SeqSegment(segments[0]);
        var last = first;
        for (int i = 1; i < segments.Length; i++)
            last = (SeqSegment)last.Append(segments[i]);
        return new ReadOnlySequence<byte>(first, 0, last, segments[^1].Length);
    }

    private class SeqSegment : ReadOnlySequenceSegment<byte>
    {
        public SeqSegment(byte[] data) => Memory = data;

        public SeqSegment Append(byte[] data)
        {
            var next = new SeqSegment(data) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    [Test]
    public async Task Sequence_Nil_ReturnsNull()
    {
        var seq = MakeSeq(new byte[] { 0xC0 });
        TokenType tt;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            tt = reader.TokenType;
        }
        await Assert.That(tt).IsEqualTo(TokenType.Null);
    }

    [Test]
    public async Task Sequence_PositiveFixInt_ReturnsInt32()
    {
        var seq = MakeSeq(new byte[] { 0x2A });
        int v;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task Sequence_NegativeFixInt_ReturnsInt32()
    {
        var seq = MakeSeq(new byte[] { 0xFF });
        int v;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(-1);
    }

    [Test]
    public async Task Sequence_Bool()
    {
        var seq = MakeSeq(new byte[] { 0xC3 });
        bool v;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            reader.TryGetBool(out v);
        }
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task Sequence_FixStr()
    {
        var seq = MakeSeq(
            new byte[] { 0xA5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }
        );
        string s;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            s = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(s).IsEqualTo("hello");
    }

    [Test]
    public async Task Sequence_CrossSegment_String_InBody()
    {
        // fixstr(5), then "he" in seg1, "llo" in seg2
        var seq = MakeSeq(
            new byte[] { 0xA5, (byte)'h', (byte)'e' },
            new byte[] { (byte)'l', (byte)'l', (byte)'o' }
        );
        string s;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            s = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(s).IsEqualTo("hello");
    }

    [Test]
    public async Task Sequence_CrossSegment_TagAndValue()
    {
        // int8 = 0xD0 + value byte split across segments
        var seq = MakeSeq(
            new byte[] { 0xD0 },
            new byte[] { 0x7B } // 123
        );
        int v;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(123);
    }

    [Test]
    public async Task Sequence_FixMap_TwoKeys()
    {
        // {"a":1} = 0x81, 0xA1 'a', 0x01
        var seq = MakeSeq(new byte[] { 0x81, 0xA1, (byte)'a', 0x01 });
        TokenType t1,
            t4;
        string k;
        int v;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            t1 = reader.TokenType;
            reader.Read();
            k = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            reader.TryGetInt32(out v);
            reader.Read();
            t4 = reader.TokenType;
        }
        await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(k).IsEqualTo("a");
        await Assert.That(v).IsEqualTo(1);
        await Assert.That(t4).IsEqualTo(TokenType.ObjectEnd);
    }

    [Test]
    public async Task Sequence_FixArray_ThreeElements()
    {
        // [1,2,3] = 0x93, 0x01, 0x02, 0x03
        var seq = MakeSeq(new byte[] { 0x93, 0x01, 0x02, 0x03 });
        TokenType t1,
            t5;
        int v1,
            v2,
            v3;
        using (var reader = new MsgPackReader(seq))
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
    public async Task Sequence_CrossSegment_Int32()
    {
        // int32(0xD2) + 4 bytes split 2+2
        var seq = MakeSeq(
            new byte[] { 0xD2, 0x00, 0x00 },
            new byte[] { 0x00, 0x2A } // = 42
        );
        int v;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task Sequence_UInt8_CrossSegment()
    {
        var seq = MakeSeq(new byte[] { 0xCC }, new byte[] { 0xFF });
        int v;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            reader.TryGetInt32(out v);
        }
        await Assert.That(v).IsEqualTo(255);
    }

    [Test]
    public async Task Sequence_Str8()
    {
        var data = new byte[3 + 5];
        data[0] = 0xD9;
        data[1] = 5;
        data[2] = (byte)'h';
        data[3] = (byte)'e';
        data[4] = (byte)'l';
        data[5] = (byte)'l';
        data[6] = (byte)'o';
        var seq = MakeSeq(data);
        string s;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read();
            s = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(s).IsEqualTo("hello");
    }

    [Test]
    public async Task Sequence_RoundTrip_Person_Generated()
    {
        var person = new PersonMsgPack { Name = "Alice", Age = 30 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
        var seq = MakeSeq(bytes);
        string name;
        int age;
        using (var reader = new MsgPackReader(seq))
        {
            reader.Read(); // ObjectStart
            reader.Read();
            reader.TryGetInt32(out _); // key0
            reader.Read();
            name = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            reader.TryGetInt32(out _); // key1
            reader.Read();
            reader.TryGetInt32(out age);
        }
        await Assert.That(name).IsEqualTo("Alice");
        await Assert.That(age).IsEqualTo(30);
    }

    // ── Bin/Ext types ──

    [Test]
    public async Task Bin8_ReturnsBytes()
    {
        var data = new byte[] { 0xC4, 0x03, 0x01, 0x02, 0x03 };
        TokenType tt;
        bool eq;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
            eq = reader.GetStringRaw().SequenceEqual(new byte[] { 1, 2, 3 });
        }
        await Assert.That(tt).IsEqualTo(TokenType.Bytes);
        await Assert.That(eq).IsTrue();
    }

    [Test]
    public async Task Bin16_ReturnsBytes()
    {
        var data = new byte[5];
        data[0] = 0xC5;
        data[1] = 0x00;
        data[2] = 0x02;
        data[3] = 0xAB;
        data[4] = 0xCD;
        int len;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            len = reader.GetStringRaw().Length;
        }
        await Assert.That(len).IsEqualTo(2);
    }

    [Test]
    public async Task FixExt1_ReturnsExtension()
    {
        var data = new byte[] { 0xD4, 0x07, 0x42 };
        TokenType tt;
        byte tag;
        int elen;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetExtension(out tag, out var extData);
            elen = extData.Length;
        }
        await Assert.That(tt).IsEqualTo(TokenType.Extension);
        await Assert.That((int)tag).IsEqualTo(7);
        await Assert.That(elen).IsEqualTo(1);
    }

    [Test]
    public async Task Ext8_ReturnsExtension()
    {
        // ext8(3), tag=7, data=[1,2,3]
        var data = new byte[] { 0xC7, 0x03, 0x07, 0x01, 0x02, 0x03 };
        TokenType tt;
        byte tag;
        int elen;
        using (var reader = new MsgPackReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetExtension(out tag, out var extData);
            elen = extData.Length;
        }
        await Assert.That(tt).IsEqualTo(TokenType.Extension);
        await Assert.That((int)tag).IsEqualTo(7);
        await Assert.That(elen).IsEqualTo(3);
    }
}
