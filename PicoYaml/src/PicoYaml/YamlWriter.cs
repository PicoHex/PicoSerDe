namespace PicoYaml;

public ref struct YamlWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private int _depth;
    private bool _afterKey;

    public long BytesWritten => _bytesWritten;

    public YamlWriter(IBufferWriter<byte> buffer)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _depth = 0;
        _afterKey = false;
    }

    public void WriteComment(string text)
    {
        for (int i = 0; i < _depth; i++)
            WriteRaw("  "u8);
        WriteRaw("# "u8);
        WriteRaw(Encoding.UTF8.GetBytes(text));
        WriteNewLine();
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        for (int i = 0; i < _depth; i++)
            WriteRaw("  "u8);
        WriteRaw(utf8Name);
        WriteRaw(":"u8);
        _afterKey = true;
    }

    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        if (NeedsQuoting(utf8Value))
        {
            WriteByte((byte)'"');
            WriteEscaped(utf8Value);
            WriteByte((byte)'"');
        }
        else
            WriteRaw(utf8Value);
        WriteNewLine();
    }

    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(value, buf);
            var slice = buf[..w];
            if (NeedsQuoting(slice))
            {
                _buffer.Write("\""u8);
                _bytesWritten++;
                _buffer.Write(slice);
                _bytesWritten += w;
                _buffer.Write("\""u8);
                _bytesWritten++;
            }
            else
            {
                _buffer.Write(slice);
                _bytesWritten += w;
            }
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToArray());
            if (NeedsQuoting(bytes))
            {
                _buffer.Write("\""u8);
                _bytesWritten++;
                WriteEscaped(bytes);
                _buffer.Write("\""u8);
                _bytesWritten++;
            }
            else
            {
                _buffer.Write(bytes);
                _bytesWritten += bytes.Length;
            }
        }
        WriteNewLine();
    }

    public void WriteNumber(int value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        Span<byte> buf = _buffer.GetSpan(16);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteBoolean(bool value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        WriteRaw(value ? "true"u8 : "false"u8);
        WriteNewLine();
    }

    public void WriteInt64(long value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException(
                "NaN and Infinity cannot be written as YAML",
                nameof(value)
            );
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteStartMapping()
    {
        if (_afterKey)
        {
            WriteNewLine();
            _afterKey = false;
        }
        _depth++;
    }

    public void WriteEndMapping()
    {
        _depth--;
    }

    public void WriteSequenceItem(ReadOnlySpan<byte> utf8Value)
    {
        if (_afterKey)
        {
            WriteNewLine();
            _afterKey = false;
        }
        for (var i = 0; i < _depth; i++)
            WriteRaw("  "u8);
        WriteRaw("- "u8);
        if (NeedsQuoting(utf8Value))
        {
            WriteByte((byte)'"');
            WriteRaw(utf8Value);
            WriteByte((byte)'"');
        }
        else
            WriteRaw(utf8Value);
        WriteNewLine();
    }

    private void WriteRaw(ReadOnlySpan<byte> utf8)
    {
        _buffer.Write(utf8);
        _bytesWritten += utf8.Length;
    }

    private void WriteByte(byte v)
    {
        var s = _buffer.GetSpan(1);
        s[0] = v;
        _buffer.Advance(1);
        _bytesWritten++;
    }

    private void WriteNewLine()
    {
        WriteByte((byte)'\n');
    }

    private void WriteEscaped(ReadOnlySpan<byte> u)
    {
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == (byte)'"')
                WriteRaw("\\\""u8);
            else if (u[i] == (byte)'\\')
                WriteRaw("\\\\"u8);
            else if (u[i] == (byte)'\n')
                WriteRaw("\\n"u8);
            else
                WriteByte(u[i]);
        }
    }

    private static bool NeedsQuoting(ReadOnlySpan<byte> u)
    {
        if (u.IsEmpty)
            return true;
        for (int i = 0; i < u.Length; i++)
        {
            var b = u[i];
            if (
                b == (byte)':'
                || b == (byte)'#'
                || b == (byte)'{'
                || b == (byte)'}'
                || b == (byte)'['
                || b == (byte)']'
                || b == (byte)','
                || b == (byte)'&'
                || b == (byte)'*'
                || b == (byte)'!'
                || b == (byte)'|'
                || b == (byte)'>'
                || b == (byte)'"'
                || b == (byte)'\''
                || b == (byte)'\n'
                || b == (byte)'\t'
            )
                return true;
        }
        return false;
    }
}
