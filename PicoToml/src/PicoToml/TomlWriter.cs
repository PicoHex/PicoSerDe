namespace PicoToml;

public ref struct TomlWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private readonly int _maxDepth;
    private int _arrayDepth;
    private long _arrayCommaMask;
    private int _inlineDepth;
    private long _inlineCommaMask;

    public TomlWriter(IBufferWriter<byte> buffer, int maxDepth = 256)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _maxDepth = maxDepth;
        _arrayDepth = 0;
        _arrayCommaMask = 0;
        _inlineDepth = 0;
        _inlineCommaMask = 0;
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
        WriteTable(Encoding.UTF8.GetBytes(name));
    }

    public void WriteTable(ReadOnlySpan<byte> utf8Name)
    {
        WriteByte((byte)'[');
        WriteRaw(utf8Name);
        WriteByte((byte)']');
        WriteNewLine();
    }

    public void WriteKeyValue(string key, string value)
    {
        WriteKeyValue(Encoding.UTF8.GetBytes(key), value);
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, string value)
    {
        WriteKey(utf8Key);
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
        WriteKeyValue(Encoding.UTF8.GetBytes(key), value);
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, scoped ReadOnlySpan<char> value)
    {
        WriteKey(utf8Key);
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
        WriteKeyValue(Encoding.UTF8.GetBytes(key), value);
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

    public void WriteKeyValue(string key, bool value)
    {
        WriteKeyValue(Encoding.UTF8.GetBytes(key), value);
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, bool value)
    {
        WriteKey(utf8Key);
        WriteRaw(" = "u8);
        WriteRaw(value ? "true"u8 : "false"u8);
        WriteNewLine();
    }

    public void WriteKeyValue(string key, long value)
    {
        WriteKeyValue(Encoding.UTF8.GetBytes(key), value);
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

    public void WriteKeyValue(string key, double value)
    {
        WriteKeyValue(Encoding.UTF8.GetBytes(key), value);
    }

    public void WriteKeyValue(ReadOnlySpan<byte> utf8Key, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException(
                "NaN and Infinity cannot be written as TOML",
                nameof(value)
            );
        WriteKey(utf8Key);
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
        if (_arrayDepth >= _maxDepth)
            throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
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

    // ── Inline table writing ──

    public void WriteStartInlineTable(string key)
    {
        if (_inlineDepth >= _maxDepth)
            throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
        WriteRaw(Encoding.UTF8.GetBytes(key));
        WriteRaw(" = { "u8);
        _inlineDepth++;
    }

    public void WriteEndInlineTable()
    {
        _inlineDepth--;
        _inlineCommaMask &= ~(1L << _inlineDepth);
        WriteRaw(" }"u8);
        if (_inlineDepth == 0)
            WriteNewLine();
    }

    private void InlineBeforeValue()
    {
        if ((_inlineCommaMask & (1L << (_inlineDepth - 1))) != 0)
            WriteRaw(", "u8);
        _inlineCommaMask |= (1L << (_inlineDepth - 1));
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
        if (_inlineDepth > 0)
            InlineBeforeValue();
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
        if (_inlineDepth > 0)
            return; // no newlines inside inline tables
        WriteByte((byte)'\n');
    }
}
