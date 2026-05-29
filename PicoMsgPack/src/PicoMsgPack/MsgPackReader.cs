namespace PicoMsgPack;

public ref struct MsgPackReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _valueSpan;
    private int _remainingElements;
    private int _depth;
    private bool _inMap;            // inside a map (object)
    private bool _expectMapKey;     // next element is a map key

    public MsgPackReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _tokenType = TokenType.None;
        _valueSpan = default;
        _remainingElements = 0;
        _depth = 0;
        _inMap = false;
        _expectMapKey = true;
    }

    public TokenType TokenType => _tokenType;
    public long BytesConsumed => _position;
    public ReadOnlySpan<byte> GetStringRaw() => _valueSpan;

    public bool Read()
    {
        // Emit ObjectEnd/ArrayEnd when all elements consumed
        if (_remainingElements == 0 && _depth > 0)
        {
            if (_inMap)
            {
                _tokenType = TokenType.ObjectEnd;
                _inMap = false;
                _depth--;
                return true;
            }
            else
            {
                _tokenType = TokenType.ArrayEnd;
                _depth--;
                return true;
            }
        }

        if (_position >= _data.Length)
            return false;

        var b = _data[_position++];

        // Positive fixint 0x00-0x7F
        if (b <= 0x7F)
        {
            _valueSpan = new[] { b };
            _tokenType = TokenType.Int32;
            FinishElement();
            return true;
        }

        // Fixmap 0x80-0x8F
        if (b >= 0x80 && b <= 0x8F)
        {
            _remainingElements = (b & 0x0F) * 2; // key+value pairs → elements
            _tokenType = TokenType.ObjectStart;
            _inMap = true;
            _expectMapKey = true;
            _depth++;
            return true;
        }

        // Fixarray 0x90-0x9F
        if (b >= 0x90 && b <= 0x9F)
        {
            _remainingElements = b & 0x0F;
            _tokenType = TokenType.ArrayStart;
            _depth++;
            return true;
        }

        // Fixstr 0xA0-0xBF
        if (b >= 0xA0 && b <= 0xBF)
        {
            return ReadString(b & 0x1F);
        }

        // Negative fixint 0xE0-0xFF
        if (b >= 0xE0)
        {
            _valueSpan = new[] { b };
            _tokenType = TokenType.Int32;
            FinishElement();
            return true;
        }

        switch (b)
        {
            case 0xC0:
                _tokenType = TokenType.Null;
                FinishElement();
                return true;

            case 0xC2: case 0xC3:
                _valueSpan = new[] { b };
                _tokenType = TokenType.Bool;
                FinishElement();
                return true;

            case 0xCC: _valueSpan = ReadBytes(1); _tokenType = TokenType.UInt8; FinishElement(); return true;
            case 0xCD: _valueSpan = ReadBytes(2); _tokenType = TokenType.Int32; FinishElement(); return true;
            case 0xCE: _valueSpan = ReadBytes(4); _tokenType = TokenType.UInt32; FinishElement(); return true;
            case 0xCF: _valueSpan = ReadBytes(8); _tokenType = TokenType.UInt64; FinishElement(); return true;

            case 0xD0: _valueSpan = ReadBytes(1); _tokenType = TokenType.Int32; FinishElement(); return true;
            case 0xD1: _valueSpan = ReadBytes(2); _tokenType = TokenType.Int32; FinishElement(); return true;
            case 0xD2: _valueSpan = ReadBytes(4); _tokenType = TokenType.Int32; FinishElement(); return true;
            case 0xD3: _valueSpan = ReadBytes(8); _tokenType = TokenType.Int64; FinishElement(); return true;

            case 0xCA: _valueSpan = ReadBytes(4); _tokenType = TokenType.Float32; FinishElement(); return true;
            case 0xCB: _valueSpan = ReadBytes(8); _tokenType = TokenType.Float64; FinishElement(); return true;

            case 0xD9: return ReadString(ReadByteLen(1));
            case 0xDA: return ReadString(ReadByteLen(2));
            case 0xDB: return ReadString(ReadByteLen(4));

            case 0xDC: _remainingElements = ReadByteLen(2); _tokenType = TokenType.ArrayStart; _depth++; return true;
            case 0xDD: _remainingElements = ReadByteLen(4); _tokenType = TokenType.ArrayStart; _depth++; return true;

            case 0xDE: _remainingElements = ReadByteLen(2) * 2; _tokenType = TokenType.ObjectStart; _inMap = true; _expectMapKey = true; _depth++; return true;
            case 0xDF: _remainingElements = ReadByteLen(4) * 2; _tokenType = TokenType.ObjectStart; _inMap = true; _expectMapKey = true; _depth++; return true;

            default:
                throw new FormatException($"Unknown MsgPack byte 0x{b:X2} at offset {_position - 1}");
        }
    }

    private bool ReadString(int len)
    {
        _valueSpan = _data.Slice(_position, len);
        _position += len;
        // In a map, string keys become PropertyName
        if (_inMap && _expectMapKey)
            _tokenType = TokenType.PropertyName;
        else
            _tokenType = TokenType.String;
        FinishElement();
        return true;
    }

    private ReadOnlySpan<byte> ReadBytes(int count)
    {
        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    private int ReadByteLen(int bytes)
    {
        int v = 0;
        for (int i = 0; i < bytes; i++)
            v = (v << 8) | _data[_position++];
        return v;
    }

    private void FinishElement()
    {
        if (_remainingElements > 0)
        {
            _remainingElements--;
            // Toggle map key expectation
            if (_inMap)
                _expectMapKey = !_expectMapKey;
        }
    }

    public bool TryGetInt32(out int v)
    {
        if (_valueSpan.IsEmpty) { v = 0; return false; }
        if (_tokenType is TokenType.Int32 or TokenType.UInt8 or TokenType.UInt32 or TokenType.Int64 or TokenType.UInt64)
        {
            v = _valueSpan.Length switch
            {
                1 => _tokenType == TokenType.UInt8 ? _valueSpan[0] : (_valueSpan[0] < 0x80 ? _valueSpan[0] : (sbyte)_valueSpan[0]),
                2 => BinaryPrimitives.ReadInt16BigEndian(_valueSpan),
                4 => BinaryPrimitives.ReadInt32BigEndian(_valueSpan),
                8 => (int)BinaryPrimitives.ReadInt64BigEndian(_valueSpan),
                _ => 0,
            };
            return true;
        }
        v = 0; return false;
    }

    public bool TryGetInt64(out long v)
    {
        if (_valueSpan.IsEmpty) { v = 0; return false; }
        if (_tokenType is TokenType.Int32 or TokenType.UInt32 or TokenType.Int64 or TokenType.UInt64)
        {
            v = _valueSpan.Length switch
            {
                1 => _valueSpan[0] < 0x80 ? _valueSpan[0] : (sbyte)_valueSpan[0],
                2 => BinaryPrimitives.ReadInt16BigEndian(_valueSpan),
                4 => _tokenType == TokenType.UInt32
                    ? BinaryPrimitives.ReadUInt32BigEndian(_valueSpan)
                    : BinaryPrimitives.ReadInt32BigEndian(_valueSpan),
                8 => BinaryPrimitives.ReadInt64BigEndian(_valueSpan),
                _ => 0,
            };
            return true;
        }
        v = 0; return false;
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != TokenType.Bool || _valueSpan.IsEmpty) { v = false; return false; }
        v = _valueSpan[0] == 0xC3;
        return true;
    }

    public bool TryGetFloat64(out double v)
    {
        if (_valueSpan.IsEmpty) { v = 0; return false; }
        if (_tokenType == TokenType.Float64)
            v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_valueSpan));
        else if (_tokenType == TokenType.Float32)
            v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(_valueSpan));
        else if (_tokenType is TokenType.Int32 or TokenType.Int64)
        {
            TryGetInt64(out var iv);
            v = iv;
        }
        else { v = 0; return false; }
        return true;
    }

    public void Skip()
    {
        if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
        {
            int targetDepth = 1;
            while (Read() && targetDepth > 0)
            {
                if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart) targetDepth++;
                else if (_tokenType is TokenType.ObjectEnd or TokenType.ArrayEnd) targetDepth--;
            }
        }
    }

    public bool TrySkip()
    {
        try { Skip(); return true; }
        catch { return false; }
    }

    public void Dispose() { }
}
