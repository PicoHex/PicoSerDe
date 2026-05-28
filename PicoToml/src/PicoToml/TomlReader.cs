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
    private bool _inArray;
    private int _arrayDepth;
    private bool _arrayStartEmitted;
    private bool _inInlineTable;
    private int _inlineTableDepth;
    private bool _inlineStartEmitted;

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
        // ── Inline table mode ──
        if (_inInlineTable)
            return ReadInlineTable();

        // ── Array mode: parse array elements ──
        if (_inArray)
            return ReadArray();

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

        // ── Array value: key = [1, 2, 3] ──
        if (_position < _data.Length && _data[_position] == (byte)'[')
        {
            _position++; // skip '['
            _inArray = true;
            _arrayDepth = 1;
            _arrayStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        // ── Inline table: key = { a = 1, b = 2 } ──
        if (_position < _data.Length && _data[_position] == (byte)'{')
        {
            _position++; // skip '{'
            _inInlineTable = true;
            _inlineTableDepth = 1;
            _inlineStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

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

    private bool ReadInlineTable()
    {
        // Emit ObjectStart on first entry
        if (!_inlineStartEmitted)
        {
            _inlineStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }

        // Skip whitespace and commas
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
                _position++;
            else
                break;
        }

        if (_position >= _data.Length)
            return false;

        // End of inline table
        if (_data[_position] == (byte)'}')
        {
            _position++;
            _inlineTableDepth--;
            if (_inlineTableDepth == 0)
            {
                _inInlineTable = false;
                if (_position < _data.Length && _data[_position] == (byte)'\r') _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n') _position++;
            }
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        // Read key = value pair
        int keyStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _keySpan = TrimEnd(_data[keyStart.._position]);
        _position++; // skip '='
        while (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        // Read value
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            int vs = _position;
            while (_position < _data.Length && _data[_position] != (byte)'"') _position++;
            _valueSpan = _data[vs.._position];
            _position++;
        }
        else
        {
            int vs = _position;
            while (_position < _data.Length && _data[_position] != (byte)','
                && _data[_position] != (byte)'}' && _data[_position] != (byte)'\n')
                _position++;
            _valueSpan = Trim(_data[vs.._position]);
        }
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private bool ReadArray()
    {
        // Emit ArrayStart on first entry (after PropertyName)
        if (!_arrayStartEmitted)
        {
            _arrayStartEmitted = true;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        // Skip whitespace, commas, newlines
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
                _position++;
            else if (b == (byte)'#')
                SkipLine();
            else
                break;
        }

        if (_position >= _data.Length)
            return false;

        // End of current array level
        if (_data[_position] == (byte)']')
        {
            _position++;
            _arrayDepth--;
            if (_arrayDepth == 0)
            {
                _inArray = false;
                // Skip trailing newline
                if (_position < _data.Length && _data[_position] == (byte)'\r')
                    _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
            }
            _tokenType = TokenType.ArrayEnd;
            return true;
        }

        // Nested array start
        if (_data[_position] == (byte)'[')
        {
            _position++;
            _arrayDepth++;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        // Read array element value
        return ReadArrayValue();
    }

    private bool ReadArrayValue()
    {
        // Quoted string
        if (_data[_position] == (byte)'"')
        {
            _position++;
            var valStart = _position;
            while (_position < _data.Length && _data[_position] != (byte)'"')
                _position++;
            _valueSpan = _data[valStart.._position];
            _position++; // skip closing "
        }
        // Literal string
        else if (_data[_position] == (byte)'\'')
        {
            _position++;
            var valStart = _position;
            while (_position < _data.Length && _data[_position] != (byte)'\'')
                _position++;
            _valueSpan = _data[valStart.._position];
            _position++; // skip closing '
        }
        else
        {
            // Unquoted value (number, bool, etc.) — scan to next , or ]
            var valStart = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)','
                && _data[_position] != (byte)']'
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            _valueSpan = Trim(_data[valStart.._position]);
        }
        _tokenType = TokenType.String;
        return true;
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

    public bool TryGetFloat64(out double v)
    {
        if (_valueSpan.IsEmpty)
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

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int start = 0;
        while (start < s.Length && (s[start] == (byte)' ' || s[start] == (byte)'\t'))
            start++;
        int end = s.Length;
        while (end > start && (s[end - 1] == (byte)' ' || s[end - 1] == (byte)'\t'))
            end--;
        return s[start..end];
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
