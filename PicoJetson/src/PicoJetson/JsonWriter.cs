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

    /// <summary>
    /// Writes one number token — separator, digits and cursor bookkeeping — as a single
    /// staged write. Every <see cref="WriteNumber(int)"/> overload (and the finite arm of
    /// <see cref="WriteNumber(double)"/> / <see cref="WriteNumber(float)"/>) lands here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token used to be four calls: <see cref="BeforeWriteValue"/> (which wrote the
    /// separator through <see cref="WriteByte"/>), <see cref="Ensure"/>, the format call and
    /// <see cref="CommitBare"/>. That is the shape of a <em>document</em>-level token, but a
    /// number is the most frequent token inside a collection: measured
    /// (`.superpowers/sdd/json-elemwrite/report.md`), a 100-element <c>List&lt;int&gt;</c>
    /// document spent 80% of its time in the element loop, and 40% of the loop in per-token
    /// bookkeeping on JIT — 54% on NativeAOT, where those calls are not inlined and each one
    /// re-reads the writer's fields from memory.
    /// </para>
    /// <para>
    /// Here the separator, the digits and the cursor update share one <see cref="Ensure"/>,
    /// so one token touches the span once and the buffer never. The formatting call is
    /// unchanged — same <c>TryFormat</c>, same empty format, same invariant provider — so the
    /// bytes are the same bytes; a what-if arm that wrote the digits straight into the buffer
    /// (no writer at all) measured the headroom: what remains above it is 1.4 ns/token on JIT
    /// and 3.7 ns/token on NativeAOT, against 3.9 and 11.7 before.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteFormattable<T>(T value, int maxBytes)
        where T : IUtf8SpanFormattable
    {
        var comma = false;
        if (_afterPropertyName)
            _afterPropertyName = false;
        else
        {
            comma = (_needsComma & (1UL << _depth)) != 0;
            _needsComma |= 1UL << _depth;
        }
        // One slot for the separator plus the value's worst case, in one reservation.
        Ensure(maxBytes + 1);
        var start = _spanPos;
        var pos = start;
        if (comma)
            _span[pos++] = (byte)',';
        value.TryFormat(_span[pos..], out var w, default, CultureInfo.InvariantCulture);
        _spanPos = pos + w;
        _bytesWritten += _spanPos - start;
        CommitBare();
    }

    public void WriteNumber(int value) => WriteFormattable(value, 16);

    public void WriteNumber(short value) => WriteFormattable(value, 8);

    public void WriteNumber(ushort value) => WriteFormattable(value, 8);

    public void WriteNumber(sbyte value) => WriteFormattable(value, 8);

    public void WriteNumber(byte value) => WriteFormattable(value, 8);

    public void WriteNumber(uint value) => WriteFormattable(value, 16);

    public void WriteNumber(ulong value) => WriteFormattable(value, 32);

    public void WriteNumber(long value) => WriteFormattable(value, 32);

    public void WriteNumber(decimal value) => WriteFormattable(value, 32);

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
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            BeforeWriteValue();
        }
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
        WriteFormattable(value, 32);
    }

    public void WriteNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            BeforeWriteValue();
        }
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
        WriteFormattable(value, 32);
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
        // The escape work runs BEFORE the opening quote reaches the write cursor: the
        // scan is a pure read of the payload, and keeping the writer's span cursor live
        // across it costs ~0.4 ns/byte in JIT codegen (see
        // .superpowers/sdd/perf-json-strscan/report.md). The output is unchanged — the
        // quote is still written ahead of the payload, it is just not held open while
        // deciding what to escape.
        //
        // The common case is a payload that needs no escaping at all, and above one vector
        // that question is answered by one vectorized pass instead of a per-byte loop that
        // has to count to prove the same thing. Short payloads keep the scalar loop below
        // verbatim: below 16 bytes the vectorized scan only runs its own scalar tail, and the
        // call would be pure overhead (measured in .superpowers/sdd/perf-json-strscan/
        // report.md). The escaping path is byte-for-byte the scalar implementation it always
        // was.
        if (utf8Value.Length >= Vector128Size && !NeedsEscapingVectorized(utf8Value))
        {
            WriteByte((byte)'"');
            WriteRaw(utf8Value);
            WriteByte((byte)'"');
            return;
        }

        int escapeCount = 0;
        foreach (var b in utf8Value)
        {
            if (b is (byte)'"' or (byte)'\\' or < 0x20)
                escapeCount++;
        }

        WriteByte((byte)'"');
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

    /// <summary>
    /// True when a payload of at least one vector contains a byte JSON must escape:
    /// <c>"</c>, <c>\</c>, or a control byte below <c>0x20</c>. Those are exactly the three
    /// conditions the escaping loop responds to and exactly the three the reader stops on, so
    /// both sides of the wire agree on what "needs escaping" means.
    /// </summary>
    /// <remarks>
    /// The scan is <see cref="JsonReader.IndexOfQuoteEscapeOrControl"/>, the vectorized
    /// 64/32/16-byte helper added for the read path (P0-3): one SIMD implementation, one set
    /// of edges. Callers gate it on <see cref="Vector128Size"/> because below that width it
    /// would only run its own scalar tail through a call.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsEscapingVectorized(scoped ReadOnlySpan<byte> value) =>
        JsonReader.IndexOfQuoteEscapeOrControl(value) >= 0;

    /// <summary>One <see cref="System.Runtime.Intrinsics.Vector128{T}"/> of bytes.</summary>
    private const int Vector128Size = 16;

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
        // Long payloads (>256 bytes of worst case) encode straight into the writer's own span;
        // see TryWriteLongString for why that is the interesting case and what it costs.
        if (max > 256 && TryWriteLongString(value, max))
            return;
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(value, buf);
            var slice = buf[..w];
            // Same fast path as the byte overload, inlined here rather than shared: a short
            // payload must not pay a call to be told it needs no escaping, and for a payload
            // of a vector or more the shared scan answers it once.
            if (w >= Vector128Size && !NeedsEscapingVectorized(slice))
            {
                WriteByte((byte)'"');
                WriteRaw(slice);
                WriteByte((byte)'"');
            }
            else
            {
                int escapeCount = 0;
                foreach (var b in slice)
                    if (b is (byte)'"' or (byte)'\\' or < 0x20)
                        escapeCount++;
                WriteByte((byte)'"');
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

    /// <summary>
    /// Long-payload fast path: encodes the UTF-8 bytes straight into the writer's own span —
    /// quotes included — so a payload whose worst case does not fit the 256-byte stack buffer
    /// costs neither the intermediate <c>char[]</c> of <c>value.ToArray()</c> nor the
    /// <c>byte[]</c> a full-length encode would need. This is the path a 1 KB member string
    /// takes, and it allocates nothing at all when the payload needs no escaping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The escape question is still answered by <see cref="NeedsEscapingVectorized"/> over the
    /// <em>encoded UTF-8 bytes</em>, exactly as the byte overload answers it: the decision, its
    /// input and its operator are unchanged, only the buffer the bytes live in is. When the
    /// answer is "escapes needed" this method returns <c>false</c> <em>without moving the write
    /// cursor</em> — the bytes staged here sit past <see cref="_spanPos"/>, are never committed,
    /// and are overwritten by the buffered path the caller falls back to. The escaping path
    /// therefore keeps its previous implementation and its previous output.
    /// </para>
    /// <para>
    /// The span is requested at the worst case (<c>GetMaxByteCount(length) + 2</c>), which is
    /// what makes encoding into it safe before the byte count is known — the same reservation
    /// MemoryPack's <c>GetSpanReference((len + 1) * 3 + 8)</c> makes. One <see cref="Ensure"/>,
    /// one <see cref="CommitBare"/>, no copy through a heap array.
    /// </para>
    /// </remarks>
    private bool TryWriteLongString(scoped ReadOnlySpan<char> value, int maxBytes)
    {
        // Opening quote + worst-case payload + closing quote, in the one staged region.
        Ensure(maxBytes + 2);
        var start = _spanPos;
        _span[start] = (byte)'"';
        var w = Encoding.UTF8.GetBytes(value, _span[(start + 1)..]);
        if (w >= Vector128Size && !NeedsEscapingVectorized(_span.Slice(start + 1, w)))
        {
            _span[start + 1 + w] = (byte)'"';
            _spanPos = start + w + 2;
            _bytesWritten += w + 2;
            CommitBare();
            return true;
        }
        // Escapes needed (or a payload below one vector, which this path cannot reach): leave the
        // cursor where it was, so what was staged above is never committed.
        return false;
    }

    /// <summary>
    /// Writes <c>"name":</c> — separator, quotes and colon included — as one staged write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name used to go out as four separate writes (<c>"</c>, the name, <c>"</c>, <c>:</c>),
    /// each paying its own <see cref="Ensure"/> bound check and its own <see cref="CommitBare"/>:
    /// five staged writes and a call each for a token that is a handful of bytes. A property
    /// name is the most frequent non-value token in a document (30 of them in `LargeFlat 50`),
    /// so a fixed cost per call dominates the payload. Here the whole token is staged once
    /// behind a single <see cref="Ensure"/> and committed once.
    /// </para>
    /// <para>
    /// The escape question is asked <em>before</em> staging, because an escapable name needs a
    /// different (longer) byte sequence and must not be half-written first: a name that needs
    /// escaping — <c>"</c>, <c>\</c>, a control character, all of which a naming policy or a
    /// dictionary key can produce at run time — takes the previous path verbatim, and so does an
    /// indented writer, whose colon carries a trailing space. Names arrive from the generated
    /// serializers as compile-time UTF-8 constants, so the scan is over a handful of bytes;
    /// from one vector up it is the same vectorized scan the string path uses, so a long name
    /// (a policy can produce one) never falls back to a byte-at-a-time loop.
    /// </para>
    /// </remarks>
    public void WritePropertyName(scoped ReadOnlySpan<byte> utf8Name)
    {
        var comma = (_needsComma & (1UL << _depth)) != 0;
        _needsComma |= (1UL << _depth);

        var n = utf8Name.Length;
        var escapable =
            n >= Vector128Size ? NeedsEscapingVectorized(utf8Name) : NameNeedsEscaping(utf8Name);
        if (_indented || escapable)
        {
            if (comma)
                WriteByte((byte)',');
            if (_indented)
                WriteIndent();
            WriteQuotedString(utf8Name);
            WriteRaw(_indented ? ": "u8 : ":"u8);
            _afterPropertyName = true;
            return;
        }

        Ensure(n + 3);
        var start = _spanPos;
        var pos = start;
        if (comma)
            _span[pos++] = (byte)',';
        _span[pos++] = (byte)'"';
        utf8Name.CopyTo(_span[pos..]);
        pos += n;
        _span[pos++] = (byte)'"';
        _span[pos++] = (byte)':';
        _spanPos = pos;
        _bytesWritten += pos - start;
        _afterPropertyName = true;
        CommitBare();
    }

    /// <summary>
    /// True when a name shorter than one vector contains a byte JSON must escape: <c>"</c>,
    /// <c>\</c> or a control byte. <see cref="NeedsEscapingVectorized"/> is its vectorized
    /// twin; below one vector that scan would only run its own scalar tail through a call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NameNeedsEscaping(scoped ReadOnlySpan<byte> name)
    {
        foreach (var b in name)
            if (b is (byte)'"' or (byte)'\\' or < 0x20)
                return true;
        return false;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
