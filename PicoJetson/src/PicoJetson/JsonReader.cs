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

public ref struct JsonReader : ITokenReader, ITransactionalTokenReader, IFusedTokenReader
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

    // ── Rented buffer ownership (C-05) ──
    // A buffer rented from the pool must stay alive while a span still points into
    // it, and no longer. Three slots hold every live buffer:
    //   _spanBuffer   owns the span published by the latest Read();
    //   _lastBuffer   owns the span published by the previous Read() (consumer code
    //                 keeps a property-name span while it reads the value token, so
    //                 the superseded span must survive exactly one more Read());
    //   _rentedBuffer owns the buffer being filled for the token in progress.
    // Steady state is therefore two live buffers. The previous model kept an
    // 8-slot rolling array whose evicted buffers were never returned at all.
    private byte[]? _rentedBuffer;
    private byte[]? _spanBuffer;
    private byte[]? _lastBuffer;

    // Streaming support: isFinalBlock tells the reader whether more data may arrive.
    // When false, IsAtEnd() sets _needsMoreData instead of signaling end-of-document.
    private readonly bool _isFinalBlock;
    private bool _needsMoreData;

    // Set when a read failed at the buffer end (sequence, non-final). Cleared by
    // RewindTo or by constructing a reader for the next chunk.
    private bool _incomplete;

    // True once any token was produced (drives the streaming resume decision).
    private bool _tokenProduced;

    // Start offset of the token most recently produced by Read().
    private long _tokenStart;

    // Transactional storage: parser state snapshot for Mark/RewindToMark and
    // the internal per-Read transaction. Token spans are cleared on rollback
    // (see ITransactionalTokenReader).
    private ParserMark _mark;

    private struct ParserMark
    {
        public int Position;
        public long SeqConsumed;
        public TokenType TokenType;
        public int Depth;
        public int TokenValueStart;
        public int TokenValueEnd;
        public long PropertyNameStart;
        public bool Rewound;
    }

    private void SaveMark(ref ParserMark m)
    {
        m.Position = _position;
        m.SeqConsumed = _seqReader.Consumed;
        m.TokenType = _tokenType;
        m.Depth = _depth;
        m.TokenValueStart = _tokenValueStart;
        m.TokenValueEnd = _tokenValueEnd;
        m.PropertyNameStart = _propertyNameStart;
        m.Rewound = Rewound;
    }

    private void RestoreMark(in ParserMark m)
    {
        _position = m.Position;
        _seqReader = new SequenceReader<byte>(_seqReader.Sequence);
        _seqReader.Advance(m.SeqConsumed);
        _tokenType = m.TokenType;
        _depth = m.Depth;
        _tokenValueStart = m.TokenValueStart;
        _tokenValueEnd = m.TokenValueEnd;
        _propertyNameStart = m.PropertyNameStart;
        Rewound = m.Rewound;
        _valueSpan = default;
        _needsMoreData = false;
        // A restored reader must be readable again (the sticky incomplete flag
        // belongs to the failed attempt, not to the restored state).
        _incomplete = false;
    }

    /// <inheritdoc />
    public long TokenStart => _tokenStart;

    /// <inheritdoc />
    public void Mark() => SaveMark(ref _mark);

    /// <inheritdoc />
    public void RewindToMark() => RestoreMark(in _mark);

    // One-time UTF-8 BOM resolution (sequence mode). Span mode resolves it in
    // the constructor and sets this to true.
    private bool _bomChecked;

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
            // Only count bytes once a token was produced: a chunk that only
            // advanced past the BOM/whitespace must not look like a resumed
            // document (the generated ReadStart guard would skip the root token).
            BytesConsumed = _tokenProduced ? _seqReader.Consumed : 0,
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
        _incomplete = false;
        IsResumed = state.BytesConsumed > 0 || state.Depth > 0;
        _tokenProduced = IsResumed;
        StreamState = state.Aux;
        Rewound = state.Rewound;
        // Only a genuinely resumed reader has already passed the BOM; the very
        // first chunk arrives with a default state (BytesConsumed == 0, Depth == 0).
        _bomChecked = state.BytesConsumed > 0 || state.Depth > 0;
    }

    public JsonReader(
        ReadOnlySpan<byte> data,
        int maxDepth = 256,
        bool isFinalBlock = true,
        JsonOptions? options = null
    )
    {
        _data = TextHelpers.SkipBom(data);
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _bomChecked = true;
        _isFinalBlock = isFinalBlock;
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = maxDepth;
        _options = options;
        _valueSpan = default;
        _rentedBuffer = null;
        _spanBuffer = null;
        _lastBuffer = null;
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
        _bomChecked = false;
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = maxDepth;
        _valueSpan = default;
        _rentedBuffer = null;
        _spanBuffer = null;
        _lastBuffer = null;
    }

    public TokenType TokenType => _tokenType;
    public int Depth => _depth;

    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    public ReadOnlySpan<byte> ValueSpan => _valueSpan;

    /// <summary>For testing: number of rented buffers still live (0 after Dispose).</summary>
    internal int TrackedBufferCount => LiveBufferCount();

    /// <summary>
    /// For testing: peak number of rented buffers alive at the same time.
    /// Since C-05 this counts <em>live</em> buffers (rented and not yet returned)
    /// instead of filled slots, so the expected bound is 2 — not 8.
    /// </summary>
    internal int PeakTrackedBufferCount { get; private set; }

    /// <summary>
    /// For testing: total TrackBuffer calls minus total ReturnBuf calls.
    /// Should be 0 after Dispose; > 0 indicates a buffer leak.
    /// </summary>
    internal int LeakedBufferCount { get; private set; }

    /// <summary>For testing: every buffer handed to the pool.</summary>
    internal int TotalPoolReturns { get; private set; }

    /// <summary>For testing: every buffer tracked for return at Dispose.</summary>
    internal int TotalTrackedBuffers { get; private set; }

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

    /// <summary>
    /// Rewinds the reader to an earlier position within the same buffer
    /// (the value previously returned by <see cref="BytesConsumed"/>).
    /// Used by generated streaming delegates to re-read a unit (property,
    /// array element) that could not be completed at the buffer end.
    /// </summary>
    public void RewindTo(long consumedOffset)
    {
        if (consumedOffset < 0 || consumedOffset > BytesConsumed)
            throw new ArgumentOutOfRangeException(nameof(consumedOffset));
        if (_isSequence)
            _seqReader.Rewind(_seqReader.Consumed - consumedOffset);
        else
            _position = (int)consumedOffset;
        _incomplete = false;
        Rewound = true;
    }

    public bool Read()
    {
        // Sticky incomplete: once a read fails at the buffer end (sequence mode,
        // non-final block), every further read on this reader instance keeps
        // returning false until the caller rewinds (RewindTo) or a new reader is
        // created for the next chunk. Inner loops therefore exit immediately
        // instead of parsing tokens past the incomplete one.
        if (_incomplete)
        {
            _needsMoreData = true;
            return false;
        }
        _tokenStart = BytesConsumed;
        bool ok = ReadCore();
        if (ok)
            _tokenProduced = true;
        if (!ok && _needsMoreData)
            _incomplete = true;
        // End of document (not a chunk boundary): no consumer can observe the
        // retained token spans any more, so hand them back to the pool now
        // instead of holding them until Dispose.
        if (!ok && !_needsMoreData)
        {
            ReturnBuffer();
            // The pooled arrays are gone: any span still pointing into them
            // (ValueSpan / GetStringRaw) would alias memory the pool may
            // already have handed to the next renter. Fail closed instead.
            _valueSpan = default;
            _tokenValueStart = 0;
            _tokenValueEnd = 0;
        }
        return ok;
    }

    private bool ReadCore()
    {
        _needsMoreData = false;
        Rewound = false;
        if (!_bomChecked)
        {
            if (_isSequence)
            {
                TextHelpers.SkipBomSeq(ref _seqReader, _isFinalBlock, out var bomNeedsMore);
                if (bomNeedsMore)
                {
                    _needsMoreData = true;
                    return false;
                }
            }
            _bomChecked = true;
        }
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
            // A comma may only follow a value or a closing bracket. A comma
            // right after the document start or an opening bracket is a
            // leading comma and is invalid JSON (review P2-5b).
            if (
                !IsResumed
                && _tokenType is TokenType.None or TokenType.ObjectStart or TokenType.ArrayStart
            )
                throw new FormatException($"Unexpected comma at offset {BytesConsumed}");
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
                if (_isFinalBlock || !_isSequence || HasCompleteLiteral("true"u8))
                    return ReadLiteral("true"u8, TokenType.Bool);
                RewindToPropertyName();
                _needsMoreData = true;
                return false;
            case (byte)'f':
                if (!_isFinalBlock && _isSequence && !HasCompleteLiteral("false"u8))
                {
                    RewindToPropertyName();
                    _needsMoreData = true;
                    return false;
                }
                return ReadLiteral("false"u8, TokenType.Bool);
            case (byte)'n':
                if (_isFinalBlock || !_isSequence || HasCompleteLiteral("null"u8))
                    return ReadLiteral("null"u8, TokenType.Null);
                RewindToPropertyName();
                _needsMoreData = true;
                return false;
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
                    _options?.NumberHandling == JsonNumberHandling.AllowNamedFloatingPointLiterals
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
                if (_isFinalBlock || !_isSequence || HasCompleteNumber())
                    return ReadNumber();
                RewindToPropertyName();
                _needsMoreData = true;
                return false;
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
        switch (_tokenType)
        {
            // Container token: skip until the matching end marker.
            case TokenType.ObjectStart or TokenType.ArrayStart:
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
            case TokenType.PropertyName:
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
            default:
                // Scalar token: the value is already fully consumed — nothing to skip.
                return true;
        }
    }

    /// <summary>
    /// Returns the decoded UTF-8 bytes of the current string or property name token.
    /// Escape sequences (\n, \r, \t, \\, \", \uXXXX) are resolved.
    /// </summary>
    public ReadOnlySpan<byte> GetStringRaw() => _valueSpan;

    public bool TryGetInt32(out int v)
    {
        switch (_tokenType)
        {
            case TokenType.Int32:
                return Utf8Parser.TryParse(_valueSpan, out v, out _);
            case TokenType.Int64
                when Utf8Parser.TryParse(_valueSpan, out long lv, out _)
                    && lv is >= int.MinValue and <= int.MaxValue:
                v = (int)lv;
                return true;
            default:
                v = 0;
                return false;
        }
    }

    public bool TryReadNextInt32(out int v)
    {
        return _isSequence ? TryReadNextInt32Seq(out v) : TryReadNextInt32Span(out v);
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType is TokenType.Int32 or TokenType.Int64)
            return Utf8Parser.TryParse(_valueSpan, out v, out _);
        v = 0;
        return false;
    }

    public bool TryGetUInt64(out ulong v)
    {
        if (_tokenType is TokenType.Int32 or TokenType.Int64 or TokenType.UInt64)
            return Utf8Parser.TryParse(_valueSpan, out v, out _);
        v = 0;
        return false;
    }

    public bool TryGetFloat64(out double v)
    {
        if (
            _tokenType
            is not (TokenType.Float64 or TokenType.Int32 or TokenType.Int64 or TokenType.UInt64)
        )
        {
            v = 0;
            return false;
        }

        switch (_valueSpan.Length)
        {
            // Handle NaN/Infinity literals (AllowNamedFloatingPointLiterals)
            case 3
                when _valueSpan[0] == (byte)'N'
                    && _valueSpan[1] == (byte)'a'
                    && _valueSpan[2] == (byte)'N':
                v = double.NaN;
                return true;
            case 8
                when _valueSpan[0] == (byte)'I'
                    && _valueSpan[1] == (byte)'n'
                    && _valueSpan[2] == (byte)'f':
                v = double.PositiveInfinity;
                return true;
            case 9
                when _valueSpan[0] == (byte)'-'
                    && _valueSpan[1] == (byte)'I'
                    && _valueSpan[2] == (byte)'n':
                v = double.NegativeInfinity;
                return true;
            default:
                return Utf8Parser.TryParse(_valueSpan, out v, out _);
        }
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

    /// <summary>
    /// JSON carries no element count in its wire form (spec §3.3): the reader finds the matching
    /// <c>]</c>/<c>}</c> by scanning bytes and keeps no element counter, so there is nothing to report.
    /// Declared, not hinted — with <see langword="false"/> the contract (§3.1) requires
    /// <see cref="TryGetCollectionCount"/> to answer <see langword="false"/> for every token, and consumers
    /// terminate their loops with <see cref="ITokenReader.Read"/> plus the end token.
    /// </summary>
    public readonly bool HasCountedCollections => false;

    /// <summary>
    /// Always <see langword="false"/>: JSON has no count to report (see <see cref="HasCountedCollections"/>).
    /// A query, not a read (spec §3.1): no position move, no token, no state change.
    /// </summary>
    public readonly bool TryGetCollectionCount(out int count)
    {
        count = 0;
        return false;
    }

    /// <summary>
    /// Task T1b forwards this to the reference semantics (<see cref="TokenReaderFused"/>): the exact
    /// <c>Read() &amp;&amp; TryGetX(out value)</c> combination of spec §3.2.1, written once so it cannot drift
    /// per format. Not <c>readonly</c> — inside a <c>readonly</c> member <c>this</c> is a read-only variable and
    /// <c>ref this</c> is <c>CS1605</c> (spec §3.1 last paragraph).
    /// <para>
    /// JSON optimization is task T14's: spec §3.3 marks only <see cref="TryReadInt32"/> for an override.
    /// </para>
    /// </summary>
    public bool TryReadInt32(out int value) => TokenReaderFused.TryReadInt32(ref this, out value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="long"/>.</summary>
    public bool TryReadInt64(out long value) => TokenReaderFused.TryReadInt64(ref this, out value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="double"/>.</summary>
    public bool TryReadFloat64(out double value) =>
        TokenReaderFused.TryReadFloat64(ref this, out value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="bool"/>.</summary>
    public bool TryReadBool(out bool value) => TokenReaderFused.TryReadBool(ref this, out value);

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
            if (result <= int.MaxValue || (neg && result == (long)int.MaxValue + 1))
                continue;
            v = 0;
            return false;
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
        if (!_isSequence)
            return _data[_position..].StartsWith(expected);
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
        if (_tokenType != TokenType.PropertyName || _propertyNameStart < 0)
            return;
        _seqReader.Rewind(_seqReader.Consumed - _propertyNameStart);
        Rewound = true;
    }

    /// <summary>True when the full literal text is present in the current sequence
    /// and terminated by a delimiter or buffer end (streaming completeness check).</summary>
    private bool HasCompleteLiteral(ReadOnlySpan<byte> lit)
    {
        var seq = _seqReader.Sequence.Slice(_seqReader.Position);
        var r = new SequenceReader<byte>(seq);
        if (r.Remaining < lit.Length)
            return false;
        foreach (var t in lit)
        {
            if (r.CurrentSpan[r.CurrentSpanIndex] != t)
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
            if (
                b
                is not (
                    (byte)'-'
                    or (byte)'+'
                    or (byte)'.'
                    or (byte)'e'
                    or (byte)'E'
                    or >= (byte)'0'
                    and <= (byte)'9'
                )
            )
                return b
                    is (byte)' '
                        or (byte)'\t'
                        or (byte)'\n'
                        or (byte)'\r'
                        or (byte)','
                        or (byte)'}'
                        or (byte)']';
            r.Advance(1);
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
        if (r.CurrentSpan[r.CurrentSpanIndex] != (byte)':')
            return true;
        r.Advance(1);
        while (!r.End && (r.CurrentSpan[r.CurrentSpanIndex] is (byte)' ' or (byte)'\t'))
            r.Advance(1);
        return !r.End;
    }

    private bool ReadStringOrProperty()
    {
        return _isSequence ? ReadStringOrPropertySeq() : ReadStringOrPropertySpan();
    }

    private bool ReadStringOrPropertySpan()
    {
        _position++;
        var start = _position;
        var sawEscape = ScanStringTokenSpan();
        if (_position >= _data.Length)
            throw new FormatException($"Unterminated string at offset {_position}");

        // No backslash anywhere in the token => the raw bytes ARE the value, and the
        // span is a slice of the input document: zero copies, zero allocations, and
        // no second sweep for escapes. Tokens with escapes keep the previous path
        // (scan + UnescapeIfNeeded, byte-for-byte unchanged).
        _valueSpan = _data[start.._position];
        _tokenValueStart = start;
        _tokenValueEnd = _position;
        _position++;
        if (sawEscape)
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

    /// <summary>
    /// Advances <c>_position</c> to the closing quote of the string token that
    /// starts there (or to the end of the data when the token is unterminated).
    /// Returns <c>true</c> when the token contains a backslash, i.e. when the raw
    /// span needs unescaping.
    /// </summary>
    /// <remarks>
    /// Fast path: one vectorized sweep over the run of plain bytes up to the next
    /// byte that can end it (a quote, a backslash or a raw control byte below 0x20). A 1 KB
    /// escape-free string therefore costs one SIMD pass instead of 1024 scalar
    /// steps, and the separate <see cref="ContainsBackslash"/> pre-check inside
    /// <see cref="UnescapeIfNeeded"/> is skipped entirely (the sweep already proved
    /// there is no backslash).
    /// Fallbacks — all byte-for-byte the previous behavior:
    /// <list type="bullet">
    /// <item>a backslash hands the rest of the token to the original byte-at-a-time
    /// scan (<see cref="ScanStringTokenSpanScalar"/>), which skips escaped bytes;</item>
    /// <item>hardware without SIMD support, and any tail shorter than one vector,
    /// use that same original scan;</item>
    /// <item>a raw control byte is consumed one byte at a time (the reader never
    /// rejected bare control bytes, and still does not).</item>
    /// </list>
    /// </remarks>
    private bool ScanStringTokenSpan()
    {
        while (_position < _data.Length)
        {
            var rel = IndexOfQuoteEscapeOrControl(_data[_position..]);
            if (rel < 0)
            {
                _position = _data.Length;
                return false;
            }
            var idx = _position + rel;
            var b = _data[idx];
            if (b == (byte)'"')
            {
                _position = idx;
                return false;
            }
            if (b == (byte)'\\')
            {
                _position = idx;
                ScanStringTokenSpanScalar();
                return true;
            }
            // Raw control byte: keep the original per-byte progress.
            _position = idx + 1;
        }
        return false;
    }

    /// <summary>
    /// Original byte-at-a-time string scan — the non-vectorized fallback. Stops on
    /// the closing quote, or at the end of the data for an unterminated token.
    /// </summary>
    private void ScanStringTokenSpanScalar()
    {
        while (_position < _data.Length)
        {
            if (_data[_position] == (byte)'"')
                break;
            if (_data[_position] == (byte)'\\' && _position + 1 < _data.Length)
                _position++;
            _position++;
        }
    }

    /// <summary>
    /// Index of the first byte that ends a plain string run: a quote,
    /// a backslash (escape) or anything below 0x20 (a raw control byte). Returns -1 when
    /// whole span is plain.
    /// </summary>
    /// <remarks>
    /// Vectorized at 64 / 32 / 16 bytes per step; those gates are JIT-time
    /// constants, so an unsupported width costs nothing. The scalar loop is the
    /// genuine fallback: it covers the tail shorter than one vector, and every byte
    /// on hardware without SIMD support.
    /// </remarks>
    internal static int IndexOfQuoteEscapeOrControl(ReadOnlySpan<byte> span)
    {
        ref var ptr = ref MemoryMarshal.GetReference(span);
        var i = 0;

        if (Vector512.IsHardwareAccelerated)
        {
            var quote = Vector512.Create((byte)'"');
            var escape = Vector512.Create((byte)'\\');
            var control = Vector512.Create((byte)0x20);
            for (; i + 64 <= span.Length; i += 64)
            {
                var chunk = Vector512.LoadUnsafe(ref Unsafe.Add(ref ptr, i));
                var hit =
                    Vector512.Equals(chunk, quote)
                    | Vector512.Equals(chunk, escape)
                    | Vector512.LessThan(chunk, control);
                var bits = hit.ExtractMostSignificantBits();
                if (bits != 0)
                    return i + BitOperations.TrailingZeroCount(bits);
            }
        }

        if (Vector256.IsHardwareAccelerated)
        {
            var quote = Vector256.Create((byte)'"');
            var escape = Vector256.Create((byte)'\\');
            var control = Vector256.Create((byte)0x20);
            for (; i + 32 <= span.Length; i += 32)
            {
                var chunk = Vector256.LoadUnsafe(ref Unsafe.Add(ref ptr, i));
                var hit =
                    Vector256.Equals(chunk, quote)
                    | Vector256.Equals(chunk, escape)
                    | Vector256.LessThan(chunk, control);
                var bits = hit.ExtractMostSignificantBits();
                if (bits != 0)
                    return i + BitOperations.TrailingZeroCount(bits);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var quote = Vector128.Create((byte)'"');
            var escape = Vector128.Create((byte)'\\');
            var control = Vector128.Create((byte)0x20);
            for (; i + 16 <= span.Length; i += 16)
            {
                var chunk = Vector128.LoadUnsafe(ref Unsafe.Add(ref ptr, i));
                var hit =
                    Vector128.Equals(chunk, quote)
                    | Vector128.Equals(chunk, escape)
                    | Vector128.LessThan(chunk, control);
                var bits = hit.ExtractMostSignificantBits();
                if (bits != 0)
                    return i + BitOperations.TrailingZeroCount(bits);
            }
        }

        for (; i < span.Length; i++)
        {
            if (span[i] is (byte)'"' or (byte)'\\' or < 0x20)
                return i;
        }
        return -1;
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
                // Fast path: copy the plain run of bytes up to the next byte that can
                // end it (a quote, a backslash or a raw control byte) in one bulk copy
                // instead of one array store per byte. A long escape-free string
                // therefore costs one vector scan + one memcpy per segment; escape
                // handling below stays byte-for-byte what it was.
                var unread = _seqReader.UnreadSpan;
                var plain = unread.IsEmpty ? -1 : IndexOfQuoteEscapeOrControl(unread);
                if (plain < 0)
                    plain = unread.Length;
                if (plain > 0)
                {
                    EnsureSeqCapacity(ref buf, di + plain, di);
                    unread[..plain].CopyTo(buf.AsSpan(di));
                    di += plain;
                    _seqReader.Advance(plain);
                    if (plain == unread.Length)
                        continue; // segment exhausted — let the loop re-check End
                }
                if (_seqReader.End)
                    break;

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
                    EnsureSeqCapacity(ref buf, di + 4, di);
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
                        case (byte)'b':
                            buf[di++] = (byte)'\b';
                            break;
                        case (byte)'f':
                            buf[di++] = (byte)'\f';
                            break;
                        case (byte)'u':
                            di = ReadUnicodeEscapeSeq(buf, di);
                            break;
                        default:
                            throw new FormatException(
                                $"Invalid escape character '{(char)b}' at offset {_seqReader.Consumed - 1}"
                            );
                    }
                }
                else
                {
                    EnsureSeqCapacity(ref buf, di + 1, di);
                    buf[di++] = b;
                    _seqReader.Advance(1);
                }
            }

            OwnSpan(buf);
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

    /// <summary>
    /// Makes sure the sequence-mode scratch buffer can hold <paramref name="needed"/>
    /// bytes, growing geometrically — at least doubling, and never below the caller's
    /// requirement, so a large segment does not trigger one regrow per segment.
    /// <paramref name="valid"/> bytes are carried over; the superseded buffer is
    /// recycled by <see cref="TrackBuffer"/>.
    /// </summary>
    private void EnsureSeqCapacity(ref byte[] buf, int needed, int valid)
    {
        if (needed <= buf.Length)
            return;
        var newBuf = ArrayPool<byte>.Shared.Rent(Math.Max(buf.Length * 2, needed));
        buf.AsSpan(0, valid).CopyTo(newBuf);
        buf = newBuf;
        TrackBuffer(buf);
    }

    private int ReadUnicodeEscapeSeq(byte[] buf, int di)
    {
        int codepoint = 0;
        for (var j = 0; j < 4; j++)
        {
            if (_seqReader.End)
                throw new FormatException(
                    $"Incomplete unicode escape at offset {_seqReader.Consumed}"
                );
            var hex = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
            codepoint <<= 4;
            codepoint |= hex switch
            {
                >= (byte)'0' and <= (byte)'9' => hex - (byte)'0',
                >= (byte)'A' and <= (byte)'F' => hex - (byte)'A' + 10,
                >= (byte)'a' and <= (byte)'f' => hex - (byte)'a' + 10,
                _ => throw new FormatException(
                    $"Invalid unicode escape character '{(char)hex}' at offset {_seqReader.Consumed}"
                ),
            };
        }

        switch (codepoint)
        {
            case >= 0xD800
            and <= 0xDBFF
                when _seqReader.End
                    || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\\':
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
                );
            case >= 0xD800 and <= 0xDBFF:
            {
                _seqReader.Advance(1);
                if (
                    _seqReader.End
                    || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'u'
                )
                    throw new FormatException(
                        $"Lone high surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
                    );
                _seqReader.Advance(1);

                var lowSurrogate = 0;
                for (var j = 0; j < 4; j++)
                {
                    if (_seqReader.End)
                        throw new FormatException(
                            $"Incomplete unicode escape at offset {_seqReader.Consumed}"
                        );
                    var hex = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                    lowSurrogate <<= 4;
                    lowSurrogate |= hex switch
                    {
                        >= (byte)'0' and <= (byte)'9' => hex - (byte)'0',
                        >= (byte)'A' and <= (byte)'F' => hex - (byte)'A' + 10,
                        >= (byte)'a' and <= (byte)'f' => hex - (byte)'a' + 10,
                        _ => throw new FormatException(
                            $"Invalid unicode escape character '{(char)hex}' at offset {_seqReader.Consumed}"
                        ),
                    };
                }

                if (lowSurrogate is < 0xDC00 or > 0xDFFF)
                    throw new FormatException(
                        $"Invalid low surrogate U+{lowSurrogate:X4} at offset {_seqReader.Consumed}"
                    );

                codepoint = 0x10000 + ((codepoint - 0xD800) << 10) + (lowSurrogate - 0xDC00);
                break;
            }
            case >= 0xDC00 and <= 0xDFFF:
                throw new FormatException(
                    $"Lone low surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
                );
        }

        switch (codepoint)
        {
            case < 0x80:
                buf[di++] = (byte)codepoint;
                break;
            case < 0x800:
                buf[di++] = (byte)(0xC0 | (codepoint >> 6));
                buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
                break;
            case < 0x10000:
                buf[di++] = (byte)(0xE0 | (codepoint >> 12));
                buf[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
                buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
                break;
            default:
                buf[di++] = (byte)(0xF0 | (codepoint >> 18));
                buf[di++] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
                buf[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
                buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
                break;
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
        if (!ContainsBackslash(_valueSpan))
            return;
        var decoded = ArrayPool<byte>.Shared.Rent(_valueSpan.Length);
        TrackBuffer(decoded);
        try
        {
            var di = 0;
            for (var si = 0; si < _valueSpan.Length; si++)
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
                        case (byte)'b':
                            decoded[di++] = (byte)'\b';
                            break;
                        case (byte)'f':
                            decoded[di++] = (byte)'\f';
                            break;
                        case (byte)'u':
                            si = ReadUnicodeEscapeSpan(decoded, ref di, si);
                            break;
                        default:
                            throw new FormatException(
                                $"Invalid escape character '{(char)_valueSpan[si]}' at offset {si}"
                            );
                    }
                }
                else
                    decoded[di++] = _valueSpan[si];
            }
            OwnSpan(decoded);
            _valueSpan = decoded.AsSpan(0, di);
        }
        catch
        {
            ReturnBuffer();
            throw;
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
        else if (Utf8Parser.TryParse(_valueSpan, out long _, out _))
            _tokenType = TokenType.Int64;
        else if (Utf8Parser.TryParse(_valueSpan, out ulong _, out _))
            _tokenType = TokenType.UInt64;
        else
            _tokenType = TokenType.Int64;
    }

    private void ReadNumberSeq()
    {
        // BUG-11: measure the token before copying — a JSON number has no
        // length limit and used to overflow a fixed 32-byte buffer. The probe
        // is a copy of the reader position, so it never mutates parser state.
        var probe = new SequenceReader<byte>(_seqReader.Sequence.Slice(_seqReader.Position));
        int measured = 0;
        while (probe.TryPeek(0, out var pb) && IsNumberByte(pb))
        {
            probe.Advance(1);
            measured++;
        }
        var buf = ArrayPool<byte>.Shared.Rent(Math.Max(32, measured + 1));
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
            switch (digitCount)
            {
                case > 1 when firstIsZero:
                    throw new FormatException("Leading zeros are not allowed");
                case 0 when di > 0:
                    throw new FormatException($"Expected digit at offset {BytesConsumed}");
            }

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
            OwnSpan(buf);
            _valueSpan = buf.AsSpan(0, di);
            _tokenValueStart = 0;
            _tokenValueEnd = di;
            if (isFloat)
                _tokenType = TokenType.Float64;
            else if (Utf8Parser.TryParse(_valueSpan, out int _, out _))
                _tokenType = TokenType.Int32;
            else if (Utf8Parser.TryParse(_valueSpan, out long _, out _))
                _tokenType = TokenType.Int64;
            else if (Utf8Parser.TryParse(_valueSpan, out ulong _, out _))
                _tokenType = TokenType.UInt64;
            else
                _tokenType = TokenType.Int64;
        }
        catch
        {
            ReturnBuffer();
            throw;
        }
    }

    private static bool IsNumberByte(byte b) =>
        b
            is >= (byte)'0'
                and <= (byte)'9'
                or (byte)'-'
                or (byte)'+'
                or (byte)'.'
                or (byte)'e'
                or (byte)'E';

    // ── Literal reading ──

    private bool ReadLiteral(ReadOnlySpan<byte> expected, TokenType token)
    {
        return _isSequence ? ReadLiteralSeq(expected, token) : ReadLiteralSpan(expected, token);
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
            OwnSpan(buf);
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

        switch (codepoint)
        {
            case >= 0xD800
            and <= 0xDBFF
                when si + 6 >= _valueSpan.Length
                    || _valueSpan[si + 1] != (byte)'\\'
                    || _valueSpan[si + 2] != (byte)'u':
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_position}"
                );
            case >= 0xD800 and <= 0xDBFF:
            {
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
                break;
            }
            case >= 0xDC00 and <= 0xDFFF:
                throw new FormatException(
                    $"Lone low surrogate U+{codepoint:X4} at offset {_position}"
                );
        }

        switch (codepoint)
        {
            case < 0x80:
                decoded[di++] = (byte)codepoint;
                break;
            case < 0x800:
                decoded[di++] = (byte)(0xC0 | (codepoint >> 6));
                decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
                break;
            case < 0x10000:
                decoded[di++] = (byte)(0xE0 | (codepoint >> 12));
                decoded[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
                decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
                break;
            default:
                decoded[di++] = (byte)(0xF0 | (codepoint >> 18));
                decoded[di++] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
                decoded[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
                decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
                break;
        }

        return si;
    }

    private void ReturnBuffer()
    {
        // A buffer can serve more than one slot (e.g. the same array backs two
        // consecutive spans), so each array is returned at most once.
        var rented = _rentedBuffer;
        var last = _lastBuffer;
        var span = _spanBuffer;
        _rentedBuffer = null;
        _lastBuffer = null;
        _spanBuffer = null;
        if (rented is not null)
            ReturnPooled(rented);
        if (last is not null && !ReferenceEquals(last, rented))
            ReturnPooled(last);
        if (span is not null && !ReferenceEquals(span, rented) && !ReferenceEquals(span, last))
            ReturnPooled(span);
    }

    /// <summary>Single funnel for pool returns so tests can count them.</summary>
    private void ReturnPooled(byte[] buf)
    {
        ArrayPool<byte>.Shared.Return(buf);
        TotalPoolReturns++;
        LeakedBufferCount--;
    }

    private void TrackBuffer(byte[] buf)
    {
        LeakedBufferCount++;
        TotalTrackedBuffers++;
        // A new token is starting: the span published two Read() calls ago can no
        // longer be observed, so recycle its buffer instead of letting it pile up.
        ReleaseLastBuffer(buf);
        // A token that outgrows its buffer copies into a larger one; the
        // superseded buffer holds no span, so recycle it right away.
        if (_rentedBuffer is not null && !ReferenceEquals(_rentedBuffer, buf))
        {
            var stale = _rentedBuffer;
            _rentedBuffer = null;
            ReturnPooled(stale);
        }
        _rentedBuffer = buf;
        UpdatePeak();
    }

    /// <summary>
    /// Publishes <paramref name="buf"/> as the owner of the span returned for the
    /// token in progress. The span of the previous token is rotated into
    /// <see cref="_lastBuffer"/> rather than released: consumer code holds a
    /// property-name span while it reads the value token, so it must survive
    /// exactly one more Read().
    /// </summary>
    private void OwnSpan(byte[] buf)
    {
        ReleaseLastBuffer(buf);
        _lastBuffer = _spanBuffer;
        _spanBuffer = buf;
        _rentedBuffer = null;
        UpdatePeak();
    }

    /// <summary>
    /// Releases the span buffer that just became two generations old — unless it is
    /// the buffer being rented right now (<paramref name="keep"/>) or the current
    /// span owner, both of which are still observable.
    /// </summary>
    private void ReleaseLastBuffer(byte[]? keep)
    {
        var last = _lastBuffer;
        if (last is null)
            return;
        _lastBuffer = null;
        if (ReferenceEquals(last, keep) || ReferenceEquals(last, _spanBuffer))
            return;
        ReturnPooled(last);
    }

    /// <summary>Number of rented buffers that are still observable (0..3, steady state 2).</summary>
    private int LiveBufferCount()
    {
        var live = 0;
        if (_rentedBuffer is not null)
            live++;
        if (_lastBuffer is not null && !ReferenceEquals(_lastBuffer, _rentedBuffer))
            live++;
        if (
            _spanBuffer is not null
            && !ReferenceEquals(_spanBuffer, _rentedBuffer)
            && !ReferenceEquals(_spanBuffer, _lastBuffer)
        )
            live++;
        return live;
    }

    private void UpdatePeak()
    {
        var live = LiveBufferCount();
        if (live > PeakTrackedBufferCount)
            PeakTrackedBufferCount = live;
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
            long v = 0;
            do
            {
                int digit = b - (byte)'0';
                v = v * 10 + digit;
                // -2147483648 is a legal int32: allow one past int.MaxValue
                // when the sign is negative.
                if (v > int.MaxValue && !(neg && v == (long)int.MaxValue + 1))
                    return 0; // overflow — fall back to the validated reader
                p++;
                if (p >= len)
                    break;
                b = d[p];
            } while (b >= (byte)'0' && b <= (byte)'9');
            dest[count++] = (int)(neg ? -v : v);
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
