namespace PicoJetson;

public ref struct JsonWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private readonly bool _indented;
    private readonly int _maxDepth;
    private int _depth;
    private ulong _needsComma;
    private bool _afterPropertyName;

    public long BytesWritten => _bytesWritten;

    public JsonWriter(IBufferWriter<byte> buffer, bool indented = false, int maxDepth = 64)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _indented = indented;
        _maxDepth = maxDepth;
        _depth = 0;
        _needsComma = 0UL;
        _afterPropertyName = false;
    }

    public void WriteNull()
    {
        BeforeWriteValue();
        WriteRaw("null"u8);
    }

    public void WriteBoolean(bool value)
    {
        BeforeWriteValue();
        WriteRaw(value ? "true"u8 : "false"u8);
    }

    public void WriteNumber(int value)
    {
        BeforeWriteValue();
        Span<byte> buf = stackalloc byte[16];
        value.TryFormat(buf, out var w);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteNumber(long value)
    {
        BeforeWriteValue();
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var w);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteNumber(double value)
    {
        BeforeWriteValue();
        if (double.IsNaN(value))
            throw new ArgumentException(
                "NaN cannot be written as JSON. Consider handling NaN before serialization.",
                nameof(value)
            );
        if (double.IsInfinity(value))
            throw new ArgumentException(
                "Infinity cannot be written as JSON. Consider handling Infinity before serialization.",
                nameof(value)
            );
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var w);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        BeforeWriteValue();
        WriteByte((byte)'"');
        // Scan for escape chars
        int escapeCount = 0;
        foreach (var b in utf8Value)
        {
            if (b is (byte)'"' or (byte)'\\' or < 0x20)
                escapeCount++;
        }

        if (escapeCount == 0)
        {
            WriteRaw(utf8Value);
        }
        else
        {
            var escaped = new byte[utf8Value.Length + escapeCount];
            int di = 0;
            foreach (var b in utf8Value)
            {
                switch (b)
                {
                    case (byte)'"':
                        escaped[di++] = (byte)'\\';
                        escaped[di++] = (byte)'"';
                        break;
                    case (byte)'\\':
                        escaped[di++] = (byte)'\\';
                        escaped[di++] = (byte)'\\';
                        break;
                    case (byte)'\n':
                        escaped[di++] = (byte)'\\';
                        escaped[di++] = (byte)'n';
                        break;
                    case (byte)'\r':
                        escaped[di++] = (byte)'\\';
                        escaped[di++] = (byte)'r';
                        break;
                    case (byte)'\t':
                        escaped[di++] = (byte)'\\';
                        escaped[di++] = (byte)'t';
                        break;
                    default:
                        escaped[di++] = b;
                        break;
                }
            }
            WriteRaw(escaped);
        }
        WriteByte((byte)'"');
    }

    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        BeforeWriteValue();
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(value, buf);
            WriteByte((byte)'"');
            var slice = buf[..w];
            int escapeCount = 0;
            foreach (var b in slice)
                if (b is (byte)'"' or (byte)'\\' or < 0x20)
                    escapeCount++;
            if (escapeCount == 0)
            {
                _buffer.Write(slice);
                _bytesWritten += w;
            }
            else
            {
                var escaped = new byte[w + escapeCount];
                int di = 0;
                foreach (var b in slice)
                {
                    switch (b)
                    {
                        case (byte)'"':
                            escaped[di++] = (byte)'\\';
                            escaped[di++] = (byte)'"';
                            break;
                        case (byte)'\\':
                            escaped[di++] = (byte)'\\';
                            escaped[di++] = (byte)'\\';
                            break;
                        case (byte)'\n':
                            escaped[di++] = (byte)'\\';
                            escaped[di++] = (byte)'n';
                            break;
                        case (byte)'\r':
                            escaped[di++] = (byte)'\\';
                            escaped[di++] = (byte)'r';
                            break;
                        case (byte)'\t':
                            escaped[di++] = (byte)'\\';
                            escaped[di++] = (byte)'t';
                            break;
                        default:
                            escaped[di++] = b;
                            break;
                    }
                }
                _buffer.Write(escaped);
                _bytesWritten += escaped.Length;
            }
            WriteByte((byte)'"');
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToArray());
            WriteString(bytes);
        }
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        if ((_needsComma & (1UL << _depth)) != 0)
            WriteByte((byte)',');
        _needsComma |= (1UL << _depth);
        if (_indented)
            WriteIndent();
        WriteByte((byte)'"');
        WriteRaw(utf8Name);
        WriteByte((byte)'"');
        WriteRaw(_indented ? ": "u8 : ":"u8);
        _afterPropertyName = true;
    }

    public void WritePropertyName(scoped ReadOnlySpan<char> name)
    {
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(name, buf);
            if ((_needsComma & (1UL << _depth)) != 0)
                WriteByte((byte)',');
            _needsComma |= (1UL << _depth);
            if (_indented)
                WriteIndent();
            WriteByte((byte)'"');
            _buffer.Write(buf[..w]);
            _bytesWritten += w;
            WriteByte((byte)'"');
            _buffer.Write(_indented ? ": "u8 : ":"u8);
            _bytesWritten += _indented ? 2 : 1;
            _afterPropertyName = true;
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(name.ToArray());
            WritePropertyName(bytes);
        }
    }

    public void WriteStartObject()
    {
        if (_depth >= _maxDepth)
            throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
        BeforeWriteValue();
        WriteByte((byte)'{');
        _depth++;
    }

    public void WriteEndObject()
    {
        _needsComma &= ~(1UL << _depth);
        _depth--;
        if (_indented)
            WriteIndent();
        WriteByte((byte)'}');
    }

    public void WriteStartArray()
    {
        if (_depth >= _maxDepth)
            throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
        BeforeWriteValue();
        WriteByte((byte)'[');
        _depth++;
    }

    public void WriteEndArray()
    {
        _needsComma &= ~(1UL << _depth);
        _depth--;
        if (_indented)
            WriteIndent();
        WriteByte((byte)']');
    }

    private void BeforeWriteValue()
    {
        if (_afterPropertyName)
        {
            _afterPropertyName = false;
            return;
        }
        if ((_needsComma & (1UL << _depth)) != 0)
            WriteByte((byte)',');
        _needsComma |= (1UL << _depth);
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

    private void WriteIndent()
    {
        WriteByte((byte)'\n');
        for (var i = 0; i < _depth; i++)
            WriteRaw("  "u8);
    }
}
