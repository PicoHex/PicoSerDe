namespace PicoIni;

public ref struct IniReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _currentValue;
    private byte[]? _rentedBuffer;

    // Pending value: when Read() returns PropertyName, the value token is stored
    // and emitted on the next Read() call.
    private ReadOnlySpan<byte> _pendingValue;
    private TokenType _pendingValueType;
    private bool _hasPendingValue;

    // Section tracking: emit implicit ObjectEnd when a new section starts while
    // we were already inside a section.
    private bool _inSection;
    private bool _hasPendingSectionEnd;

    public IniReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _currentValue = default;
        _rentedBuffer = null;
        _pendingValue = default;
        _pendingValueType = TokenType.None;
        _hasPendingValue = false;
        _inSection = false;
        _hasPendingSectionEnd = false;
    }

    public IniReader(ReadOnlySequence<byte> data)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = TokenType.None;
        _currentValue = default;
        _rentedBuffer = null;
        _pendingValue = default;
        _pendingValueType = TokenType.None;
        _hasPendingValue = false;
        _inSection = false;
        _hasPendingSectionEnd = false;
    }

    public TokenType TokenType => _tokenType;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;
    public ReadOnlySpan<byte> GetStringRaw() => _currentValue;

    public bool Read()
    {
        // Emit pending ObjectEnd
        if (_hasPendingSectionEnd)
        {
            _tokenType = TokenType.ObjectEnd;
            _hasPendingSectionEnd = false;
            _inSection = false;
            return true;
        }

        // Emit pending section start (from section transition)
        if (_hasPendingSectionStart)
        {
            _currentValue = _pendingSectionName;
            _hasPendingSectionStart = false;
            _tokenType = TokenType.ObjectStart;
            _inSection = true;
            return true;
        }

        // Emit pending value from previous PropertyName read
        if (_hasPendingValue)
        {
            _tokenType = _pendingValueType;
            _currentValue = _pendingValue;
            _hasPendingValue = false;
            return true;
        }

        return _isSequence ? ReadSeq() : ReadSpan();
    }

    public bool TryGetInt32(out int v)
    {
        if (_tokenType != TokenType.Int32) { v = 0; return false; }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType is not (TokenType.Int32 or TokenType.Int64)) { v = 0; return false; }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetFloat64(out double v)
    {
        if (_tokenType is not (TokenType.Float64 or TokenType.Int32 or TokenType.Int64)) { v = 0; return false; }
        return Utf8Parser.TryParse(_currentValue, out v, out _);
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != TokenType.Bool) { v = false; return false; }
        v = _currentValue[0] == (byte)'t';
        return true;
    }

    public void Skip() { }
    public bool TrySkip() => true;

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }

    // ── Span-mode read ──

    private bool ReadSpan()
    {
        while (_position < _data.Length)
        {
            var b = _data[_position];

            if (b is (byte)'\n' or (byte)'\r')
            {
                _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n' && _data[_position - 1] == (byte)'\r')
                    _position++;
                continue;
            }

            if (b is (byte)' ' or (byte)'\t') { _position++; continue; }

            if (b == (byte)'[')
                return ReadSectionSpan();
            if (b is (byte)';' or (byte)'#')
            {
                SkipToNextLineSpan();
                continue;
            }
            return ReadKeyValueSpan();
        }
        return false;
    }

    private bool ReadSectionSpan()
    {
        if (_inSection)
        {
            // Emit pending end, store section name for next Read()
            _tokenType = TokenType.ObjectEnd;
            _inSection = false;
            _pendingSectionName = ReadSectionNameSpan();
            _hasPendingSectionStart = true;
            return true;
        }

        _currentValue = ReadSectionNameSpan();
        _inSection = true;
        _tokenType = TokenType.ObjectStart;
        return true;
    }

    private ReadOnlySpan<byte> ReadSectionNameSpan()
    {
        _position++; // skip '['
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)']')
            _position++;
        if (_position >= _data.Length)
            throw new FormatException("Unterminated section at end of input");
        var name = _data[start.._position];
        _position++; // skip ']'
        SkipToNextLineSpan();
        return name;
    }

    private ReadOnlySpan<byte> _pendingSectionName;
    private bool _hasPendingSectionStart;

    private bool ReadKeyValueSpan()
    {
        // Read key
        var keyStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _currentValue = TrimEnd(_data[keyStart.._position]);
        _tokenType = TokenType.PropertyName;
        _position++;

        // Skip whitespace after =
        while (_position < _data.Length && (_data[_position] == (byte)' ' || _data[_position] == (byte)'\t'))
            _position++;

        // Parse value
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            // Quoted string value
            _position++;
            var buf = ArrayPool<byte>.Shared.Rent(256);
            _rentedBuffer = buf;
            int di = 0;
            while (_position < _data.Length && _data[_position] != (byte)'"')
            {
                if (_data[_position] == (byte)'\\' && _position + 1 < _data.Length)
                {
                    _position++;
                    buf[di++] = _data[_position] switch
                    {
                        (byte)'n' => (byte)'\n',
                        (byte)'t' => (byte)'\t',
                        (byte)'r' => (byte)'\r',
                        (byte)'\\' => (byte)'\\',
                        (byte)'"' => (byte)'"',
                        var c => c,
                    };
                    _position++;
                }
                else
                    buf[di++] = _data[_position++];
            }
            if (_position < _data.Length) _position++;
            _pendingValue = buf.AsSpan(0, di);
            _pendingValueType = TokenType.String;
        }
        else
        {
            // Unquoted value — detect type
            var valStart = _position;
            while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r')
                _position++;
            var raw = _data[valStart.._position];

            // Strip inline comment
            var semiIdx = raw.IndexOf((byte)';');
            var hashIdx = raw.IndexOf((byte)'#');
            var commentIdx = semiIdx < 0 ? hashIdx : hashIdx < 0 ? semiIdx : Math.Min(semiIdx, hashIdx);
            if (commentIdx >= 0)
                raw = TrimEnd(raw[..commentIdx]);
            raw = Trim(raw);

            _pendingValue = raw;
            _pendingValueType = DetectValueType(raw);
        }

        SkipToNextLineSpan();
        _hasPendingValue = true;
        return true;
    }

    private static TokenType DetectValueType(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) return TokenType.String;
        if (raw.SequenceEqual("true"u8) || raw.SequenceEqual("false"u8)) return TokenType.Bool;
        if (raw.SequenceEqual("null"u8)) return TokenType.Null;

        bool hasDot = false, hasExp = false;
        int start = raw[0] == (byte)'-' ? 1 : 0;
        if (start >= raw.Length) return TokenType.String;
        for (int i = start; i < raw.Length; i++)
        {
            byte c = raw[i];
            if (c == (byte)'.') hasDot = true;
            else if (c is (byte)'e' or (byte)'E') hasExp = true;
            else if (c < (byte)'0' || c > (byte)'9') return TokenType.String;
        }
        return (hasDot || hasExp) ? TokenType.Float64 : TokenType.Int32;
    }

    // ── Sequence-mode read ──

    private bool ReadSeq()
    {
        if (_hasPendingSectionEnd)
        {
            _tokenType = TokenType.ObjectEnd;
            _hasPendingSectionEnd = false;
            _inSection = false;
            return true;
        }

        if (_hasPendingValue)
        {
            _tokenType = _pendingValueType;
            _currentValue = _pendingValue;
            _hasPendingValue = false;
            return true;
        }

        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)'\n' or (byte)'\r')
            {
                _seqReader.Advance(1);
                if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
                    _seqReader.Advance(1);
                continue;
            }
            if (b is (byte)' ' or (byte)'\t') { _seqReader.Advance(1); continue; }
            if (b == (byte)'[') return ReadSectionSeq();
            if (b is (byte)';' or (byte)'#') { SkipToNextLineSeq(); continue; }
            return ReadKeyValueSeq();
        }
        return false;
    }

    private bool ReadSectionSeq()
    {
        if (_inSection)
        {
            _tokenType = TokenType.ObjectEnd;
            _inSection = false;
            _pendingSectionName = ReadSectionNameSeq();
            _hasPendingSectionStart = true;
            return true;
        }
        _currentValue = ReadSectionNameSeq();
        _inSection = true;
        _tokenType = TokenType.ObjectStart;
        return true;
    }

    private ReadOnlySpan<byte> ReadSectionNameSeq()
    {
        _seqReader.Advance(1);
        var buf = ArrayPool<byte>.Shared.Rent(64);
        _rentedBuffer = buf;
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        if (_seqReader.End) throw new FormatException("Unterminated section");
        _seqReader.Advance(1);
        SkipToNextLineSeq();
        return buf.AsSpan(0, di);
    }

    private bool ReadKeyValueSeq()
    {
        var buf = ArrayPool<byte>.Shared.Rent(256);
        _rentedBuffer = buf;
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'=')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _currentValue = TrimEnd(buf.AsSpan(0, di));
        _tokenType = TokenType.PropertyName;
        _seqReader.Advance(1);
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        var raw = Trim(buf.AsSpan(0, di));
        _pendingValue = raw;
        _pendingValueType = DetectValueType(raw);
        SkipToNextLineSeq();
        _hasPendingValue = true;
        return true;
    }

    private void SkipToNextLineSpan()
    {
        while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r')
            _position++;
        if (_position < _data.Length)
        {
            if (_data[_position] == (byte)'\r' && _position + 1 < _data.Length && _data[_position + 1] == (byte)'\n')
                _position += 2;
            else
                _position++;
        }
    }

    private void SkipToNextLineSeq()
    {
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r')
            _seqReader.Advance(1);
        if (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
            if (!_seqReader.End && b == (byte)'\r' && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
                _seqReader.Advance(1);
        }
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> s)
    {
        int e = s.Length;
        while (e > 0 && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t')) e--;
        return s[..e];
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int st = 0, e = s.Length;
        while (st < e && (s[st] == (byte)' ' || s[st] == (byte)'\t')) st++;
        while (e > st && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t')) e--;
        return s[st..e];
    }
}
