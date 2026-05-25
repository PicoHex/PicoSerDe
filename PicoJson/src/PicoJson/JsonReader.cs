namespace PicoJson;

public ref struct JsonReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private TokenType _tokenType;
    private int _depth;
    private ReadOnlySpan<byte> _valueSpan;

    public JsonReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _tokenType = TokenType.None;
        _depth = 0;
        _valueSpan = default;
    }

    public JsonReader(ReadOnlySequence<byte> data)
        : this(data.IsSingleSegment ? data.FirstSpan : data.ToArray()) { }

    public TokenType TokenType => _tokenType;
    public int Depth => _depth;
    public long BytesConsumed => _position;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;

    public bool Read()
    {
        SkipWhitespace();
        if (_position >= _data.Length)
            return false;
        if (_data[_position] == (byte)',')
        {
            _position++;
            SkipWhitespace();
            if (_position >= _data.Length)
                return false;
        }
        var b = _data[_position];
        switch (b)
        {
            case (byte)'{':
                _tokenType = TokenType.ObjectStart;
                _position++;
                _depth++;
                return true;
            case (byte)'}':
                _tokenType = TokenType.ObjectEnd;
                _position++;
                _depth--;
                return true;
            case (byte)'[':
                _tokenType = TokenType.ArrayStart;
                _position++;
                _depth++;
                return true;
            case (byte)']':
                _tokenType = TokenType.ArrayEnd;
                _position++;
                _depth--;
                return true;
            case (byte)'"':
                return ReadStringOrProperty();
            case (byte)'t':
                return ReadLiteral("true"u8, TokenType.Bool);
            case (byte)'f':
                return ReadLiteral("false"u8, TokenType.Bool);
            case (byte)'n':
                return ReadLiteral("null"u8, TokenType.Null);
            case (byte)'-':
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
                return ReadNumber();
            default:
                throw new FormatException($"Unexpected byte 0x{b:X2} at position {_position}");
        }
    }

    public void Skip()
    {
        if (!TrySkip())
            throw new FormatException($"Failed to skip at position {_position}");
    }

    public bool TrySkip()
    {
        var start = _tokenType is TokenType.ObjectStart or TokenType.ArrayStart
            ? _depth - 1
            : _depth;
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

    public ReadOnlySpan<byte> GetStringRaw() => _valueSpan;

    public bool TryGetInt32(out int v)
    {
        if (_tokenType != TokenType.Int32)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
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
        if (_tokenType is not (TokenType.Float64 or TokenType.Int32))
        {
            v = 0;
            return false;
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

    private void SkipWhitespace()
    {
        while (
            _position < _data.Length
            && _data[_position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'
        )
            _position++;
    }

    private bool ReadStringOrProperty()
    {
        _position++;
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)'"')
            _position++;
        if (_position >= _data.Length)
            throw new FormatException("Unterminated string");
        _valueSpan = _data[start.._position];
        _position++;

        var saved = _position;
        SkipWhitespace();
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

    private bool ReadNumber()
    {
        var start = _position;
        if (_data[_position] == (byte)'-')
            _position++;
        bool isFloat = false;
        while (_position < _data.Length && IsDigit(_data[_position]))
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'.')
        {
            isFloat = true;
            _position++;
            while (_position < _data.Length && IsDigit(_data[_position]))
                _position++;
        }
        if (_position < _data.Length && (_data[_position] is (byte)'e' or (byte)'E'))
        {
            isFloat = true;
            _position++;
            if (_position < _data.Length && _data[_position] is (byte)'+' or (byte)'-')
                _position++;
            while (_position < _data.Length && IsDigit(_data[_position]))
                _position++;
        }
        _valueSpan = _data[start.._position];
        _tokenType = isFloat ? TokenType.Float64 : TokenType.Int32;
        return true;
    }

    private bool ReadLiteral(ReadOnlySpan<byte> expected, TokenType token)
    {
        if (_position + expected.Length > _data.Length)
            throw new FormatException("EOF");
        if (!_data.Slice(_position, expected.Length).SequenceEqual(expected))
            throw new FormatException("Invalid literal");
        _valueSpan = _data.Slice(_position, expected.Length);
        _tokenType = token;
        _position += expected.Length;
        return true;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}
