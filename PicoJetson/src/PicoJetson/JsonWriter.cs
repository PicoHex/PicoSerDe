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

    /// <summary>Exposes the underlying buffer for converter support in nested helpers.</summary>
    internal IBufferWriter<byte> Buffer => _buffer;

    public JsonWriter(IBufferWriter<byte> buffer, bool indented = false, int maxDepth = 63)
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
        WriteQuotedString(utf8Value);
    }

    private void WriteQuotedString(scoped ReadOnlySpan<byte> utf8Value)
    {
        WriteByte((byte)'"');
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
            // Each escape needs at most 5 extra bytes (\\u0000).
            // Pre-allocate worst-case and truncate.
            var escaped = new byte[utf8Value.Length + escapeCount * 5];
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
                        if (b < 0x20)
                        {
                            // \uXXXX for control characters
                            escaped[di++] = (byte)'\\';
                            escaped[di++] = (byte)'u';
                            escaped[di++] = (byte)'0';
                            escaped[di++] = (byte)'0';
                            HexToBytes(b, escaped.AsSpan(di));
                            di += 2;
                        }
                        else
                        {
                            escaped[di++] = b;
                        }
                        break;
                }
            }
            _buffer.Write(escaped.AsSpan(0, di));
            _bytesWritten += di;
        }
        WriteByte((byte)'"');
    }

    private static void HexToBytes(byte b, Span<byte> dest)
    {
        dest[0] = ToHex((b >> 4) & 0xF);
        dest[1] = ToHex(b & 0xF);
    }

    private static byte ToHex(int n) => (byte)(n < 10 ? '0' + n : 'A' + n - 10);

    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        BeforeWriteValue();
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(value, buf);
            var slice = buf[..w];
            WriteByte((byte)'"');
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
                var escaped = new byte[w + escapeCount * 5];
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
                            if (b < 0x20)
                            {
                                escaped[di++] = (byte)'\\';
                                escaped[di++] = (byte)'u';
                                escaped[di++] = (byte)'0';
                                escaped[di++] = (byte)'0';
                                HexToBytes(b, escaped.AsSpan(di));
                                di += 2;
                            }
                            else
                                escaped[di++] = b;
                            break;
                    }
                }
                _buffer.Write(escaped.AsSpan(0, di));
                _bytesWritten += di;
            }
            WriteByte((byte)'"');
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToArray());
            WriteString(bytes);
        }
    }

    public void WritePropertyName(scoped ReadOnlySpan<byte> utf8Name)
    {
        if ((_needsComma & (1UL << _depth)) != 0)
            WriteByte((byte)',');
        _needsComma |= (1UL << _depth);
        if (_indented)
            WriteIndent();
        WriteQuotedString(utf8Name);
        WriteRaw(_indented ? ": "u8 : ":"u8);
        _afterPropertyName = true;
    }

    public void WritePropertyName(scoped ReadOnlySpan<char> name)
    {
        // Delegate to the byte overload which does full escaping
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(name, buf);
            WritePropertyName(buf[..w]);
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

    private void WriteRaw(scoped ReadOnlySpan<byte> utf8)
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
