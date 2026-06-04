namespace PicoYaml;

public ref struct YamlReader
{
    // Span mode fields
    private ReadOnlySpan<byte> _data;
    private int _position;

    // Sequence mode fields
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;

    // Common
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _keySpan;
    private ReadOnlySpan<byte> _valueSpan;
    private int _depth;
    private readonly int _maxDepth;
    private int[] _indentStack;
    private int _stackCount;
    private bool _inFlow;
    private bool _flowStartEmitted;
    private bool _docStartPending;
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

    // Anchor/alias support — inline storage, zero heap allocation
    private const int MaxAnchors = 4;
    private const int MaxAnchorPairs = 8;

    private string? _a0Name,
        _a1Name,
        _a2Name,
        _a3Name;
    private bool _a0IsMapping,
        _a1IsMapping,
        _a2IsMapping,
        _a3IsMapping;
    private byte[]? _a0Scalar,
        _a1Scalar,
        _a2Scalar,
        _a3Scalar;

    // Inline mapping pairs per anchor (max 8 per anchor)
    private byte[]? _a0k0,
        _a0v0,
        _a0k1,
        _a0v1,
        _a0k2,
        _a0v2,
        _a0k3,
        _a0v3,
        _a0k4,
        _a0v4,
        _a0k5,
        _a0v5,
        _a0k6,
        _a0v6,
        _a0k7,
        _a0v7;
    private byte[]? _a1k0,
        _a1v0,
        _a1k1,
        _a1v1,
        _a1k2,
        _a1v2,
        _a1k3,
        _a1v3,
        _a1k4,
        _a1v4,
        _a1k5,
        _a1v5,
        _a1k6,
        _a1v6,
        _a1k7,
        _a1v7;
    private byte[]? _a2k0,
        _a2v0,
        _a2k1,
        _a2v1,
        _a2k2,
        _a2v2,
        _a2k3,
        _a2v3,
        _a2k4,
        _a2v4,
        _a2k5,
        _a2v5,
        _a2k6,
        _a2v6,
        _a2k7,
        _a2v7;
    private byte[]? _a3k0,
        _a3v0,
        _a3k1,
        _a3v1,
        _a3k2,
        _a3v2,
        _a3k3,
        _a3v3,
        _a3k4,
        _a3v4,
        _a3k5,
        _a3v5,
        _a3k6,
        _a3v6,
        _a3k7,
        _a3v7;
    private int _a0pairs,
        _a1pairs,
        _a2pairs,
        _a3pairs;
    private int _anchorCount;
    private string? _pendingAnchorName;
    private string? _pendingMappingAnchor;
    private string? _nextAnchorName;

    // Current mapping accumulator — uses anchor 0's pair slots
    private int _curPairCount;

    // Replay state
    private int _replayAnchorIdx;
    private int _replayIndex;
    private int _replayPairCount;

    // Safety: prevents infinite loops in pathological YAML
    private const int MaxTokens = 100_000;
    private int _tokenCount;

    public YamlReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _depth = 0;
        _indentStack = new int[64];
        _stackCount = 0;
        _inFlow = false;
        _flowStartEmitted = false;
        _docStartPending = false;
        _rentedBuffer = null;
        _rb0 = _rb1 = _rb2 = _rb3 = _rb4 = _rb5 = _rb6 = _rb7 = null;
        _bufCount = 0;
        _maxDepth = 256;
        _tokenCount = 0;
    }

    public YamlReader(ReadOnlySequence<byte> data)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _depth = 0;
        _indentStack = new int[64];
        _stackCount = 0;
        _inFlow = false;
        _flowStartEmitted = false;
        _docStartPending = false;
        _rentedBuffer = null;
        _rb0 = _rb1 = _rb2 = _rb3 = _rb4 = _rb5 = _rb6 = _rb7 = null;
        _bufCount = 0;
        _maxDepth = 256;
        _tokenCount = 0;
    }

    public TokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> KeySpan => _keySpan;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;
    public int Depth => _depth;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    public bool Read()
    {
        if (++_tokenCount > MaxTokens)
            throw new FormatException(
                $"Exceeded maximum token count of {MaxTokens}; likely an infinite loop in YAML parsing"
            );
        if (_replayAnchorIdx >= -1 && _replayIndex < _replayPairCount)
        {
            (byte[] Key, byte[] Value) pair;
            if (_replayAnchorIdx == -1)
                pair = GetAccumulatorPair(_replayIndex++);
            else
                pair = GetAnchorPair(_replayAnchorIdx, _replayIndex++);
            _keySpan = pair.Key;
            _valueSpan = pair.Value;
            _tokenType = TokenType.PropertyName;
            if (_replayIndex >= _replayPairCount)
            {
                _replayAnchorIdx = -1;
                _replayPairCount = 0;
            }
            return true;
        }
        return _isSequence ? ReadSeq() : ReadSpan();
    }

    public bool TryGetInt32(out int v)
    {
        if (_valueSpan.IsEmpty)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    public bool TryGetInt64(out long v)
    {
        if (_valueSpan.IsEmpty)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    public bool TryGetFloat64(out double v)
    {
        if (_valueSpan.IsEmpty)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    public bool TryGetBool(out bool v)
    {
        if (_valueSpan.IsEmpty)
        {
            v = false;
            return false;
        }
        if (_valueSpan.SequenceEqual("true"u8))
        {
            v = true;
            return true;
        }
        if (_valueSpan.SequenceEqual("false"u8))
        {
            v = false;
            return true;
        }
        v = false;
        return false;
    }

    public void Skip()
    {
        if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
        {
            int targetDepth = 1;
            while (Read())
            {
                if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
                    targetDepth++;
                else if (_tokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
                {
                    targetDepth--;
                    if (targetDepth == 0)
                        return;
                }
            }
        }
    }

    public bool TrySkip()
    {
        try
        {
            Skip();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
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

    private static void ReturnBuf(ref byte[]? buf)
    {
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
            buf = null;
        }
    }

    // ── Span-mode Read ──
    private bool ReadSpan()
    {
        if (_inFlow)
            return ReadFlowSpan();
        Retry:
        if (_position >= _data.Length)
        {
            if (_stackCount > 0)
            {
                PopIndent();
                _tokenType = TokenType.ObjectEnd;
                _depth--;
                return true;
            }
            return false;
        }
        int lineStart = _position,
            lineIndent = 0;
        while (_position < _data.Length && _data[_position] == (byte)' ')
        {
            lineIndent++;
            _position++;
        }
        if (
            _position >= _data.Length
            || _data[_position] == (byte)'\n'
            || _data[_position] == (byte)'\r'
        )
        {
            SkipLineSpan();
            goto Retry;
        }
        if (_data[_position] == (byte)'#')
        {
            SkipLineSpan();
            goto Retry;
        }
        // Standalone node tag line (e.g. "!person") applies to the following block node.
        // The deserializer ignores the tag itself, so skip the whole line. Doing so also
        // guarantees forward progress: without this branch the key scan below would consume
        // across the newline looking for ':', mis-parsing the document and stalling.
        if (_data[_position] == (byte)'!')
        {
            SkipLineSpan();
            goto Retry;
        }
        // Multi-document separator: --- at indent 0
        if (
            lineIndent == 0
            && _data[_position] == (byte)'-'
            && _position + 2 < _data.Length
            && _data[_position + 1] == (byte)'-'
            && _data[_position + 2] == (byte)'-'
        )
        {
            _position += 3;
            SkipLineSpan();
            if (_stackCount > 0)
            {
                // End current document
                PopIndent();
                _tokenType = TokenType.ObjectEnd;
                _depth--;
            }
            else
            {
                // Start of first document — emit ObjectStart next
                _docStartPending = true;
                goto Retry;
            }
            return true;
        }
        if (_docStartPending)
        {
            _docStartPending = false;
            PushIndent(0);
            PushIndent(lineIndent > 0 ? lineIndent : 0);
            _depth++;
            _tokenType = TokenType.ObjectStart;
            _position = lineStart;
            return true;
        }
        if (_stackCount == 0 && lineIndent > 0)
        {
            _position = lineStart;
            PushIndent(0);
            PushIndent(lineIndent);
            if (_depth >= _maxDepth)
                throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
            _tokenType = TokenType.ObjectStart;
            _depth++;
            return true;
        }
        if (_stackCount > 0 && lineIndent < _indentStack[_stackCount - 1])
        {
            _position = lineStart;
            PopIndent();
            FinalizeMappingAnchor();
            _tokenType = TokenType.ObjectEnd;
            _depth--;
            return true;
        }
        if (_stackCount > 0 && lineIndent > _indentStack[_stackCount - 1])
        {
            _position = lineStart;
            PushIndent(lineIndent);
            if (_depth >= _maxDepth)
                throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
            _tokenType = TokenType.ObjectStart;
            _depth++;
            return true;
        }
        if (
            _data[_position] == (byte)'-'
            && _position + 1 < _data.Length
            && _data[_position + 1] == (byte)' '
        )
        {
            _position += 2;
            int vs = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            _valueSpan = Trim(_data[vs.._position]);
            SkipNewlineSpan();
            _tokenType = TokenType.String;
            return true;
        }
        // Complex key: ? key\n: value
        if (_data[_position] == (byte)'?')
        {
            _position++;
            if (_position < _data.Length && _data[_position] == (byte)' ')
                _position++;
            int ckStart = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            _keySpan = Trim(_data[ckStart.._position]);
            SkipNewlineSpan();
            // Expect ': value' on next line
            while (_position < _data.Length && _data[_position] == (byte)' ')
                _position++;
            if (_position < _data.Length && _data[_position] == (byte)':')
            {
                _position++;
                if (_position < _data.Length && _data[_position] == (byte)' ')
                    _position++;
                int cvStart = _position;
                while (
                    _position < _data.Length
                    && _data[_position] != (byte)'\n'
                    && _data[_position] != (byte)'\r'
                )
                    _position++;
                _valueSpan = Trim(_data[cvStart.._position]);
            }
            SkipNewlineSpan();
            _tokenType = TokenType.PropertyName;
            return true;
        }
        int ks = _position;
        while (_position < _data.Length && _data[_position] != (byte)':')
            _position++;
        _keySpan = TrimEnd(_data[ks.._position]);
        _position++;
        if (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        // Check for block scalar: | or >
        if (_position < _data.Length && _data[_position] is (byte)'|' or (byte)'>')
        {
            bool isFolded = _data[_position] == (byte)'>';
            _position++; // skip | or >
            // Parse chomping indicator: + (keep) or - (strip)
            bool keepChomp = false,
                stripChomp = false;
            if (_position < _data.Length && _data[_position] == (byte)'+')
            {
                keepChomp = true;
                _position++;
            }
            else if (_position < _data.Length && _data[_position] == (byte)'-')
            {
                stripChomp = true;
                _position++;
            }
            SkipLineSpan();
            // Determine base indent from next line
            int baseIndent = 0;
            int indentStart = _position;
            while (_position < _data.Length && _data[_position] == (byte)' ')
            {
                baseIndent++;
                _position++;
            }
            // Rent buffer for composed output
            int estSize = _data.Length - _position + 256;
            var buf = ArrayPool<byte>.Shared.Rent(estSize);
            try
            {
                int di = 0;
                bool firstLine = true;
                while (_position < _data.Length)
                {
                    if (!firstLine)
                    {
                        // Strip baseIndent from line start
                        for (
                            int i = 0;
                            i < baseIndent
                                && _position < _data.Length
                                && _data[_position] == (byte)' ';
                            i++
                        )
                            _position++;
                    }
                    firstLine = false;
                    // Read content until end of line
                    int lineContentStart = _position;
                    while (
                        _position < _data.Length
                        && _data[_position] != (byte)'\n'
                        && _data[_position] != (byte)'\r'
                    )
                        _position++;
                    int lineLen = _position - lineContentStart;
                    if (lineLen > 0)
                    {
                        _data.Slice(lineContentStart, lineLen).CopyTo(buf.AsSpan(di));
                        di += lineLen;
                    }
                    // Handle newline
                    bool hadNewline = _position < _data.Length;
                    SkipNewlineSpan();
                    // Check if next line is still part of block (indented >= baseIndent)
                    int nextBlockIndent = 0;
                    int savedPos = _position;
                    while (_position < _data.Length && _data[_position] == (byte)' ')
                    {
                        nextBlockIndent++;
                        _position++;
                    }
                    if (nextBlockIndent < baseIndent || _position >= _data.Length)
                    {
                        _position = savedPos; // don't consume next line
                        if (!stripChomp && hadNewline && di > 0)
                        {
                            buf[di++] = (byte)'\n';
                        }
                        break;
                    }
                    // Restore to the start of the content on next line
                    _position = savedPos + baseIndent;
                    if (isFolded)
                    {
                        // Folded: replace single newline with space
                        // If next line is blank, keep newline
                        int blankCheck = savedPos + baseIndent;
                        if (
                            blankCheck < _data.Length
                            && _data[blankCheck] != (byte)'\n'
                            && _data[blankCheck] != (byte)'\r'
                        )
                            buf[di++] = (byte)' ';
                        else
                            buf[di++] = (byte)'\n';
                    }
                    else
                    {
                        buf[di++] = (byte)'\n';
                    }
                }
                if (keepChomp && di > 0)
                {
                    buf[di++] = (byte)'\n';
                }
                _valueSpan = buf.AsSpan(0, di);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            _tokenType = TokenType.PropertyName;
            StoreAnchorIfNeeded();
            AccumulateMappingPair();
            return true;
        }
        int afterKey = _position;

        // Check for &anchor before value
        if (_position < _data.Length && _data[_position] == (byte)'&')
        {
            _position++;
            int anchorStart = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)' '
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            _pendingAnchorName = Encoding.UTF8.GetString(_data[anchorStart.._position]);
            while (_position < _data.Length && _data[_position] == (byte)' ')
                _position++;
            afterKey = _position;
        }

        // Skip !tag before value
        if (_position < _data.Length && _data[_position] == (byte)'!')
        {
            _position++;
            while (
                _position < _data.Length
                && _data[_position] != (byte)' '
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            while (_position < _data.Length && _data[_position] == (byte)' ')
                _position++;
            afterKey = _position;
        }

        SkipLineSpan();
        int nextIndent = 0;
        while (_position < _data.Length && _data[_position] == (byte)' ')
        {
            nextIndent++;
            _position++;
        }
        _position = afterKey;
        if (nextIndent > lineIndent)
        {
            _tokenType = TokenType.PropertyName;
            return true;
        }
        if (_position < _data.Length && _data[_position] == (byte)'{')
        {
            _position++;
            _inFlow = true;
            _flowStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }
        if (
            _position < _data.Length
            && (_data[_position] == (byte)'"' || _data[_position] == (byte)'\'')
        )
        {
            var q = _data[_position];
            _position++;
            int vs2 = _position;
            while (_position < _data.Length && _data[_position] != q)
                _position++;
            _valueSpan = _data[vs2.._position];
            _position++;
            SkipNewlineSpan();
        }
        else
        {
            // Check for *alias
            if (_position < _data.Length && _data[_position] == (byte)'*')
            {
                _position++;
                int aliasStart = _position;
                while (
                    _position < _data.Length
                    && _data[_position] != (byte)' '
                    && _data[_position] != (byte)'\n'
                    && _data[_position] != (byte)'\r'
                )
                    _position++;
                var aliasName = Encoding.UTF8.GetString(_data[aliasStart.._position]);

                // P3 fix: resolve self-referencing alias from pending mapping anchor
                if (_pendingMappingAnchor == aliasName && _curPairCount > 0)
                {
                    // Replay from current accumulator (a0's slots)
                    _replayAnchorIdx = -1; // special: use a0 accumulator
                    _replayPairCount = _curPairCount;
                    _replayIndex = 0;
                    _valueSpan = default;
                }
                else
                {
                    int anchorIdx = FindAnchorIdx(aliasName);
                    if (anchorIdx < 0)
                        throw new FormatException(
                            $"Unresolved alias '*{aliasName}' at offset {BytesConsumed}"
                        );
                    if (IsAnchorMapping(anchorIdx))
                    {
                        _replayAnchorIdx = anchorIdx;
                        _replayPairCount = GetAnchorPairCount(anchorIdx);
                        _replayIndex = 0;
                        _valueSpan = default;
                    }
                    else
                        _valueSpan = GetAnchorScalar(anchorIdx);
                }
                SkipNewlineSpan();
            }
            else
            {
                int vs2 = _position;
                while (
                    _position < _data.Length
                    && _data[_position] != (byte)'\n'
                    && _data[_position] != (byte)'\r'
                )
                    _position++;
                _valueSpan = Trim(_data[vs2.._position]);
                SkipNewlineSpan();
            }
        }
        _tokenType = TokenType.PropertyName;
        StoreAnchorIfNeeded();
        AccumulateMappingPair();
        return true;
    }

    private bool ReadFlowSpan()
    {
        if (!_flowStartEmitted)
        {
            _flowStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }
        while (_position < _data.Length)
        {
            _position = SimdHelpers.SkipWhitespace(_data, _position);
            if (_position < _data.Length && _data[_position] == (byte)',')
            {
                _position++;
                continue;
            }
            break;
        }
        if (_position >= _data.Length)
            return false;
        if (_data[_position] == (byte)'}')
        {
            _position++;
            _inFlow = false;
            SkipNewlineSpan();
            _tokenType = TokenType.ObjectEnd;
            return true;
        }
        int ks = _position;
        while (_position < _data.Length && _data[_position] != (byte)':')
            _position++;
        _keySpan = TrimEnd(_data[ks.._position]);
        _position++;
        if (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;
        if (
            _position < _data.Length
            && (_data[_position] == (byte)'"' || _data[_position] == (byte)'\'')
        )
        {
            var q = _data[_position];
            _position++;
            int vs = _position;
            while (_position < _data.Length && _data[_position] != q)
                _position++;
            _valueSpan = _data[vs.._position];
            _position++;
        }
        else
        {
            int vs = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)','
                && _data[_position] != (byte)'}'
                && _data[_position] != (byte)'\n'
            )
                _position++;
            _valueSpan = Trim(_data[vs.._position]);
        }
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private void SkipLineSpan()
    {
        while (
            _position < _data.Length
            && _data[_position] != (byte)'\n'
            && _data[_position] != (byte)'\r'
        )
            _position++;
        SkipNewlineSpan();
    }

    private void SkipNewlineSpan()
    {
        if (_position < _data.Length && _data[_position] == (byte)'\r')
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'\n')
            _position++;
    }

    // ── Sequence-mode Read ──
    private bool ReadSeq()
    {
        if (_inFlow)
            return ReadFlowSeq();

        while (true)
        {
            if (_seqReader.End)
            {
                if (_stackCount > 0)
                {
                    PopIndent();
                    _tokenType = TokenType.ObjectEnd;
                    _depth--;
                    return true;
                }
                return false;
            }

            int lineIndent = 0;
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' '
            )
            {
                lineIndent++;
                _seqReader.Advance(1);
            }

            bool skipLine = false;
            if (
                _seqReader.End
                || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r'
            )
            {
                SkipLineSeq();
                skipLine = true;
            }
            else if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'#')
            {
                SkipLineSeq();
                skipLine = true;
            }
            if (skipLine)
                continue;

            // Multi-document separator: --- at indent 0
            var remaining = _seqReader.Remaining;
            if (
                lineIndent == 0
                && remaining >= 3
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'-'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex + 1] == (byte)'-'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex + 2] == (byte)'-'
            )
            {
                _seqReader.Advance(3);
                SkipLineSeq();
                if (_stackCount > 0)
                {
                    PopIndent();
                    _tokenType = TokenType.ObjectEnd;
                    _depth--;
                }
                else
                {
                    _docStartPending = true;
                    continue;
                }
                return true;
            }
            if (_docStartPending)
            {
                _docStartPending = false;
                PushIndent(0);
                PushIndent(lineIndent > 0 ? lineIndent : 0);
                _depth++;
                _tokenType = TokenType.ObjectStart;
                return true;
            }

            if (_stackCount == 0 && lineIndent > 0)
            {
                PushIndent(0);
                PushIndent(lineIndent);
                if (_depth >= _maxDepth)
                    throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
                _tokenType = TokenType.ObjectStart;
                _depth++;
                return true;
            }
            if (_stackCount > 0 && lineIndent < _indentStack[_stackCount - 1])
            {
                PopIndent();
                _tokenType = TokenType.ObjectEnd;
                _depth--;
                return true;
            }
            if (_stackCount > 0 && lineIndent > _indentStack[_stackCount - 1])
            {
                PushIndent(lineIndent);
                if (_depth >= _maxDepth)
                    throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
                _tokenType = TokenType.ObjectStart;
                _depth++;
                return true;
            }

            // Sequence item
            if (
                _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'-'
                && _seqReader.Remaining >= 2
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex + 1] == (byte)' '
            )
            {
                _seqReader.Advance(2);
                var buf = RentBuf(256);
                int di = 0;
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                _valueSpan = Trim(buf.AsSpan(0, di));
                SkipNewlineSeq();
                _tokenType = TokenType.String;
                return true;
            }

            // Complex key: ? key\n: value
            if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'?')
            {
                _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' '
                )
                    _seqReader.Advance(1);
                var ckBuf = RentBuf(256);
                int ckd = 0;
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
                )
                {
                    ckBuf[ckd++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                _keySpan = Trim(ckBuf.AsSpan(0, ckd));
                SkipNewlineSeq();
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' '
                )
                    _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)':'
                )
                {
                    _seqReader.Advance(1);
                    if (
                        !_seqReader.End
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' '
                    )
                        _seqReader.Advance(1);
                    var cvBuf = RentBuf(256);
                    int cvd = 0;
                    while (
                        !_seqReader.End
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
                    )
                    {
                        cvBuf[cvd++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                        _seqReader.Advance(1);
                    }
                    _valueSpan = Trim(cvBuf.AsSpan(0, cvd));
                }
                SkipNewlineSeq();
                _tokenType = TokenType.PropertyName;
                return true;
            }

            // Key: value
            var keyBuf = RentBuf(256);
            int kd = 0;
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)':'
            )
            {
                keyBuf[kd++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _keySpan = TrimEnd(keyBuf.AsSpan(0, kd));
            _seqReader.Advance(1); // skip ':'
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
                _seqReader.Advance(1);

            // Handle !tag and &anchor before value
            while (!_seqReader.End)
            {
                var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                if (b == (byte)'!')
                {
                    _seqReader.Advance(1);
                    while (
                        !_seqReader.End
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)' '
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
                    )
                        _seqReader.Advance(1);
                    while (
                        !_seqReader.End
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' '
                    )
                        _seqReader.Advance(1);
                }
                else if (b == (byte)'&')
                {
                    _seqReader.Advance(1);
                    var anchorBuf = RentBuf(64);
                    int ad = 0;
                    while (
                        !_seqReader.End
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)' '
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
                    )
                    {
                        anchorBuf[ad++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                        _seqReader.Advance(1);
                    }
                    _pendingAnchorName = Encoding.UTF8.GetString(anchorBuf.AsSpan(0, ad));
                    while (
                        !_seqReader.End
                        && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' '
                    )
                        _seqReader.Advance(1);
                }
                else
                    break;
            }

            // Skip peek logic for sequence mode — emit value directly
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'{')
            {
                _seqReader.Advance(1);
                _inFlow = true;
                _flowStartEmitted = false;
                _tokenType = TokenType.PropertyName;
                return true;
            }

            // Check for *alias before value
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'*')
            {
                _seqReader.Advance(1);
                var aliasBuf = RentBuf(64);
                int ad = 0;
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)' '
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
                )
                {
                    aliasBuf[ad++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                var aliasName = Encoding.UTF8.GetString(aliasBuf.AsSpan(0, ad));
                int anchorIdx = FindAnchorIdx(aliasName);
                if (anchorIdx < 0)
                    throw new FormatException(
                        $"Unresolved alias '*{aliasName}' at offset {BytesConsumed}"
                    );
                if (IsAnchorMapping(anchorIdx))
                {
                    _replayAnchorIdx = anchorIdx;
                    _replayPairCount = GetAnchorPairCount(anchorIdx);
                    _replayIndex = 0;
                    _valueSpan = default;
                }
                else
                    _valueSpan = GetAnchorScalar(anchorIdx);
                SkipNewlineSeq();
                _tokenType = TokenType.PropertyName;
                StoreAnchorIfNeeded();
                AccumulateMappingPair();
                return true;
            }

            var valBuf = RentBuf(256);
            int vd = 0;
            if (
                !_seqReader.End
                && (
                    _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"'
                    || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\''
                )
            )
            {
                _seqReader.Advance(1);
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"'
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\''
                )
                {
                    valBuf[vd++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                _seqReader.Advance(1);
            }
            else
            {
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
                )
                {
                    valBuf[vd++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
            }
            _valueSpan = Trim(valBuf.AsSpan(0, vd));
            SkipNewlineSeq();
            StoreAnchorIfNeeded();
            AccumulateMappingPair();
            _tokenType = TokenType.PropertyName;
            return true;
        }
    }

    private bool ReadFlowSeq()
    {
        if (!_flowStartEmitted)
        {
            _flowStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }
        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
                _seqReader.Advance(1);
            else
                break;
        }
        if (_seqReader.End)
            return false;
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'}')
        {
            _seqReader.Advance(1);
            _inFlow = false;
            SkipNewlineSeq();
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        var keyBuf = RentBuf(256);
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)':')
        {
            keyBuf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _keySpan = TrimEnd(keyBuf.AsSpan(0, di));
        _seqReader.Advance(1);
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        var valBuf = RentBuf(256);
        di = 0;
        if (
            !_seqReader.End
            && (
                _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"'
                || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\''
            )
        )
        {
            _seqReader.Advance(1);
            while (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\''
            )
            {
                valBuf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _seqReader.Advance(1);
        }
        else
        {
            while (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)','
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'}'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            )
            {
                valBuf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
        }
        _valueSpan = Trim(valBuf.AsSpan(0, di));
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private void SkipLineSeq()
    {
        while (
            !_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
        )
            _seqReader.Advance(1);
        SkipNewlineSeq();
    }

    private void SkipNewlineSeq()
    {
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r')
            _seqReader.Advance(1);
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
            _seqReader.Advance(1);
    }

    // ── Helpers ──
    private byte[] RentBuf(int size)
    {
        var buf = ArrayPool<byte>.Shared.Rent(size);
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
        return buf;
    }

    private void PushIndent(int i)
    {
        if (_stackCount < 64)
            _indentStack[_stackCount++] = i;
    }

    private void PopIndent()
    {
        if (_stackCount > 0)
            _stackCount--;
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> s)
    {
        int e = s.Length;
        while (e > 0 && s[e - 1] == (byte)' ')
            e--;
        return s[..e];
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int st = 0,
            e = s.Length;
        while (st < e && s[st] == (byte)' ')
            st++;
        while (e > st && s[e - 1] == (byte)' ')
            e--;
        return s[st..e];
    }

    private bool IsAnchorMapping(int idx)
    {
        return idx switch
        {
            0 => _a0IsMapping,
            1 => _a1IsMapping,
            2 => _a2IsMapping,
            _ => _a3IsMapping,
        };
    }

    private int GetAnchorPairCount(int idx)
    {
        return idx switch
        {
            0 => _a0pairs,
            1 => _a1pairs,
            2 => _a2pairs,
            _ => _a3pairs,
        };
    }

    private byte[] GetAnchorScalar(int idx)
    {
        return idx switch
        {
            0 => _a0Scalar!,
            1 => _a1Scalar!,
            2 => _a2Scalar!,
            _ => _a3Scalar!,
        };
    }

    // ── Anchor storage helpers (inline, zero heap allocation) ──

    private int FindAnchorIdx(string name)
    {
        if (_a0Name == name)
            return 0;
        if (_a1Name == name)
            return 1;
        if (_a2Name == name)
            return 2;
        if (_a3Name == name)
            return 3;
        return -1;
    }

    private void SetScalarAnchor(string name, byte[] scalar)
    {
        if (_a0Name is null)
        {
            _a0Name = name;
            _a0IsMapping = false;
            _a0Scalar = scalar;
            _a0pairs = 0;
            _anchorCount++;
            return;
        }
        if (_a1Name is null)
        {
            _a1Name = name;
            _a1IsMapping = false;
            _a1Scalar = scalar;
            _a1pairs = 0;
            _anchorCount++;
            return;
        }
        if (_a2Name is null)
        {
            _a2Name = name;
            _a2IsMapping = false;
            _a2Scalar = scalar;
            _a2pairs = 0;
            _anchorCount++;
            return;
        }
        if (_a3Name is null)
        {
            _a3Name = name;
            _a3IsMapping = false;
            _a3Scalar = scalar;
            _a3pairs = 0;
            _anchorCount++;
            return;
        }
        throw new FormatException($"Too many anchors (max {MaxAnchors})");
    }

    private void AddPairToAnchor(
        ref int pairCount,
        ref byte[]? k0,
        ref byte[]? v0,
        ref byte[]? k1,
        ref byte[]? v1,
        ref byte[]? k2,
        ref byte[]? v2,
        ref byte[]? k3,
        ref byte[]? v3,
        ref byte[]? k4,
        ref byte[]? v4,
        ref byte[]? k5,
        ref byte[]? v5,
        ref byte[]? k6,
        ref byte[]? v6,
        ref byte[]? k7,
        ref byte[]? v7,
        byte[] key,
        byte[] value
    )
    {
        switch (pairCount++)
        {
            case 0:
                k0 = key;
                v0 = value;
                break;
            case 1:
                k1 = key;
                v1 = value;
                break;
            case 2:
                k2 = key;
                v2 = value;
                break;
            case 3:
                k3 = key;
                v3 = value;
                break;
            case 4:
                k4 = key;
                v4 = value;
                break;
            case 5:
                k5 = key;
                v5 = value;
                break;
            case 6:
                k6 = key;
                v6 = value;
                break;
            default:
                k7 = key;
                v7 = value;
                break;
        }
    }

    private void SetMappingAnchor(
        string name,
        int pairCount,
        byte[]? k0,
        byte[]? v0,
        byte[]? k1,
        byte[]? v1,
        byte[]? k2,
        byte[]? v2,
        byte[]? k3,
        byte[]? v3,
        byte[]? k4,
        byte[]? v4,
        byte[]? k5,
        byte[]? v5,
        byte[]? k6,
        byte[]? v6,
        byte[]? k7,
        byte[]? v7
    )
    {
        if (_a0Name is null)
        {
            _a0Name = name;
            _a0IsMapping = true;
            _a0pairs = pairCount;
            _a0k0 = k0;
            _a0v0 = v0;
            _a0k1 = k1;
            _a0v1 = v1;
            _a0k2 = k2;
            _a0v2 = v2;
            _a0k3 = k3;
            _a0v3 = v3;
            _a0k4 = k4;
            _a0v4 = v4;
            _a0k5 = k5;
            _a0v5 = v5;
            _a0k6 = k6;
            _a0v6 = v6;
            _a0k7 = k7;
            _a0v7 = v7;
            _anchorCount++;
            return;
        }
        if (_a1Name is null)
        {
            _a1Name = name;
            _a1IsMapping = true;
            _a1pairs = pairCount;
            _a1k0 = k0;
            _a1v0 = v0;
            _a1k1 = k1;
            _a1v1 = v1;
            _a1k2 = k2;
            _a1v2 = v2;
            _a1k3 = k3;
            _a1v3 = v3;
            _a1k4 = k4;
            _a1v4 = v4;
            _a1k5 = k5;
            _a1v5 = v5;
            _a1k6 = k6;
            _a1v6 = v6;
            _a1k7 = k7;
            _a1v7 = v7;
            _anchorCount++;
            return;
        }
        if (_a2Name is null)
        {
            _a2Name = name;
            _a2IsMapping = true;
            _a2pairs = pairCount;
            _a2k0 = k0;
            _a2v0 = v0;
            _a2k1 = k1;
            _a2v1 = v1;
            _a2k2 = k2;
            _a2v2 = v2;
            _a2k3 = k3;
            _a2v3 = v3;
            _a2k4 = k4;
            _a2v4 = v4;
            _a2k5 = k5;
            _a2v5 = v5;
            _a2k6 = k6;
            _a2v6 = v6;
            _a2k7 = k7;
            _a2v7 = v7;
            _anchorCount++;
            return;
        }
        if (_a3Name is null)
        {
            _a3Name = name;
            _a3IsMapping = true;
            _a3pairs = pairCount;
            _a3k0 = k0;
            _a3v0 = v0;
            _a3k1 = k1;
            _a3v1 = v1;
            _a3k2 = k2;
            _a3v2 = v2;
            _a3k3 = k3;
            _a3v3 = v3;
            _a3k4 = k4;
            _a3v4 = v4;
            _a3k5 = k5;
            _a3v5 = v5;
            _a3k6 = k6;
            _a3v6 = v6;
            _a3k7 = k7;
            _a3v7 = v7;
            _anchorCount++;
            return;
        }
        throw new FormatException($"Too many anchors (max {MaxAnchors})");
    }

    private void StoreAnchorIfNeeded()
    {
        if (_pendingAnchorName is null)
            return;

        if (FindAnchorIdx(_pendingAnchorName) >= 0)
            throw new FormatException(
                $"Duplicate anchor '&{_pendingAnchorName}' at offset {BytesConsumed}"
            );

        if (_anchorCount >= MaxAnchors)
            throw new FormatException($"Too many anchors (max {MaxAnchors})");

        if (_valueSpan.IsEmpty)
        {
            _nextAnchorName = _pendingAnchorName;
            _pendingAnchorName = null;
            _curPairCount = 0;
            return;
        }

        SetScalarAnchor(_pendingAnchorName, _valueSpan.ToArray());
        _pendingAnchorName = null;
    }

    private void AccumulateMappingPair()
    {
        if (_nextAnchorName is not null)
        {
            _pendingMappingAnchor = _nextAnchorName;
            _nextAnchorName = null;
            return;
        }
        if (_pendingMappingAnchor is not null && _curPairCount < MaxAnchorPairs)
        {
            // Use anchor 0's pair slots as temporary accumulator
            AddPairToAnchor(
                ref _curPairCount,
                ref _a0k0,
                ref _a0v0,
                ref _a0k1,
                ref _a0v1,
                ref _a0k2,
                ref _a0v2,
                ref _a0k3,
                ref _a0v3,
                ref _a0k4,
                ref _a0v4,
                ref _a0k5,
                ref _a0v5,
                ref _a0k6,
                ref _a0v6,
                ref _a0k7,
                ref _a0v7,
                _keySpan.ToArray(),
                _valueSpan.ToArray()
            );
        }
    }

    private void FinalizeMappingAnchor()
    {
        if (_pendingMappingAnchor is null || _curPairCount == 0)
            return;

        SetMappingAnchor(
            _pendingMappingAnchor,
            _curPairCount,
            _a0k0,
            _a0v0,
            _a0k1,
            _a0v1,
            _a0k2,
            _a0v2,
            _a0k3,
            _a0v3,
            _a0k4,
            _a0v4,
            _a0k5,
            _a0v5,
            _a0k6,
            _a0v6,
            _a0k7,
            _a0v7
        );
        _pendingMappingAnchor = null;
        _curPairCount = 0;
    }

    private (byte[] Key, byte[] Value) GetAccumulatorPair(int i)
    {
        return i switch
        {
            0 => (_a0k0!, _a0v0!),
            1 => (_a0k1!, _a0v1!),
            2 => (_a0k2!, _a0v2!),
            3 => (_a0k3!, _a0v3!),
            4 => (_a0k4!, _a0v4!),
            5 => (_a0k5!, _a0v5!),
            6 => (_a0k6!, _a0v6!),
            _ => (_a0k7!, _a0v7!),
        };
    }

    private (byte[] Key, byte[] Value) GetAnchorPair(int anchorIdx, int i)
    {
        return anchorIdx switch
        {
            0 => i switch
            {
                0 => (_a0k0!, _a0v0!),
                1 => (_a0k1!, _a0v1!),
                2 => (_a0k2!, _a0v2!),
                3 => (_a0k3!, _a0v3!),
                4 => (_a0k4!, _a0v4!),
                5 => (_a0k5!, _a0v5!),
                6 => (_a0k6!, _a0v6!),
                _ => (_a0k7!, _a0v7!),
            },
            1 => i switch
            {
                0 => (_a1k0!, _a1v0!),
                1 => (_a1k1!, _a1v1!),
                2 => (_a1k2!, _a1v2!),
                3 => (_a1k3!, _a1v3!),
                4 => (_a1k4!, _a1v4!),
                5 => (_a1k5!, _a1v5!),
                6 => (_a1k6!, _a1v6!),
                _ => (_a1k7!, _a1v7!),
            },
            2 => i switch
            {
                0 => (_a2k0!, _a2v0!),
                1 => (_a2k1!, _a2v1!),
                2 => (_a2k2!, _a2v2!),
                3 => (_a2k3!, _a2v3!),
                4 => (_a2k4!, _a2v4!),
                5 => (_a2k5!, _a2v5!),
                6 => (_a2k6!, _a2v6!),
                _ => (_a2k7!, _a2v7!),
            },
            _ => i switch
            {
                0 => (_a3k0!, _a3v0!),
                1 => (_a3k1!, _a3v1!),
                2 => (_a3k2!, _a3v2!),
                3 => (_a3k3!, _a3v3!),
                4 => (_a3k4!, _a3v4!),
                5 => (_a3k5!, _a3v5!),
                6 => (_a3k6!, _a3v6!),
                _ => (_a3k7!, _a3v7!),
            },
        };
    }

    private (byte[] Key, byte[] Value) GetPair(
        ref int idx,
        ref byte[]? k0,
        ref byte[]? v0,
        ref byte[]? k1,
        ref byte[]? v1,
        ref byte[]? k2,
        ref byte[]? v2,
        ref byte[]? k3,
        ref byte[]? v3,
        ref byte[]? k4,
        ref byte[]? v4,
        ref byte[]? k5,
        ref byte[]? v5,
        ref byte[]? k6,
        ref byte[]? v6,
        ref byte[]? k7,
        ref byte[]? v7,
        int i
    )
    {
        return i switch
        {
            0 => (k0!, v0!),
            1 => (k1!, v1!),
            2 => (k2!, v2!),
            3 => (k3!, v3!),
            4 => (k4!, v4!),
            5 => (k5!, v5!),
            6 => (k6!, v6!),
            _ => (k7!, v7!),
        };
    }

    // ── Fast path array reading (used by source generators) ──

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
                v = v * 10 + (b - (byte)'0');
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
                v = v * 10 + (b - (byte)'0');
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
