namespace PicoJetson;

public ref struct JsonWriter
{
    private readonly IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private readonly bool _indented;
    private readonly int _maxDepth;
    private readonly JsonOptions? _options;
    private int _depth;
    private ulong _needsComma;
    private bool _afterPropertyName;

    public long BytesWritten => _bytesWritten;

    /// <summary>Exposes the underlying buffer for converter support in nested helpers.</summary>
    public IBufferWriter<byte> Buffer => _buffer;

    /// <summary>Options supplied at construction, or null when default behavior is desired.</summary>
    public JsonOptions? Options => _options;

    public JsonWriter(
        IBufferWriter<byte> buffer,
        bool indented = false,
        int maxDepth = 63,
        JsonOptions? options = null
    )
    {
        if (maxDepth is < 0 or > 63)
            throw new ArgumentOutOfRangeException(
                nameof(maxDepth),
                maxDepth,
                "maxDepth must be between 0 and 63 (the comma-tracking bitmask supports at most 63 levels of nesting)."
            );
        _buffer = buffer;
        _bytesWritten = 0;
        _indented = indented;
        _maxDepth = maxDepth;
        _options = options;
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
        value.TryFormat(buf, out var w, default, CultureInfo.InvariantCulture);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteNumber(long value)
    {
        BeforeWriteValue();
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var w, default, CultureInfo.InvariantCulture);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteNumber(decimal value)
    {
        BeforeWriteValue();
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var w, default, CultureInfo.InvariantCulture);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteNumber(double value)
    {
        BeforeWriteValue();
        if (double.IsNaN(value))
        {
            if (
                _options?.NumberHandling
                == PicoJetson.JsonNumberHandling.AllowNamedFloatingPointLiterals
            )
            {
                WriteRaw("NaN"u8);
                return;
            }
            throw new ArgumentException(
                "NaN cannot be written as JSON. Consider handling NaN before serialization.",
                nameof(value)
            );
        }
        if (double.IsInfinity(value))
        {
            if (
                _options?.NumberHandling
                == PicoJetson.JsonNumberHandling.AllowNamedFloatingPointLiterals
            )
            {
                WriteRaw(double.IsPositiveInfinity(value) ? "Infinity"u8 : "-Infinity"u8);
                return;
            }
            throw new ArgumentException(
                "Infinity cannot be written as JSON. Consider handling Infinity before serialization.",
                nameof(value)
            );
        }
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var w, default, CultureInfo.InvariantCulture);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    /// <summary>
    /// Writes a string value from pre-encoded UTF-8 bytes. The bytes are
    /// escaped but NOT validated: invalid UTF-8 sequences are written
    /// verbatim and produce invalid JSON output. Callers must supply valid
    /// UTF-8 (e.g. the output of <see cref="Encoding.UTF8.GetBytes(string)"/>).
    /// </summary>
    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        BeforeWriteValue();
        WriteQuotedString(utf8Value);
    }

    /// <summary>
    /// Writes a pre-serialized JSON value (object, array, or primitive) verbatim
    /// into the current position. The caller MUST supply syntactically valid
    /// JSON — no validation or escaping is performed. Mirrors
    /// <c>Utf8JsonWriter.WriteRawValue</c>; intended for pre-validated payloads
    /// such as tool input schemas.
    /// </summary>
    public void WriteRawValue(ReadOnlySpan<byte> utf8Json)
    {
        BeforeWriteValue();
        WriteRaw(utf8Json);
    }

    /// <summary>String overload of <see cref="WriteRawValue(ReadOnlySpan{byte})"/>.</summary>
    public void WriteRawValue(ReadOnlySpan<char> json)
    {
        var bytes = Encoding.UTF8.GetBytes(json.ToArray());
        WriteRawValue(bytes);
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
            // Direct WriteQuotedString, NOT the public byte overload: the
            // public overload calls BeforeWriteValue again, which emits a
            // spurious ',' (the _needsComma bit was already set by the
            // BeforeWriteValue above) — long strings (>256 UTF-8 bytes)
            // produced ",\"value\"" and endpoints rejected the body with
            // "expected value".
            WriteQuotedString(bytes);
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
