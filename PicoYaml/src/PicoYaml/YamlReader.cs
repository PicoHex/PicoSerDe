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
    private byte[]?[] _rentedBuffers;
    private int _bufCount;

    // Anchor/alias support
    private Dictionary<string, StoredAnchor>? _anchors;
    private string? _pendingAnchorName;
    private string? _pendingMappingAnchor;
    private string? _nextAnchorName;
    private List<(byte[] Key, byte[] Value)>? _currentMappingPairs;
    private List<(byte[] Key, byte[] Value)>? _replayPairs;
    private int _replayIndex;

    private struct StoredAnchor
    {
        public bool IsMapping;
        public TokenType ScalarType;
        public byte[] ScalarValue;
        public List<(byte[] Key, byte[] Value)>? MappingPairs;
    }

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
        _rentedBuffers = new byte[]?[8];
        _bufCount = 0;
        _maxDepth = 256;
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
        _rentedBuffers = new byte[]?[8];
        _bufCount = 0;
        _maxDepth = 256;
    }

    public TokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> KeySpan => _keySpan;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;
    public int Depth => _depth;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    public bool Read()
    {
        if (_replayPairs is not null && _replayIndex < _replayPairs.Count)
        {
            var pair = _replayPairs[_replayIndex++];
            _keySpan = pair.Key;
            _valueSpan = pair.Value;
            _tokenType = TokenType.PropertyName;
            if (_replayIndex >= _replayPairs.Count)
                _replayPairs = null;
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
        v = _valueSpan[0] == (byte)'t';
        return true;
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
        for (int i = 0; i < _bufCount; i++)
        {
            if (_rentedBuffers[i] is not null)
            {
                ArrayPool<byte>.Shared.Return(_rentedBuffers[i]!);
                _rentedBuffers[i] = null;
            }
        }
        _bufCount = 0;
        _rentedBuffer = null;
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
                if (_pendingMappingAnchor == aliasName && _currentMappingPairs is not null)
                {
                    // Snapshot current pairs for replay (anchor not yet finalized)
                    _replayPairs = new List<(byte[], byte[])>(_currentMappingPairs);
                    _replayIndex = 0;
                    _valueSpan = default;
                }
                else if (_anchors is null || !_anchors.TryGetValue(aliasName, out var stored))
                    throw new FormatException(
                        $"Unresolved alias '*{aliasName}' at offset {BytesConsumed}"
                    );
                else if (stored.IsMapping && stored.MappingPairs is not null)
                {
                    _replayPairs = stored.MappingPairs;
                    _replayIndex = 0;
                    _valueSpan = default;
                }
                else
                    _valueSpan = stored.ScalarValue;
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

        RetrySeq:
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
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
        {
            lineIndent++;
            _seqReader.Advance(1);
        }
        if (
            _seqReader.End
            || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
            || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r'
        )
        {
            SkipLineSeq();
            goto RetrySeq;
        }
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'#')
        {
            SkipLineSeq();
            goto RetrySeq;
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

        // Key: value
        var keyBuf = RentBuf(256);
        int kd = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)':')
        {
            keyBuf[kd++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _keySpan = TrimEnd(keyBuf.AsSpan(0, kd));
        _seqReader.Advance(1); // skip ':'
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        // Skip peek logic for sequence mode — emit value directly
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'{')
        {
            _seqReader.Advance(1);
            _inFlow = true;
            _flowStartEmitted = false;
            _tokenType = TokenType.PropertyName;
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
        _tokenType = TokenType.PropertyName;
        return true;
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
        if (_bufCount < _rentedBuffers.Length)
            _rentedBuffers[_bufCount++] = buf;
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

    private void StoreAnchorIfNeeded()
    {
        if (_pendingAnchorName is not null)
        {
            _anchors ??= new Dictionary<string, StoredAnchor>();
            if (_anchors.ContainsKey(_pendingAnchorName))
                throw new FormatException(
                    $"Duplicate anchor '&{_pendingAnchorName}' at offset {BytesConsumed}"
                );

            // Empty value + pending anchor → mapping anchor (defer until ObjectEnd)
            if (_valueSpan.IsEmpty)
            {
                _nextAnchorName = _pendingAnchorName;
                _pendingAnchorName = null;
                _currentMappingPairs = new List<(byte[], byte[])>();
                return;
            }

            _anchors[_pendingAnchorName] = new StoredAnchor
            {
                IsMapping = false,
                ScalarType = _tokenType,
                ScalarValue = _valueSpan.ToArray(),
            };
            _pendingAnchorName = null;
        }
    }

    private void AccumulateMappingPair()
    {
        if (_nextAnchorName is not null)
        {
            _pendingMappingAnchor = _nextAnchorName;
            _nextAnchorName = null;
            return; // skip the mapping's own key
        }
        if (_pendingMappingAnchor is not null && _currentMappingPairs is not null)
        {
            _currentMappingPairs.Add((_keySpan.ToArray(), _valueSpan.ToArray()));
        }
    }

    private void FinalizeMappingAnchor()
    {
        if (_pendingMappingAnchor is not null && _currentMappingPairs is not null)
        {
            _anchors ??= new Dictionary<string, StoredAnchor>();
            _anchors[_pendingMappingAnchor] = new StoredAnchor
            {
                IsMapping = true,
                MappingPairs = _currentMappingPairs,
            };
            _pendingMappingAnchor = null;
            _currentMappingPairs = null;
        }
    }
}
