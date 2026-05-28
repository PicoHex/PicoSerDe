namespace PicoYaml;

public ref struct YamlReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _keySpan;
    private ReadOnlySpan<byte> _valueSpan;
    private int _depth;
    private int[] _indentStack;
    private int _stackCount;

    public YamlReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _depth = 0;
        _indentStack = new int[64];
        _stackCount = 0;
    }

    public TokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> KeySpan => _keySpan;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;
    public int Depth => _depth;

    public bool Read()
    {
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

        // Compute line indent and check for close
        int lineStart = _position;
        int lineIndent = 0;
        while (_position < _data.Length && _data[_position] == (byte)' ')
        {
            lineIndent++;
            _position++;
        }

        // Blank line? Skip
        if (
            _position >= _data.Length
            || _data[_position] == (byte)'\n'
            || _data[_position] == (byte)'\r'
        )
        {
            SkipLine();
            goto Retry;
        }

        // Comment line? Skip
        if (_data[_position] == (byte)'#')
        {
            SkipLine();
            goto Retry;
        }

        // Check indent relative to stack
        if (_stackCount == 0 && lineIndent > 0)
        {
            // First indented content after root: implicit root mapping start
            _position = lineStart;
            PushIndent(0);
            PushIndent(lineIndent);
            _tokenType = TokenType.ObjectStart;
            _depth++;
            return true;
        }

        if (_stackCount > 0 && lineIndent < _indentStack[_stackCount - 1])
        {
            // Return to line start, let next Read() emit close
            _position = lineStart;
            PopIndent();
            _tokenType = TokenType.ObjectEnd;
            _depth--;
            return true;
        }

        // Detect implicit nested mapping start
        if (_stackCount > 0 && lineIndent > _indentStack[_stackCount - 1])
        {
            _position = lineStart; // don't consume this line
            PushIndent(lineIndent);
            _tokenType = TokenType.ObjectStart;
            _depth++;
            return true;
        }

        // ── Parse content ──

        // Sequence item: "- value"
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
            SkipNewline();
            _tokenType = TokenType.String;
            return true;
        }

        // Key: value
        int ks = _position;
        while (_position < _data.Length && _data[_position] != (byte)':')
            _position++;
        _keySpan = TrimEnd(_data[ks.._position]);
        _position++;
        if (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        // Peek next line indent to see if value is nested mapping
        int afterKey = _position;
        SkipLine();
        int nextIndent = 0;
        int peekPos = _position;
        while (_position < _data.Length && _data[_position] == (byte)' ')
        {
            nextIndent++;
            _position++;
        }
        _position = afterKey; // restore

        if (nextIndent > lineIndent)
        {
            // Value is nested: emit PropertyName, expect ObjectStart on next Read()
            _tokenType = TokenType.PropertyName;
            return true;
        }

        // Scalar value — check for quoted string first
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
            _position++; // skip closing quote
            SkipNewline();
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
            SkipNewline();
        }
        _tokenType = TokenType.PropertyName;
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

    private void SkipLine()
    {
        while (
            _position < _data.Length
            && _data[_position] != (byte)'\n'
            && _data[_position] != (byte)'\r'
        )
            _position++;
        SkipNewline();
    }

    private void SkipNewline()
    {
        if (_position < _data.Length && _data[_position] == (byte)'\r')
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'\n')
            _position++;
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
}
