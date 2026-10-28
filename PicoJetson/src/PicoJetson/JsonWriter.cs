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

    /// <summary>Bytes handed out by <see cref="_buffer"/> and not yet committed.</summary>
    private Span<byte> _span;

    /// <summary>Write cursor inside <see cref="_span"/>.</summary>
    private int _spanPos;

    /// <summary>Minimum span the writer asks for, so short tokens amortise one fetch.</summary>
    private const int MinSpanSize = 256;

    public long BytesWritten => _bytesWritten;

    /// <summary>Exposes the underlying buffer for converter support in nested helpers.</summary>
    /// <remarks>
    /// Reading this commits the pending span first: a converter or a custom serializer
    /// writes into the same buffer and would otherwise land <em>ahead</em> of the bytes
    /// still sitting in the cache. The span is dropped, so the next write fetches a
    /// fresh one after the hand-off.
    /// </remarks>
    public IBufferWriter<byte> Buffer
    {
        get
        {
            Flush();
            return _buffer;
        }
    }

    /// <summary>
    /// Declares that a complete value was written straight into <see cref="Buffer"/>
    /// — the route taken by <see cref="IJsonConverter{T}"/> implementations and by
    /// <c>JsonSerializer.SerializeCustom&lt;T&gt;</c>, both of which only receive the
    /// raw buffer and therefore never pass through the writer's value bookkeeping.
    /// Clears the pending property-name flag so the enclosing
    /// <see cref="WriteEndObject"/> / <see cref="WriteEndArray"/> accepts the value
    /// and so the next value still gets its separator. Idempotent: when no property
    /// name is pending (for example a raw array element) nothing changes.
    /// </summary>
    public void NotifyRawValueWritten()
    {
        if (_afterPropertyName)
            _afterPropertyName = false;
    }

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
        _span = default;
        _spanPos = 0;
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
        Ensure(16);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(short value)
    {
        BeforeWriteValue();
        Ensure(8);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(ushort value)
    {
        BeforeWriteValue();
        Ensure(8);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(sbyte value)
    {
        BeforeWriteValue();
        Ensure(8);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(byte value)
    {
        BeforeWriteValue();
        Ensure(8);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(uint value)
    {
        BeforeWriteValue();
        Ensure(16);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(ulong value)
    {
        BeforeWriteValue();
        Ensure(32);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(long value)
    {
        BeforeWriteValue();
        Ensure(32);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    public void WriteNumber(decimal value)
    {
        BeforeWriteValue();
        Ensure(32);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    /// <summary>
    /// Writes a <see cref="float"/> as its shortest round-trip text. Formatting a
    /// <c>float</c> through <see cref="WriteNumber(double)"/> first widens it to
    /// <c>double</c>, which exposes the binary expansion (e.g. <c>0.1f</c> becomes
    /// <c>0.10000000149011612</c>); the <c>float</c> overload formats the value at its
    /// own precision instead. Non-finite values follow the same policy as
    /// <see cref="WriteNumber(double)"/>.
    /// </summary>
    public void WriteNumber(float value)
    {
        BeforeWriteValue();
        if (float.IsNaN(value))
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
        if (float.IsInfinity(value))
        {
            if (
                _options?.NumberHandling
                == PicoJetson.JsonNumberHandling.AllowNamedFloatingPointLiterals
            )
            {
                WriteRaw(float.IsPositiveInfinity(value) ? "Infinity"u8 : "-Infinity"u8);
                return;
            }
            throw new ArgumentException(
                "Infinity cannot be written as JSON. Consider handling Infinity before serialization.",
                nameof(value)
            );
        }
        Ensure(32);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
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
        Ensure(32);
        value.TryFormat(_span[_spanPos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos += w;
        _bytesWritten += w;
        CommitBare();
    }

    /// <summary>
    /// Writes a string value from pre-encoded UTF-8 bytes. The bytes are
    /// escaped but NOT validated: invalid UTF-8 sequences are written
    /// verbatim and produce invalid JSON output. Callers must supply valid
    /// UTF-8 (e.g. the output of <see cref="Encoding.UTF8.GetBytes(string)"/>).
    /// </summary>
    public void WriteString(scoped ReadOnlySpan<byte> utf8Value)
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
    public void WriteRawValue(scoped ReadOnlySpan<byte> utf8Json)
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
            WriteRaw(escaped.AsSpan(0, di));
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
                WriteRaw(slice);
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
                WriteRaw(escaped.AsSpan(0, di));
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
        ThrowIfPropertyNamePending("object");
        _needsComma &= ~(1UL << _depth);
        _depth--;
        if (_indented)
            WriteIndent();
        WriteByte((byte)'}');
        if (_depth == 0)
            Flush();
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
        ThrowIfPropertyNamePending("array");
        _needsComma &= ~(1UL << _depth);
        _depth--;
        if (_indented)
            WriteIndent();
        WriteByte((byte)']');
        if (_depth == 0)
            Flush();
    }

    /// <summary>
    /// Rejects closing a container while a property name is still waiting for its
    /// value: emitting the brace would produce illegal JSON such as
    /// <c>{"a":}</c> instead of failing at the write site. Called before any
    /// state mutation, so a failed close leaves neither a half-written brace nor
    /// a changed depth behind.
    /// </summary>
    private readonly void ThrowIfPropertyNamePending(string containerName)
    {
        if (_afterPropertyName)
            throw new FormatException(
                $"Cannot close {containerName}: property name was written with no value."
            );
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

    /// <summary>
    /// Makes room for <paramref name="n"/> more bytes in <see cref="_span"/>. Together with
    /// <see cref="Flush"/> this is the only place that touches the underlying buffer while a
    /// value is being written, so a run of tokens costs two interface calls per <em>chunk</em>
    /// instead of two per token.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int n)
    {
        if (_span.Length - _spanPos < n)
        {
            Flush();
            _span = _buffer.GetSpan(n < MinSpanSize ? MinSpanSize : n);
        }
    }

    /// <summary>Commits the bytes sitting in <see cref="_span"/> and drops the span.</summary>
    /// <remarks>
    /// Dropping it is not optional: the buffer owns those bytes now, so keeping the span
    /// would let the next write land in memory that has already been committed — a double
    /// commit, i.e. duplicated bytes. An empty span never fits <see cref="Ensure"/>, so the
    /// next write refetches; that is also why <c>Ensure(n)</c> with <c>n &gt;= 1</c> cannot
    /// spin.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Flush()
    {
        if (_spanPos > 0)
        {
            _buffer.Advance(_spanPos);
            _spanPos = 0;
        }
        _span = default;
    }

    /// <summary>Commits immediately when no container is open.</summary>
    /// <remarks>
    /// Caching only pays off inside a container, and a container is the only thing that is
    /// guaranteed to close — <see cref="WriteEndObject"/> / <see cref="WriteEndArray"/>
    /// commit the tail when the outermost one closes. A bare scalar or string (a converter
    /// writing <c>"2026-01-01"</c>, a <c>JsonWriter</c> driven by hand, a top-level
    /// <c>null</c>) never closes anything, so its bytes must reach the buffer before the
    /// caller can read it. Depth 0 therefore behaves exactly like the writer without a
    /// cache: one commit per token, nothing left pending.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitBare()
    {
        if (_depth == 0)
            Flush();
    }

    private void WriteRaw(scoped ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > _span.Length - _spanPos)
        {
            Flush();
            if (utf8.Length >= MinSpanSize)
            {
                // Too long to be worth copying into a cache slot we would hand straight
                // back: write it through the buffer in one call.
                _buffer.Write(utf8);
                _bytesWritten += utf8.Length;
                CommitBare();
                return;
            }
            _span = _buffer.GetSpan(MinSpanSize);
        }
        utf8.CopyTo(_span[_spanPos..]);
        _spanPos += utf8.Length;
        _bytesWritten += utf8.Length;
        CommitBare();
    }

    private void WriteByte(byte value)
    {
        Ensure(1);
        _span[_spanPos++] = value;
        _bytesWritten++;
        CommitBare();
    }

    private void WriteIndent()
    {
        WriteByte((byte)'\n');
        for (var i = 0; i < _depth; i++)
            WriteRaw("  "u8);
    }
}
