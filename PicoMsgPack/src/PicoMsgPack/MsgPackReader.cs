namespace PicoMsgPack;

public ref struct MsgPackReader
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
    private byte[] _singleByteBuffer; // pre-allocated 1-byte array to avoid heap alloc per int
    private IntStack64 _elementStack;
    private byte _tag; // for Extension tokens
    private int _depth;
    private BoolStack64 _isMapStack;
    private BoolStack64 _expectKeyStack;

    private const int MaxDepth = 64;

    public MsgPackReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _valueSpan = default;
        _rentedBuffer = null;
        _singleByteBuffer = new byte[1];
        _elementStack = default;
        _tag = 0;
        _isMapStack = default;
        _expectKeyStack = default;
        _depth = 0;
    }

    public MsgPackReader(ReadOnlySequence<byte> data)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = TokenType.None;
        _valueSpan = default;
        _rentedBuffer = null;
        _singleByteBuffer = new byte[1];
        _elementStack = default;
        _tag = 0;
        _isMapStack = default;
        _expectKeyStack = default;
        _depth = 0;
    }

    public TokenType TokenType => _tokenType;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    /// <summary>Direct buffer access for optimized generated code (span mode only).</summary>
    public ReadOnlySpan<byte> RawBuffer => _isSequence ? default : _data;

    /// <summary>Direct position access for optimized generated code (span mode only).</summary>
    public int RawPos => _isSequence ? -1 : _position;

    public void SetRawPos(int pos) { _position = pos; }

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

    private ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (_isSequence)
        {
            var buf = RentBuf(count);
            _seqReader.TryCopyTo(buf.AsSpan(0, count));
            _seqReader.Advance(count);
            return buf.AsSpan(0, count);
        }
        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    private int ReadByteLen(int bytes)
    {
        if (_isSequence)
        {
            int v = 0;
            for (int i = 0; i < bytes; i++)
            {
                _seqReader.TryRead(out byte b);
                v = (v << 8) | b;
            }
            return v;
        }
        int vs = 0;
        for (int i = 0; i < bytes; i++)
            vs = (vs << 8) | _data[_position++];
        return vs;
    }

    private byte[] RentBuf(int size)
    {
        var buf = ArrayPool<byte>.Shared.Rent(size);
        _rentedBuffer = buf;
        return buf;
    }

    // ── Element tracking (shared) ──

    private void PushLevel(bool isMap, int count)
    {
        _isMapStack[_depth] = isMap;
        _expectKeyStack[_depth] = isMap;
        _elementStack[_depth] = count;
        _depth++;
    }

    private void CountElement()
    {
        if (_depth > 0 && _elementStack[_depth - 1] > 0)
        {
            _elementStack[_depth - 1]--;
            if (_isMapStack[_depth - 1])
                _expectKeyStack[_depth - 1] = !_expectKeyStack[_depth - 1];
        }
    }

    // ── Read ──

    public bool Read()
    {
        if (_depth > 0 && _elementStack[_depth - 1] == 0)
        {
            _depth--;
            _tokenType = _isMapStack[_depth] ? TokenType.ObjectEnd : TokenType.ArrayEnd;
            return true;
        }
        if (IsAtEnd())
            return false;

        var b = PeekByte();
        AdvanceByte(); // consume tag byte

        // Positive fixint 0x00-0x7F
        if (b <= 0x7F)
        {
            _singleByteBuffer[0] = b;
            _valueSpan = _singleByteBuffer;
            _tokenType = TokenType.Int32;
            CountElement();
            return true;
        }

        // Fixmap 0x80-0x8F
        if (b >= 0x80 && b <= 0x8F)
        {
            PushLevel(true, (b & 0x0F) * 2);
            _tokenType = TokenType.ObjectStart;
            return true;
        }

        // Fixarray 0x90-0x9F
        if (b >= 0x90 && b <= 0x9F)
        {
            PushLevel(false, b & 0x0F);
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        // Fixstr 0xA0-0xBF
        if (b >= 0xA0 && b <= 0xBF)
            return ReadString(b & 0x1F);

        // Negative fixint 0xE0-0xFF
        if (b >= 0xE0)
        {
            _singleByteBuffer[0] = b;
            _valueSpan = _singleByteBuffer;
            _tokenType = TokenType.Int32;
            CountElement();
            return true;
        }

        switch (b)
        {
            case 0xC0:
                _tokenType = TokenType.Null;
                CountElement();
                return true;
            case 0xC2:
            case 0xC3:
                _singleByteBuffer[0] = b;
                _valueSpan = _singleByteBuffer;
                _tokenType = TokenType.Bool;
                CountElement();
                return true;
            case 0xCC:
                _valueSpan = ReadBytes(1);
                _tokenType = TokenType.UInt8;
                CountElement();
                return true;
            case 0xCD:
                _valueSpan = ReadBytes(2);
                _tokenType = TokenType.Int32;
                CountElement();
                return true;
            case 0xCE:
                _valueSpan = ReadBytes(4);
                _tokenType = TokenType.UInt32;
                CountElement();
                return true;
            case 0xCF:
                _valueSpan = ReadBytes(8);
                _tokenType = TokenType.UInt64;
                CountElement();
                return true;
            case 0xD0:
                _valueSpan = ReadBytes(1);
                _tokenType = TokenType.Int32;
                CountElement();
                return true;
            case 0xD1:
                _valueSpan = ReadBytes(2);
                _tokenType = TokenType.Int32;
                CountElement();
                return true;
            case 0xD2:
                _valueSpan = ReadBytes(4);
                _tokenType = TokenType.Int32;
                CountElement();
                return true;
            case 0xD3:
                _valueSpan = ReadBytes(8);
                _tokenType = TokenType.Int64;
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
                int extLen = ReadByteLen(1);
                _tag = PeekByte();
                AdvanceByte();
                _valueSpan = ReadBytes(extLen);
                _tokenType = TokenType.Extension;
                CountElement();
                return true;
            }
            case 0xC8:
            {
                int extLen = ReadByteLen(2);
                _tag = PeekByte();
                AdvanceByte();
                _valueSpan = ReadBytes(extLen);
                _tokenType = TokenType.Extension;
                CountElement();
                return true;
            }
            case 0xC9:
            {
                int extLen = ReadByteLen(4);
                _tag = PeekByte();
                AdvanceByte();
                _valueSpan = ReadBytes(extLen);
                _tokenType = TokenType.Extension;
                CountElement();
                return true;
            }
            case 0xD9:
                return ReadString(ReadByteLen(1));
            case 0xDA:
                return ReadString(ReadByteLen(2));
            case 0xDB:
                return ReadString(ReadByteLen(4));
            case 0xDC:
                PushLevel(false, ReadByteLen(2));
                _tokenType = TokenType.ArrayStart;
                return true;
            case 0xDD:
                PushLevel(false, ReadByteLen(4));
                _tokenType = TokenType.ArrayStart;
                return true;
            case 0xDE:
                PushLevel(true, ReadByteLen(2) * 2);
                _tokenType = TokenType.ObjectStart;
                return true;
            case 0xDF:
                PushLevel(true, ReadByteLen(4) * 2);
                _tokenType = TokenType.ObjectStart;
                return true;
            default:
                throw new FormatException(
                    $"Unknown MsgPack byte 0x{b:X2} at offset {BytesConsumed}"
                );
        }
    }

    private bool ReadString(int len)
    {
        _valueSpan = ReadBytes(len);
        if (_depth > 0 && _isMapStack[_depth - 1] && _expectKeyStack[_depth - 1])
            _tokenType = TokenType.PropertyName;
        else
            _tokenType = TokenType.String;
        CountElement();
        return true;
    }

    // ── Typed accessors ──

    public bool TryGetInt32(out int v)
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
                or TokenType.UInt32
                or TokenType.Int64
                or TokenType.UInt64
        )
        {
            v = _valueSpan.Length switch
            {
                1
                    => _tokenType == TokenType.UInt8
                        ? _valueSpan[0]
                        : (_valueSpan[0] < 0x80 ? _valueSpan[0] : (sbyte)_valueSpan[0]),
                2 => BinaryPrimitives.ReadInt16BigEndian(_valueSpan),
                4 => BinaryPrimitives.ReadInt32BigEndian(_valueSpan),
                8 => (int)BinaryPrimitives.ReadInt64BigEndian(_valueSpan),
                _ => 0,
            };
            return true;
        }
        v = 0;
        return false;
    }

    public bool TryGetInt64(out long v)
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
                or TokenType.UInt32
                or TokenType.Int64
                or TokenType.UInt64
        )
        {
            v = _valueSpan.Length switch
            {
                1
                    => _tokenType == TokenType.UInt8
                        ? _valueSpan[0]
                        : (_valueSpan[0] < 0x80 ? _valueSpan[0] : (sbyte)_valueSpan[0]),
                2 => BinaryPrimitives.ReadInt16BigEndian(_valueSpan),
                4
                    => _tokenType == TokenType.UInt32
                        ? BinaryPrimitives.ReadUInt32BigEndian(_valueSpan)
                        : BinaryPrimitives.ReadInt32BigEndian(_valueSpan),
                8 => BinaryPrimitives.ReadInt64BigEndian(_valueSpan),
                _ => 0,
            };
            return true;
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
        if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
        {
            int targetDepth = 1;
            while (Read() && targetDepth > 0)
            {
                if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
                    targetDepth++;
                else if (_tokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
                    targetDepth--;
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
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
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
