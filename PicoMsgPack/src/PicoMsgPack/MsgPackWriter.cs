namespace PicoMsgPack;

public ref struct MsgPackWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;

    public long BytesWritten => _bytesWritten;

    /// <summary>Underlying buffer — used by SG-generated code to hand off to custom serializers.</summary>
    public IBufferWriter<byte> Buffer => _buffer;

    public MsgPackWriter(IBufferWriter<byte> buffer)
    {
        _buffer = buffer;
        _bytesWritten = 0;
    }

    public void WriteNull()
    {
        Span<byte> s = _buffer.GetSpan(1);
        s[0] = 0xC0;
        _buffer.Advance(1);
        _bytesWritten++;
    }

    public void WriteBoolean(bool value)
    {
        Span<byte> s = _buffer.GetSpan(1);
        s[0] = value ? (byte)0xC3 : (byte)0xC2;
        _buffer.Advance(1);
        _bytesWritten++;
    }

    public void WriteInt32(int value)
    {
        if (value >= 0 && value <= 127)
        {
            Span<byte> s = _buffer.GetSpan(1);
            s[0] = (byte)value;
            _buffer.Advance(1);
            _bytesWritten++;
        }
        else if (value >= -32 && value < 0)
        {
            Span<byte> s = _buffer.GetSpan(1);
            s[0] = (byte)value;
            _buffer.Advance(1);
            _bytesWritten++;
        }
        else if (value >= 0 && value <= 255)
        {
            Span<byte> s = _buffer.GetSpan(2);
            s[0] = 0xCC;
            s[1] = (byte)value;
            _buffer.Advance(2);
            _bytesWritten += 2;
        }
        else if (value >= short.MinValue && value <= short.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(3);
            s[0] = 0xD1;
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(1), (short)value);
            _buffer.Advance(3);
            _bytesWritten += 3;
        }
        else
        {
            Span<byte> s = _buffer.GetSpan(5);
            s[0] = 0xD2;
            BinaryPrimitives.WriteInt32BigEndian(s.Slice(1), value);
            _buffer.Advance(5);
            _bytesWritten += 5;
        }
    }

    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        int len = utf8Value.Length;
        if (len <= 31)
        {
            Span<byte> s = _buffer.GetSpan(1 + len);
            s[0] = (byte)(0xA0 | len);
            utf8Value.CopyTo(s.Slice(1));
            _buffer.Advance(1 + len);
            _bytesWritten += 1 + len;
        }
        else if (len <= 255)
        {
            Span<byte> s = _buffer.GetSpan(2 + len);
            s[0] = 0xD9;
            s[1] = (byte)len;
            utf8Value.CopyTo(s.Slice(2));
            _buffer.Advance(2 + len);
            _bytesWritten += 2 + len;
        }
        else if (len <= 65535)
        {
            Span<byte> s = _buffer.GetSpan(3 + len);
            s[0] = 0xDA;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)len);
            utf8Value.CopyTo(s.Slice(3));
            _buffer.Advance(3 + len);
            _bytesWritten += 3 + len;
        }
        else
        {
            Span<byte> s = _buffer.GetSpan(5 + len);
            s[0] = 0xDB;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)len);
            utf8Value.CopyTo(s.Slice(5));
            _buffer.Advance(5 + len);
            _bytesWritten += 5 + len;
        }
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name) => WriteString(utf8Name);

    public void WriteStartObject(int count)
    {
        if (count <= 15)
        {
            Span<byte> s = _buffer.GetSpan(1);
            s[0] = (byte)(0x80 | count);
            _buffer.Advance(1);
            _bytesWritten++;
        }
        else if (count <= ushort.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(3);
            s[0] = 0xDE;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)count);
            _buffer.Advance(3);
            _bytesWritten += 3;
        }
        else
        {
            Span<byte> s = _buffer.GetSpan(5);
            s[0] = 0xDF;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)count);
            _buffer.Advance(5);
            _bytesWritten += 5;
        }
    }

    public void WriteEndObject() { /* MsgPack maps don't have end markers; count is specified upfront */
    }

    public void WriteInt64(long value)
    {
        if (value >= 0 && value <= 127)
        {
            Span<byte> s = _buffer.GetSpan(1);
            s[0] = (byte)value;
            _buffer.Advance(1);
            _bytesWritten++;
        }
        else if (value >= -32 && value < 0)
        {
            Span<byte> s = _buffer.GetSpan(1);
            s[0] = (byte)value;
            _buffer.Advance(1);
            _bytesWritten++;
        }
        else if (value >= 0 && value <= byte.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(2);
            s[0] = 0xCC;
            s[1] = (byte)value;
            _buffer.Advance(2);
            _bytesWritten += 2;
        }
        else if (value >= sbyte.MinValue && value < 0)
        {
            Span<byte> s = _buffer.GetSpan(2);
            s[0] = 0xD0;
            s[1] = (byte)value;
            _buffer.Advance(2);
            _bytesWritten += 2;
        }
        else if (value >= 0 && value <= ushort.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(3);
            s[0] = 0xCD;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)value);
            _buffer.Advance(3);
            _bytesWritten += 3;
        }
        else if (value >= short.MinValue && value <= short.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(3);
            s[0] = 0xD1;
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(1), (short)value);
            _buffer.Advance(3);
            _bytesWritten += 3;
        }
        else if (value >= 0 && value <= uint.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(5);
            s[0] = 0xCE;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)value);
            _buffer.Advance(5);
            _bytesWritten += 5;
        }
        else if (value >= int.MinValue && value <= int.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(5);
            s[0] = 0xD2;
            BinaryPrimitives.WriteInt32BigEndian(s.Slice(1), (int)value);
            _buffer.Advance(5);
            _bytesWritten += 5;
        }
        else
        {
            Span<byte> s = _buffer.GetSpan(9);
            s[0] = 0xD3;
            BinaryPrimitives.WriteInt64BigEndian(s.Slice(1), value);
            _buffer.Advance(9);
            _bytesWritten += 9;
        }
    }

    public void WriteFloat64(double value)
    {
        Span<byte> s = _buffer.GetSpan(9);
        s[0] = 0xCB;
        BinaryPrimitives.WriteInt64BigEndian(s.Slice(1), BitConverter.DoubleToInt64Bits(value));
        _buffer.Advance(9);
        _bytesWritten += 9;
    }

    public void WriteStartArray(int count)
    {
        if (count <= 15)
        {
            Span<byte> s = _buffer.GetSpan(1);
            s[0] = (byte)(0x90 | count);
            _buffer.Advance(1);
            _bytesWritten++;
        }
        else if (count <= ushort.MaxValue)
        {
            Span<byte> s = _buffer.GetSpan(3);
            s[0] = 0xDC;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)count);
            _buffer.Advance(3);
            _bytesWritten += 3;
        }
        else
        {
            Span<byte> s = _buffer.GetSpan(5);
            s[0] = 0xDD;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)count);
            _buffer.Advance(5);
            _bytesWritten += 5;
        }
    }

    public void WriteEndArray() { }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        int len = value.Length;
        if (len <= 255)
        {
            Span<byte> s = _buffer.GetSpan(2 + len);
            s[0] = 0xC4;
            s[1] = (byte)len;
            value.CopyTo(s.Slice(2));
            _buffer.Advance(2 + len);
            _bytesWritten += 2 + len;
        }
        else if (len <= 65535)
        {
            Span<byte> s = _buffer.GetSpan(3 + len);
            s[0] = 0xC5;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)len);
            value.CopyTo(s.Slice(3));
            _buffer.Advance(3 + len);
            _bytesWritten += 3 + len;
        }
        else
        {
            Span<byte> s = _buffer.GetSpan(5 + len);
            s[0] = 0xC6;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)len);
            value.CopyTo(s.Slice(5));
            _buffer.Advance(5 + len);
            _bytesWritten += 5 + len;
        }
    }

    public void WriteExtension(byte tag, ReadOnlySpan<byte> data)
    {
        int len = data.Length;
        if (len == 1)
        {
            var s = _buffer.GetSpan(3);
            s[0] = 0xD4;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _buffer.Advance(3);
            _bytesWritten += 3;
        }
        else if (len == 2)
        {
            var s = _buffer.GetSpan(4);
            s[0] = 0xD5;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _buffer.Advance(4);
            _bytesWritten += 4;
        }
        else if (len == 4)
        {
            var s = _buffer.GetSpan(6);
            s[0] = 0xD6;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _buffer.Advance(6);
            _bytesWritten += 6;
        }
        else if (len == 8)
        {
            var s = _buffer.GetSpan(10);
            s[0] = 0xD7;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _buffer.Advance(10);
            _bytesWritten += 10;
        }
        else if (len == 16)
        {
            var s = _buffer.GetSpan(18);
            s[0] = 0xD8;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _buffer.Advance(18);
            _bytesWritten += 18;
        }
        else if (len <= 255)
        {
            var s = _buffer.GetSpan(3 + len);
            s[0] = 0xC7;
            s[1] = (byte)len;
            s[2] = tag;
            data.CopyTo(s.Slice(3));
            _buffer.Advance(3 + len);
            _bytesWritten += 3 + len;
        }
        else if (len <= 65535)
        {
            var s = _buffer.GetSpan(4 + len);
            s[0] = 0xC8;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)len);
            s[3] = tag;
            data.CopyTo(s.Slice(4));
            _buffer.Advance(4 + len);
            _bytesWritten += 4 + len;
        }
        else
        {
            var s = _buffer.GetSpan(6 + len);
            s[0] = 0xC9;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)len);
            s[5] = tag;
            data.CopyTo(s.Slice(6));
            _buffer.Advance(6 + len);
            _bytesWritten += 6 + len;
        }
    }
}
