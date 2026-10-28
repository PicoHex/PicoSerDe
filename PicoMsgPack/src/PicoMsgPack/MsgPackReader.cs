namespace PicoMsgPack;

/// <summary>Captures MsgPack reader state for pause/resume across chunks.</summary>
public struct MsgPackReaderState
{
    internal int Depth;
    internal long BytesConsumed;
    internal SequencePosition Position;
    internal int[]? ElementStack;
    internal bool[]? IsMapStack;
}

public ref struct MsgPackReader : ITokenReader, IFusedTokenReader
{
    // Span mode
    private ReadOnlySpan<byte> _data;
    private int _position;

    // Sequence mode
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;

    // Common
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _valueSpan;
    private byte[]? _rentedBuffer;
    private byte _singleByte; // inline field used via MemoryMarshal.CreateSpan for zero-allocation single-byte values
    private IntStack64 _elementStack;
    private byte _tag; // for Extension tokens
    private int _depth;
    private BoolStack64 _isMapStack;

    // ── Counted scope (task perf/mp-counted-scope) ──
    // The innermost open container's element accounting lives in these two scalars
    // instead of on the level stacks. A scope opens at the container's start token --
    // the wire header carries its element count, so the reader already knows it -- and
    // closes when that count runs out. Inside a scope one token costs one decrement of
    // `_topRemaining`, in place of the read-modify-write of `_elementStack[_depth - 1]`
    // plus the `_isMapStack`/`_expectKeyStack` double indexing (and their two stack
    // writes) that every token used to pay; that accounting is what the attribution
    // report priced inside `Read()` (perf-mp-collection §6.2).
    //
    //   _topRemaining > 0  -> this many element tokens still belong to the scope;
    //   _topRemaining == 0 -> the scope is exhausted, so the next Read() owes its end
    //                         token (ObjectEnd/ArrayEnd -- `_topIsMap` picks). Only
    //                         meaningful while `_depth > 0`.
    //
    // The stacks stay authoritative at scope boundaries only: `_elementStack[_depth - 1]`
    // is refreshed when a nested scope is pushed onto it and read back when that scope
    // pops, which is what keeps any interleaving of containers behaving as before.
    // `_isMapStack` stays a stack because each enclosing level's end-token kind is needed
    // when it closes.
    //
    // The map key/value alternation is NOT tracked in a third stack: a map level's
    // counter counts remaining key+value tokens, so it is always even at a key, and
    // `_topRemaining`'s parity therefore *is* "a key is due". Parity replaces the flip.
    private int _topRemaining;
    private bool _topIsMap;

    // #5 (task perf/mp-collection): the integer value of the current token is
    // derived inside ReadSequential()/ReadSpan(), where the tag byte is already
    // in hand, so TryGetInt32/TryGetInt64 do not have to re-derive it from
    // TokenType *and* _valueSpan.Length (two switches plus a call). _intKind:
    // 0 = the token reported by the last successful Read() is not an integer (or
    // no token has been read yet), 1 = _intValue holds its exact value,
    // 2 = it is an integer whose value does not fit a long (UInt64 above
    // long.MaxValue: both int accessors must report failure). The cache is
    // invalidated wherever _tokenType is set, so it can never describe a
    // different token than TokenType does -- including after a failed Read(),
    // where both keep describing the last returned token, as before. Both fields
    // are auto-defaulted by the struct constructors, like _singleByte.
    private long _intValue;
    private byte _intKind;

    private const int MaxDepth = 64;
    private readonly bool _isFinalBlock;
    private bool _needsMoreData;

    public bool NeedsMoreData => _needsMoreData;
    public int Depth => _depth;

    public MsgPackReader(ReadOnlySpan<byte> data, bool isFinalBlock = true)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _isFinalBlock = isFinalBlock;
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _valueSpan = default;
        _rentedBuffer = null;
        // _singleByte is init to 0 by default, no allocation needed
        _elementStack = default;
        _tag = 0;
        _isMapStack = default;
        _depth = 0;
        _topRemaining = 0;
        _topIsMap = false;
    }

    public MsgPackReader(ReadOnlySequence<byte> data, bool isFinalBlock = true)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _isFinalBlock = isFinalBlock;
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _valueSpan = default;
        _rentedBuffer = null;
        // _singleByte is init to 0 by default, no allocation needed
        _elementStack = default;
        _tag = 0;
        _isMapStack = default;
        _depth = 0;
        _topRemaining = 0;
        _topIsMap = false;
    }

    /// <summary>Exports parser state for later resumption.</summary>
    public readonly MsgPackReaderState ExportState()
    {
        var s = new MsgPackReaderState
        {
            Depth = _depth,
            BytesConsumed = _seqReader.Consumed,
            Position = _seqReader.Position,
        };
        if (_depth > 0)
        {
            s.ElementStack = new int[_depth];
            s.IsMapStack = new bool[_depth];
            for (int i = 0; i < _depth; i++)
            {
                // The innermost level's counter is the live counted scope, not its stack
                // slot (a slot is only refreshed when the level is buried under a nested
                // one), so the export reads it from the scope. This method is `readonly`:
                // the snapshot is adjusted, never this reader.
                s.ElementStack[i] = i == _depth - 1 ? _topRemaining : _elementStack[i];
                s.IsMapStack[i] = _isMapStack[i];
            }
        }
        return s;
    }

    /// <summary>Resumes from a saved state.</summary>
    public MsgPackReader(ReadOnlySequence<byte> data, bool isFinalBlock, MsgPackReaderState state)
        : this(data, isFinalBlock)
    {
        _depth = state.Depth;
        _needsMoreData = false;
        _topRemaining = 0;
        _topIsMap = false;
        if (_depth > 0 && state.ElementStack is not null && state.IsMapStack is not null)
        {
            for (int i = 0; i < _depth; i++)
            {
                _elementStack[i] = state.ElementStack[i];
                _isMapStack[i] = state.IsMapStack[i];
            }
            // Resume re-opens the innermost scope from its saved counter; the enclosing
            // levels' counters are already flushed into their own slots.
            _topRemaining = _elementStack[_depth - 1];
            _topIsMap = _isMapStack[_depth - 1];
        }
    }

    public TokenType TokenType => _tokenType;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    /// <summary>Direct buffer access for optimized generated code (span mode only).</summary>
    public ReadOnlySpan<byte> RawBuffer => _isSequence ? default : _data;

    /// <summary>Direct position access for optimized generated code (span mode only).</summary>
    public int RawPos => _isSequence ? -1 : _position;

    public void SetRawPos(int pos)
    {
        _position = pos;
    }

    public ReadOnlySpan<byte> GetStringRaw() => _valueSpan;

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

    private void Advance(int count)
    {
        if (_isSequence)
            _seqReader.Advance(count);
        else
            _position += count;
    }

    private ReadOnlySpan<byte> ReadBytes(long count)
    {
        // Length prefixes are accumulated in long so a hostile 4-byte length
        // cannot wrap negative; an over-long value must fail loudly as a
        // FormatException instead of surfacing as ArgumentOutOfRangeException
        // from Slice/Rent.
        if (count < 0 || count > int.MaxValue || count > _data.Length + (long)_seqReader.Remaining)
            throw new FormatException($"Invalid length prefix {count} at offset {BytesConsumed}");
        if (_isSequence)
        {
            if (_seqReader.Remaining < count)
                throw new FormatException(
                    $"Expected {count} bytes, only {_seqReader.Remaining} remaining"
                );
            var buf = RentBuf((int)count);
            _seqReader.TryCopyTo(buf.AsSpan(0, (int)count));
            _seqReader.Advance((int)count);
            return buf.AsSpan(0, (int)count);
        }
        if (_position + count > _data.Length)
            throw new FormatException(
                $"Expected {count} bytes at offset {_position}, only {_data.Length - _position} remaining"
            );
        var span = _data.Slice(_position, (int)count);
        _position += (int)count;
        return span;
    }

    private long ReadByteLen(int bytes)
    {
        if (_isSequence)
        {
            if (_seqReader.Remaining < bytes)
                throw new FormatException(
                    $"Expected {bytes} length bytes, only {_seqReader.Remaining} remaining"
                );
            long v = 0;
            for (int i = 0; i < bytes; i++)
            {
                _seqReader.TryRead(out byte b);
                v = (v << 8) | b;
            }
            return v;
        }
        if (_position + bytes > _data.Length)
            throw new FormatException(
                $"Expected {bytes} length bytes at offset {_position}, only {_data.Length - _position} remaining"
            );
        long vs = 0;
        for (int i = 0; i < bytes; i++)
            vs = (vs << 8) | _data[_position++];
        return vs;
    }

    private byte[] RentBuf(int size)
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
        var buf = ArrayPool<byte>.Shared.Rent(size);
        _rentedBuffer = buf;
        return buf;
    }

    // ── Element tracking (shared) ──

    /// <summary>
    /// Opens a counted scope for a container whose element count the wire header just
    /// supplied. The enclosing scope's remaining count is flushed into its stack slot
    /// first -- once per container, not once per token -- and is read back when this one
    /// pops.
    /// </summary>
    private void PushLevel(bool isMap, int count)
    {
        if (_depth >= MaxDepth)
            throw new FormatException($"Maximum depth of {MaxDepth} exceeded");
        if (_depth > 0)
            _elementStack[_depth - 1] = _topRemaining;
        _isMapStack[_depth] = isMap;
        _depth++;
        _topRemaining = count;
        _topIsMap = isMap;
    }

    /// <summary>
    /// Spends one element of the counted scope -- the entire per-token cost of element
    /// accounting. The map key/value flip it used to perform is gone: the parity of
    /// <see cref="_topRemaining"/> answers "is a key due" (see <see cref="ReadStringSpan"/>).
    /// </summary>
    private void CountElement()
    {
        if (_topRemaining > 0)
            _topRemaining--;
    }

    // ── Read ──

    /// <summary>
    /// Computes the total byte length of the token starting at the current
    /// position (header + payload), or -1 when the length bytes are not yet
    /// fully available. Used to avoid tokenizing partial values in streaming
    /// sequence mode.
    /// </summary>
    private int PeekTokenLength()
    {
        byte b;
        if (!_seqReader.TryPeek(0, out b))
            return -1;

        switch (b)
        {
            case <= 0x7F:
            case >= 0xE0:
            case 0xC0:
            case 0xC2:
            case 0xC3:
            case 0xC1:
            case >= 0x80 and <= 0x9F:
                return 1;
            case >= 0xA0 and <= 0xBF:
                return 1 + (b & 0x1F);
            case 0xCC:
            case 0xD0:
                return 2;
            case 0xCD:
            case 0xD1:
            case 0xDC:
                return 3;
            case 0xCE:
            case 0xD2:
            case 0xCA:
            case 0xDD:
            case 0xDF:
                return 5;
            case 0xCF:
            case 0xD3:
            case 0xCB:
            case 0xDE:
                return 9;
            case 0xD4:
                return 3;
            case 0xD5:
                return 4;
            case 0xD6:
                return 6;
            case 0xD7:
                return 10;
            case 0xD8:
                return 18;
            case 0xD9:
                if (!_seqReader.TryPeek(1, out var len8))
                    return -1;
                return 2 + len8;
            case 0xC4:
                if (!_seqReader.TryPeek(1, out var bin8))
                    return -1;
                return 2 + bin8;
            case 0xC7:
                if (!_seqReader.TryPeek(1, out var ext8))
                    return -1;
                return 3 + ext8;
            case 0xDA:
            case 0xC5:
            case 0xC8:
            {
                if (!_seqReader.TryPeek(1, out var hi) || !_seqReader.TryPeek(2, out var lo))
                    return -1;
                var len16 = (hi << 8) | lo;
                return b switch
                {
                    0xDA or 0xC5 => 3 + len16,
                    _ => 4 + len16,
                };
            }
            case 0xDB:
            case 0xC6:
            case 0xC9:
            {
                uint len32 = 0;
                for (int i = 1; i <= 4; i++)
                {
                    if (!_seqReader.TryPeek(i, out var lb))
                        return -1;
                    len32 = (len32 << 8) | lb;
                }
                var total = b switch
                {
                    0xDB or 0xC6 => 5L + len32,
                    _ => 6L + len32,
                };
                return total > int.MaxValue ? -1 : (int)total;
            }
        }
    }

    public bool Read()
    {
        // Task 4e: the span path (byte[] / ReadOnlySpan overloads, i.e. every
        // non-streaming deserialize) tokenizes straight out of _data; the
        // sequence path (streaming) is the untouched original state machine.
        // `_isSequence` is readonly per instance, so this branch is resolved
        // by the predictor on the first call.
        if (!_isSequence)
            return ReadSpan();
        return ReadSequential();
    }

    private bool ReadSpan()
    {
        // Mechanically derived from the original Read() body by
        // PicoMsgPack/tools/readspan/derive_readspan.py -- do not edit by
        // hand: regenerate it (same input, same output, `--write` is
        // idempotent). Every byte access is a direct index into _data (no
        // IsAtEnd / PeekByte / AdvanceByte / ReadBytes mode branch). Token
        // types, _valueSpan contents, _tag, _depth accounting, BytesConsumed
        // and every exception (type, order and message) are unchanged.
        _needsMoreData = false;
        // The counted scope is exhausted: this read owes the end token of the level it
        // was opened for (a container with zero elements never enters an element read,
        // so it lands here too). The end-token kind comes from the scope itself, and the
        // enclosing scope's counter is read back from the stack slot it was flushed into
        // when this one was pushed.
        if (_depth > 0 && _topRemaining == 0)
        {
            _tokenType = _topIsMap ? TokenType.ObjectEnd : TokenType.ArrayEnd;
            _depth--;
            _intKind = 0;
            if (_depth > 0)
            {
                _topRemaining = _elementStack[_depth - 1];
                _topIsMap = _isMapStack[_depth - 1];
            }
            else
            {
                _topRemaining = 0;
                _topIsMap = false;
            }
            // Span mode: once the root container closes, any remaining byte
            // is trailing garbage (review P2-4). Sequence mode keeps the
            // streaming semantics untouched.
            if (_depth == 0 && _position < _data.Length)
                throw TrailingGarbage(_position);
            return true;
        }
        if (_position >= _data.Length)
        {
            _needsMoreData = !_isFinalBlock;
            return false;
        }

        // #5: the token about to be produced is an integer only if one of the
        // integer arms below says so.
        _intKind = 0;
        var b = _data[_position];
        _position++; // consume tag byte

        switch (b)
        {
            // Positive fixint 0x00-0x7F
            case <= 0x7F:
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = b;
                _intKind = 1;
                CountElement();
                return true;
            // Fixmap 0x80-0x8F
            case >= 0x80 and <= 0x8F:
                CountElement();
                PushLevel(true, (b & 0x0F) * 2);
                _tokenType = TokenType.ObjectStart;
                return true;
            // Fixarray 0x90-0x9F
            case >= 0x90 and <= 0x9F:
                CountElement();
                PushLevel(false, b & 0x0F);
                _tokenType = TokenType.ArrayStart;
                return true;
            // Fixstr 0xA0-0xBF
            case >= 0xA0 and <= 0xBF:
                return ReadStringSpan(b & 0x1F);
            // Negative fixint 0xE0-0xFF
            case >= 0xE0:
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = (sbyte)b;
                _intKind = 1;
                CountElement();
                return true;
            default:
                switch (b)
                {
                    case 0xC0:
                        _tokenType = TokenType.Null;
                        CountElement();
                        return true;
                    case 0xC2:
                    case 0xC3:
                        _singleByte = b;
                        _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                        _tokenType = TokenType.Bool;
                        CountElement();
                        return true;
                    case 0xCC:
                        _valueSpan = ReadBytesSpan(1);
                        _tokenType = TokenType.UInt8;
                        _intValue = _valueSpan[0];
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCD:
                        _valueSpan = ReadBytesSpan(2);
                        _tokenType = TokenType.UInt16;
                        _intValue = BinaryPrimitives.ReadUInt16BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCE:
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.UInt32;
                        _intValue = BinaryPrimitives.ReadUInt32BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCF:
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.UInt64;
                        CacheUInt64(BinaryPrimitives.ReadUInt64BigEndian(_valueSpan));
                        CountElement();
                        return true;
                    case 0xD0:
                        _valueSpan = ReadBytesSpan(1);
                        _tokenType = TokenType.Int32;
                        _intValue = (sbyte)_valueSpan[0];
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xD1:
                        _valueSpan = ReadBytesSpan(2);
                        _tokenType = TokenType.Int32;
                        _intValue = BinaryPrimitives.ReadInt16BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xD2:
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.Int32;
                        _intValue = BinaryPrimitives.ReadInt32BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xD3:
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.Int64;
                        _intValue = BinaryPrimitives.ReadInt64BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCA:
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.Float32;
                        CountElement();
                        return true;
                    case 0xCB:
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.Float64;
                        CountElement();
                        return true;
                    // bin family
                    case 0xC4:
                        _valueSpan = ReadBytesSpan(ReadByteLenSpan(1));
                        _tokenType = TokenType.Bytes;
                        CountElement();
                        return true;
                    case 0xC5:
                        _valueSpan = ReadBytesSpan(ReadByteLenSpan(2));
                        _tokenType = TokenType.Bytes;
                        CountElement();
                        return true;
                    case 0xC6:
                        _valueSpan = ReadBytesSpan(ReadByteLenSpan(4));
                        _tokenType = TokenType.Bytes;
                        CountElement();
                        return true;
                    // ext family
                    case 0xD4:
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(1);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD5:
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(2);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD6:
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD7:
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD8:
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(16);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xC7:
                    {
                        var extLen = ReadByteLenSpan(1);
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(extLen);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    }
                    case 0xC8:
                    {
                        var extLen = ReadByteLenSpan(2);
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(extLen);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    }
                    case 0xC9:
                    {
                        var extLen = ReadByteLenSpan(4);
                        _tag = _data[_position];
                        _position++;
                        _valueSpan = ReadBytesSpan(extLen);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    }
                    case 0xD9:
                        return ReadStringSpan((int)ReadByteLenSpan(1));
                    case 0xDA:
                        return ReadStringSpan((int)ReadByteLenSpan(2));
                    case 0xDB:
                        return ReadStringSpan((int)Math.Min(ReadByteLenSpan(4), int.MaxValue));
                    case 0xDC:
                        CountElement();
                        PushLevel(false, (int)ReadByteLenSpan(2));
                        _tokenType = TokenType.ArrayStart;
                        return true;
                    case 0xDD:
                        CountElement();
                        PushLevel(false, (int)Math.Min(ReadByteLenSpan(4), int.MaxValue));
                        _tokenType = TokenType.ArrayStart;
                        return true;
                    case 0xDE:
                        CountElement();
                        PushLevel(true, (int)ReadByteLenSpan(2) * 2);
                        _tokenType = TokenType.ObjectStart;
                        return true;
                    case 0xDF:
                        CountElement();
                        PushLevel(true, (int)Math.Min(ReadByteLenSpan(4), int.MaxValue / 2) * 2);
                        _tokenType = TokenType.ObjectStart;
                        return true;
                    default:
                        throw UnknownByte(b, _position);
                }
        }
    }

    /// <summary>
    /// The two failure paths of <see cref="ReadSpan"/>, built out of line.
    /// Interpolating a string allocates a stack buffer inside whichever method
    /// formats it; when that happens in the tokenizer, every token pays to zero
    /// a ~112-byte frame (an inlining variant of this method measured a 1168-byte
    /// frame and a zeroing loop per token). Building the exception here keeps the
    /// hot frame small. Same exception type, same message.
    /// </summary>
    private static FormatException UnknownByte(byte b, int position) =>
        new($"Unknown MsgPack byte 0x{b:X2} at offset {position}");

    /// <summary>See <see cref="UnknownByte"/>.</summary>
    private static FormatException TrailingGarbage(int position) =>
        new($"Unexpected data after the document at offset {position}");

    private bool ReadSequential()
    {
        _needsMoreData = false;
        // The counted scope is exhausted: this read owes the end token of the level it
        // was opened for (a container with zero elements never enters an element read,
        // so it lands here too). The end-token kind comes from the scope itself, and the
        // enclosing scope's counter is read back from the stack slot it was flushed into
        // when this one was pushed.
        if (_depth > 0 && _topRemaining == 0)
        {
            _tokenType = _topIsMap ? TokenType.ObjectEnd : TokenType.ArrayEnd;
            _depth--;
            _intKind = 0;
            if (_depth > 0)
            {
                _topRemaining = _elementStack[_depth - 1];
                _topIsMap = _isMapStack[_depth - 1];
            }
            else
            {
                _topRemaining = 0;
                _topIsMap = false;
            }
            // Span mode: once the root container closes, any remaining byte
            // is trailing garbage (review P2-4). Sequence mode keeps the
            // streaming semantics untouched.
            if (_depth == 0 && !_isSequence && _position < _data.Length)
                throw new FormatException(
                    $"Unexpected data after the document at offset {_position}"
                );
            return true;
        }
        if (IsAtEnd())
        {
            _needsMoreData = !_isFinalBlock;
            return false;
        }

        // Streaming sequence mode: refuse to tokenize a partial value whose
        // payload bytes have not fully arrived yet.
        if (!_isFinalBlock && _isSequence)
        {
            int tokenLength = PeekTokenLength();
            if (tokenLength < 0 || _seqReader.Remaining < tokenLength)
            {
                _needsMoreData = true;
                return false;
            }
        }

        // #5: the token about to be produced is an integer only if one of the
        // integer arms below says so.
        _intKind = 0;
        var b = PeekByte();
        AdvanceByte(); // consume tag byte

        switch (b)
        {
            // Positive fixint 0x00-0x7F
            case <= 0x7F:
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = b;
                _intKind = 1;
                CountElement();
                return true;
            // Fixmap 0x80-0x8F
            case >= 0x80 and <= 0x8F:
                CountElement();
                PushLevel(true, (b & 0x0F) * 2);
                _tokenType = TokenType.ObjectStart;
                return true;
            // Fixarray 0x90-0x9F
            case >= 0x90 and <= 0x9F:
                CountElement();
                PushLevel(false, b & 0x0F);
                _tokenType = TokenType.ArrayStart;
                return true;
            // Fixstr 0xA0-0xBF
            case >= 0xA0 and <= 0xBF:
                return ReadString(b & 0x1F);
            // Negative fixint 0xE0-0xFF
            case >= 0xE0:
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = (sbyte)b;
                _intKind = 1;
                CountElement();
                return true;
            default:
                switch (b)
                {
                    case 0xC0:
                        _tokenType = TokenType.Null;
                        CountElement();
                        return true;
                    case 0xC2:
                    case 0xC3:
                        _singleByte = b;
                        _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                        _tokenType = TokenType.Bool;
                        CountElement();
                        return true;
                    case 0xCC:
                        _valueSpan = ReadBytes(1);
                        _tokenType = TokenType.UInt8;
                        _intValue = _valueSpan[0];
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCD:
                        _valueSpan = ReadBytes(2);
                        _tokenType = TokenType.UInt16;
                        _intValue = BinaryPrimitives.ReadUInt16BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCE:
                        _valueSpan = ReadBytes(4);
                        _tokenType = TokenType.UInt32;
                        _intValue = BinaryPrimitives.ReadUInt32BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCF:
                        _valueSpan = ReadBytes(8);
                        _tokenType = TokenType.UInt64;
                        CacheUInt64(BinaryPrimitives.ReadUInt64BigEndian(_valueSpan));
                        CountElement();
                        return true;
                    case 0xD0:
                        _valueSpan = ReadBytes(1);
                        _tokenType = TokenType.Int32;
                        _intValue = (sbyte)_valueSpan[0];
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xD1:
                        _valueSpan = ReadBytes(2);
                        _tokenType = TokenType.Int32;
                        _intValue = BinaryPrimitives.ReadInt16BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xD2:
                        _valueSpan = ReadBytes(4);
                        _tokenType = TokenType.Int32;
                        _intValue = BinaryPrimitives.ReadInt32BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xD3:
                        _valueSpan = ReadBytes(8);
                        _tokenType = TokenType.Int64;
                        _intValue = BinaryPrimitives.ReadInt64BigEndian(_valueSpan);
                        _intKind = 1;
                        CountElement();
                        return true;
                    case 0xCA:
                        _valueSpan = ReadBytes(4);
                        _tokenType = TokenType.Float32;
                        CountElement();
                        return true;
                    case 0xCB:
                        _valueSpan = ReadBytes(8);
                        _tokenType = TokenType.Float64;
                        CountElement();
                        return true;
                    // bin family
                    case 0xC4:
                        _valueSpan = ReadBytes(ReadByteLen(1));
                        _tokenType = TokenType.Bytes;
                        CountElement();
                        return true;
                    case 0xC5:
                        _valueSpan = ReadBytes(ReadByteLen(2));
                        _tokenType = TokenType.Bytes;
                        CountElement();
                        return true;
                    case 0xC6:
                        _valueSpan = ReadBytes(ReadByteLen(4));
                        _tokenType = TokenType.Bytes;
                        CountElement();
                        return true;
                    // ext family
                    case 0xD4:
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(1);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD5:
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(2);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD6:
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(4);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD7:
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(8);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xD8:
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(16);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    case 0xC7:
                    {
                        var extLen = ReadByteLen(1);
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(extLen);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    }
                    case 0xC8:
                    {
                        var extLen = ReadByteLen(2);
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(extLen);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    }
                    case 0xC9:
                    {
                        var extLen = ReadByteLen(4);
                        _tag = PeekByte();
                        AdvanceByte();
                        _valueSpan = ReadBytes(extLen);
                        _tokenType = TokenType.Extension;
                        CountElement();
                        return true;
                    }
                    case 0xD9:
                        return ReadString((int)ReadByteLen(1));
                    case 0xDA:
                        return ReadString((int)ReadByteLen(2));
                    case 0xDB:
                        return ReadString((int)Math.Min(ReadByteLen(4), int.MaxValue));
                    case 0xDC:
                        CountElement();
                        PushLevel(false, (int)ReadByteLen(2));
                        _tokenType = TokenType.ArrayStart;
                        return true;
                    case 0xDD:
                        CountElement();
                        PushLevel(false, (int)Math.Min(ReadByteLen(4), int.MaxValue));
                        _tokenType = TokenType.ArrayStart;
                        return true;
                    case 0xDE:
                        CountElement();
                        PushLevel(true, (int)ReadByteLen(2) * 2);
                        _tokenType = TokenType.ObjectStart;
                        return true;
                    case 0xDF:
                        CountElement();
                        PushLevel(true, (int)Math.Min(ReadByteLen(4), int.MaxValue / 2) * 2);
                        _tokenType = TokenType.ObjectStart;
                        return true;
                    default:
                        throw new FormatException(
                            $"Unknown MsgPack byte 0x{b:X2} at offset {BytesConsumed}"
                        );
                }
        }
    }

    // ── Span-mode payload helpers (twins of the mode-branched originals) ──

    /// <summary>
    /// <see cref="ReadBytes"/> for span mode. Both guards are kept, in the
    /// original order, with the original messages: a hostile length prefix
    /// must fail exactly as it did before. In span mode <c>_seqReader</c> is
    /// <c>default</c> (Remaining == 0), so the original "no longer than the
    /// buffer plus the unread sequence" bound is just <c>_data.Length</c>.
    /// </summary>
    private ReadOnlySpan<byte> ReadBytesSpan(long count)
    {
        if (count < 0 || count > int.MaxValue || count > _data.Length)
            throw new FormatException($"Invalid length prefix {count} at offset {_position}");
        if (_position + count > _data.Length)
            throw new FormatException(
                $"Expected {count} bytes at offset {_position}, only {_data.Length - _position} remaining"
            );
        var span = _data.Slice(_position, (int)count);
        _position += (int)count;
        return span;
    }

    /// <summary><see cref="ReadByteLen"/> for span mode.</summary>
    private long ReadByteLenSpan(int bytes)
    {
        if (_position + bytes > _data.Length)
            throw new FormatException(
                $"Expected {bytes} length bytes at offset {_position}, only {_data.Length - _position} remaining"
            );
        long vs = 0;
        for (int i = 0; i < bytes; i++)
            vs = (vs << 8) | _data[_position++];
        return vs;
    }

    /// <summary><see cref="ReadString"/> for span mode.</summary>
    private bool ReadStringSpan(int len)
    {
        _valueSpan = ReadBytesSpan(len);
        // "A key is due" is read off the counted scope instead of a flipped stack slot:
        // a map level counts remaining key+value tokens, so a key is due exactly when
        // that count is even (it starts at 2n and drops by one per token). Checked before
        // CountElement(), i.e. against the count that still includes this token.
        if (_depth > 0 && _topIsMap && (_topRemaining & 1) == 0)
            _tokenType = TokenType.PropertyName;
        else
            _tokenType = TokenType.String;
        CountElement();
        return true;
    }

    private bool ReadString(int len)
    {
        _valueSpan = ReadBytes(len);
        // See ReadStringSpan: parity of the counted scope, not a key/value flip.
        if (_depth > 0 && _topIsMap && (_topRemaining & 1) == 0)
            _tokenType = TokenType.PropertyName;
        else
            _tokenType = TokenType.String;
        CountElement();
        return true;
    }

    // ── Typed accessors ──

    /// <summary>
    /// Reads the current token as an <see cref="int"/>. The value was derived by
    /// <see cref="Read"/> already, so this is a range check on the cached value;
    /// a token that is not an integer, or a value outside the int range, still
    /// reports false exactly as the previous re-derivation did.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetInt32(out int v)
    {
        if (_intKind == 1)
        {
            long lv = _intValue;
            if (lv >= int.MinValue && lv <= int.MaxValue)
            {
                v = (int)lv;
                return true;
            }
        }
        v = 0;
        return false;
    }

    /// <summary>See <see cref="TryGetInt32"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetInt64(out long v)
    {
        if (_intKind == 1)
        {
            v = _intValue;
            return true;
        }
        v = 0;
        return false;
    }

    /// <summary>
    /// Caches a <c>uint64</c> token. A value above <see cref="long.MaxValue"/> does
    /// not fit a <c>long</c> (so it fits an <c>int</c> even less), and both accessors
    /// must report failure for it: the caller then raises FormatException instead of
    /// silently wrapping to a negative value. <see cref="TryGetUInt64"/> keeps
    /// reading the raw bytes, so the unsigned accessor still returns it in full.
    /// </summary>
    private void CacheUInt64(ulong uv)
    {
        if (uv > long.MaxValue)
        {
            _intKind = 2;
            return;
        }
        _intValue = (long)uv;
        _intKind = 1;
    }

    public bool TryGetUInt64(out ulong v)
    {
        if (_valueSpan.IsEmpty)
        {
            v = 0;
            return false;
        }
        if (
            _tokenType
            is TokenType.Int32
                or TokenType.UInt8
                or TokenType.UInt16
                or TokenType.UInt32
                or TokenType.Int64
                or TokenType.UInt64
        )
        {
            switch (_valueSpan.Length)
            {
                case 1:
                    // 0x00-0x7F positive fixint (Int32) or UInt8; 0xE0-0xFF negative fixint (Int64) is rejected
                    if (_tokenType == TokenType.Int64 && _valueSpan[0] >= 0x80)
                    {
                        v = 0;
                        return false;
                    }
                    v = _valueSpan[0];
                    return true;
                case 2:
                    if (_tokenType == TokenType.UInt16)
                    {
                        v = BinaryPrimitives.ReadUInt16BigEndian(_valueSpan);
                        return true;
                    }

                    {
                        var s = BinaryPrimitives.ReadInt16BigEndian(_valueSpan);
                        if (s < 0)
                        {
                            v = 0;
                            return false;
                        }
                        v = (ulong)s;
                        return true;
                    }
                case 4:
                    if (_tokenType == TokenType.UInt32)
                    {
                        v = BinaryPrimitives.ReadUInt32BigEndian(_valueSpan);
                        return true;
                    }

                    {
                        var i = BinaryPrimitives.ReadInt32BigEndian(_valueSpan);
                        if (i < 0)
                        {
                            v = 0;
                            return false;
                        }
                        v = (ulong)i;
                        return true;
                    }
                case 8:
                    if (_tokenType == TokenType.UInt64)
                    {
                        v = BinaryPrimitives.ReadUInt64BigEndian(_valueSpan);
                        return true;
                    }

                    {
                        var l = BinaryPrimitives.ReadInt64BigEndian(_valueSpan);
                        if (l < 0)
                        {
                            v = 0;
                            return false;
                        }
                        v = (ulong)l;
                        return true;
                    }
                default:
                    v = 0;
                    return false;
            }
        }
        v = 0;
        return false;
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != TokenType.Bool || _valueSpan.IsEmpty)
        {
            v = false;
            return false;
        }
        v = _valueSpan[0] == 0xC3;
        return true;
    }

    public bool TryGetExtension(out byte tag, out ReadOnlySpan<byte> data)
    {
        if (_tokenType != TokenType.Extension || _valueSpan.IsEmpty)
        {
            tag = 0;
            data = default;
            return false;
        }
        tag = _tag;
        data = _valueSpan;
        return true;
    }

    /// <summary>
    /// MessagePack carries the element count in every array/map header (fixarray/fixmap
    /// <c>0x90-0x9F</c>/<c>0x80-0x8F</c>, array16/32 <c>0xDC</c>/<c>0xDD</c>, map16/32
    /// <c>0xDE</c>/<c>0xDF</c>), so the count is always known right after a start token — the reader
    /// already stores it on its own level stack for the end-token decision.
    /// </summary>
    public readonly bool HasCountedCollections => true;

    /// <summary>
    /// How many element iterations the collection opened by the current token will still yield, read
    /// from the level stack the reader itself uses for its end-token decision — so a counted loop and a
    /// <see cref="Read"/>-driven loop produce the same token sequence.
    /// <para>
    /// <b>A map level counts key-value pairs, not tokens</b> (spec §3.1 contract, §3.3 MessagePack row):
    /// MessagePack stores <c>2n</c> elements for <c>n</c> pairs, so the level counter is halved — one
    /// <c>for</c> iteration is one pair. Reporting the raw counter would make the generated loop emit
    /// <c>2n</c> iterations over <c>n</c> pairs (spec RK-3). Map32's clamp
    /// (<c>Math.Min(len, int.MaxValue / 2) * 2</c>) is inherited, never re-derived here.
    /// </para>
    /// <para>
    /// <b>Only a start token answers.</b> No collection token was produced yet (<c>_depth == 0</c>), or the
    /// current token is a scalar or an end token ⇒ <c>false</c> with <paramref name="count"/> = 0. That is
    /// why the "odd level counter" state is not reachable here: right after a start token the level
    /// <c>_depth - 1</c> names has just been pushed, an array with <c>n</c> and a map with <c>2n</c>.
    /// </para>
    /// <para>
    /// <b>Never changes observable state</b> (spec §3.1): no position move, no change to
    /// <see cref="TokenType"/>/<see cref="Depth"/>/<see cref="NeedsMoreData"/>, no span invalidation.
    /// A truncated collection is not an error here — this is a query, not a read.
    /// </para>
    /// </summary>
    public readonly bool TryGetCollectionCount(out int count)
    {
        if (_depth == 0 || _tokenType is not (TokenType.ArrayStart or TokenType.ObjectStart))
        {
            count = 0;
            return false;
        }

        // The innermost level's counter is the live counted scope (see _topRemaining); a
        // stack slot would be stale until the level is buried under a nested container.
        count = _topIsMap ? _topRemaining / 2 : _topRemaining;
        return true;
    }

    /// <summary>
    /// Advances one token and reads it as an <see cref="int"/> — <c>Read() &amp;&amp; TryGetInt32()</c> in one
    /// dispatch (task T6, spec §3.3 MessagePack row).
    /// <para>
    /// <b>Where the fast path is allowed to differ.</b> Only the <i>success</i> branch forks: the streaming
    /// path, the end-token path (<c>_elementStack[..] == 0</c>) and every non-integer tag byte forward to
    /// <see cref="TokenReaderFused.TryReadInt32{T}"/>, i.e. to the literal <c>Read() &amp;&amp; TryGetInt32()</c>
    /// combination, so a token the fast path does not recognise cannot take a second code path at all
    /// (spec §3.3: <c>default: <see langword="return"/> 参考正本</c>).
    /// </para>
    /// <para>
    /// <b>Why an integer arm is still fully materialised</b> (spec §3.2.3, RK-2): the arms below write
    /// <see cref="TokenType"/>, the value span, the cached integer and the element accounting exactly as
    /// <see cref="ReadSpan"/> does, because "fused" must not mean "less state". What it saves is the
    /// <i>second</i> dispatch — the <see cref="Read"/> call plus the re-derivation inside
    /// <see cref="TryGetInt32"/> — not the state writes the rest of the reader depends on.
    /// </para>
    /// <para>
    /// <b>Range.</b> Every MessagePack integer encoding is decoded (positive/negative fixint, uint8/16/32/64,
    /// int8/16/32/64). A value outside <see cref="int.MinValue"/>..<see cref="int.MaxValue"/> returns
    /// <see langword="false"/> with <paramref name="value"/> = 0 <b>after</b> the token has been consumed and
    /// materialised — the state <c>Read() &amp;&amp; TryGetInt32()</c> leaves behind for the same token (it too
    /// consumes the token and then refuses the value), and never a truncated value.
    /// </para>
    /// <para>Not <c>readonly</c>: <c>ref this</c> in a readonly member is <c>CS1605</c>.</para>
    /// </summary>
    public bool TryReadInt32(out int value)
    {
        // Streaming keeps its own state machine untouched (spec §2.2 N4): the fused path is span-only.
        if (_isSequence)
            return TokenReaderFused.TryReadInt32(ref this, out value);

        // The end token the level stack owes: the container-close path (and its trailing-garbage
        // check) is never a value read, so it goes through the reference implementation verbatim.
        if (_depth > 0 && _topRemaining == 0)
            return TokenReaderFused.TryReadInt32(ref this, out value);

        // Both lines below are ReadSpan()'s prologue, in its order and with its result: a span-mode
        // read that finds no byte left reports a chunk boundary exactly as Read() would.
        if (_position >= _data.Length)
        {
            _needsMoreData = !_isFinalBlock;
            value = 0;
            return false;
        }

        _needsMoreData = false;
        var b = _data[_position];
        switch (b)
        {
            // Positive fixint 0x00-0x7F
            case <= 0x7F:
                _position++;
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = b;
                _intKind = 1;
                CountElement();
                value = b;
                return true;
            // Negative fixint 0xE0-0xFF
            case >= 0xE0:
                _position++;
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = (sbyte)b;
                _intKind = 1;
                CountElement();
                value = (sbyte)b;
                return true;
            default:
                switch (b)
                {
                    case 0xCC: // uint8
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(1);
                        _tokenType = TokenType.UInt8;
                        _intValue = _valueSpan[0];
                        _intKind = 1;
                        CountElement();
                        value = _valueSpan[0];
                        return true;
                    }
                    case 0xCD: // uint16
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(2);
                        _tokenType = TokenType.UInt16;
                        var uv16 = BinaryPrimitives.ReadUInt16BigEndian(_valueSpan);
                        _intValue = uv16;
                        _intKind = 1;
                        CountElement();
                        value = uv16;
                        return true;
                    }
                    case 0xCE: // uint32 — decoded in full, then range-checked
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.UInt32;
                        var uv32 = BinaryPrimitives.ReadUInt32BigEndian(_valueSpan);
                        _intValue = uv32;
                        _intKind = 1;
                        CountElement();
                        return Narrow(uv32, out value);
                    }
                    case 0xCF: // uint64 — CacheUInt64 keeps kind 2 above long.MaxValue, as ReadSpan does
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.UInt64;
                        var uv64 = BinaryPrimitives.ReadUInt64BigEndian(_valueSpan);
                        CacheUInt64(uv64);
                        CountElement();
                        return Narrow(uv64, out value);
                    }
                    case 0xD0: // int8
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(1);
                        _tokenType = TokenType.Int32;
                        var iv8 = (sbyte)_valueSpan[0];
                        _intValue = iv8;
                        _intKind = 1;
                        CountElement();
                        value = iv8;
                        return true;
                    }
                    case 0xD1: // int16
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(2);
                        _tokenType = TokenType.Int32;
                        var iv16 = BinaryPrimitives.ReadInt16BigEndian(_valueSpan);
                        _intValue = iv16;
                        _intKind = 1;
                        CountElement();
                        value = iv16;
                        return true;
                    }
                    case 0xD2: // int32
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.Int32;
                        var iv32 = BinaryPrimitives.ReadInt32BigEndian(_valueSpan);
                        _intValue = iv32;
                        _intKind = 1;
                        CountElement();
                        value = iv32;
                        return true;
                    }
                    case 0xD3: // int64 — decoded in full, then range-checked
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.Int64;
                        var iv64 = BinaryPrimitives.ReadInt64BigEndian(_valueSpan);
                        _intValue = iv64;
                        _intKind = 1;
                        CountElement();
                        return Narrow(iv64, out value);
                    }
                    // Not an integer token: the reference implementation decides, unchanged.
                    default:
                        return TokenReaderFused.TryReadInt32(ref this, out value);
                }
        }
    }

    /// <summary>
    /// Narrows an already decoded and materialised <c>uint32</c>/<c>uint64</c> token to
    /// <see cref="int"/>. Returning <see langword="false"/> is the same answer
    /// <c>Read() &amp;&amp; TryGetInt32()</c> gives for that token: the token was consumed and the state
    /// describes it, only the value is refused (never truncated).
    /// </summary>
    private static bool Narrow(ulong wide, out int value)
    {
        if (wide <= int.MaxValue)
        {
            value = (int)wide;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>See <see cref="Narrow(ulong, out int)"/> — the signed half of the same range check.</summary>
    private static bool Narrow(long wide, out int value)
    {
        if (wide >= int.MinValue && wide <= int.MaxValue)
        {
            value = (int)wide;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// See <see cref="Narrow(ulong, out int)"/> — the same range check for the <see cref="long"/> accessor:
    /// only <c>uint64</c> above <see cref="long.MaxValue"/> is refused.
    /// </summary>
    private static bool Narrow(ulong wide, out long value)
    {
        if (wide <= long.MaxValue)
        {
            value = (long)wide;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// Advances one token and reads it as a <see cref="long"/> — <c>Read() &amp;&amp; TryGetInt64()</c> in one
    /// dispatch (task T7, spec §3.3 MessagePack row, on the <c>switch</c> skeleton T6 established).
    /// <para>
    /// <b>Where the fast path is allowed to differ.</b> Only the <i>success</i> branch forks, exactly as in
    /// <see cref="TryReadInt32"/>: streaming, the end-token path and every tag byte that is not an
    /// integer family member forward to <see cref="TokenReaderFused.TryReadInt64{T}"/>, i.e. to the literal
    /// <c>Read() &amp;&amp; TryGetInt64()</c> combination.
    /// </para>
    /// <para>
    /// <b>Range.</b> Every MessagePack integer encoding fits <see cref="long"/> except <c>uint64</c> above
    /// <see cref="long.MaxValue"/>; that token is consumed and materialised first, then refused with
    /// <paramref name="value"/> = 0 — the answer <c>Read() &amp;&amp; TryGetInt64()</c> gives (it consumes the
    /// token too, and <see cref="CacheUInt64"/> keeps <c>_intKind</c> = 2 for it so both accessors refuse
    /// it), never a wrapped negative value.
    /// </para>
    /// <para>Not <c>readonly</c>: <c>ref this</c> in a readonly member is <c>CS1605</c>.</para>
    /// </summary>
    public bool TryReadInt64(out long value)
    {
        // Streaming keeps its own state machine untouched (spec §2.2 N4): the fused path is span-only.
        if (_isSequence)
            return TokenReaderFused.TryReadInt64(ref this, out value);

        // The end token the level stack owes: the container-close path (and its trailing-garbage
        // check) is never a value read, so it goes through the reference implementation verbatim.
        if (_depth > 0 && _topRemaining == 0)
            return TokenReaderFused.TryReadInt64(ref this, out value);

        // Both lines below are ReadSpan()'s prologue, in its order and with its result.
        if (_position >= _data.Length)
        {
            _needsMoreData = !_isFinalBlock;
            value = 0;
            return false;
        }

        _needsMoreData = false;
        // ReadSpan() resets the integer cache before its switch, so a payload that throws
        // half-way through a token leaves the same field state on both paths.
        _intKind = 0;
        var b = _data[_position];
        switch (b)
        {
            // Positive fixint 0x00-0x7F
            case <= 0x7F:
                _position++;
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = b;
                _intKind = 1;
                CountElement();
                value = b;
                return true;
            // Negative fixint 0xE0-0xFF
            case >= 0xE0:
                _position++;
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Int32;
                _intValue = (sbyte)b;
                _intKind = 1;
                CountElement();
                value = (sbyte)b;
                return true;
            default:
                switch (b)
                {
                    case 0xCC: // uint8
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(1);
                        _tokenType = TokenType.UInt8;
                        _intValue = _valueSpan[0];
                        _intKind = 1;
                        CountElement();
                        value = _valueSpan[0];
                        return true;
                    }
                    case 0xCD: // uint16
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(2);
                        _tokenType = TokenType.UInt16;
                        var uv16 = BinaryPrimitives.ReadUInt16BigEndian(_valueSpan);
                        _intValue = uv16;
                        _intKind = 1;
                        CountElement();
                        value = uv16;
                        return true;
                    }
                    case 0xCE: // uint32 — always fits, but decoded through the same path as uint64
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.UInt32;
                        var uv32 = BinaryPrimitives.ReadUInt32BigEndian(_valueSpan);
                        _intValue = uv32;
                        _intKind = 1;
                        CountElement();
                        value = uv32;
                        return true;
                    }
                    case 0xCF: // uint64 — materialised, then range-checked against long.MaxValue
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.UInt64;
                        var uv64 = BinaryPrimitives.ReadUInt64BigEndian(_valueSpan);
                        CacheUInt64(uv64);
                        CountElement();
                        return Narrow(uv64, out value);
                    }
                    case 0xD0: // int8
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(1);
                        _tokenType = TokenType.Int32;
                        var iv8 = (sbyte)_valueSpan[0];
                        _intValue = iv8;
                        _intKind = 1;
                        CountElement();
                        value = iv8;
                        return true;
                    }
                    case 0xD1: // int16
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(2);
                        _tokenType = TokenType.Int32;
                        var iv16 = BinaryPrimitives.ReadInt16BigEndian(_valueSpan);
                        _intValue = iv16;
                        _intKind = 1;
                        CountElement();
                        value = iv16;
                        return true;
                    }
                    case 0xD2: // int32
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(4);
                        _tokenType = TokenType.Int32;
                        var iv32 = BinaryPrimitives.ReadInt32BigEndian(_valueSpan);
                        _intValue = iv32;
                        _intKind = 1;
                        CountElement();
                        value = iv32;
                        return true;
                    }
                    case 0xD3: // int64 — every value of this encoding fits a long
                    {
                        _position++;
                        _valueSpan = ReadBytesSpan(8);
                        _tokenType = TokenType.Int64;
                        var iv64 = BinaryPrimitives.ReadInt64BigEndian(_valueSpan);
                        _intValue = iv64;
                        _intKind = 1;
                        CountElement();
                        value = iv64;
                        return true;
                    }
                    // Not an integer token: the reference implementation decides, unchanged.
                    default:
                        return TokenReaderFused.TryReadInt64(ref this, out value);
                }
        }
    }

    /// <summary>
    /// Advances one token and reads it as a <see cref="double"/> — <c>Read() &amp;&amp; TryGetFloat64()</c> in one
    /// dispatch (task T8, spec §3.3 MessagePack row).
    /// <para>
    /// <b>Which tokens succeed</b> is <see cref="TryGetFloat64"/>'s answer, not a decision made here:
    /// <c>float32</c>/<c>float64</c> (<i>this</i> fast path), and — through the forwarded branch — the
    /// <c>Int32</c>/<c>Int64</c> token family, which the reader has always served as an exact integer
    /// value. Unsigned tokens (<c>uint8/16/32/64</c>) answer <see langword="false"/> and keep their token
    /// current. Adding an integer arm here would give a second definition of that rule; the fast path
    /// therefore only fuses the two encodings whose payload <i>is</i> a float.
    /// </para>
    /// <para>
    /// <b>float32 → double is the same widening the reference performs</b>: both read the big-endian
    /// <c>int32</c> bit pattern, call <see cref="BitConverter.Int32BitsToSingle(int)"/> and widen implicitly
    /// (exact), so <c>NaN</c>, <c>±Inf</c> and <c>-0.0</c> keep their bit patterns on both arms. A float
    /// token is <b>not</b> an integer: <c>_intKind</c> is reset to 0 for it, exactly as
    /// <see cref="ReadSpan"/> does before its switch.
    /// </para>
    /// <para>Not <c>readonly</c>: <c>ref this</c> in a readonly member is <c>CS1605</c>.</para>
    /// </summary>
    public bool TryReadFloat64(out double value)
    {
        if (_isSequence)
            return TokenReaderFused.TryReadFloat64(ref this, out value);

        if (_depth > 0 && _topRemaining == 0)
            return TokenReaderFused.TryReadFloat64(ref this, out value);

        if (_position >= _data.Length)
        {
            _needsMoreData = !_isFinalBlock;
            value = 0;
            return false;
        }

        _needsMoreData = false;
        // ReadSpan() resets the integer cache before its switch, so a payload that throws
        // half-way through a token leaves the same field state on both paths.
        _intKind = 0;
        switch (_data[_position])
        {
            case 0xCA: // float32
            {
                _position++;
                _valueSpan = ReadBytesSpan(4);
                _tokenType = TokenType.Float32;
                CountElement();
                value = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32BigEndian(_valueSpan)
                );
                return true;
            }
            case 0xCB: // float64
            {
                _position++;
                _valueSpan = ReadBytesSpan(8);
                _tokenType = TokenType.Float64;
                CountElement();
                value = BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64BigEndian(_valueSpan)
                );
                return true;
            }
            // Not a float token — including the integer family, whose answer the reference owns.
            default:
                return TokenReaderFused.TryReadFloat64(ref this, out value);
        }
    }

    /// <summary>
    /// Advances one token and reads it as a <see cref="bool"/> — <c>Read() &amp;&amp; TryGetBool()</c> in one
    /// dispatch (task T8, spec §3.3 MessagePack row).
    /// <para>
    /// <b>Only the two bool encodings succeed</b> (<c>0xC3</c> = true, <c>0xC2</c> = false), which is
    /// exactly <see cref="TryGetBool"/>'s rule (<c>TokenType.Bool</c> plus a one-byte value span whose
    /// <c>0xC3</c> bit decides the answer). Every other tag — <c>nil</c>, integers, strings, containers —
    /// forwards to the reference implementation, so "a non-bool token consumed the token and answered
    /// false, with the token still current" is decided in one place only.
    /// </para>
    /// <para>
    /// A bool token is not an integer: <c>_intKind</c> is reset to 0 for it, exactly as
    /// <see cref="ReadSpan"/> does before its switch.
    /// </para>
    /// <para>Not <c>readonly</c>: <c>ref this</c> in a readonly member is <c>CS1605</c>.</para>
    /// </summary>
    public bool TryReadBool(out bool value)
    {
        if (_isSequence)
            return TokenReaderFused.TryReadBool(ref this, out value);

        if (_depth > 0 && _topRemaining == 0)
            return TokenReaderFused.TryReadBool(ref this, out value);

        if (_position >= _data.Length)
        {
            _needsMoreData = !_isFinalBlock;
            value = false;
            return false;
        }

        _needsMoreData = false;
        // ReadSpan() resets the integer cache before its switch, so a payload that throws
        // half-way through a token leaves the same field state on both paths.
        _intKind = 0;
        switch (_data[_position])
        {
            // 0xC3 = true, 0xC2 = false — the value span is the tag byte itself, as ReadSpan stores it.
            case 0xC3:
            case 0xC2:
            {
                var b = _data[_position];
                _position++;
                _singleByte = b;
                _valueSpan = MemoryMarshal.CreateSpan(ref _singleByte, 1);
                _tokenType = TokenType.Bool;
                CountElement();
                value = b == 0xC3;
                return true;
            }
            default:
                return TokenReaderFused.TryReadBool(ref this, out value);
        }
    }

    public bool TryGetFloat64(out double v)
    {
        if (_valueSpan.IsEmpty)
        {
            v = 0;
            return false;
        }
        if (_tokenType == TokenType.Float64)
            v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_valueSpan));
        else if (_tokenType == TokenType.Float32)
            v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(_valueSpan));
        else if (_tokenType is TokenType.Int32 or TokenType.Int64)
        {
            TryGetInt64(out var iv);
            v = iv;
        }
        else
        {
            v = 0;
            return false;
        }
        return true;
    }

    public void Skip()
    {
        if (_tokenType is not (TokenType.ObjectStart or TokenType.ArrayStart))
            return;
        var targetDepth = 1;
        while (Read() && targetDepth > 0)
        {
            switch (_tokenType)
            {
                case TokenType.ObjectStart or TokenType.ArrayStart:
                    targetDepth++;
                    break;
                case TokenType.ObjectEnd or TokenType.ArrayEnd:
                    targetDepth--;
                    break;
            }
        }
    }

    /// <summary>
    /// Skips an unknown value when its bytes are all present. A truncated
    /// payload reports false (streaming can retry with more data) while a real
    /// malformed payload raises FormatException — it is never swallowed.
    /// </summary>
    public bool TrySkip()
    {
        if (NeedsMoreData)
            return false;
        Skip();
        return true;
    }

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }

    // ── Fast path array reading (used by source generators) ──

    /// <summary>Decode MsgPack integer from current position, advance position.</summary>
    private bool TryDecodeInt32(ref int p, out int v)
    {
        v = 0;
        int len = _data.Length;
        if (p >= len)
            return false;
        byte b = _data[p++];
        switch (b)
        {
            case <= 0x7F:
                v = b;
                return true;
            case >= 0xE0:
                v = b - 256;
                return true;
            case 0xCC when p < len:
                v = _data[p++];
                return true;
            case 0xCD when p + 1 < len:
                v = (_data[p] << 8) | _data[p + 1];
                p += 2;
                return true;
            case 0xCE when p + 3 < len:
            {
                uint uv =
                    (uint)(_data[p] << 24)
                    | (uint)(_data[p + 1] << 16)
                    | (uint)(_data[p + 2] << 8)
                    | _data[p + 3];
                if (uv > int.MaxValue)
                    return false;
                v = (int)uv;
                p += 4;
                return true;
            }
            case 0xD0 when p < len:
                v = (sbyte)_data[p++];
                return true;
            case 0xD1 when p + 1 < len:
                v = (short)(_data[p] << 8 | _data[p + 1]);
                p += 2;
                return true;
            case 0xD2 when p + 3 < len:
                v = _data[p] << 24 | _data[p + 1] << 16 | _data[p + 2] << 8 | _data[p + 3];
                p += 4;
                return true;
            default:
                // uint64, int64, and floating-point encodings are not int32 —
                // signal failure so the caller falls back to the validated reader
                // instead of silently producing 0.
                return false;
        }
    }

    /// <summary>Fast path: read int32 array directly from buffer. Returns count read, or 0 to fallback.</summary>
    public int TryReadInt32Array(Span<int> dest)
    {
        if (_isSequence)
            return 0;
        var p = _position;
        var len = _data.Length;
        if (p >= len)
            return 0;
        byte hdr = _data[p++];
        int count;
        switch (hdr)
        {
            case >= 0x90 and <= 0x9F:
                count = hdr - 0x90;
                break;
            case 0xDC when p + 1 < len:
                count = _data[p] << 8 | _data[p + 1];
                p += 2;
                break;
            case 0xDD when p + 3 < len:
                count = _data[p] << 24 | _data[p + 1] << 16 | _data[p + 2] << 8 | _data[p + 3];
                p += 4;
                break;
            default:
                return 0;
        }
        if (count > dest.Length)
            return 0;
        for (int i = 0; i < count; i++)
        {
            if (p >= len)
                return 0;
            if (p < len && _data[p] == 0xD3)
                return 0;
            if (!TryDecodeInt32(ref p, out var iv))
                return 0;
            dest[i] = iv;
        }
        _position = p;
        return count;
    }

    /// <summary>Fast path: read int64 array directly from buffer.</summary>
    public int TryReadInt64Array(Span<long> dest)
    {
        if (_isSequence)
            return 0;
        var p = _position;
        var len = _data.Length;
        if (p >= len)
            return 0;
        byte hdr = _data[p++];
        int count;
        switch (hdr)
        {
            case >= 0x90 and <= 0x9F:
                count = hdr - 0x90;
                break;
            case 0xDC when p + 1 < len:
                count = _data[p] << 8 | _data[p + 1];
                p += 2;
                break;
            case 0xDD when p + 3 < len:
                count = _data[p] << 24 | _data[p + 1] << 16 | _data[p + 2] << 8 | _data[p + 3];
                p += 4;
                break;
            default:
                return 0;
        }
        if (count > dest.Length)
            return 0;
        for (int i = 0; i < count; i++)
        {
            if (p >= len)
                return 0;
            long v;
            byte b = _data[p++];
            switch (b)
            {
                case <= 0x7F:
                    v = b;
                    break;
                case >= 0xE0:
                    v = b - 256;
                    break;
                case 0xD0 when p < len:
                    v = (sbyte)_data[p++];
                    break;
                case 0xD1 when p + 1 < len:
                    v = (short)(_data[p] << 8 | _data[p + 1]);
                    p += 2;
                    break;
                case 0xD2 when p + 3 < len:
                    v = _data[p] << 24 | _data[p + 1] << 16 | _data[p + 2] << 8 | _data[p + 3];
                    p += 4;
                    break;
                case 0xD3 when p + 7 < len:
                    v =
                        (long)_data[p] << 56
                        | (long)_data[p + 1] << 48
                        | (long)_data[p + 2] << 40
                        | (long)_data[p + 3] << 32
                        | (long)_data[p + 4] << 24
                        | (long)_data[p + 5] << 16
                        | (long)_data[p + 6] << 8
                        | _data[p + 7];
                    p += 8;
                    break;
                default:
                    return 0;
            }
            dest[i] = v;
        }
        _position = p;
        return count;
    }

    /// <summary>Fast path: read double array directly from buffer.</summary>
    public int TryReadDoubleArray(Span<double> dest)
    {
        if (_isSequence)
            return 0;
        var p = _position;
        var len = _data.Length;
        if (p >= len)
            return 0;
        byte hdr = _data[p++];
        int count;
        switch (hdr)
        {
            case >= 0x90 and <= 0x9F:
                count = hdr - 0x90;
                break;
            case 0xDC when p + 1 < len:
                count = _data[p] << 8 | _data[p + 1];
                p += 2;
                break;
            case 0xDD when p + 3 < len:
                count = _data[p] << 24 | _data[p + 1] << 16 | _data[p + 2] << 8 | _data[p + 3];
                p += 4;
                break;
            default:
                return 0;
        }
        if (count > dest.Length)
            return 0;
        for (int i = 0; i < count; i++)
        {
            if (p >= len)
                return 0;
            byte b = _data[p++];
            switch (b)
            {
                case 0xCB when p + 7 < len:
                    dest[i] = BitConverter.Int64BitsToDouble(
                        BinaryPrimitives.ReadInt64BigEndian(_data.Slice(p, 8))
                    );
                    p += 8;
                    break;
                case 0xCA when p + 3 < len:
                    dest[i] = BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32BigEndian(_data.Slice(p, 4))
                    );
                    p += 4;
                    break;
                default:
                    return 0;
            }
        }
        _position = p;
        return count;
    }

    /// <summary>Fast path: read bool array directly from buffer.</summary>
    public int TryReadBoolArray(Span<bool> dest)
    {
        if (_isSequence)
            return 0;
        var p = _position;
        var len = _data.Length;
        if (p >= len)
            return 0;
        byte hdr = _data[p++];
        int count;
        switch (hdr)
        {
            case >= 0x90 and <= 0x9F:
                count = hdr - 0x90;
                break;
            case 0xDC when p + 1 < len:
                count = _data[p] << 8 | _data[p + 1];
                p += 2;
                break;
            case 0xDD when p + 3 < len:
                count = _data[p] << 24 | _data[p + 1] << 16 | _data[p + 2] << 8 | _data[p + 3];
                p += 4;
                break;
            default:
                return 0;
        }
        if (count > dest.Length)
            return 0;
        for (int i = 0; i < count; i++)
        {
            if (p >= len)
                return 0;
            byte b = _data[p++];
            switch (b)
            {
                case 0xC3:
                    dest[i] = true;
                    break;
                case 0xC2:
                    dest[i] = false;
                    break;
                default:
                    return 0;
            }
        }
        _position = p;
        return count;
    }
}

[System.Runtime.CompilerServices.InlineArray(64)]
internal struct IntStack64
{
    private int _e0;
}

[System.Runtime.CompilerServices.InlineArray(64)]
internal struct BoolStack64
{
    private bool _e0;
}
