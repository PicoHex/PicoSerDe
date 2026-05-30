namespace PicoToml;

public ref struct TomlWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private int _arrayDepth;
    private long _arrayCommaMask;

    public TomlWriter(IBufferWriter<byte> buffer)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _arrayDepth = 0;
        _arrayCommaMask = 0;
    }

    public long BytesWritten => _bytesWritten;

    public void WriteComment(string text)
    {
        WriteRaw("# "u8);
        WriteRaw(Encoding.UTF8.GetBytes(text));
        WriteNewLine();
    }

    public void WriteTable(string name)
    {
        WriteByte((byte)'[');
        WriteRaw(Encoding.UTF8.GetBytes(name));
        WriteByte((byte)']');
        WriteNewLine();
    }

    public void WriteKeyValue(string key, string value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = \""u8);
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(value.AsSpan(), buf);
            _buffer.Write(buf[..w]);
            _bytesWritten += w;
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            _buffer.Write(bytes);
            _bytesWritten += bytes.Length;
        }
        WriteByte((byte)'"');
        WriteNewLine();
    }

    public void WriteKeyValue(string key, scoped ReadOnlySpan<char> value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = \""u8);
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
        WriteByte((byte)'"');
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

    public void WriteKeyValue(string key, bool value)
    {
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        WriteRaw(value ? "true"u8 : "false"u8);
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

    public void WriteKeyValue(string key, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException(
                "NaN and Infinity cannot be written as TOML",
                nameof(value)
            );
        WriteKey(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = "u8);
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteBlankLine()
    {
        WriteNewLine();
    }

    // ── Array writing ──

    public void WriteStartArray(ReadOnlySpan<byte> key)
    {
        if (_arrayDepth > 0)
            ArrayBeforeValue();
        if (key.Length > 0)
        {
            WriteKey(key);
            WriteRaw(" = "u8);
        }
        WriteByte((byte)'[');
        _arrayDepth++;
    }

    public void WriteEndArray()
    {
        _arrayDepth--;
        _arrayCommaMask &= ~(1L << _arrayDepth);
        WriteByte((byte)']');
        if (_arrayDepth == 0)
            WriteNewLine();
    }

    public void WriteArrayValue(int value)
    {
        ArrayBeforeValue();
        Span<byte> buf = _buffer.GetSpan(16);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
    }

    public void WriteArrayValue(long value)
    {
        ArrayBeforeValue();
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
    }

    public void WriteArrayValue(string value)
    {
        ArrayBeforeValue();
        WriteByte((byte)'"');
        WriteRaw(Encoding.UTF8.GetBytes(value));
        WriteByte((byte)'"');
    }

    public void WriteArrayValue(bool value)
    {
        ArrayBeforeValue();
        WriteRaw(value ? "true"u8 : "false"u8);
    }

    public void WriteArrayValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException(
                "NaN and Infinity cannot be written as TOML",
                nameof(value)
            );
        ArrayBeforeValue();
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
    }

    private void ArrayBeforeValue()
    {
        if ((_arrayCommaMask & (1L << (_arrayDepth - 1))) != 0)
            WriteRaw(", "u8);
        _arrayCommaMask |= (1L << (_arrayDepth - 1));
    }

    // ── Private helpers ──

    private void WriteKey(ReadOnlySpan<byte> utf8)
    {
        if (NeedsKeyQuoting(utf8))
        {
            WriteByte((byte)'"');
            WriteRaw(utf8);
            WriteByte((byte)'"');
        }
        else
            WriteRaw(utf8);
    }

    private static bool NeedsKeyQuoting(ReadOnlySpan<byte> u)
    {
        if (u.IsEmpty)
            return true;
        for (int i = 0; i < u.Length; i++)
        {
            var b = u[i];
            if (
                b
                is (byte)' '
                    or (byte)'\t'
                    or (byte)'='
                    or (byte)'['
                    or (byte)']'
                    or (byte)'.'
                    or (byte)'#'
                    or (byte)'"'
            )
                return true;
        }
        return false;
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
        WriteByte((byte)'\n');
    }
}
