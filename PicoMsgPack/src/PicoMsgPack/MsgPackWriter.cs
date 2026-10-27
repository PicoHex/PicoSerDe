namespace PicoMsgPack;

/// <summary>
/// MessagePack encoder writing straight into a span it owns. The underlying
/// <see cref="IBufferWriter{T}"/> is touched only in <see cref="Ensure"/> (when the
/// current span runs out) and in <see cref="Flush"/>, so a run of writes costs two
/// interface calls per <em>chunk</em> instead of two per token.
/// <para>
/// Consequence: bytes can still be sitting in the local span when the caller stops
/// writing. Anything that reads the buffer must first commit the tail — reading
/// <see cref="Buffer"/> does it, and so do <see cref="WriteEndObject"/> /
/// <see cref="WriteEndArray"/> when the outermost container closes. Generated
/// serializers hand off with <c>_ = mw.Buffer;</c> (they compile into the consumer
/// assembly, so they can only touch the public surface).
/// </para>
/// <para>
/// <b>Bare values (no enclosing container).</b> Writing a scalar or a string without
/// opening or closing a map/array leaves no implicit flush: nothing will ever close the
/// outermost container, so the bytes stay uncommitted. Before reading the underlying
/// buffer you <em>must</em> commit them yourself — <c>_ = mw.Buffer;</c>. This is the one
/// rule a custom <see cref="ISerializer{T}"/> author has to know:
/// <code>
/// var mw = new MsgPackWriter(writer);
/// mw.WriteString("abc"u8);
/// _ = mw.Buffer; // commits the tail; without it the caller sees 0 bytes
/// </code>
/// Reading <see cref="Buffer"/> has that side effect on purpose: it is the hand-off point,
/// both for generated code and for nested writers sharing the same buffer.
/// </para>
/// </summary>
public ref struct MsgPackWriter
{
    /// <summary>Minimum span the writer asks for, so short tokens amortise one fetch.</summary>
    private const int MinSpanSize = 256;

    /// <summary>Application-defined extension tag MessagePack reserves for timestamps.</summary>
    private const byte TimestampExtTag = 0xFF;

    /// <summary>Payload length of a timestamp 96 extension (4-byte nsec + 8-byte sec).</summary>
    private const byte TimestampExtLength96 = 12;

    /// <summary>Raw <see cref="DateTime.Ticks"/> of the Unix epoch, 1970-01-01T00:00:00Z.</summary>
    private const long UnixEpochTicks = 621_355_968_000_000_000L;

    /// <summary>Highest second count a timestamp 32 carries (2^32).</summary>
    private const long Timestamp32Limit = 1L << 32;

    /// <summary>Highest second count a timestamp 64 carries (2^34).</summary>
    private const long Timestamp64Limit = 1L << 34;

    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private int _depth;
    private readonly MsgPackOptions? _options;

    /// <summary>Bytes handed out by <see cref="_buffer"/> and not yet committed.</summary>
    private Span<byte> _span;

    /// <summary>Write cursor inside <see cref="_span"/>.</summary>
    private int _spanPos;

    private const int MaxDepth = 64;

    public long BytesWritten => _bytesWritten;

    /// <summary>
    /// Underlying buffer — used by SG-generated code to hand off to custom serializers.
    /// </summary>
    /// <remarks>
    /// Reading this commits the pending span (a custom serializer writes into the same
    /// buffer and would otherwise overwrite it) and drops the cached span, so the next
    /// write fetches a fresh one.
    /// </remarks>
    public IBufferWriter<byte> Buffer
    {
        get
        {
            Flush();
            return _buffer;
        }
    }

    /// <summary>Options supplied at construction, or null when default behavior is desired.</summary>
    public MsgPackOptions? Options => _options;

    public MsgPackWriter(IBufferWriter<byte> buffer, MsgPackOptions? options = null)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _depth = 0;
        _options = options;
        _span = default;
        _spanPos = 0;
    }

    /// <summary>
    /// Commits every byte written so far to the underlying buffer.
    /// </summary>
    /// <remarks>
    /// Deliberately <c>internal</c>: the public surface keeps a single flush exit,
    /// <see cref="Buffer"/>, whose getter commits the tail — so this optimisation did
    /// not have to add a public member. Generated serializers run in the consumer
    /// assembly and therefore hand off with <c>_ = mw.Buffer;</c>; this member stays
    /// for in-assembly tests that want to say "commit" out loud.
    /// </remarks>
    internal void FlushToBuffer() => Flush();

    /// <summary>Makes room for <paramref name="n"/> more bytes in <see cref="_span"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining
    )]
    private void Ensure(int n)
    {
        if (_span.Length - _spanPos < n)
        {
            Flush();
            _span = _buffer.GetSpan(n < MinSpanSize ? MinSpanSize : n);
        }
    }

    /// <summary>Commits the span (if anything is pending) and drops it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining
    )]
    private void Flush()
    {
        if (_spanPos > 0)
        {
            _buffer.Advance(_spanPos);
            _spanPos = 0;
        }
        // The buffer owns those bytes now: keeping the span would let the next write land
        // in memory that has already been committed (double commit / corruption).
        // Ensure() refetches on the next write, because an empty span never fits.
        _span = default;
    }

    public void WriteNull()
    {
        Ensure(1);
        _span[_spanPos++] = 0xC0;
        _bytesWritten++;
    }

    public void WriteBoolean(bool value)
    {
        Ensure(1);
        _span[_spanPos++] = value ? (byte)0xC3 : (byte)0xC2;
        _bytesWritten++;
    }

    public void WriteInt32(int value)
    {
        if (value >= 0 && value <= 127)
        {
            Ensure(1);
            _span[_spanPos++] = (byte)value;
            _bytesWritten++;
        }
        else if (value >= -32 && value < 0)
        {
            Ensure(1);
            _span[_spanPos++] = (byte)value;
            _bytesWritten++;
        }
        else if (value >= 0 && value <= 255)
        {
            Ensure(2);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCC;
            s[1] = (byte)value;
            _spanPos += 2;
            _bytesWritten += 2;
        }
        else if (value >= short.MinValue && value <= short.MaxValue)
        {
            Ensure(3);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD1;
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(1), (short)value);
            _spanPos += 3;
            _bytesWritten += 3;
        }
        else
        {
            Ensure(5);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD2;
            BinaryPrimitives.WriteInt32BigEndian(s.Slice(1), value);
            _spanPos += 5;
            _bytesWritten += 5;
        }
    }

    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        int len = utf8Value.Length;
        if (len <= 31)
        {
            Ensure(1 + len);
            var s = _span.Slice(_spanPos);
            s[0] = (byte)(0xA0 | len);
            utf8Value.CopyTo(s.Slice(1));
            _spanPos += 1 + len;
            _bytesWritten += 1 + len;
        }
        else if (len <= 255)
        {
            Ensure(2 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD9;
            s[1] = (byte)len;
            utf8Value.CopyTo(s.Slice(2));
            _spanPos += 2 + len;
            _bytesWritten += 2 + len;
        }
        else if (len <= 65535)
        {
            Ensure(3 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDA;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)len);
            utf8Value.CopyTo(s.Slice(3));
            _spanPos += 3 + len;
            _bytesWritten += 3 + len;
        }
        else
        {
            Ensure(5 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDB;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)len);
            utf8Value.CopyTo(s.Slice(5));
            _spanPos += 5 + len;
            _bytesWritten += 5 + len;
        }
    }

    /// <summary>
    /// Writes a string, encoding it straight into the span this writer owns.
    /// </summary>
    /// <remarks>
    /// Generated serializers used to hand strings over as a freshly allocated
    /// <c>byte[]</c> (<c>WriteString(Encoding.UTF8.GetBytes(value))</c>), which cost one
    /// allocation per string member and encoded the characters twice — once into the
    /// array, once into the span. Passing the characters lets the encoder land directly
    /// behind the header.
    /// <para>
    /// The header length is a function of the UTF-8 <em>byte</em> count, not the char
    /// count, so it cannot be chosen before the encoding runs. Two arms cover that:
    /// up to 31 characters the payload cannot exceed 93 bytes, which is a fixstr or str8
    /// header — the encoder writes the payload first and the header is backfilled from the
    /// count it returns, so there is no counting pass and (for anything that fits fixstr, the
    /// overwhelmingly common case) no move at all; longer strings take the exact byte count
    /// first. <see cref="Encoding.GetByteCount(ReadOnlySpan{char})"/> is exact, which keeps
    /// <see cref="Ensure"/> honest: <c>GetMaxByteCount</c> would reserve three bytes per char
    /// (the underlying buffer would grow to three times an all-ASCII payload) and a long
    /// header would then need a memmove of a long payload.
    /// </para>
    /// </remarks>
    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        if (value.Length <= 31)
        {
            // At most 93 bytes, so the header is fixstr or str8 and - whatever the characters
            // are - no counting is needed: encode behind a one-byte header slot and backfill
            // it. Only a payload that outgrew fixstr has to move, and then by one byte.
            Ensure(2 + value.Length * 3);
            var head = _span.Slice(_spanPos);
            int written = Encoding.UTF8.GetBytes(value, head.Slice(1));
            if (written <= 31)
            {
                head[0] = (byte)(0xA0 | written);
                _spanPos += 1 + written;
                _bytesWritten += 1 + written;
            }
            else
            {
                // str8: overlapping move (Span.CopyTo is a memmove), then the two-byte header.
                head.Slice(1, written).CopyTo(head.Slice(2, written));
                head[0] = 0xD9;
                head[1] = (byte)written;
                _spanPos += 2 + written;
                _bytesWritten += 2 + written;
            }
            return;
        }

        int len = Encoding.UTF8.GetByteCount(value);
        if (len <= 31)
        {
            Ensure(1 + len);
            var s = _span.Slice(_spanPos);
            s[0] = (byte)(0xA0 | len);
            Encoding.UTF8.GetBytes(value, s.Slice(1));
            _spanPos += 1 + len;
            _bytesWritten += 1 + len;
        }
        else if (len <= 255)
        {
            Ensure(2 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD9;
            s[1] = (byte)len;
            Encoding.UTF8.GetBytes(value, s.Slice(2));
            _spanPos += 2 + len;
            _bytesWritten += 2 + len;
        }
        else if (len <= 65535)
        {
            Ensure(3 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDA;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)len);
            Encoding.UTF8.GetBytes(value, s.Slice(3));
            _spanPos += 3 + len;
            _bytesWritten += 3 + len;
        }
        else
        {
            Ensure(5 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDB;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)len);
            Encoding.UTF8.GetBytes(value, s.Slice(5));
            _spanPos += 5 + len;
            _bytesWritten += 5 + len;
        }
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name) => WriteString(utf8Name);

    public void WriteStartObject(int count)
    {
        if (_depth >= MaxDepth)
            throw new FormatException(
                $"Maximum depth of {MaxDepth} exceeded (possible cyclic object graph)"
            );
        _depth++;
        if (count <= 15)
        {
            Ensure(1);
            _span[_spanPos++] = (byte)(0x80 | count);
            _bytesWritten++;
        }
        else if (count <= ushort.MaxValue)
        {
            Ensure(3);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDE;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)count);
            _spanPos += 3;
            _bytesWritten += 3;
        }
        else
        {
            Ensure(5);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDF;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)count);
            _spanPos += 5;
            _bytesWritten += 5;
        }
    }

    public void WriteEndObject()
    {
        _depth--;
        /* MsgPack maps don't have end markers; count is specified upfront */
        // The outermost value is complete: commit the tail so a caller that reads the
        // buffer right after WriteEndObject (hand-written code, the sample) sees it.
        if (_depth == 0)
            Flush();
    }

    public void WriteInt64(long value)
    {
        if (value >= 0 && value <= 127)
        {
            Ensure(1);
            _span[_spanPos++] = (byte)value;
            _bytesWritten++;
        }
        else if (value >= -32 && value < 0)
        {
            Ensure(1);
            _span[_spanPos++] = (byte)value;
            _bytesWritten++;
        }
        else if (value >= 0 && value <= byte.MaxValue)
        {
            Ensure(2);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCC;
            s[1] = (byte)value;
            _spanPos += 2;
            _bytesWritten += 2;
        }
        else if (value >= sbyte.MinValue && value < 0)
        {
            Ensure(2);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD0;
            s[1] = (byte)value;
            _spanPos += 2;
            _bytesWritten += 2;
        }
        else if (value >= 0 && value <= ushort.MaxValue)
        {
            Ensure(3);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCD;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)value);
            _spanPos += 3;
            _bytesWritten += 3;
        }
        else if (value >= short.MinValue && value <= short.MaxValue)
        {
            Ensure(3);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD1;
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(1), (short)value);
            _spanPos += 3;
            _bytesWritten += 3;
        }
        else if (value >= 0 && value <= uint.MaxValue)
        {
            Ensure(5);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCE;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)value);
            _spanPos += 5;
            _bytesWritten += 5;
        }
        else if (value >= int.MinValue && value <= int.MaxValue)
        {
            Ensure(5);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD2;
            BinaryPrimitives.WriteInt32BigEndian(s.Slice(1), (int)value);
            _spanPos += 5;
            _bytesWritten += 5;
        }
        else
        {
            Ensure(9);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD3;
            BinaryPrimitives.WriteInt64BigEndian(s.Slice(1), value);
            _spanPos += 9;
            _bytesWritten += 9;
        }
    }

    public void WriteUInt64(ulong value)
    {
        if (value <= 127)
        {
            Ensure(1);
            _span[_spanPos++] = (byte)value;
            _bytesWritten++;
        }
        else if (value <= byte.MaxValue)
        {
            Ensure(2);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCC;
            s[1] = (byte)value;
            _spanPos += 2;
            _bytesWritten += 2;
        }
        else if (value <= ushort.MaxValue)
        {
            Ensure(3);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCD;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)value);
            _spanPos += 3;
            _bytesWritten += 3;
        }
        else if (value <= uint.MaxValue)
        {
            Ensure(5);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCE;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)value);
            _spanPos += 5;
            _bytesWritten += 5;
        }
        else
        {
            Ensure(9);
            var s = _span.Slice(_spanPos);
            s[0] = 0xCF;
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(1), value);
            _spanPos += 9;
            _bytesWritten += 9;
        }
    }

    public void WriteFloat64(double value)
    {
        Ensure(9);
        var s = _span.Slice(_spanPos);
        s[0] = 0xCB;
        BinaryPrimitives.WriteInt64BigEndian(s.Slice(1), BitConverter.DoubleToInt64Bits(value));
        _spanPos += 9;
        _bytesWritten += 9;
    }

    /// <summary>
    /// Writes a <see cref="DateTime"/> as the MessagePack <b>timestamp extension</b>
    /// (application-defined tag <c>0xFF</c>) — the shape MessagePack-CSharp emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three encodings, chosen from the value (spec §"Timestamp extension type"):
    /// </para>
    /// <list type="table">
    /// <item>
    /// <term>timestamp 32</term>
    /// <description><c>0xD6 0xFF</c> + 4-byte big-endian seconds — only when the
    /// sub-second part is zero <em>and</em> the seconds fit <c>[0, 2³²)</c>.</description>
    /// </item>
    /// <item>
    /// <term>timestamp 64</term>
    /// <description><c>0xD7 0xFF</c> + 8-byte big-endian <c>(nsec &lt;&lt; 34) | sec</c> —
    /// 30 bits of nanoseconds above 34 bits of seconds, for <c>[0, 2³⁴)</c> seconds.
    /// </description>
    /// </item>
    /// <item>
    /// <term>timestamp 96</term>
    /// <description><c>0xC7 0x0C 0xFF</c> + 4-byte big-endian nanoseconds + 8-byte
    /// big-endian signed seconds — everything else, including instants before 1970.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// <b>Kind policy.</b> The wire carries an instant, not a <see cref="DateTimeKind"/>:
    /// <see cref="DateTimeKind.Utc"/> is written as is, <see cref="DateTimeKind.Local"/> is
    /// converted with <see cref="DateTime.ToUniversalTime"/>, and
    /// <see cref="DateTimeKind.Unspecified"/> is <b>interpreted as UTC</b> — its ticks go on
    /// the wire unchanged, so a value read back is the same instant carrying
    /// <see cref="DateTimeKind.Utc"/>. Round-tripping an <c>Unspecified</c> or <c>Local</c>
    /// value therefore preserves the instant and normalises the Kind.
    /// </para>
    /// <para>
    /// Set <see cref="MsgPackOptions.DateTimeFormat"/> to
    /// <see cref="MsgPackDateTimeFormat.IsoString"/> to keep the legacy 28-character
    /// <c>"O"</c> text instead.
    /// </para>
    /// </remarks>
    public void WriteDateTime(DateTime value)
    {
        if (_options is { DateTimeFormat: MsgPackDateTimeFormat.IsoString })
        {
            WriteString(Encoding.UTF8.GetBytes(value.ToString("O")));
            return;
        }

        if (value.Kind == DateTimeKind.Local)
            value = value.ToUniversalTime();

        // Seconds/nanoseconds since the epoch, floored on purpose: a pre-1970 instant has
        // negative ticks, and truncation towards zero would leave a negative remainder and
        // nanoseconds outside the 30-bit field.
        long ticks = value.Ticks - UnixEpochTicks;
        long sec = ticks / TimeSpan.TicksPerSecond;
        long rem = ticks % TimeSpan.TicksPerSecond;
        if (rem < 0)
        {
            sec--;
            rem += TimeSpan.TicksPerSecond;
        }
        // A tick is 100 ns, so the sub-second part is always a whole number of nanoseconds
        // and the 30-bit field can hold anything below 1e9.
        uint nsec = (uint)(rem * 100);

        if (nsec == 0 && sec >= 0 && sec < Timestamp32Limit)
        {
            Ensure(6);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD6;
            s[1] = TimestampExtTag;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(2), (uint)sec);
            _spanPos += 6;
            _bytesWritten += 6;
        }
        else if (sec >= 0 && sec < Timestamp64Limit)
        {
            Ensure(10);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD7;
            s[1] = TimestampExtTag;
            BinaryPrimitives.WriteUInt64BigEndian(s.Slice(2), ((ulong)nsec << 34) | (ulong)sec);
            _spanPos += 10;
            _bytesWritten += 10;
        }
        else
        {
            Ensure(15);
            var s = _span.Slice(_spanPos);
            s[0] = 0xC7;
            s[1] = TimestampExtLength96;
            s[2] = TimestampExtTag;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(3), nsec);
            BinaryPrimitives.WriteInt64BigEndian(s.Slice(7), sec);
            _spanPos += 15;
            _bytesWritten += 15;
        }
    }

    public void WriteStartArray(int count)
    {
        if (_depth >= MaxDepth)
            throw new FormatException(
                $"Maximum depth of {MaxDepth} exceeded (possible cyclic object graph)"
            );
        _depth++;
        if (count <= 15)
        {
            Ensure(1);
            _span[_spanPos++] = (byte)(0x90 | count);
            _bytesWritten++;
        }
        else if (count <= ushort.MaxValue)
        {
            Ensure(3);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDC;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)count);
            _spanPos += 3;
            _bytesWritten += 3;
        }
        else
        {
            Ensure(5);
            var s = _span.Slice(_spanPos);
            s[0] = 0xDD;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)count);
            _spanPos += 5;
            _bytesWritten += 5;
        }
    }

    public void WriteEndArray()
    {
        _depth--;
        // See WriteEndObject: closing the outermost container commits the tail.
        if (_depth == 0)
            Flush();
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        int len = value.Length;
        if (len <= 255)
        {
            Ensure(2 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xC4;
            s[1] = (byte)len;
            value.CopyTo(s.Slice(2));
            _spanPos += 2 + len;
            _bytesWritten += 2 + len;
        }
        else if (len <= 65535)
        {
            Ensure(3 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xC5;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)len);
            value.CopyTo(s.Slice(3));
            _spanPos += 3 + len;
            _bytesWritten += 3 + len;
        }
        else
        {
            Ensure(5 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xC6;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)len);
            value.CopyTo(s.Slice(5));
            _spanPos += 5 + len;
            _bytesWritten += 5 + len;
        }
    }

    public void WriteExtension(byte tag, ReadOnlySpan<byte> data)
    {
        int len = data.Length;
        if (len == 1)
        {
            Ensure(3);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD4;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _spanPos += 3;
            _bytesWritten += 3;
        }
        else if (len == 2)
        {
            Ensure(4);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD5;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _spanPos += 4;
            _bytesWritten += 4;
        }
        else if (len == 4)
        {
            Ensure(6);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD6;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _spanPos += 6;
            _bytesWritten += 6;
        }
        else if (len == 8)
        {
            Ensure(10);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD7;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _spanPos += 10;
            _bytesWritten += 10;
        }
        else if (len == 16)
        {
            Ensure(18);
            var s = _span.Slice(_spanPos);
            s[0] = 0xD8;
            s[1] = tag;
            data.CopyTo(s.Slice(2));
            _spanPos += 18;
            _bytesWritten += 18;
        }
        else if (len <= 255)
        {
            Ensure(3 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xC7;
            s[1] = (byte)len;
            s[2] = tag;
            data.CopyTo(s.Slice(3));
            _spanPos += 3 + len;
            _bytesWritten += 3 + len;
        }
        else if (len <= 65535)
        {
            Ensure(4 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xC8;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)len);
            s[3] = tag;
            data.CopyTo(s.Slice(4));
            _spanPos += 4 + len;
            _bytesWritten += 4 + len;
        }
        else
        {
            Ensure(6 + len);
            var s = _span.Slice(_spanPos);
            s[0] = 0xC9;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)len);
            s[5] = tag;
            data.CopyTo(s.Slice(6));
            _spanPos += 6 + len;
            _bytesWritten += 6 + len;
        }
    }
}
