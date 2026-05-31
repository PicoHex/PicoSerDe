namespace PicoToml;

public ref struct TomlReader
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
    private ReadOnlySpan<byte> _tablePath;
    private bool _isArrayTable;
    private bool _inArray;
    private int _arrayDepth;
    private bool _arrayStartEmitted;
    private bool _inInlineTable;
    private int _inlineTableDepth;
    private bool _inlineStartEmitted;
    private byte[]? _rentedBuffer;
    private byte[]?[] _rentedBuffers;
    private int _bufCount;
    private int _depth;
    private readonly int _maxDepth;

    public TomlReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _tablePath = default;
        _isArrayTable = false;
        _inArray = false;
        _arrayDepth = 0;
        _arrayStartEmitted = false;
        _inInlineTable = false;
        _inlineTableDepth = 0;
        _inlineStartEmitted = false;
        _rentedBuffer = null;
        _rentedBuffers = new byte[]?[8];
        _bufCount = 0;
        _depth = 0;
        _maxDepth = 256;
    }

    public TomlReader(ReadOnlySequence<byte> data)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _tablePath = default;
        _isArrayTable = false;
        _inArray = false;
        _arrayDepth = 0;
        _arrayStartEmitted = false;
        _inInlineTable = false;
        _inlineTableDepth = 0;
        _inlineStartEmitted = false;
        _rentedBuffer = null;
        _rentedBuffers = new byte[]?[8];
        _bufCount = 0;
        _depth = 0;
        _maxDepth = 256;
    }

    public TokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> KeySpan => _keySpan;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;
    public ReadOnlySpan<byte> TablePath => _tablePath;
    public bool IsArrayTable => _isArrayTable;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;
    public int Depth => _depth;

    public bool Read() => _isSequence ? ReadSeq() : ReadSpan();

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

    public bool TryReadNextInt32(out int v)
    {
        if (_isSequence)
            return TryReadNextInt32Seq(out v);
        return TryReadNextInt32Span(out v);
    }

    private bool TryReadNextInt32Span(out int v)
    {
        v = 0;
        if (_position >= _data.Length)
            return false;
        // Skip whitespace and commas
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (b <= 32 || b == (byte)',')
                _position++;
            else
                break;
        }
        if (_position >= _data.Length || _data[_position] == (byte)']')
            return false;

        bool neg = false;
        if (_data[_position] == (byte)'-')
        {
            neg = true;
            _position++;
        }
        if (
            _position >= _data.Length
            || _data[_position] < (byte)'0'
            || _data[_position] > (byte)'9'
        )
            return false;

        int result = 0;
        do
        {
            result = result * 10 + (_data[_position] - (byte)'0');
            _position++;
        } while (
            _position < _data.Length
            && _data[_position] >= (byte)'0'
            && _data[_position] <= (byte)'9'
        );
        v = neg ? -result : result;
        return true;
    }

    private bool TryReadNextInt32Seq(out int v)
    {
        v = 0;
        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b <= 32 || b == (byte)',')
                _seqReader.Advance(1);
            else
                break;
        }
        if (_seqReader.End || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)']')
            return false;

        var buf = RentBuf(16);
        int di = 0;
        while (
            !_seqReader.End
            && TextHelpers.IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex])
        )
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        return Utf8Parser.TryParse(buf.AsSpan(0, di), out v, out _);
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
        if (_inInlineTable)
            return ReadInlineTableSpan();
        if (_inArray)
            return ReadArraySpan();

        return SkipToMeaningfulLine() ? ReadTokenSpan() : false;
    }

    private bool SkipToMeaningfulLine()
    {
        while (true)
        {
            _position = SimdHelpers.SkipWhitespace(_data, _position);
            bool lineWasComment = false;
            while (_position < _data.Length)
            {
                byte b = _data[_position];
                if (b == (byte)'\n' || b == (byte)'\r')
                {
                    _position++;
                    continue;
                }
                if (b == (byte)'#')
                {
                    SkipLineSpan();
                    lineWasComment = true;
                    break;
                }
                return true;
            }
            if (!lineWasComment)
                return false;
        }
    }

    private bool ReadTokenSpan()
    {
        if (_data[_position] == (byte)'[')
            return ReadTableHeaderSpan();

        return ReadKeyValueSpan();
    }

    private bool ReadTableHeaderSpan()
    {
        _position++;
        _isArrayTable = false;
        if (_position < _data.Length && _data[_position] == (byte)'[')
        {
            _isArrayTable = true;
            _position++;
        }

        int tblStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)']')
            _position++;
        _tablePath = TrimEnd(_data[tblStart.._position]);
        _position++;
        if (_isArrayTable && _position < _data.Length && _data[_position] == (byte)']')
            _position++;
        SkipLineSpan();
        _depth = 1; // TOML sections are flat (non-nesting)
        _tokenType = _isArrayTable ? TokenType.ArrayStart : TokenType.ObjectStart;
        return true;
    }

    private bool ReadKeyValueSpan()
    {
        int keyStart = _position;
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            keyStart = _position;
            while (_position < _data.Length && _data[_position] != (byte)'"')
                _position++;
            _keySpan = _data[keyStart.._position];
            _position++;
        }
        else
        {
            while (_position < _data.Length && _data[_position] != (byte)'=')
                _position++;
            _keySpan = TrimEnd(_data[keyStart.._position]);
        }

        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _position++;
        while (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        if (_position < _data.Length && _data[_position] == (byte)'[')
        {
            _position++;
            _inArray = true;
            _arrayDepth = 1;
            _arrayStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        if (_position < _data.Length && _data[_position] == (byte)'{')
        {
            _position++;
            _inInlineTable = true;
            _inlineTableDepth = 1;
            _inlineStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        ReadValueSpan();
        SkipLineSpan();
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private void ReadValueSpan()
    {
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            if (
                _position + 1 < _data.Length
                && _data[_position] == (byte)'"'
                && _data[_position + 1] == (byte)'"'
            )
            {
                _position += 2;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
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
                        return;
                    }
                    _position++;
                }
            }
            else
            {
                int vs = _position;
                while (_position < _data.Length && _data[_position] != (byte)'"')
                    _position++;
                _valueSpan = _data[vs.._position];
                _position++;
            }
        }
        else if (_position < _data.Length && _data[_position] == (byte)'\'')
        {
            _position++;
            if (
                _position + 1 < _data.Length
                && _data[_position] == (byte)'\''
                && _data[_position + 1] == (byte)'\''
            )
            {
                _position += 2;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
                int ms = _position;
                while (_position + 2 < _data.Length)
                {
                    if (
                        _data[_position] == (byte)'\''
                        && _data[_position + 1] == (byte)'\''
                        && _data[_position + 2] == (byte)'\''
                    )
                    {
                        _valueSpan = _data[ms.._position];
                        _position += 3;
                        return;
                    }
                    _position++;
                }
            }
            else
            {
                int vs = _position;
                while (_position < _data.Length && _data[_position] != (byte)'\'')
                    _position++;
                _valueSpan = _data[vs.._position];
                _position++;
            }
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
            _valueSpan = _data[vs.._position];
        }
    }

    private bool ReadInlineTableSpan()
    {
        if (!_inlineStartEmitted)
        {
            _inlineStartEmitted = true;
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
            _inlineTableDepth--;
            if (_inlineTableDepth == 0)
            {
                _inInlineTable = false;
                if (_position < _data.Length && _data[_position] == (byte)'\r')
                    _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
            }
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        int ks = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _keySpan = TrimEnd(_data[ks.._position]);
        _position++;
        while (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            int vs = _position;
            while (_position < _data.Length && _data[_position] != (byte)'"')
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

    private bool ReadArraySpan()
    {
        if (!_arrayStartEmitted)
        {
            _arrayStartEmitted = true;
            _tokenType = TokenType.ArrayStart;
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
            if (_position < _data.Length && _data[_position] == (byte)'#')
            {
                SkipLineSpan();
                continue;
            }
            break;
        }
        if (_position >= _data.Length)
            return false;

        if (_data[_position] == (byte)']')
        {
            _position++;
            _arrayDepth--;
            if (_arrayDepth == 0)
            {
                _inArray = false;
                if (_position < _data.Length && _data[_position] == (byte)'\r')
                    _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
            }
            _tokenType = TokenType.ArrayEnd;
            return true;
        }

        if (_data[_position] == (byte)'[')
        {
            _position++;
            _arrayDepth++;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        return ReadArrayValueSpan();
    }

    private bool ReadArrayValueSpan()
    {
        if (_data[_position] == (byte)'"')
        {
            _position++;
            int vs = _position;
            while (_position < _data.Length && _data[_position] != (byte)'"')
                _position++;
            _valueSpan = _data[vs.._position];
            _position++;
        }
        else if (_data[_position] == (byte)'\'')
        {
            _position++;
            int vs = _position;
            while (_position < _data.Length && _data[_position] != (byte)'\'')
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
                && _data[_position] != (byte)']'
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            _valueSpan = Trim(_data[vs.._position]);
        }
        _tokenType = TokenType.String;
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
        if (_position < _data.Length && _data[_position] == (byte)'\r')
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'\n')
            _position++;
    }

    // ── Sequence-mode Read ──

    private bool ReadSeq()
    {
        if (_inInlineTable)
            return ReadInlineTableSeq();
        if (_inArray)
            return ReadArraySeq();

        while (true)
        {
            bool lineWasComment = false;
            while (!_seqReader.End)
            {
                var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                if (b == (byte)'\n' || b == (byte)'\r')
                {
                    _seqReader.Advance(1);
                    continue;
                }
                if (b == (byte)'#')
                {
                    SkipLineSeq();
                    lineWasComment = true;
                    break;
                }
                // Found a meaningful token
                if (b == (byte)'[')
                    return ReadTableHeaderSeq();
                return ReadKeyValueSeq();
            }
            if (!lineWasComment)
                return false;
        }
    }

    private bool ReadTableHeaderSeq()
    {
        _seqReader.Advance(1);
        _isArrayTable = false;
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'[')
        {
            _isArrayTable = true;
            _seqReader.Advance(1);
        }

        var buf = RentBuf(64);
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _tablePath = buf.AsSpan(0, di);
        _seqReader.Advance(1);
        if (
            _isArrayTable
            && !_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)']'
        )
            _seqReader.Advance(1);
        SkipLineSeq();
        _depth = 1; // TOML sections are flat (non-nesting)
        _tokenType = _isArrayTable ? TokenType.ArrayStart : TokenType.ObjectStart;
        return true;
    }

    private bool ReadKeyValueSeq()
    {
        var buf = RentBuf(256);
        int di = 0;

        // Quoted key
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"'
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _keySpan = buf.AsSpan(0, di);
            _seqReader.Advance(1);
        }
        else
        {
            di = 0;
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'='
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _keySpan = TrimEnd(buf.AsSpan(0, di));
        }

        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'=')
            _seqReader.Advance(1);
        _seqReader.Advance(1);
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        var nb = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
        if (nb == (byte)'[')
        {
            _seqReader.Advance(1);
            _inArray = true;
            _arrayDepth = 1;
            _arrayStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        if (nb == (byte)'{')
        {
            _seqReader.Advance(1);
            _inInlineTable = true;
            _inlineTableDepth = 1;
            _inlineStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        ReadValueSeq();
        SkipLineSeq();
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private void ReadValueSeq()
    {
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            // Multiline basic string
            if (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"'
                && _seqReader.Length > 1
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex + 1] == (byte)'"'
            )
            {
                _seqReader.Advance(2);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                )
                    _seqReader.Advance(1);
                var buf = RentBuf(256);
                int di = 0;
                while (!_seqReader.End)
                {
                    var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    if (b == (byte)'"' && !_seqReader.End)
                    {
                        var remaining = _seqReader.Remaining;
                        if (remaining >= 3)
                        {
                            var s = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex..];
                            if (s[0] == (byte)'"' && s[1] == (byte)'"' && s[2] == (byte)'"')
                            {
                                _seqReader.Advance(3);
                                break;
                            }
                        }
                    }
                    buf[di++] = b;
                    _seqReader.Advance(1);
                }
                _valueSpan = buf.AsSpan(0, di);
            }
            else
            {
                var buf = RentBuf(256);
                int di = 0;
                while (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"'
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                _valueSpan = buf.AsSpan(0, di);
                _seqReader.Advance(1);
            }
        }
        else
        {
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
            _valueSpan = buf.AsSpan(0, di);
        }
    }

    private bool ReadInlineTableSeq()
    {
        if (!_inlineStartEmitted)
        {
            _inlineStartEmitted = true;
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
            _inlineTableDepth--;
            if (_inlineTableDepth == 0)
            {
                _inInlineTable = false;
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r'
                )
                    _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                )
                    _seqReader.Advance(1);
            }
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        var buf = RentBuf(256);
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'=')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _keySpan = TrimEnd(buf.AsSpan(0, di));
        _seqReader.Advance(1);
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            di = 0;
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"'
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _valueSpan = buf.AsSpan(0, di);
            _seqReader.Advance(1);
        }
        else
        {
            di = 0;
            while (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)','
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'}'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _valueSpan = Trim(buf.AsSpan(0, di));
        }
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private bool ReadArraySeq()
    {
        if (!_arrayStartEmitted)
        {
            _arrayStartEmitted = true;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
                _seqReader.Advance(1);
            else if (b == (byte)'#')
                SkipLineSeq();
            else
                break;
        }
        if (_seqReader.End)
            return false;

        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)']')
        {
            _seqReader.Advance(1);
            _arrayDepth--;
            if (_arrayDepth == 0)
            {
                _inArray = false;
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r'
                )
                    _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                )
                    _seqReader.Advance(1);
            }
            _tokenType = TokenType.ArrayEnd;
            return true;
        }

        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'[')
        {
            _seqReader.Advance(1);
            _arrayDepth++;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        return ReadArrayValueSeq();
    }

    private bool ReadArrayValueSeq()
    {
        var buf = RentBuf(256);
        int di = 0;
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"'
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _seqReader.Advance(1);
        }
        else if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\'')
        {
            _seqReader.Advance(1);
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\''
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _seqReader.Advance(1);
        }
        else
        {
            while (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)','
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
        }
        _valueSpan = buf.AsSpan(0, di);
        _tokenType = TokenType.String;
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
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r')
            _seqReader.Advance(1);
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
            _seqReader.Advance(1);
    }

    // ── Helpers ──

    private byte[] RentBuf(int size)
    {
        var buf = ArrayPool<byte>.Shared.Rent(size);
        // Track all rented buffers for cleanup
        if (_bufCount < _rentedBuffers.Length)
            _rentedBuffers[_bufCount++] = buf;
        _rentedBuffer = buf;
        return buf;
    }

    // ── Fast path array reading (used by source generators) ──

    public int TryReadInt32ArrayFast(Span<int> dest)
    {
        if (_isSequence) return 0;
        var p = _position;
        var d = _data;
        var len = d.Length;
        if (p >= len || d[p] != (byte)'[') return 0;
        p++;
        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32) { p = SimdHelpers.SkipWhitespace(d, p); continue; }
            if (b == (byte)']') { p++; break; }
            if (b == (byte)',') { p++; continue; }
            bool neg = false;
            if (b == (byte)'-') { neg = true; p++; if (p >= len) return 0; b = d[p]; }
            if (b < (byte)'0' || b > (byte)'9') return 0;
            int v = 0;
            do { v = v * 10 + (b - (byte)'0'); p++; if (p >= len) break; b = d[p]; }
            while (b >= (byte)'0' && b <= (byte)'9');
            dest[count++] = neg ? -v : v;
        }
        _position = p;
        return count;
    }

    public int TryReadInt64ArrayFast(Span<long> dest)
    {
        if (_isSequence) return 0;
        var p = _position;
        var d = _data;
        var len = d.Length;
        if (p >= len || d[p] != (byte)'[') return 0;
        p++;
        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32) { p = SimdHelpers.SkipWhitespace(d, p); continue; }
            if (b == (byte)']') { p++; break; }
            if (b == (byte)',') { p++; continue; }
            bool neg = false;
            if (b == (byte)'-') { neg = true; p++; if (p >= len) return 0; b = d[p]; }
            if (b < (byte)'0' || b > (byte)'9') return 0;
            long v = 0;
            do { v = v * 10 + (b - (byte)'0'); p++; if (p >= len) break; b = d[p]; }
            while (b >= (byte)'0' && b <= (byte)'9');
            dest[count++] = neg ? -v : v;
        }
        _position = p;
        return count;
    }

    public int TryReadBoolArrayFast(Span<bool> dest)
    {
        if (_isSequence) return 0;
        var p = _position;
        var d = _data;
        var len = d.Length;
        if (p >= len || d[p] != (byte)'[') return 0;
        p++;
        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32) { p = SimdHelpers.SkipWhitespace(d, p); continue; }
            if (b == (byte)']') { p++; break; }
            if (b == (byte)',') { p++; continue; }
            if (b == (byte)'t') { if (p + 3 >= len || d[p+1] != 'r' || d[p+2] != 'u' || d[p+3] != 'e') return 0; p += 4; dest[count++] = true; }
            else if (b == (byte)'f') { if (p + 4 >= len || d[p+1] != 'a' || d[p+2] != 'l' || d[p+3] != 's' || d[p+4] != 'e') return 0; p += 5; dest[count++] = false; }
            else return 0;
        }
        _position = p;
        return count;
    }
}
