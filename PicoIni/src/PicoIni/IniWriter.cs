namespace PicoIni;

public ref struct IniWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;

    public IniWriter(IBufferWriter<byte> buffer)
    {
        _buffer = buffer;
        _bytesWritten = 0;
    }

    public long BytesWritten => _bytesWritten;

    public void WriteComment(string text) => WriteComment(";", text);

    public void WriteComment(string prefix, string text)
    {
        WriteRaw(Encoding.UTF8.GetBytes(prefix));
        WriteRaw(" "u8);
        WriteRaw(Encoding.UTF8.GetBytes(text));
        WriteNewLine();
    }

    public void WriteSection(string name)
    {
        WriteByte((byte)'[');
        WriteRaw(Encoding.UTF8.GetBytes(name));
        WriteByte((byte)']');
        WriteNewLine();
    }

    public void WriteSection(ReadOnlySpan<byte> utf8Name)
    {
        WriteByte((byte)'[');
        WriteRaw(utf8Name);
        WriteByte((byte)']');
        WriteNewLine();
    }

    public void WriteKeyValue(string key, string value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        WriteValue(Encoding.UTF8.GetBytes(value));
        WriteNewLine();
    }

    public void WriteKeyValue(string key, int value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = _buffer.GetSpan(16);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, long value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, bool value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        WriteRaw(value ? "true"u8 : "false"u8);
        WriteNewLine();
    }

    public void WriteKeyValue(string key, double value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, decimal value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = _buffer.GetSpan(64);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, ReadOnlySpan<byte> utf8Value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        WriteValue(utf8Value);
        WriteNewLine();
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, ReadOnlySpan<byte> utf8Value)
    {
        WriteKey(utf8Key);
        WriteRaw(" = "u8);
        WriteValue(utf8Value);
        WriteNewLine();
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, int value)
    {
        WriteKey(utf8Key);
        WriteRaw(" = "u8);
        Span<byte> buf = _buffer.GetSpan(16);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, long value)
    {
        WriteKey(utf8Key);
        WriteRaw(" = "u8);
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, bool value)
    {
        WriteKey(utf8Key);
        WriteRaw(" = "u8);
        WriteRaw(value ? "true"u8 : "false"u8);
        WriteNewLine();
    }

    public void WriteKeyValue(string key, DateTime value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = stackalloc byte[64];
        Utf8Formatter.TryFormat(value, buf, out int w, 'O');
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, Guid value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = stackalloc byte[64];
        Utf8Formatter.TryFormat(value, buf, out int w, 'D');
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, TimeSpan value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = stackalloc byte[64];
        Utf8Formatter.TryFormat(value, buf, out int w, 'c');
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, DateOnly value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<char> cbuf = stackalloc char[32];
        value.TryFormat(cbuf, out int cw, "O");
        Span<byte> buf = stackalloc byte[64];
        int bw = Encoding.UTF8.GetBytes(cbuf[..cw], buf);
        _buffer.Write(buf[..bw]);
        _bytesWritten += bw;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, TimeOnly value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<char> cbuf = stackalloc char[32];
        value.TryFormat(cbuf, out int cw, "O");
        Span<byte> buf = stackalloc byte[64];
        int bw = Encoding.UTF8.GetBytes(cbuf[..cw], buf);
        _buffer.Write(buf[..bw]);
        _bytesWritten += bw;
        WriteNewLine();
    }

    public void WriteKeyValue(string key, scoped ReadOnlySpan<char> value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(value, buf);
            _buffer.Write(buf[..w]);
            _bytesWritten += w;
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToArray());
            _buffer.Write(bytes);
            _bytesWritten += bytes.Length;
        }
        WriteNewLine();
    }

    public void WriteBlankLine()
    {
        WriteNewLine();
    }

    // ── Private helpers ──

    private void WriteKey(ReadOnlySpan<byte> utf8Key)
    {
        WriteRaw(utf8Key);
    }

    private void WriteValue(ReadOnlySpan<byte> utf8Value)
    {
        if (NeedsQuoting(utf8Value))
        {
            WriteQuotedValue(utf8Value);
        }
        else
        {
            WriteRaw(utf8Value);
        }
    }

    private static bool NeedsQuoting(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return false;
        for (int i = 0; i < utf8.Length; i++)
        {
            var b = utf8[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'=' or (byte)';' or (byte)'#' or (byte)'"')
                return true;
        }
        return false;
    }

    private void WriteQuotedValue(ReadOnlySpan<byte> utf8)
    {
        WriteByte((byte)'"');
        for (int i = 0; i < utf8.Length; i++)
        {
            if (utf8[i] == (byte)'"')
            {
                WriteByte((byte)'\\');
            }
            WriteByte(utf8[i]);
        }
        WriteByte((byte)'"');
    }

    private void WriteRaw(ReadOnlySpan<byte> utf8)
    {
        _buffer.Write(utf8);
        _bytesWritten += utf8.Length;
    }

    private void WriteByte(byte value)
    {
        var s = _buffer.GetSpan(1);
        s[0] = value;
        _buffer.Advance(1);
        _bytesWritten++;
    }

    private void WriteNewLine()
    {
        var s = _buffer.GetSpan(2);
        s[0] = (byte)'\r';
        s[1] = (byte)'\n';
        _buffer.Advance(2);
        _bytesWritten += 2;
    }
}
