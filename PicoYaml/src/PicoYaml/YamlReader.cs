namespace PicoYaml;

/// <summary>Captures YamlReader state for pause/resume across chunks.</summary>
public struct YamlReaderState
{
    internal int Depth;
    internal int MaxDepth;
    internal long BytesConsumed;
    internal SequencePosition Position;
    internal int StackCount;
    internal int[]? IndentStack;
    internal bool InFlow;
    internal bool FlowStartEmitted;
    internal bool InFlowSeq;
    internal bool FlowSeqStartEmitted;
    internal bool DocStartPending;
}

public ref struct YamlReader : ITokenReader, ITransactionalTokenReader, IFusedTokenReader
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

    // True when the current value came from explicit quoting (or a block
    // scalar): an empty quoted scalar is "" — never a null literal.
    private bool _valueWasQuoted;

    // True when the current value is a bare sequence dash ("-" alone on its
    // line, optionally followed by a comment): the item's value is the block
    // below, not an inline scalar. Explicit state, so a null item that merely
    // *looks* empty ("- # note", "- *alias") cannot be mistaken for it.
    private bool _seqItemWasBareDash;
    private int _depth;
    private readonly int _maxDepth;
    private int[] _indentStack;
    private int _stackCount;
    private bool _inFlow;
    private bool _flowStartEmitted;

    // Flow SEQUENCE state (`[1, 2, 3]`). Kept separate from _inFlow, which is the
    // flow MAPPING state (`{k: v}`): the two have different token shapes and the
    // reader used to have no sequence branch at all, so a bracketed value fell
    // through to the scalar path and the member deserialized to an empty list.
    private bool _inFlowSeq;
    private bool _flowSeqStartEmitted;
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

    // Dedicated accumulator fields (P7 fix) — separate from anchor 0's storage
    // so that accumulating a second anchored mapping does not corrupt anchor 0.
    private byte[]? _accK0,
        _accV0,
        _accK1,
        _accV1,
        _accK2,
        _accV2,
        _accK3,
        _accV3,
        _accK4,
        _accV4,
        _accK5,
        _accV5,
        _accK6,
        _accV6,
        _accK7,
        _accV7;

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
    private readonly bool _isFinalBlock;
    private bool _needsMoreData;

    // One-time UTF-8 BOM resolution (sequence mode).
    private bool _bomChecked;

    // Sequence-mode normalization: the incoming chunk is copied into a
    // contiguous buffer and parsed with span semantics; unconsumed bytes are
    // retained by the serializer through ExportState.Position.
    private ReadOnlySequence<byte> _sourceSequence;
    private bool _normalizedSequence;
    private byte[]? _chunkCopy;
    private int _dataOffset;

    // True length of the copied chunk (the parse buffer never exceeds it).
    private int _realLength;
    private int _tokenStart;

    // Transactional storage: parser state snapshots for Mark/RewindToMark.
    // The indent stack is copied into a reader-owned buffer; token spans are
    // cleared on rollback (see ITransactionalTokenReader).
    private ParserMark _mark;
    private ParserMark _readMark;
    private int[]? _markIndentStack;

    private struct ParserMark
    {
        public int Position;
        public TokenType TokenType;
        public int Depth;
        public int StackCount;
        public bool InFlow;
        public bool FlowStartEmitted;
        public bool InFlowSeq;
        public bool FlowSeqStartEmitted;
        public bool DocStartPending;
        public int ReplayAnchorIdx;
        public int ReplayIndex;
        public int ReplayPairCount;
        public int CurPairCount;
        public int AnchorCount;
        public int A0Pairs;
        public int A1Pairs;
        public int A2Pairs;
        public int A3Pairs;
        public int TokenCount;
        public string? PendingAnchorName;
        public string? PendingMappingAnchor;
        public string? NextAnchorName;
    }

    private void SaveMark(ref ParserMark m)
    {
        m.Position = _position;
        m.TokenType = _tokenType;
        m.Depth = _depth;
        m.StackCount = _stackCount;
        m.InFlow = _inFlow;
        m.FlowStartEmitted = _flowStartEmitted;
        m.InFlowSeq = _inFlowSeq;
        m.FlowSeqStartEmitted = _flowSeqStartEmitted;
        m.DocStartPending = _docStartPending;
        m.ReplayAnchorIdx = _replayAnchorIdx;
        m.ReplayIndex = _replayIndex;
        m.ReplayPairCount = _replayPairCount;
        m.CurPairCount = _curPairCount;
        m.AnchorCount = _anchorCount;
        m.A0Pairs = _a0pairs;
        m.A1Pairs = _a1pairs;
        m.A2Pairs = _a2pairs;
        m.A3Pairs = _a3pairs;
        m.TokenCount = _tokenCount;
        m.PendingAnchorName = _pendingAnchorName;
        m.PendingMappingAnchor = _pendingMappingAnchor;
        m.NextAnchorName = _nextAnchorName;
        if (_stackCount > 0)
        {
            _markIndentStack ??= new int[64];
            Array.Copy(_indentStack, _markIndentStack, _stackCount);
        }
    }

    private void RestoreMark(in ParserMark m)
    {
        _position = m.Position;
        _tokenType = m.TokenType;
        _depth = m.Depth;
        _stackCount = m.StackCount;
        _inFlow = m.InFlow;
        _flowStartEmitted = m.FlowStartEmitted;
        _inFlowSeq = m.InFlowSeq;
        _flowSeqStartEmitted = m.FlowSeqStartEmitted;
        _docStartPending = m.DocStartPending;
        _replayAnchorIdx = m.ReplayAnchorIdx;
        _replayIndex = m.ReplayIndex;
        _replayPairCount = m.ReplayPairCount;
        _curPairCount = m.CurPairCount;
        _anchorCount = m.AnchorCount;
        _a0pairs = m.A0Pairs;
        _a1pairs = m.A1Pairs;
        _a2pairs = m.A2Pairs;
        _a3pairs = m.A3Pairs;
        _tokenCount = m.TokenCount;
        _pendingAnchorName = m.PendingAnchorName;
        _pendingMappingAnchor = m.PendingMappingAnchor;
        _nextAnchorName = m.NextAnchorName;
        if (m.StackCount > 0 && _markIndentStack is not null)
            Array.Copy(_markIndentStack, _indentStack, m.StackCount);
        _keySpan = default;
        _valueSpan = default;
        _needsMoreData = false;
    }

    /// <inheritdoc />
    public void Mark() => SaveMark(ref _mark);

    /// <inheritdoc />
    public void RewindToMark() => RestoreMark(in _mark);

    public bool NeedsMoreData => _needsMoreData;

    /// <summary>Start offset of the token most recently produced by <see cref="Read"/>.</summary>
    public long TokenStart => _tokenStart;

    /// <summary>True when this reader resumes a previously exported state.</summary>
    public bool IsResumed { get; private set; }

    public YamlReader(ReadOnlySpan<byte> data, bool isFinalBlock = true)
    {
        _data = TextHelpers.SkipBom(data);
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _isFinalBlock = isFinalBlock;
        _bomChecked = true;
        _needsMoreData = false;
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

    public YamlReader(ReadOnlySequence<byte> data, bool isFinalBlock = true)
    {
        _data = default;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _normalizedSequence = true;
        _sourceSequence = data;
        _chunkCopy = null;
        _dataOffset = 0;
        _isFinalBlock = isFinalBlock;
        _bomChecked = false;
        if (!data.IsEmpty)
        {
            int len = (int)data.Length;
            _realLength = len;
            _chunkCopy = ArrayPool<byte>.Shared.Rent(len);
            data.CopyTo(_chunkCopy);
            _data = _chunkCopy.AsSpan(0, len);
        }
        _needsMoreData = false;
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

    /// <summary>
    /// True when the current value came from an explicitly quoted scalar (or a block
    /// scalar). An empty quoted scalar is the empty string, never a null literal and
    /// never a sequence marker — which is what lets the generated nested-sequence code
    /// tell <c>- ""</c> from a bare <c>-</c>.
    /// </summary>
    public bool ValueWasQuoted => _valueWasQuoted;

    /// <summary>
    /// True when the current item token is a bare sequence dash: the dash line carries
    /// no inline value, so the item's value is the indented block that follows (if any).
    /// Read from an explicit flag, not inferred from an empty value: a quoted
    /// <c>- ""</c>, a comment-only <c>- # note</c> and an alias <c>- *m</c> are all
    /// different things.
    /// </summary>
    public bool IsNestedSequenceItem => _seqItemWasBareDash;

    /// <summary>
    /// True when the current item token carries the unquoted flow-empty marker
    /// (<c>- []</c>): a nested sequence with no elements. A quoted "[]" is a string
    /// element and is never reported here.
    /// </summary>
    public bool IsEmptySequenceMarker =>
        !_valueWasQuoted
        && _valueSpan.Length == 2
        && _valueSpan[0] == (byte)'['
        && _valueSpan[1] == (byte)']';

    /// <summary>
    /// True when the current value is a YAML null literal: <c>null</c> (any
    /// case), <c>~</c>, or a missing value (<c>Key:</c> with nothing after the
    /// colon). A quoted scalar is never null — <c>V: ""</c> is the empty
    /// string.
    /// </summary>
    public bool IsNullLiteral
    {
        get
        {
            if (_valueWasQuoted)
                return false;
            var v = _valueSpan;
            if (v.IsEmpty)
                return true;
            if (v.Length == 1 && v[0] == (byte)'~')
                return true;
            return v.Length == 4 && System.Text.Ascii.EqualsIgnoreCase(v, "null"u8);
        }
    }
    public int Depth => _depth;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    /// <summary>Direct position access for optimized generated code (span mode only).</summary>
    public int RawPos => _isSequence ? -1 : _position;

    /// <summary>Sets the raw span position (span mode only). Mirrors JsonReader.SetRawPos.</summary>
    public void SetRawPos(int pos)
    {
        _position = pos;
    }

    public readonly YamlReaderState ExportState()
    {
        var s = new YamlReaderState
        {
            Depth = _depth,
            MaxDepth = _maxDepth,
            BytesConsumed = _normalizedSequence ? _position : _seqReader.Consumed,
            Position = _normalizedSequence
                ? _sourceSequence.GetPosition(Math.Min(_dataOffset + _position, _realLength))
                : _seqReader.Position,
            StackCount = _stackCount,
            InFlow = _inFlow,
            FlowStartEmitted = _flowStartEmitted,
            InFlowSeq = _inFlowSeq,
            FlowSeqStartEmitted = _flowSeqStartEmitted,
            DocStartPending = _docStartPending,
        };
        if (_stackCount > 0)
        {
            s.IndentStack = new int[_stackCount];
            Array.Copy(_indentStack, s.IndentStack, _stackCount);
        }
        return s;
    }

    public YamlReader(ReadOnlySequence<byte> data, bool isFinalBlock, YamlReaderState state)
        : this(data, isFinalBlock)
    {
        _depth = state.Depth;
        // A default state (first chunk) carries MaxDepth == 0; keep the
        // constructor default in that case.
        if (state.MaxDepth > 0)
            _maxDepth = state.MaxDepth;
        _needsMoreData = false;
        // Only a genuinely resumed reader has already passed the BOM.
        _bomChecked = state.BytesConsumed > 0 || state.Depth > 0;
        IsResumed = state.BytesConsumed > 0 || state.Depth > 0;
        _stackCount = state.StackCount;
        _inFlow = state.InFlow;
        _flowStartEmitted = state.FlowStartEmitted;
        _inFlowSeq = state.InFlowSeq;
        _flowSeqStartEmitted = state.FlowSeqStartEmitted;
        _docStartPending = state.DocStartPending;
        if (state.IndentStack is not null)
        {
            _indentStack = new int[64];
            Array.Copy(state.IndentStack, _indentStack, state.StackCount);
        }
    }

    public bool Read()
    {
        _tokenStart = _position;
        _needsMoreData = false;
        SaveMark(ref _readMark);
        if (!_bomChecked)
        {
            if (_normalizedSequence)
            {
                if (TextHelpers.IsBomAtStart(_data))
                {
                    _dataOffset += TextHelpers.BomLength;
                    _data = _data[TextHelpers.BomLength..];
                }
                else if (_data.Length < TextHelpers.BomLength && !_isFinalBlock)
                {
                    _needsMoreData = true;
                    return false;
                }
                _bomChecked = true;
            }
            else if (_isSequence)
            {
                TextHelpers.SkipBomSeq(ref _seqReader, _isFinalBlock, out var bomNeedsMore);
                if (bomNeedsMore)
                {
                    _needsMoreData = true;
                    return false;
                }
                _bomChecked = true;
            }
            else
                _bomChecked = true;
        }
        if (_normalizedSequence)
        {
            int __tokenStart = _position;
            try
            {
                var normalized = ReadImpl();
                if (!normalized)
                {
                    // Never consume an incomplete token: the next attempt (with
                    // more data) restarts exactly at this token.
                    RestoreMark(in _readMark);
                    _needsMoreData = !_isFinalBlock;
                    // A failed read must not leave the previous token visible:
                    // callers that loop on Read() would otherwise keep looking at
                    // a stale PropertyName and spin.
                    ClearToken();
                    return false;
                }
                // A token that reaches the sentinel may continue in the next
                // chunk; defer it instead of consuming a possibly partial value.
                if (
                    !_isFinalBlock
                    && _position >= _data.Length
                    && (_inFlow || _data.Length == 0 || _data[_data.Length - 1] != (byte)10)
                )
                {
                    _position = __tokenStart;
                    _needsMoreData = true;
                    ClearToken();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
                when (!_isFinalBlock
                    && ex
                        is IncompleteInputException
                            or FormatException
                            or IndexOutOfRangeException
                            or ArgumentOutOfRangeException
                )
            {
                RestoreMark(in _readMark);
                _needsMoreData = true;
                ClearToken();
                return false;
            }
        }
        try
        {
            var result = ReadImpl();
            if (!result)
            {
                _needsMoreData = !_isFinalBlock;
                // Same as the span path: no token means no stale token type.
                ClearToken();
            }
            return result;
        }
        catch (IncompleteInputException)
        {
            // Non-final span reads must report NeedsMoreData, never throw.
            RestoreMark(in _readMark);
            _needsMoreData = true;
            return false;
        }
    }

    /// <summary>Drops the current token so a failed <see cref="Read"/> leaves none behind.</summary>
    private void ClearToken()
    {
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _valueWasQuoted = false;
        _seqItemWasBareDash = false;
    }

    /// <summary>Streaming resume: rewinds to an earlier span-relative offset.</summary>
    public void RewindTo(long consumedOffset)
    {
        if (consumedOffset < 0 || consumedOffset > _position)
            throw new ArgumentOutOfRangeException(nameof(consumedOffset));
        _position = (int)consumedOffset;
        _needsMoreData = false;
    }

    private bool ReadImpl()
    {
        _valueWasQuoted = false;
        _seqItemWasBareDash = false;
        // R2 Task 4: the value span belongs to the token that CARRIES an inline value. It
        // used to survive into the next token, so a PropertyName whose value is the indented
        // block below it (`k:` + block) reported the PREVIOUS token's value ("null", a number,
        // whatever) — indistinguishable from `k: null`. Clearing it makes "no inline value"
        // observable as an empty span, which is what lets generated code tell a null literal
        // from a block value without a new reader API. Tokens that have an inline value set
        // it below, so nothing that reads a scalar changes.
        _valueSpan = default;
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

    public bool TryGetUInt64(out ulong v)
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

    /// <summary>
    /// YAML has no collection token at all (spec §3.3: <see cref="TokenType.ArrayStart"/> is never assigned by
    /// this reader — a sequence is one String token per item line), so a count has neither a source nor a
    /// consumer. Declared, not hinted — with <see langword="false"/> the contract (§3.1) requires
    /// <see cref="TryGetCollectionCount"/> to answer <see langword="false"/> for every token.
    /// </summary>
    public readonly bool HasCountedCollections => false;

    /// <summary>
    /// Always <see langword="false"/>: YAML has no count to report (see <see cref="HasCountedCollections"/>).
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
    /// YAML never overrides a fused read: spec §3.3 and §6 S4 leave this format unoptimized, because a
    /// sequence is one String token per item line and there is no collection token to fuse.
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
        if (_chunkCopy is not null)
        {
            ArrayPool<byte>.Shared.Return(_chunkCopy);
            _chunkCopy = null;
        }
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
        if (_inFlowSeq)
            return ReadFlowSeqSpan();
        Retry:
        if (_position >= _data.Length)
        {
            if (_stackCount > 0)
            {
                // A non-final chunk may still contain more lines of this
                // mapping; the synthetic ObjectEnd must wait for them.
                if (!_isFinalBlock)
                    throw new IncompleteInputException();
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
        // Document end marker: ... at indent 0 — the document is finished.
        if (
            lineIndent == 0
            && _data[_position] == (byte)'.'
            && _position + 2 < _data.Length
            && _data[_position + 1] == (byte)'.'
            && _data[_position + 2] == (byte)'.'
        )
        {
            _position = _data.Length; // stop reading this document
            if (_stackCount > 0)
            {
                PopIndent();
                _tokenType = TokenType.ObjectEnd;
                _depth--;
                return true;
            }
            return false;
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
        // A LINE-LEADING flow collection is a NODE, not a mapping key: `{}` at the start
        // of a line used to fall through to the block key scan below, which looked for the
        // ':' a flow mapping never needs and threw "Invalid YAML mapping line". The
        // same-line form (`V: {}`) already had this branch at the value position; this one
        // covers the positions a value branch cannot reach - the document root (`{}`, the
        // empty document an exporter writes for an empty object) and a flow collection that
        // sits on its own indented line. Both enter the SAME flow readers, so the token
        // stream is identical to the inline form and the generated deserializers need no
        // change. A plain scalar may not begin with '{' or '[', so nothing that used to be
        // a scalar or a key can reach here.
        if (_data[_position] is (byte)'{' or (byte)'[')
        {
            // The close must be inside this buffer: the flow readers below cannot resume
            // from a partly-read collection, and a FINAL document whose flow collection
            // never closes is malformed, not empty. (Returning false here instead would
            // report "no value" and hand back a default object for `{Name: x` - exactly the
            // swallow this fix must not add.)
            if (!FlowValueComplete(_position))
            {
                if (!_isFinalBlock)
                    throw new IncompleteInputException();
                throw new FormatException(
                    _data[_position] == (byte)'{'
                        ? "Unterminated YAML flow mapping: expected '}'."
                        : "Unterminated YAML flow sequence: expected ']'."
                );
            }
            var open = _data[_position];
            _position++;
            if (open == (byte)'{')
            {
                _inFlow = true;
                _flowStartEmitted = false;
            }
            else
            {
                _inFlowSeq = true;
                _flowSeqStartEmitted = false;
            }
            return open == (byte)'{' ? ReadFlowSpan() : ReadFlowSeqSpan();
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
            && (
                _position + 1 >= _data.Length
                || _data[_position + 1] is (byte)' ' or (byte)'\n' or (byte)'\r'
            )
        )
        {
            _position++;
            if (_position < _data.Length && _data[_position] == (byte)' ')
                _position++;
            if (_position < _data.Length && _data[_position] == (byte)'"')
            {
                _position++;
                _valueWasQuoted = true;
                _valueSpan = ReadDoubleQuotedStringSpan();
                // consume any trailing content on the line (comment/whitespace)
                while (
                    _position < _data.Length
                    && _data[_position] != (byte)'\n'
                    && _data[_position] != (byte)'\r'
                )
                    _position++;
            }
            else if (_position < _data.Length && _data[_position] == (byte)'\'')
            {
                _position++;
                _valueWasQuoted = true;
                _valueSpan = ReadSingleQuotedStringSpan();
            }
            else if (_position < _data.Length && _data[_position] == (byte)'*')
            {
                // An alias item carries no inline scalar: reuse the scalar the anchor
                // holds when there is one, and the same empty value the mapping-value
                // alias path exposes for a mapping anchor. Either way the item is never
                // a bare-dash marker (I-2).
                _position++;
                int aliasStart = _position;
                while (
                    _position < _data.Length
                    && _data[_position] is not ((byte)' ' or (byte)'\n' or (byte)'\r')
                )
                    _position++;
                var aliasName = Encoding.UTF8.GetString(_data[aliasStart.._position]);
                int anchorIdx = FindAnchorIdx(aliasName);
                _valueSpan =
                    anchorIdx >= 0 && !IsAnchorMapping(anchorIdx)
                        ? GetAnchorScalar(anchorIdx)
                        : default;
                while (
                    _position < _data.Length
                    && _data[_position] != (byte)'\n'
                    && _data[_position] != (byte)'\r'
                )
                    _position++;
            }
            else
            {
                int vs = _position;
                while (
                    _position < _data.Length
                    && _data[_position] != (byte)'\n'
                    && _data[_position] != (byte)'\r'
                )
                    _position++;
                var itemText = _data[vs.._position];
                _valueSpan = StripInlineComment(itemText);
                // I-2: an explicit flag instead of inferring "nested block" from an
                // empty span — a dash followed only by whitespace and/or an inline
                // comment has no inline value, so its value is the block below (if
                // any). A quoted or aliased item never sets this.
                _seqItemWasBareDash = HasNoInlineValue(itemText);
            }
            if (_position >= _data.Length && !_isFinalBlock)
                throw new IncompleteInputException();
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
                _valueSpan = StripInlineComment(_data[cvStart.._position]);
            }
            SkipNewlineSpan();
            _tokenType = TokenType.PropertyName;
            return true;
        }
        // A QUOTED key: `"n\nline": 2`. The writer quotes any key that is not a safe
        // plain scalar (a separator, a quote, a control character), so the scan has to
        // honour the closing quote: a `:` INSIDE the key would otherwise end the key, and
        // the escapes would come back as literal backslash sequences. The content is
        // exact, so it is NOT trimmed — a quoted key may legitimately end with a space
        // (spec §3 S9 x yaml).
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            _keySpan = ReadDoubleQuotedStringSpan();
            while (_position < _data.Length && _data[_position] == (byte)' ')
                _position++;
        }
        else
        {
            int ks = _position;
            // Line-bound key scan: never consume past \n/\r (audit BUG-01).
            while (
                _position < _data.Length
                && _data[_position] != (byte)':'
                && _data[_position] is not ((byte)'\n' or (byte)'\r')
            )
                _position++;
            _keySpan = TrimEnd(_data[ks.._position]);
        }
        if (_position >= _data.Length && !_isFinalBlock)
            throw new IncompleteInputException();
        if (_position >= _data.Length || _data[_position] != (byte)':')
            throw new FormatException("Invalid YAML mapping line: expected ':'.");
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
            // Rent buffer for composed output — use RentBuf so the buffer
            // is tracked in _rb* and stays alive until Dispose(), preventing
            // use-after-free when callers access _valueSpan after Read() returns.
            int estSize = _data.Length - _position + 256;
            var buf = RentBuf(estSize);
            int di = 0;
            bool firstLine = true;
            while (_position < _data.Length)
            {
                if (!firstLine)
                {
                    // Strip baseIndent from line start
                    for (
                        int i = 0;
                        i < baseIndent && _position < _data.Length && _data[_position] == (byte)' ';
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
                if (!hadNewline && !_isFinalBlock)
                    throw new IncompleteInputException();
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
            _valueWasQuoted = true;
            _valueSpan = buf.AsSpan(0, di);
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
        // Flow collections are detected BEFORE the "indented block follows" test:
        // `G: [` puts the '[' at the end of the key line, so that test would return
        // a PropertyName with no value and the bracketed text would never be read.
        if (_position < _data.Length && _data[_position] == (byte)'{')
        {
            if (!FlowValueComplete(_position) && !_isFinalBlock)
                throw new IncompleteInputException();
            _position++;
            _inFlow = true;
            _flowStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }
        if (_position < _data.Length && _data[_position] == (byte)'[')
        {
            // Flow SEQUENCE: `[1, 2, 3]`. Without this branch the bracketed text fell
            // through to the scalar path, so a member written that way deserialized to
            // an empty list - silent data loss.
            if (!FlowValueComplete(_position) && !_isFinalBlock)
                throw new IncompleteInputException();
            _position++;
            _inFlowSeq = true;
            _flowSeqStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }
        if (nextIndent > lineIndent)
        {
            // If a pending anchor exists, defer it for mapping anchor accumulation.
            // Without this, StoreAnchorIfNeeded() called on the first inner scalar
            // would incorrectly store it as a scalar anchor.
            if (_pendingAnchorName is not null)
            {
                _nextAnchorName = _pendingAnchorName;
                _pendingAnchorName = null;
                _curPairCount = 0;
            }
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
            _valueWasQuoted = true;
            if (q == (byte)'"')
                _valueSpan = ReadDoubleQuotedStringSpan();
            else
                _valueSpan = ReadSingleQuotedStringSpan();
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
                _valueSpan = StripInlineComment(_data[vs2.._position]);
                SkipNewlineSpan();
            }
        }
        _tokenType = TokenType.PropertyName;
        StoreAnchorIfNeeded();
        AccumulateMappingPair();
        return true;
    }

    /// <summary>
    /// Tokenizes a flow sequence (<c>[1, 2, 3]</c>) into the same shape a block
    /// sequence produces: an <c>ObjectStart</c> wrapper, one <c>String</c> token per
    /// element, then <c>ObjectEnd</c>. The generated deserializer therefore needs no
    /// change - it already consumes that shape.
    /// </summary>
    private bool ReadFlowSeqSpan()
    {
        if (!_flowSeqStartEmitted)
        {
            _flowSeqStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }

        // Separators: spaces, line breaks (flow sequences may span lines), commas
        // (a trailing comma before ']' is legal YAML) and comments.
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
            {
                _position++;
                continue;
            }
            if (b == (byte)'#')
            {
                while (
                    _position < _data.Length && _data[_position] is not ((byte)'\n' or (byte)'\r')
                )
                    _position++;
                continue;
            }
            break;
        }

        if (_position >= _data.Length)
        {
            // Still open: a non-final chunk must wait for the closing bracket.
            if (!_isFinalBlock)
                throw new IncompleteInputException();
            _inFlowSeq = false;
            _flowSeqStartEmitted = false;
            _valueSpan = default;
            _valueWasQuoted = false;
            _seqItemWasBareDash = false;
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        if (_data[_position] == (byte)']')
        {
            _position++;
            _inFlowSeq = false;
            _flowSeqStartEmitted = false;
            _valueSpan = default;
            _valueWasQuoted = false;
            _seqItemWasBareDash = false;
            SkipNewlineSpan();
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        if (_data[_position] == (byte)'[')
        {
            // Nested flow is not wired into the generated nested-sequence reader yet
            // (it expects the block form's bare-dash marker). Fail loudly rather than
            // quietly emit empty inner lists - the failure mode this reader just fixed.
            throw new FormatException(
                "Nested flow sequences are not supported yet: write the outer sequence in block style."
            );
        }

        if (_data[_position] is (byte)'\"' or (byte)'\'')
        {
            var q = _data[_position];
            _position++;
            _valueWasQuoted = true;
            _valueSpan =
                q == (byte)'\"' ? ReadDoubleQuotedStringSpan() : ReadSingleQuotedStringSpan();
        }
        else
        {
            var startElem = _position;
            while (
                _position < _data.Length
                && _data[_position] is not ((byte)',' or (byte)']' or (byte)'\n' or (byte)'\r')
            )
                _position++;
            _valueSpan = TrimEnd(_data[startElem.._position]);
            _valueWasQuoted = false;
        }

        _seqItemWasBareDash = false;
        _tokenType = TokenType.String;
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
        // A QUOTED key: `"n\nline": 2`. The writer quotes any key that is not a safe
        // plain scalar (a separator, a quote, a control character), so the scan has to
        // honour the closing quote: a `:` INSIDE the key would otherwise end the key, and
        // the escapes would come back as literal backslash sequences. The content is
        // exact, so it is NOT trimmed — a quoted key may legitimately end with a space
        // (spec §3 S9 x yaml).
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            _keySpan = ReadDoubleQuotedStringSpan();
            while (_position < _data.Length && _data[_position] == (byte)' ')
                _position++;
        }
        else
        {
            int ks = _position;
            // Line-bound key scan: never consume past \n/\r (audit BUG-01).
            while (
                _position < _data.Length
                && _data[_position] != (byte)':'
                && _data[_position] is not ((byte)'\n' or (byte)'\r')
            )
                _position++;
            _keySpan = TrimEnd(_data[ks.._position]);
        }
        if (_position >= _data.Length && !_isFinalBlock)
            throw new IncompleteInputException();
        if (_position >= _data.Length || _data[_position] != (byte)':')
            throw new FormatException("Invalid YAML mapping line: expected ':'.");
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
            _valueWasQuoted = true;
            if (q == (byte)'"')
                _valueSpan = ReadDoubleQuotedStringSpan();
            else
                _valueSpan = ReadSingleQuotedStringSpan();
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
            _valueSpan = StripInlineComment(_data[vs.._position]);
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

    // ── Double-quoted string scan + unescape (YAML spec: \" \\ \n \r \t \0 \b \f \a \v \e \/ \xXX \uXXXX \UXXXXXXXX) ──

    /// <summary>
    /// Scans a YAML double-quoted scalar starting just after the opening quote.
    /// Honours backslash escapes so \" does not terminate the value, then
    /// unescapes the content. Single-quoted scalars use a separate (''-doubled)
    /// path and are not handled here.
    /// </summary>
    /// <summary>
    /// True when the flow collection starting at <paramref name="start"/>
    /// (a '{' or '[') has its matching close inside the buffer; quotes and
    /// comments are honoured.
    /// </summary>
    private bool FlowValueComplete(int start)
    {
        int i = start;
        int depth = 0;
        while (i < _data.Length)
        {
            byte b = _data[i];
            if (b == (byte)35)
            {
                while (i < _data.Length && _data[i] != (byte)10 && _data[i] != (byte)13)
                    i++;
                continue;
            }
            if (b == (byte)34 || b == (byte)39)
            {
                byte q = b;
                i++;
                while (i < _data.Length && _data[i] != q)
                {
                    if (q == (byte)34 && _data[i] == (byte)92)
                        i++;
                    i++;
                }
                if (i >= _data.Length)
                    return false;
                i++;
                continue;
            }
            if (b == (byte)123 || b == (byte)91)
                depth++;
            else if (b == (byte)125 || b == (byte)93)
            {
                depth--;
                if (depth == 0)
                    return true;
            }
            i++;
        }
        return false;
    }

    private ReadOnlySpan<byte> ReadDoubleQuotedStringSpan()
    {
        int start = _position;
        bool hasEscape = false;
        while (_position < _data.Length)
        {
            byte b = _data[_position];
            if (b == (byte)'"')
                break;
            if (b == (byte)'\\')
            {
                hasEscape = true;
                _position++;
                if (_position < _data.Length)
                    _position++;
                continue;
            }
            _position++;
        }
        if (_position >= _data.Length && !_isFinalBlock)
            throw new IncompleteInputException();
        var content = _data[start.._position];
        if (_position < _data.Length)
            _position++; // consume closing quote
        if (!hasEscape)
            return content;
        return UnescapeDoubleQuoted(content);
    }

    private ReadOnlySpan<byte> UnescapeDoubleQuoted(ReadOnlySpan<byte> src)
    {
        // Every escape sequence is at least as many bytes as its UTF-8 output, so a
        // buffer of src.Length is always large enough (\uXXXX 6→4, \UXXXXXXXX 10→4).
        var buf = RentBuf(src.Length);
        int di = 0;
        int si = 0;
        while (si < src.Length)
        {
            if (src[si] == (byte)'\\' && si + 1 < src.Length)
            {
                si++;
                byte esc = src[si];
                si++;
                switch (esc)
                {
                    case (byte)'"':
                        buf[di++] = (byte)'"';
                        break;
                    case (byte)'\\':
                        buf[di++] = (byte)'\\';
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
                    case (byte)'0':
                        buf[di++] = 0;
                        break;
                    case (byte)'b':
                        buf[di++] = (byte)'\b';
                        break;
                    case (byte)'f':
                        buf[di++] = (byte)'\f';
                        break;
                    case (byte)'a':
                        buf[di++] = 0x07;
                        break; // \a BEL
                    case (byte)'v':
                        buf[di++] = 0x0B;
                        break; // \v VT
                    case (byte)'e':
                        buf[di++] = 0x1B;
                        break; // \e ESC
                    case (byte)'/':
                        buf[di++] = (byte)'/';
                        break;
                    case (byte)' ':
                        buf[di++] = (byte)' ';
                        break;
                    case (byte)'x':
                        di = TextHelpers.AppendCodepoint(
                            buf,
                            di,
                            TextHelpers.ReadHexEscape(src, ref si, 2)
                        );
                        break;
                    case (byte)'u':
                        // A supplementary character may be written as two escapes
                        // (\uD83D\uDE00): join the surrogate pair before encoding.
                        di = TextHelpers.AppendCodepoint(
                            buf,
                            di,
                            TextHelpers.ReadUnicodeEscape(src, ref si)
                        );
                        break;
                    case (byte)'U':
                        di = TextHelpers.AppendCodepoint(
                            buf,
                            di,
                            TextHelpers.ReadHexEscape(src, ref si, 8)
                        );
                        break;
                    default:
                        buf[di++] = esc;
                        break; // passthrough unknown escape
                }
            }
            else
            {
                buf[di++] = src[si++];
            }
        }
        return buf.AsSpan(0, di);
    }

    private ReadOnlySpan<byte> ReadSingleQuotedStringSpan()
    {
        // opening ' already consumed, _position at first content byte.
        // YAML '' inside a single-quoted string represents a literal '.
        int start = _position;
        bool hasDoubleQuote = false;
        while (_position < _data.Length)
        {
            if (_data[_position] == (byte)'\'')
            {
                if (_position + 1 < _data.Length && _data[_position + 1] == (byte)'\'')
                {
                    hasDoubleQuote = true;
                    _position += 2;
                    continue;
                }
                break; // closing single quote
            }
            _position++;
        }
        if (_position >= _data.Length && !_isFinalBlock)
            throw new IncompleteInputException();
        var content = _data[start.._position];
        if (_position < _data.Length)
            _position++; // consume closing quote
        if (!hasDoubleQuote)
            return content;
        // Compact '' → '
        var buf = RentBuf(content.Length);
        int di = 0;
        int si = 0;
        while (si < content.Length)
        {
            if (
                content[si] == (byte)'\''
                && si + 1 < content.Length
                && content[si + 1] == (byte)'\''
            )
            {
                buf[di++] = (byte)'\'';
                si += 2;
            }
            else
            {
                buf[di++] = content[si++];
            }
        }
        return buf.AsSpan(0, di);
    }

    // ── Sequence-mode double-quoted string scan + unescape ──

    /// <summary>
    /// Sequence-mode counterpart of <see cref="ReadDoubleQuotedStringSpan"/>.
    /// Reads a YAML double-quoted scalar (opening quote already consumed) from
    /// the sequence into a rented buffer, honouring \" so it does not terminate
    /// the value, then unescapes when any backslash was seen.
    /// </summary>
    private ReadOnlySpan<byte> ReadDoubleQuotedStringSeq()
    {
        var buf = RentBuf(256);
        int di = 0;
        bool hasEscape = false;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"')
        {
            byte b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b == (byte)'\\')
            {
                hasEscape = true;
                if (di + 2 > buf.Length)
                    buf = GrowYamlSeqBuf(buf, di);
                buf[di++] = b;
                _seqReader.Advance(1);
                if (!_seqReader.End)
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                continue;
            }
            if (di >= buf.Length)
                buf = GrowYamlSeqBuf(buf, di);
            buf[di++] = b;
            _seqReader.Advance(1);
        }
        if (!_seqReader.End)
            _seqReader.Advance(1); // consume closing quote
        var content = buf.AsSpan(0, di);
        if (!hasEscape)
            return content;
        return UnescapeDoubleQuoted(content);
    }

    private byte[] GrowYamlSeqBuf(byte[] buf, int di)
    {
        if (buf.Length > 131072)
            throw new FormatException("YAML double-quoted string exceeds maximum length");
        var nb = RentBuf(buf.Length * 2);
        buf.AsSpan(0, di).CopyTo(nb);
        return nb;
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
                _valueSpan = StripInlineComment(buf.AsSpan(0, di));
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
                    _valueSpan = StripInlineComment(cvBuf.AsSpan(0, cvd));
                }
                SkipNewlineSeq();
                _tokenType = TokenType.PropertyName;
                return true;
            }

            // Key: value
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
            {
                // Quoted key, sequence-mode half (see ReadSpan): honour the closing
                // quote and unescape, so a key carrying `:` or a newline survives.
                _seqReader.Advance(1);
                _keySpan = ReadDoubleQuotedStringSeq();
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' '
                )
                    _seqReader.Advance(1);
            }
            else
            {
                var keyBuf = RentBuf(256);
                int kd = 0;
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)':'
                )
                {
                    keyBuf[kd++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                _keySpan = TrimEnd(keyBuf.AsSpan(0, kd));
            }
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

            // Handle double-quoted value with escape support
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
            {
                _seqReader.Advance(1);
                _valueWasQuoted = true;
                _valueSpan = ReadDoubleQuotedStringSeq();
                SkipNewlineSeq();
                StoreAnchorIfNeeded();
                AccumulateMappingPair();
                _tokenType = TokenType.PropertyName;
                return true;
            }

            var valBuf = RentBuf(256);
            int vd = 0;
            if (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\''
            )
            {
                _seqReader.Advance(1);
                _valueWasQuoted = true;
                while (
                    !_seqReader.End
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
            _valueSpan = StripInlineComment(valBuf.AsSpan(0, vd));
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
            _valueWasQuoted = true;
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
        _valueSpan = StripInlineComment(valBuf.AsSpan(0, di));
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

    private const byte Space = (byte)' ';
    private const byte Tab = 9;

    /// <summary>
    /// Drops a trailing inline comment: a '#' that starts the span or follows
    /// whitespace (the YAML rule — "v#note" stays intact). Quoted scalars never
    /// reach here; they are scanned by the quoted-string readers.
    /// </summary>
    private static ReadOnlySpan<byte> StripInlineComment(ReadOnlySpan<byte> s)
    {
        var t = Trim(s);
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] != (byte)'#')
                continue;
            if (i > 0 && t[i - 1] != Space && t[i - 1] != Tab)
                continue;
            return TrimEnd(t[..i]);
        }
        return t;
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

    /// <summary>
    /// True when a sequence item's text (everything after the dash, up to the line end)
    /// carries no inline value: only whitespace, or whitespace and a trailing comment.
    /// That is exactly the "my value is the block below" case — <c>-</c>, <c>-   </c> and
    /// <c>- # note</c>, but never <c>- ""</c>, <c>- *alias</c> or <c>- 1</c>.
    /// </summary>
    private static bool HasNoInlineValue(ReadOnlySpan<byte> itemText)
    {
        foreach (var b in itemText)
        {
            if (b is Space or Tab or (byte)'\r')
                continue;
            // A '#' here opens an inline comment (the dash already consumed its
            // separator), so the rest of the line is trivia, not a value.
            return b == (byte)'#';
        }
        return true;
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
            // Fall through — accumulate the current pair too.
            // Without this, the first pair after _nextAnchorName is lost.
        }
        if (_pendingMappingAnchor is not null)
        {
            if (_curPairCount >= MaxAnchorPairs)
                throw new FormatException(
                    $"Too many key-value pairs in anchored mapping (max {MaxAnchorPairs}) at offset {BytesConsumed}"
                );
            // Use dedicated accumulator fields (_accK/V*) separate from anchor 0's storage.
            // This prevents corruption of anchor 0 when a second anchored mapping accumulates.
            AddPairToAnchor(
                ref _curPairCount,
                ref _accK0,
                ref _accV0,
                ref _accK1,
                ref _accV1,
                ref _accK2,
                ref _accV2,
                ref _accK3,
                ref _accV3,
                ref _accK4,
                ref _accV4,
                ref _accK5,
                ref _accV5,
                ref _accK6,
                ref _accV6,
                ref _accK7,
                ref _accV7,
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
            _accK0,
            _accV0,
            _accK1,
            _accV1,
            _accK2,
            _accV2,
            _accK3,
            _accV3,
            _accK4,
            _accV4,
            _accK5,
            _accV5,
            _accK6,
            _accV6,
            _accK7,
            _accV7
        );
        _pendingMappingAnchor = null;
        _curPairCount = 0;
    }

    private (byte[] Key, byte[] Value) GetAccumulatorPair(int i)
    {
        return i switch
        {
            0 => (_accK0!, _accV0!),
            1 => (_accK1!, _accV1!),
            2 => (_accK2!, _accV2!),
            3 => (_accK3!, _accV3!),
            4 => (_accK4!, _accV4!),
            5 => (_accK5!, _accV5!),
            6 => (_accK6!, _accV6!),
            _ => (_accK7!, _accV7!),
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
            long v = 0;
            do
            {
                v = v * 10 + (b - (byte)'0');
                // -2147483648 is a legal int32; anything beyond bails out
                // instead of silently wrapping (review P3-3).
                if (v > int.MaxValue && !(neg && v == (long)int.MaxValue + 1))
                    return 0;
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
                int digit = b - (byte)'0';
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
