namespace PicoJetson;

/// <summary>Captures parser state for pause/resume across chunk boundaries.</summary>
public struct JsonReaderState
{
    /// <summary>Nesting depth at the moment of interruption.</summary>
    internal int Depth;

    /// <summary>Format-specific streaming scratch state (e.g. the in-progress
    /// List for array streaming). Carried across chunk boundaries.</summary>
    public object? Aux;

    /// <summary>True when the previous attempt rewound to a property-name start
    /// (incomplete value); the ReadStart section must re-run on resume.</summary>
    public bool Rewound;

    /// <summary>Maximum allowed depth.</summary>
    internal int MaxDepth;

    /// <summary>Number of bytes consumed in the current sequence.</summary>
    internal long BytesConsumed;

    /// <summary>Reader position for PipeReader.AdvanceTo.</summary>
    internal SequencePosition Position;
}

public ref struct JsonReader
{
    // Span mode fields
    private ReadOnlySpan<byte> _data;
    private int _position;

    // Sequence mode fields
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;

    // Common
    private TokenType _tokenType;
    private int _depth;
    private readonly int _maxDepth;
    private ReadOnlySpan<byte> _valueSpan;
    private int _tokenValueStart;
    private int _tokenValueEnd;
    private byte[]? _rentedBuffer;
    private byte[]? _rb0,
        _rb1,
        _rb2,
        _rb3,
        _rb4,
        _rb5,
        _rb6,
        _rb7;
    private int _bufCount;

    // Streaming support: isFinalBlock tells the reader whether more data may arrive.
    // When false, IsAtEnd() sets _needsMoreData instead of signaling end-of-document.
    private readonly bool _isFinalBlock;
    private bool _needsMoreData;

    // Per-instance options (comment/number handling, ...). No ambient state.
    private readonly JsonOptions? _options;

    // Streaming resume support: position (in the sequence) where the current
    // property name started, so an incomplete value read can rewind and let
    // the next attempt re-read the property from scratch.
    private long _propertyNameStart = -1;

    /// <summary>Options supplied at construction, or null when default behavior is desired.</summary>
    public JsonOptions? Options => _options;

    /// <summary>True when this reader resumes a previously exported state (streaming).</summary>
    public bool IsResumed { get; }

    /// <summary>Format-specific streaming scratch state (carried via JsonReaderState.Aux).</summary>
    public object? StreamState { get; set; }

    /// <summary>True when the previous call rewound to the current property name
    /// (an incomplete value was seen) — the ReadStart section must re-run.</summary>
    public bool Rewound { get; private set; }

    /// <summary>True when Read() returned false because a chunk boundary was reached (not EOF).</summary>
    public bool NeedsMoreData => _needsMoreData;

    /// <summary>Exports the current parser state for later resumption with a new buffer.</summary>
    public readonly JsonReaderState ExportState()
    {
        return new JsonReaderState
        {
            Depth = _depth,
            MaxDepth = _maxDepth,
            BytesConsumed = _seqReader.Consumed,
            Position = _seqReader.Position,
            Aux = StreamState,
            Rewound = Rewound,
        };
    }

    /// <summary>Creates a reader that resumes from a previously saved state.
    /// The <paramref name="data"/> must start at the same position where the
    /// previous reader stopped (the unconsumed portion of the old sequence).</summary>
    public JsonReader(
        ReadOnlySequence<byte> data,
        bool isFinalBlock,
        JsonReaderState state,
        JsonOptions? options = null
    )
        : this(
            data,
            options?.MaxDepth ?? (state.MaxDepth > 0 ? state.MaxDepth : 256),
            isFinalBlock,
            options
        )
    {
        _depth = state.Depth;
        _needsMoreData = false;
        IsResumed = state.BytesConsumed > 0 || state.Depth > 0;
        StreamState = state.Aux;
        Rewound = state.Rewound;
    }

    public JsonReader(
        ReadOnlySpan<byte> data,
        int maxDepth = 256,
        bool isFinalBlock = true,
        JsonOptions? options = null
    )
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _isFinalBlock = isFinalBlock;
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = maxDepth;
        _options = options;
        _valueSpan = default;
        _rentedBuffer = null;
        _rb0 = _rb1 = _rb2 = _rb3 = _rb4 = _rb5 = _rb6 = _rb7 = null;
        _bufCount = 0;
    }

    public JsonReader(
        ReadOnlySequence<byte> data,
        int maxDepth = 256,
        bool isFinalBlock = true,
        JsonOptions? options = null
    )
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _isFinalBlock = isFinalBlock;
        _options = options;
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = maxDepth;
        _valueSpan = default;
        _rentedBuffer = null;
        _rb0 = _rb1 = _rb2 = _rb3 = _rb4 = _rb5 = _rb6 = _rb7 = null;
        _bufCount = 0;
    }

    public TokenType TokenType => _tokenType;
    public int Depth => _depth;

    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    public ReadOnlySpan<byte> ValueSpan => _valueSpan;

    /// <summary>For testing: number of tracked rented buffers (0 after Dispose).</summary>
    internal int TrackedBufferCount => _bufCount;

    /// <summary>For testing: peak tracked count during reading.</summary>
    internal int PeakTrackedBufferCount { get; private set; }

    /// <summary>
    /// For testing: total TrackBuffer calls minus total ReturnBuf calls.
    /// Should be 0 after Dispose; > 0 indicates a buffer leak.
    /// </summary>
    internal int LeakedBufferCount { get; private set; }

    /// <summary>Direct buffer access for optimized generated code (span mode only).</summary>
    public ReadOnlySpan<byte> RawBuffer => _isSequence ? default : _data;

    /// <summary>Direct position access for optimized generated code (span mode only).</summary>
    public int RawPos => _isSequence ? -1 : _position;

    /// <summary>Byte offset of the current token's value within the source buffer.</summary>
    public int TokenValueStart => _isSequence ? -1 : _tokenValueStart;

    /// <summary>Byte offset of the end of the current token's value (exclusive).</summary>
    public int TokenValueEnd => _isSequence ? -1 : _tokenValueEnd;

    public void SetRawPos(int pos)
    {
        _position = pos;
    }

    public bool Read()
    {
        _needsMoreData = false;
        Rewound = false;
        SkipWhitespace();
        Retry:
        if (IsAtEnd())
        {
            _needsMoreData = !_isFinalBlock;
            return false;
        }

        // Comment handling: skip // and /* */ if enabled
        if (PeekByte() == (byte)'/')
        {
            var opts = _options;
            if (opts?.ReadCommentHandling == PicoJetson.JsonCommentHandling.Skip)
            {
                AdvanceByte();
                if (IsAtEnd())
                {
                    _needsMoreData = !_isFinalBlock;
                    return false;
                }
                if (PeekByte() == (byte)'/')
                {
                    // Line comment: skip to end of line
                    while (!IsAtEnd() && PeekByte() != (byte)'\n' && PeekByte() != (byte)'\r')
                        AdvanceByte();
                    SkipWhitespace();
                    goto Retry;
                }
                if (PeekByte() == (byte)'*')
                {
                    // Block comment: skip to */
                    AdvanceByte();
                    while (!IsAtEnd())
                    {
                        if (PeekByte() == (byte)'*')
                        {
                            AdvanceByte();
                            if (!IsAtEnd() && PeekByte() == (byte)'/')
                            {
                                AdvanceByte();
                                SkipWhitespace();
                                goto Retry;
                            }
                        }
                        else
                            AdvanceByte();
                    }
                    if (!_isFinalBlock)
                    {
                        _needsMoreData = true;
                        return false;
                    }
                    throw new FormatException(
                        $"Unterminated block comment at offset {BytesConsumed}"
                    );
                }
                // A '/' not followed by '/' or '*' is invalid JSON, even in
                // Skip mode — never silently swallow it.
                throw new FormatException(
                    $"Unexpected byte 0x{(byte)'/':X2} at offset {BytesConsumed - 1}"
                );
            }
        }

        if (PeekByte() == (byte)',')
        {
            AdvanceByte();
            SkipWhitespace();
            // Allow trailing commas if requested
            var opts = _options;
            if (IsAtEnd())
            {
                // In streaming mode a comma at the buffer end is not
                // necessarily trailing — more data may arrive. Signal
                // NeedMoreData and let the next attempt decide.
                if (!_isFinalBlock)
                {
                    _needsMoreData = true;
                    return false;
                }
                if (opts?.AllowTrailingCommas == true)
                {
                    _needsMoreData = !_isFinalBlock;
                    return false;
                }
                throw new FormatException("Trailing comma at end of document");
            }
            if (PeekByte() is (byte)'}' or (byte)']' && opts?.AllowTrailingCommas != true)
                throw new FormatException("Trailing comma before closing bracket");
            // With AllowTrailingCommas enabled, fall through to the switch below,
            // which emits the proper ObjectEnd/ArrayEnd token instead of returning
            // with a stale token type.
        }

        var b = PeekByte();
        switch (b)
        {
            case (byte)'{':
                if (_depth >= _maxDepth)
                    throw new FormatException(
                        $"Maximum depth of {_maxDepth} exceeded at offset {BytesConsumed}"
                    );
                _tokenType = TokenType.ObjectStart;
                AdvanceByte();
                _depth++;
                return true;
            case (byte)'}':
                if (_depth == 0)
                    throw new FormatException($"Unmatched }} at offset {BytesConsumed}");
                _tokenType = TokenType.ObjectEnd;
                AdvanceByte();
                _depth--;
                return true;
            case (byte)'[':
                if (_depth >= _maxDepth)
                    throw new FormatException(
                        $"Maximum depth of {_maxDepth} exceeded at offset {BytesConsumed}"
                    );
                _tokenType = TokenType.ArrayStart;
                AdvanceByte();
                _depth++;
                return true;
            case (byte)']':
                if (_depth == 0)
                    throw new FormatException($"Unmatched ] at offset {BytesConsumed}");
                _tokenType = TokenType.ArrayEnd;
                AdvanceByte();
                _depth--;
                return true;
            case (byte)'"':
                // In streaming mode, verify the complete string (+ suffix) is in the
                // current sequence before starting to read. Prevents partial-value reads.
                if (!_isFinalBlock && _isSequence && !HasCompletePropertyOrString())
                {
                    RewindToPropertyName();
                    _needsMoreData = true;
                    return false;
                }
                return ReadStringOrProperty();
            case (byte)'t':
                if (!_isFinalBlock && _isSequence && !HasCompleteLiteral("true"u8))
                {
                    RewindToPropertyName();
                    _needsMoreData = true;
                    return false;
                }
                return ReadLiteral("true"u8, TokenType.Bool);
            case (byte)'f':
                if (!_isFinalBlock && _isSequence && !HasCompleteLiteral("false"u8))
                {
                    RewindToPropertyName();
                    _needsMoreData = true;
                    return false;
                }
                return ReadLiteral("false"u8, TokenType.Bool);
            case (byte)'n':
                if (!_isFinalBlock && _isSequence && !HasCompleteLiteral("null"u8))
                {
                    RewindToPropertyName();
                    _needsMoreData = true;
                    return false;
                }
                return ReadLiteral("null"u8, TokenType.Null);
            case (byte)'N':
                if (
                    _options?.NumberHandling
                    == PicoJetson.JsonNumberHandling.AllowNamedFloatingPointLiterals
                )
                    return ReadLiteral("NaN"u8, TokenType.Float64);
                throw new FormatException(
                    $"Unexpected byte 0x{(byte)'N':X2} at offset {BytesConsumed}"
                );
            case (byte)'-':
                // Check for -Infinity
                if (
                    _options?.NumberHandling
                        == PicoJetson.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    && PeekStartsWith("-Infinity"u8)
                )
                    return ReadLiteral("-Infinity"u8, TokenType.Float64);
                if (!_isFinalBlock && _isSequence && !HasCompleteNumber())
                {
                    RewindToPropertyName();
                    _needsMoreData = true;
                    return false;
                }
                goto case (byte)'0';
            case (byte)'I':
                // Infinity
                if (
                    _options?.NumberHandling
                        == PicoJetson.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    && PeekStartsWith("Infinity"u8)
                )
                    return ReadLiteral("Infinity"u8, TokenType.Float64);
                throw new FormatException(
                    $"Unexpected byte 0x{(byte)'I':X2} at offset {BytesConsumed}"
                );
            case (byte)'0':
            case (byte)'1':
            case (byte)'2':
            case (byte)'3':
            case (byte)'4':
            case (byte)'5':
            case (byte)'6':
            case (byte)'7':
            case (byte)'8':
            case (byte)'9':
                if (!_isFinalBlock && _isSequence && !HasCompleteNumber())
                {
                    RewindToPropertyName();
                    _needsMoreData = true;
                    return false;
                }
                return ReadNumber();
            default:
                throw new FormatException($"Unexpected byte 0x{b:X2} at offset {BytesConsumed}");
        }
    }

    public void Skip()
    {
        if (!TrySkip())
            throw new FormatException($"Failed to skip at offset {BytesConsumed}");
    }

    public bool TrySkip()
    {
        // Container token: skip until the matching end marker.
        if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
        {
            var start = _depth - 1;
            try
            {
                while (Read())
                {
                    if (_depth == start)
                        return true;
                }
            }
            catch (FormatException) { }
            return false;
        }

        // Property name: skip the following value token.
        if (_tokenType == TokenType.PropertyName)
        {
            var start = _depth;
            try
            {
                while (Read())
                {
                    if (_depth == start)
                        return true;
                }
            }
            catch (FormatException) { }
            return false;
        }

        // Scalar token: the value is already fully consumed — nothing to skip.
        return true;
    }

    /// <summary>
    /// Returns the decoded UTF-8 bytes of the current string or property name token.
    /// Escape sequences (\n, \r, \t, \\, \", \uXXXX) are resolved.
    /// </summary>
    public ReadOnlySpan<byte> GetStringRaw() => _valueSpan;

    public bool TryGetInt32(out int v)
    {
        if (_tokenType is TokenType.Int32)
            return Utf8Parser.TryParse(_valueSpan, out v, out _);

        if (_tokenType is TokenType.Int64)
        {
            if (
                Utf8Parser.TryParse(_valueSpan, out long lv, out _)
                && lv >= int.MinValue
                && lv <= int.MaxValue
            )
            {
                v = (int)lv;
                return true;
            }
        }

        v = 0;
        return false;
    }

    public bool TryReadNextInt32(out int v)
    {
        if (_isSequence)
            return TryReadNextInt32Seq(out v);
        return TryReadNextInt32Span(out v);
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType is not (TokenType.Int32 or TokenType.Int64))
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    public bool TryGetFloat64(out double v)
    {
        if (_tokenType is not (TokenType.Float64 or TokenType.Int32 or TokenType.Int64))
        {
            v = 0;
            return false;
        }
        // Handle NaN/Infinity literals (AllowNamedFloatingPointLiterals)
        if (
            _valueSpan.Length == 3
            && _valueSpan[0] == (byte)'N'
            && _valueSpan[1] == (byte)'a'
            && _valueSpan[2] == (byte)'N'
        )
        {
            v = double.NaN;
            return true;
        }
        if (
            _valueSpan.Length == 8
            && _valueSpan[0] == (byte)'I'
            && _valueSpan[1] == (byte)'n'
            && _valueSpan[2] == (byte)'f'
        )
        {
            v = double.PositiveInfinity;
            return true;
        }
        if (
            _valueSpan.Length == 9
            && _valueSpan[0] == (byte)'-'
            && _valueSpan[1] == (byte)'I'
            && _valueSpan[2] == (byte)'n'
        )
        {
            v = double.NegativeInfinity;
            return true;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != TokenType.Bool)
        {
            v = false;
            return false;
        }
        v = _valueSpan[0] == (byte)'t';
        return true;
    }

    private bool TryReadNextInt32Span(out int v)
    {
        var len = _data.Length;
        if (_position >= len)
        {
            v = 0;
            return false;
        }
        if (_data[_position] == (byte)',')
            _position++;
        if (_position < len && _data[_position] <= 32)
            SkipWhitespaceSpan();
        if (_position >= len || _data[_position] is (byte)']' or (byte)'}')
        {
            v = 0;
            return false;
        }

        bool neg = false;
        if (_data[_position] == (byte)'-')
        {
            neg = true;
            _position++;
        }
        if (_position >= len || !IsDigit(_data[_position]))
        {
            v = 0;
            return false;
        }

        // Overflow-checked accumulation: bail on values outside int range
        // instead of silently wrapping.
        long result = 0;
        do
        {
            result = result * 10 + (_data[_position] - (byte)'0');
            _position++;
            if (result > int.MaxValue)
            {
                v = 0;
                return false;
            }
        } while (_position < len && IsDigit(_data[_position]));
        v = neg ? (int)-result : (int)result;
        return true;
    }

    private bool TryReadNextInt32Seq(out int v)
    {
        SkipWhitespaceSeq();
        if (_seqReader.End)
        {
            v = 0;
            return false;
        }
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)',')
        {
            _seqReader.Advance(1);
            SkipWhitespaceSeq();
        }
        if (_seqReader.End)
        {
            v = 0;
            return false;
        }
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'-')
            _seqReader.Advance(1);
        var buf = ArrayPool<byte>.Shared.Rent(16);
        TrackBuffer(buf);
        int di = 0;
        while (!_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex]))
        {
            if (di >= buf.Length)
            {
                v = 0;
                return false;
            }
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        return Utf8Parser.TryParse(buf.AsSpan(0, di), out v, out _);
    }

    // ── Mode-agnostic helpers ──

    private bool IsAtEnd() => _isSequence ? _seqReader.End : _position >= _data.Length;

    private byte PeekByte() =>
        _isSequence ? _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] : _data[_position];

    private void AdvanceByte()
    {
        if (_isSequence)
            _seqReader.Advance(1);
        else
            _position++;
    }

    private void SkipWhitespace()
    {
        if (_isSequence)
            SkipWhitespaceSeq();
        else
            SkipWhitespaceSpan();
    }

    private void SkipWhitespaceSpan()
    {
        _position = SimdHelpers.SkipWhitespace(_data, _position);
    }

    private void SkipWhitespaceSeq()
    {
        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                return;
            _seqReader.Advance(1);
        }
    }

    /// <summary>
    /// Checks whether the remaining data at the current reader position starts
    /// with the specified UTF-8 bytes, without advancing the reader.
    /// Mode-aware: works correctly for both Span and Sequence modes.
    /// </summary>
    private bool PeekStartsWith(ReadOnlySpan<byte> expected)
    {
        if (_isSequence)
        {
            if (_seqReader.Remaining < expected.Length)
                return false;
            var seq = _seqReader.Sequence.Slice(_seqReader.Position, expected.Length);
            // Fast path: single segment (common for small literals)
            if (seq.FirstSpan.Length >= expected.Length)
                return seq.FirstSpan.Slice(0, expected.Length).SequenceEqual(expected);
            // Multi-segment fallback
            Span<byte> buf = stackalloc byte[expected.Length];
            seq.CopyTo(buf);
            return buf.SequenceEqual(expected);
        }
        return _data[_position..].StartsWith(expected);
    }

    // ── String / Property reading ──

    /// <summary>
    /// Checks whether the current position points to a complete JSON string
    /// (including the closing quote) followed by at least one byte.
    /// Used in streaming mode (!isFinalBlock) to avoid starting a value
    /// whose closing delimiter or token-type signifier (':') is in a later chunk.
    /// </summary>
    /// <summary>Rewinds the sequence reader to the start of the current property name,
    /// so a resumed attempt re-reads the property from scratch (streaming resume).</summary>
    private void RewindToPropertyName()
    {
        if (_tokenType == TokenType.PropertyName && _propertyNameStart >= 0)
        {
            _seqReader.Rewind(_seqReader.Consumed - _propertyNameStart);
            Rewound = true;
        }
    }

    /// <summary>True when the full literal text is present in the current sequence
    /// and terminated by a delimiter or buffer end (streaming completeness check).</summary>
    private bool HasCompleteLiteral(ReadOnlySpan<byte> lit)
    {
        var seq = _seqReader.Sequence.Slice(_seqReader.Position);
        var r = new SequenceReader<byte>(seq);
        if (r.Remaining < lit.Length)
            return false;
        for (int i = 0; i < lit.Length; i++)
        {
            if (r.CurrentSpan[r.CurrentSpanIndex] != lit[i])
                return false;
            r.Advance(1);
        }
        // Terminated by a delimiter? If the buffer ends right after the literal,
        // more data may arrive — treat as incomplete for safety.
        if (r.End)
            return false;
        var next = r.CurrentSpan[r.CurrentSpanIndex];
        return next
            is (byte)' '
                or (byte)'\t'
                or (byte)'\n'
                or (byte)'\r'
                or (byte)','
                or (byte)'}'
                or (byte)']';
    }

    /// <summary>True when the number starting at the current position is terminated
    /// by a delimiter or the buffer end (streaming completeness check).</summary>
    private bool HasCompleteNumber()
    {
        var seq = _seqReader.Sequence.Slice(_seqReader.Position);
        var r = new SequenceReader<byte>(seq);
        while (!r.End)
        {
            var b = r.CurrentSpan[r.CurrentSpanIndex];
            if (b is (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E')
            {
                r.Advance(1);
                continue;
            }
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                r.Advance(1);
                continue;
            }
            return b
                is (byte)' '
                    or (byte)'\t'
                    or (byte)'\n'
                    or (byte)'\r'
                    or (byte)','
                    or (byte)'}'
                    or (byte)']';
        }
        return false; // buffer ended mid-number — more data may arrive
    }

    private bool HasCompletePropertyOrString()
    {
        var seq = _seqReader.Sequence.Slice(_seqReader.Position);
        var r = new SequenceReader<byte>(seq);
        if (r.End)
            return false;
        r.Advance(1); // skip opening "

        while (!r.End)
        {
            var b = r.CurrentSpan[r.CurrentSpanIndex];
            if (b == (byte)'"')
            {
                r.Advance(1);
                break;
            }
            if (b == (byte)'\\')
            {
                if (r.Remaining < 2)
                    return false;
                r.Advance(2);
                continue;
            }
            r.Advance(1);
        }
        if (r.End)
            return false;

        while (!r.End && (r.CurrentSpan[r.CurrentSpanIndex] is (byte)' ' or (byte)'\t'))
            r.Advance(1);

        // A property name must be followed by its ':' (which may live in the
        // next chunk). A string value is complete once its closing quote and a
        // trailing delimiter/end are present.
        if (r.End)
            return false;
        if (r.CurrentSpan[r.CurrentSpanIndex] == (byte)':')
        {
            r.Advance(1);
            while (!r.End && (r.CurrentSpan[r.CurrentSpanIndex] is (byte)' ' or (byte)'\t'))
                r.Advance(1);
            return !r.End;
        }
        return true;
    }

    private bool ReadStringOrProperty()
    {
        if (_isSequence)
            return ReadStringOrPropertySeq();
        return ReadStringOrPropertySpan();
    }

    private bool ReadStringOrPropertySpan()
    {
        _position++;
        var start = _position;
        while (_position < _data.Length)
        {
            if (_data[_position] == (byte)'"')
                break;
            if (_data[_position] == (byte)'\\' && _position + 1 < _data.Length)
                _position++;
            _position++;
        }
        if (_position >= _data.Length)
            throw new FormatException($"Unterminated string at offset {_position}");

        _valueSpan = _data[start.._position];
        _tokenValueStart = start;
        _tokenValueEnd = _position;
        _position++;
        UnescapeIfNeeded();

        var saved = _position;
        SkipWhitespaceSpan();
        if (_position < _data.Length && _data[_position] == (byte)':')
        {
            _tokenType = TokenType.PropertyName;
            _position++;
        }
        else
        {
            _tokenType = TokenType.String;
            _position = saved;
        }
        return true;
    }

    private bool ReadStringOrPropertySeq()
    {
        var propStart = _seqReader.Consumed; // position of the opening quote
        _seqReader.Advance(1); // skip opening "

        var buf = ArrayPool<byte>.Shared.Rent(256);
        TrackBuffer(buf);
        int di = 0;

        try
        {
            while (!_seqReader.End)
            {
                var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                if (b == (byte)'"')
                {
                    _seqReader.Advance(1);
                    break;
                }
                if (b == (byte)'\\')
                {
                    _seqReader.Advance(1);
                    if (_seqReader.End)
                        throw new FormatException(
                            $"Unterminated escape sequence at offset {_seqReader.Consumed}"
                        );
                    b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                    // Ensure capacity BEFORE writing: a \uXXXX escape can
                    // produce up to 4 UTF-8 bytes.
                    if (di + 4 > buf.Length)
                    {
                        var newBuf = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        buf.AsSpan(0, di).CopyTo(newBuf);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = newBuf;
                        TrackBuffer(buf);
                    }
                    switch (b)
                    {
                        case (byte)'"':
                            buf[di++] = (byte)'"';
                            break;
                        case (byte)'\\':
                            buf[di++] = (byte)'\\';
                            break;
                        case (byte)'/':
                            buf[di++] = (byte)'/';
                            break;
                        case (byte)'n':
                            buf[di++] = (byte)'\n';
                            break;
                        case (byte)'r':
                            buf[di++] = (byte)'\r';
                            break;
                        case (byte)'t':
                            buf[di++] = (byte)'\t';
                            break;
                        case (byte)'u':
                            di = ReadUnicodeEscapeSeq(buf, di);
                            break;
                        default:
                            buf[di++] = b;
                            break;
                    }
                    if (di >= buf.Length)
                    {
                        var newBuf = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        buf.AsSpan(0, di).CopyTo(newBuf);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = newBuf;
                        TrackBuffer(buf);
                    }
                }
                else
                {
                    buf[di++] = b;
                    _seqReader.Advance(1);
                    if (di >= buf.Length)
                    {
                        var newBuf = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        buf.AsSpan(0, di).CopyTo(newBuf);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = newBuf;
                        TrackBuffer(buf);
                    }
                }
            }

            _valueSpan = buf.AsSpan(0, di);
            _tokenValueStart = 0;
            _tokenValueEnd = di;

            SkipWhitespaceSeq();
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)':')
            {
                _tokenType = TokenType.PropertyName;
                _propertyNameStart = propStart;
                _seqReader.Advance(1);
            }
            else
            {
                _tokenType = TokenType.String;
            }
        }
        catch
        {
            ReturnBuffer();
            throw;
        }

        return true;
    }

    private int ReadUnicodeEscapeSeq(byte[] buf, int di)
    {
        int codepoint = 0;
        for (int j = 0; j < 4; j++)
        {
            if (_seqReader.End)
                throw new FormatException(
                    $"Incomplete unicode escape at offset {_seqReader.Consumed}"
                );
            var hex = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
            codepoint <<= 4;
            if (hex is >= (byte)'0' and <= (byte)'9')
                codepoint |= hex - (byte)'0';
            else if (hex is >= (byte)'A' and <= (byte)'F')
                codepoint |= hex - (byte)'A' + 10;
            else if (hex is >= (byte)'a' and <= (byte)'f')
                codepoint |= hex - (byte)'a' + 10;
            else
                throw new FormatException(
                    $"Invalid unicode escape character '{(char)hex}' at offset {_seqReader.Consumed}"
                );
        }

        if (codepoint is >= 0xD800 and <= 0xDBFF)
        {
            if (_seqReader.End || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\\')
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
                );
            _seqReader.Advance(1);
            if (_seqReader.End || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'u')
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
                );
            _seqReader.Advance(1);

            int lowSurrogate = 0;
            for (int j = 0; j < 4; j++)
            {
                if (_seqReader.End)
                    throw new FormatException(
                        $"Incomplete unicode escape at offset {_seqReader.Consumed}"
                    );
                var hex = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
                lowSurrogate <<= 4;
                if (hex is >= (byte)'0' and <= (byte)'9')
                    lowSurrogate |= hex - (byte)'0';
                else if (hex is >= (byte)'A' and <= (byte)'F')
                    lowSurrogate |= hex - (byte)'A' + 10;
                else if (hex is >= (byte)'a' and <= (byte)'f')
                    lowSurrogate |= hex - (byte)'a' + 10;
                else
                    throw new FormatException(
                        $"Invalid unicode escape character '{(char)hex}' at offset {_seqReader.Consumed}"
                    );
            }

            if (lowSurrogate is < 0xDC00 or > 0xDFFF)
                throw new FormatException(
                    $"Invalid low surrogate U+{lowSurrogate:X4} at offset {_seqReader.Consumed}"
                );

            codepoint = 0x10000 + ((codepoint - 0xD800) << 10) + (lowSurrogate - 0xDC00);
        }
        else if (codepoint is >= 0xDC00 and <= 0xDFFF)
        {
            throw new FormatException(
                $"Lone low surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
            );
        }

        if (codepoint < 0x80)
        {
            buf[di++] = (byte)codepoint;
        }
        else if (codepoint < 0x800)
        {
            buf[di++] = (byte)(0xC0 | (codepoint >> 6));
            buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else if (codepoint < 0x10000)
        {
            buf[di++] = (byte)(0xE0 | (codepoint >> 12));
            buf[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else
        {
            buf[di++] = (byte)(0xF0 | (codepoint >> 18));
            buf[di++] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
            buf[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }

        return di;
    }

    private static bool ContainsBackslash(ReadOnlySpan<byte> span)
    {
        ref var ptr = ref MemoryMarshal.GetReference(span);
        int i = 0;

        if (Vector512.IsHardwareAccelerated && span.Length >= 64)
        {
            var slash = Vector512.Create((byte)'\\');
            for (; i + 64 <= span.Length; i += 64)
            {
                var chunk = Vector512.LoadUnsafe(ref Unsafe.Add(ref ptr, i));
                if (Vector512.Equals(chunk, slash) != Vector512<byte>.Zero)
                    return true;
            }
            span = span[i..];
            i = 0;
        }
        else if (Vector256.IsHardwareAccelerated && span.Length >= 32)
        {
            var slash = Vector256.Create((byte)'\\');
            for (; i + 32 <= span.Length; i += 32)
            {
                var chunk = Vector256.LoadUnsafe(ref Unsafe.Add(ref ptr, i));
                if (Vector256.Equals(chunk, slash) != Vector256<byte>.Zero)
                    return true;
            }
            span = span[i..];
            i = 0;
        }
        else if (Vector128.IsHardwareAccelerated && span.Length >= 16)
        {
            var slash = Vector128.Create((byte)'\\');
            for (; i + 16 <= span.Length; i += 16)
            {
                var chunk = Vector128.LoadUnsafe(ref Unsafe.Add(ref ptr, i));
                if (Vector128.Equals(chunk, slash) != Vector128<byte>.Zero)
                    return true;
            }
            span = span[i..];
        }
        return span.IndexOf((byte)'\\') >= 0;
    }

    private void UnescapeIfNeeded()
    {
        if (ContainsBackslash(_valueSpan))
        {
            var decoded = ArrayPool<byte>.Shared.Rent(_valueSpan.Length);
            TrackBuffer(decoded);
            try
            {
                int di = 0;
                for (int si = 0; si < _valueSpan.Length; si++)
                {
                    if (_valueSpan[si] == (byte)'\\' && si + 1 < _valueSpan.Length)
                    {
                        si++;
                        switch (_valueSpan[si])
                        {
                            case (byte)'"':
                                decoded[di++] = (byte)'"';
                                break;
                            case (byte)'\\':
                                decoded[di++] = (byte)'\\';
                                break;
                            case (byte)'/':
                                decoded[di++] = (byte)'/';
                                break;
                            case (byte)'n':
                                decoded[di++] = (byte)'\n';
                                break;
                            case (byte)'r':
                                decoded[di++] = (byte)'\r';
                                break;
                            case (byte)'t':
                                decoded[di++] = (byte)'\t';
                                break;
                            case (byte)'u':
                                si = ReadUnicodeEscapeSpan(decoded, ref di, si);
                                break;
                            default:
                                decoded[di++] = _valueSpan[si];
                                break;
                        }
                    }
                    else
                        decoded[di++] = _valueSpan[si];
                }
                _valueSpan = decoded.AsSpan(0, di);
            }
            catch
            {
                ReturnBuffer();
                throw;
            }
        }
    }

    // ── Number reading ──

    private bool ReadNumber()
    {
        long start;
        if (_isSequence)
        {
            start = _seqReader.Consumed;
            ReadNumberSeq();
            return true;
        }
        start = _position;
        ReadNumberSpan();
        return true;
    }

    private void ReadNumberSpan()
    {
        var start = _position;
        if (_data[_position] == (byte)'-')
            _position++;
        // Strict JSON: reject leading zeros (RFC 8259 §6)
        int firstDigitPos = _position;
        bool isFloat = false;
        while (_position < _data.Length && IsDigit(_data[_position]))
            _position++;
        if (_position == firstDigitPos)
            throw new FormatException($"Expected digit at offset {firstDigitPos}");
        if (_position > firstDigitPos + 1 && _data[firstDigitPos] == (byte)'0')
            throw new FormatException($"Leading zeros are not allowed at offset {firstDigitPos}");
        if (_position < _data.Length && _data[_position] == (byte)'.')
        {
            isFloat = true;
            _position++;
            int fracStart = _position;
            while (_position < _data.Length && IsDigit(_data[_position]))
                _position++;
            if (_position == fracStart)
                throw new FormatException(
                    $"Expected digit after decimal point at offset {fracStart}"
                );
        }
        if (_position < _data.Length && (_data[_position] is (byte)'e' or (byte)'E'))
        {
            isFloat = true;
            _position++;
            if (_position < _data.Length && _data[_position] is (byte)'+' or (byte)'-')
                _position++;
            int expStart = _position;
            while (_position < _data.Length && IsDigit(_data[_position]))
                _position++;
            if (_position == expStart)
                throw new FormatException($"Expected digit in exponent at offset {expStart}");
        }
        _valueSpan = _data[start.._position];
        _tokenValueStart = start;
        _tokenValueEnd = _position;
        if (isFloat)
            _tokenType = TokenType.Float64;
        else if (Utf8Parser.TryParse(_valueSpan, out int _, out _))
            _tokenType = TokenType.Int32;
        else
            _tokenType = TokenType.Int64;
    }

    private void ReadNumberSeq()
    {
        var buf = ArrayPool<byte>.Shared.Rent(32);
        TrackBuffer(buf);
        int di = 0;
        try
        {
            bool isFloat = false;
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'-')
            {
                buf[di++] = (byte)'-';
                _seqReader.Advance(1);
            }
            // Strict JSON: reject leading zeros
            bool firstIsZero = false;
            int digitCount = 0;
            while (!_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex]))
            {
                if (
                    ++digitCount == 1
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'0'
                )
                    firstIsZero = true;
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            if (digitCount > 1 && firstIsZero)
                throw new FormatException("Leading zeros are not allowed");
            if (digitCount == 0 && di > 0)
                throw new FormatException($"Expected digit at offset {BytesConsumed}");
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'.')
            {
                isFloat = true;
                buf[di++] = (byte)'.';
                _seqReader.Advance(1);
                while (
                    !_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex])
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
            }
            if (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] is (byte)'e' or (byte)'E'
            )
            {
                isFloat = true;
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] is (byte)'+' or (byte)'-'
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                while (
                    !_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex])
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
            }
            _valueSpan = buf.AsSpan(0, di);
            _tokenValueStart = 0;
            _tokenValueEnd = di;
            if (isFloat)
                _tokenType = TokenType.Float64;
            else if (Utf8Parser.TryParse(_valueSpan, out int _, out _))
                _tokenType = TokenType.Int32;
            else
                _tokenType = TokenType.Int64;
        }
        catch
        {
            ReturnBuffer();
            throw;
        }
    }

    // ── Literal reading ──

    private bool ReadLiteral(ReadOnlySpan<byte> expected, TokenType token)
    {
        if (_isSequence)
            return ReadLiteralSeq(expected, token);
        return ReadLiteralSpan(expected, token);
    }

    private bool ReadLiteralSpan(ReadOnlySpan<byte> expected, TokenType token)
    {
        if (_position + expected.Length > _data.Length)
            throw new FormatException($"Unexpected EOF at offset {_position}");
        if (!_data.Slice(_position, expected.Length).SequenceEqual(expected))
            throw new FormatException($"Invalid literal at offset {_position}");
        _valueSpan = _data.Slice(_position, expected.Length);
        _tokenValueStart = _position;
        _tokenType = token;
        _position += expected.Length;
        _tokenValueEnd = _position;
        return true;
    }

    private bool ReadLiteralSeq(ReadOnlySpan<byte> expected, TokenType token)
    {
        var buf = ArrayPool<byte>.Shared.Rent(expected.Length);
        TrackBuffer(buf);
        try
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (
                    _seqReader.End
                    || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != expected[i]
                )
                    throw new FormatException($"Invalid literal at offset {_seqReader.Consumed}");
                buf[i] = expected[i];
                _seqReader.Advance(1);
            }
            _valueSpan = buf.AsSpan(0, expected.Length);
            _tokenType = token;
            return true;
        }
        catch
        {
            ReturnBuffer();
            throw;
        }
    }

    // ── Unicode escape (span path) ──

    private int ReadUnicodeEscapeSpan(byte[] decoded, ref int di, int si)
    {
        if (si + 4 >= _valueSpan.Length)
            throw new FormatException($"Incomplete unicode escape at offset {_position}");

        int codepoint = 0;
        for (int j = 0; j < 4; j++)
        {
            si++;
            var hex = _valueSpan[si];
            codepoint <<= 4;
            if (hex is >= (byte)'0' and <= (byte)'9')
                codepoint |= hex - (byte)'0';
            else if (hex is >= (byte)'A' and <= (byte)'F')
                codepoint |= hex - (byte)'A' + 10;
            else if (hex is >= (byte)'a' and <= (byte)'f')
                codepoint |= hex - (byte)'a' + 10;
            else
                throw new FormatException(
                    $"Invalid unicode escape character '{(char)hex}' at offset {_position}"
                );
        }

        if (codepoint is >= 0xD800 and <= 0xDBFF)
        {
            if (
                si + 6 >= _valueSpan.Length
                || _valueSpan[si + 1] != (byte)'\\'
                || _valueSpan[si + 2] != (byte)'u'
            )
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_position}"
                );

            si += 2;
            int lowSurrogate = 0;
            for (int j = 0; j < 4; j++)
            {
                si++;
                var hex = _valueSpan[si];
                lowSurrogate <<= 4;
                if (hex is >= (byte)'0' and <= (byte)'9')
                    lowSurrogate |= hex - (byte)'0';
                else if (hex is >= (byte)'A' and <= (byte)'F')
                    lowSurrogate |= hex - (byte)'A' + 10;
                else if (hex is >= (byte)'a' and <= (byte)'f')
                    lowSurrogate |= hex - (byte)'a' + 10;
                else
                    throw new FormatException(
                        $"Invalid unicode escape character '{(char)hex}' at offset {_position}"
                    );
            }

            if (lowSurrogate is < 0xDC00 or > 0xDFFF)
                throw new FormatException(
                    $"Invalid low surrogate U+{lowSurrogate:X4} at offset {_position}"
                );

            codepoint = 0x10000 + ((codepoint - 0xD800) << 10) + (lowSurrogate - 0xDC00);
        }
        else if (codepoint is >= 0xDC00 and <= 0xDFFF)
        {
            throw new FormatException($"Lone low surrogate U+{codepoint:X4} at offset {_position}");
        }

        if (codepoint < 0x80)
        {
            decoded[di++] = (byte)codepoint;
        }
        else if (codepoint < 0x800)
        {
            decoded[di++] = (byte)(0xC0 | (codepoint >> 6));
            decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else if (codepoint < 0x10000)
        {
            decoded[di++] = (byte)(0xE0 | (codepoint >> 12));
            decoded[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else
        {
            decoded[di++] = (byte)(0xF0 | (codepoint >> 18));
            decoded[di++] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
            decoded[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }

        return si;
    }

    private void ReturnBuffer()
    {
        ReturnBuf(ref _rb0);
        ReturnBuf(ref _rb1);
        ReturnBuf(ref _rb2);
        ReturnBuf(ref _rb3);
        ReturnBuf(ref _rb4);
        ReturnBuf(ref _rb5);
        ReturnBuf(ref _rb6);
        ReturnBuf(ref _rb7);
        _bufCount = 0;
        _rentedBuffer = null;
    }

    private void ReturnBuf(ref byte[]? buf)
    {
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
            buf = null;
            LeakedBufferCount--;
        }
    }

    private void TrackBuffer(byte[] buf)
    {
        LeakedBufferCount++;
        if (_bufCount > PeakTrackedBufferCount)
            PeakTrackedBufferCount = _bufCount;
        switch (_bufCount++)
        {
            case 0:
                _rb0 = buf;
                break;
            case 1:
                _rb1 = buf;
                break;
            case 2:
                _rb2 = buf;
                break;
            case 3:
                _rb3 = buf;
                break;
            case 4:
                _rb4 = buf;
                break;
            case 5:
                _rb5 = buf;
                break;
            case 6:
                _rb6 = buf;
                break;
            default:
                ReturnBuf(ref _rb7);
                _rb7 = buf;
                break;
        }
        _rentedBuffer = buf;
    }

    public void Dispose()
    {
        ReturnBuffer();
    }

    // ── Fast path array reading (used by source generators) ──

    /// <summary>Fast path: read int32 array from raw buffer (span mode only). Returns count read, or 0 to fallback.</summary>
    public int TryReadInt32ArrayFast(scoped Span<int> dest)
    {
        if (_isSequence)
            return 0;
        var p = _position;
        var d = _data;
        var len = d.Length;
        if (p >= len)
            return 0;

        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32)
            {
                p = SimdHelpers.SkipWhitespace(d, p);
                continue;
            }
            if (b == (byte)']')
            {
                p++;
                break;
            }
            if (b == (byte)',')
            {
                p++;
                continue;
            }
            bool neg = false;
            if (b == (byte)'-')
            {
                neg = true;
                p++;
                if (p >= len)
                    return 0;
                b = d[p];
            }
            if (b < (byte)'0' || b > (byte)'9')
                return 0;
            int v = 0;
            do
            {
                int digit = b - (byte)'0';
                if (v > (int.MaxValue - digit) / 10)
                    return 0; // overflow — fall back to the validated reader
                v = v * 10 + digit;
                p++;
                if (p >= len)
                    break;
                b = d[p];
            } while (b >= (byte)'0' && b <= (byte)'9');
            dest[count++] = neg ? -v : v;
        }
        _position = p;
        return count;
    }

    /// <summary>Fast path: read int64 array from raw buffer (span mode only).</summary>
    public int TryReadInt64ArrayFast(scoped Span<long> dest)
    {
        if (_isSequence)
            return 0;
        var p = _position;
        var d = _data;
        var len = d.Length;
        if (p >= len)
            return 0;

        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32)
            {
                p = SimdHelpers.SkipWhitespace(d, p);
                continue;
            }
            if (b == (byte)']')
            {
                p++;
                break;
            }
            if (b == (byte)',')
            {
                p++;
                continue;
            }
            bool neg = false;
            if (b == (byte)'-')
            {
                neg = true;
                p++;
                if (p >= len)
                    return 0;
                b = d[p];
            }
            if (b < (byte)'0' || b > (byte)'9')
                return 0;
            long v = 0;
            do
            {
                long digit = b - (byte)'0';
                if (v > (long.MaxValue - digit) / 10)
                    return 0; // overflow — fall back to the validated reader
                v = v * 10 + digit;
                p++;
                if (p >= len)
                    break;
                b = d[p];
            } while (b >= (byte)'0' && b <= (byte)'9');
            dest[count++] = neg ? -v : v;
        }
        _position = p;
        return count;
    }

    /// <summary>Fast path: read bool array from raw buffer (span mode only).</summary>
    public int TryReadBoolArrayFast(scoped Span<bool> dest)
    {
        if (_isSequence)
            return 0;
        var p = _position;
        var d = _data;
        var len = d.Length;
        if (p >= len)
            return 0;
        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32)
            {
                p = SimdHelpers.SkipWhitespace(d, p);
                continue;
            }
            if (b == (byte)']')
            {
                p++;
                break;
            }
            if (b == (byte)',')
            {
                p++;
                continue;
            }
            if (b == (byte)'t')
            {
                if (p + 3 >= len || d[p + 1] != 'r' || d[p + 2] != 'u' || d[p + 3] != 'e')
                    return 0;
                p += 4;
                dest[count++] = true;
            }
            else if (b == (byte)'f')
            {
                if (
                    p + 4 >= len
                    || d[p + 1] != 'a'
                    || d[p + 2] != 'l'
                    || d[p + 3] != 's'
                    || d[p + 4] != 'e'
                )
                    return 0;
                p += 5;
                dest[count++] = false;
            }
            else
                return 0;
        }
        _position = p;
        return count;
    }
}
