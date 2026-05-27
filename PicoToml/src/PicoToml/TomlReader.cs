namespace PicoToml;

public ref struct TomlReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;

    private TokenType _tokenType;
    private ReadOnlySpan<byte> _keySpan;
    private ReadOnlySpan<byte> _valueSpan;
    private ReadOnlySpan<byte> _tablePath;
    private bool _isArrayTable;

    public TomlReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
    }

    public TokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> KeySpan => _keySpan;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;
    public ReadOnlySpan<byte> TablePath => _tablePath;
    public bool IsArrayTable => _isArrayTable;

    public bool Read()
    {
        Start:
        // Skip blank lines and comments
        while (_position < _data.Length)
        {
            if (_data[_position] == (byte)'\n' || _data[_position] == (byte)'\r')
            {
                _position++;
                continue;
            }
            if (_data[_position] == (byte)'#')
            {
                SkipLine();
                goto Start;
            }
            break;
        }
        if (_position >= _data.Length)
            return false;

        // Table header: [name] or [[name]]
        if (_data[_position] == (byte)'[')
        {
            _position++;
            _isArrayTable = false;

            if (_position < _data.Length && _data[_position] == (byte)'[')
            {
                _isArrayTable = true;
                _position++;
            }

            int tblStart = _position;
            // For [name], scan to ']'. For [[name]], scan to first ']'.
            while (_position < _data.Length && _data[_position] != (byte)']')
                _position++;

            _tablePath = TrimEnd(_data[tblStart.._position]);
            _position++; // skip first ']'
            if (_isArrayTable && _position < _data.Length && _data[_position] == (byte)']')
                _position++; // skip second ']'

            SkipLine();
            _tokenType = _isArrayTable ? TokenType.ArrayStart : TokenType.ObjectStart;
            return true;
        }

        // Read key: scan until '='
        int keyStart = _position;
        // Quoted key
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            keyStart = _position;
            while (_position < _data.Length && _data[_position] != (byte)'"')
                _position++;
            _keySpan = _data[keyStart.._position];
            _position++; // skip closing "
        }
        else
        {
            while (_position < _data.Length && _data[_position] != (byte)'=')
                _position++;
            _keySpan = TrimEnd(_data[keyStart.._position]);
        }

        // Skip to past '='
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _position++; // skip '='

        // Skip whitespace after '='
        while (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        // Read quoted string value
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++; // skip opening "
            // Multiline basic string: """..."""
            if (
                _position + 1 < _data.Length
                && _data[_position] == (byte)'"'
                && _data[_position + 1] == (byte)'"'
            )
            {
                _position += 2; // skip the two extra "
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++; // skip leading newline
                int ms = _position;
                while (_position + 2 < _data.Length)
                {
                    if (
                        _data[_position] == (byte)'"'
                        && _data[_position + 1] == (byte)'"'
                        && _data[_position + 2] == (byte)'"'
                    )
                    {
                        _valueSpan = _data[ms.._position];
                        _position += 3;
                        break;
                    }
                    _position++;
                }
            }
            else
            {
                int valStart = _position;
                while (_position < _data.Length && _data[_position] != (byte)'"')
                    _position++;
                _valueSpan = _data[valStart.._position];
                _position++; // skip closing "
            }
        }
        // Read unquoted value (number, bool, date, etc.)
        else
        {
            int valStart = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            _valueSpan = _data[valStart.._position];
        }

        // Skip trailing newline
        SkipLine();

        _tokenType = TokenType.PropertyName;
        return true;
    }

    public bool TryGetInt32(out int v)
    {
        if (_tokenType != TokenType.PropertyName)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType != TokenType.PropertyName)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != TokenType.PropertyName)
        {
            v = false;
            return false;
        }
        v = _valueSpan[0] == (byte)'t';
        return true;
    }

    public bool TryGetFloat64(out double v)
    {
        if (_tokenType != TokenType.PropertyName)
        {
            v = 0;
            return false;
        }
        return Utf8Parser.TryParse(_valueSpan, out v, out _);
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> s)
    {
        int end = s.Length;
        while (end > 0 && (s[end - 1] == (byte)' ' || s[end - 1] == (byte)'\t'))
            end--;
        return s[..end];
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private void SkipLine()
    {
        while (
            _position < _data.Length
            && _data[_position] != (byte)'\n'
            && _data[_position] != (byte)'\r'
        )
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'\r')
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'\n')
            _position++;
    }
}
