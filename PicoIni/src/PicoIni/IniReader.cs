namespace PicoIni;

public ref struct IniReader
{
    private ReadOnlySpan<byte> _data;
    private int _position;
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;
    private IniTokenType _tokenType;
    private ReadOnlySpan<byte> _sectionName;
    private ReadOnlySpan<byte> _key;
    private ReadOnlySpan<byte> _valueSpan;
    private ReadOnlySpan<byte> _commentText;
    private byte[]? _rentedBuffer;
    private bool _skipNewLine;

    public IniReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = IniTokenType.None;
        _sectionName = default;
        _key = default;
        _valueSpan = default;
        _commentText = default;
        _rentedBuffer = null;
        _skipNewLine = false;
    }

    public IniReader(ReadOnlySequence<byte> data)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = IniTokenType.None;
        _sectionName = default;
        _key = default;
        _valueSpan = default;
        _commentText = default;
        _rentedBuffer = null;
        _skipNewLine = false;
    }

    public IniTokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> SectionName => _sectionName;
    public ReadOnlySpan<byte> Key => _key;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;
    public ReadOnlySpan<byte> CommentText => _commentText;

    public bool Read()
    {
        if (_isSequence)
            return ReadSeq();
        return ReadSpan();
    }

    public bool SectionNameEquals(ReadOnlySpan<byte> name)
    {
        if (_sectionName.Length != name.Length) return false;
        for (int i = 0; i < name.Length; i++)
        {
            var a = _sectionName[i];
            var b = name[i];
            if (a != b && (a | 0x20) != (b | 0x20)) return false;
        }
        return true;
    }

    public bool SectionNameEquals(string name) =>
        SectionNameEquals(Encoding.UTF8.GetBytes(name));

    public bool TryGetInt32(out int v)
    {
        if (_tokenType != IniTokenType.Key) { v = 0; return false; }
        return int.TryParse(Encoding.UTF8.GetString(_valueSpan), out v);
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType != IniTokenType.Key) { v = 0; return false; }
        return long.TryParse(Encoding.UTF8.GetString(_valueSpan), out v);
    }

    public bool TryGetFloat64(out double v)
    {
        if (_tokenType != IniTokenType.Key) { v = 0; return false; }
        return double.TryParse(Encoding.UTF8.GetString(_valueSpan),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out v);
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType != IniTokenType.Key) { v = false; return false; }
        if (_valueSpan.SequenceEqual("true"u8)) { v = true; return true; }
        if (_valueSpan.SequenceEqual("false"u8)) { v = false; return true; }
        v = false;
        return bool.TryParse(Encoding.UTF8.GetString(_valueSpan), out v);
    }

    public void Dispose()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }

    private bool ReadSpan()
    {
        while (_position < _data.Length)
        {
            var b = _data[_position];

            if (b is (byte)'\n' or (byte)'\r')
            {
                _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n'
                    && _data[_position - 1] == (byte)'\r')
                    _position++;
                _tokenType = IniTokenType.Blank;
                return true;
            }

            if (b is (byte)' ' or (byte)'\t')
            {
                _position++;
                continue;
            }

            if (b == (byte)'[')
                return ReadSectionSpan();
            if (b is (byte)';' or (byte)'#')
                return ReadCommentSpan();
            return ReadKeyValueSpan();
        }
        return false;
    }

    private bool ReadSectionSpan()
    {
        _position++; // skip '['
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)']')
            _position++;
        if (_position >= _data.Length)
            throw new FormatException("Unterminated section at end of input");
        _sectionName = _data[start.._position];
        _position++; // skip ']'
        SkipToNextLineSpan();
        _tokenType = IniTokenType.SectionStart;
        return true;
    }

    private bool ReadCommentSpan()
    {
        _position++; // skip ; or #
        var start = _position;
        while (_position < _data.Length
            && _data[_position] != (byte)'\n'
            && _data[_position] != (byte)'\r')
            _position++;
        _commentText = _data[start.._position];
        SkipToNextLineSpan();
        _tokenType = IniTokenType.Comment;
        return true;
    }

    private bool ReadKeyValueSpan()
    {
        var keyStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _key = TrimEnd(_data[keyStart.._position]);
        _position++; // skip '='

        while (_position < _data.Length
            && (_data[_position] == (byte)' ' || _data[_position] == (byte)'\t'))
            _position++;

        var valStart = _position;
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++; // skip opening "
            valStart = _position;
            var buf = ArrayPool<byte>.Shared.Rent(256);
            _rentedBuffer = buf;
            int di = 0;
            while (_position < _data.Length && _data[_position] != (byte)'"')
            {
                if (_data[_position] == (byte)'\\' && _position + 1 < _data.Length)
                {
                    _position++;
                    switch (_data[_position])
                    {
                        case (byte)'n': buf[di++] = (byte)'\n'; break;
                        case (byte)'t': buf[di++] = (byte)'\t'; break;
                        case (byte)'r': buf[di++] = (byte)'\r'; break;
                        case (byte)'\\': buf[di++] = (byte)'\\'; break;
                        case (byte)'"': buf[di++] = (byte)'"'; break;
                        default: buf[di++] = _data[_position]; break;
                    }
                    _position++;
                }
                else
                {
                    buf[di++] = _data[_position++];
                }
            }
            if (_position < _data.Length) _position++; // skip closing "
            _valueSpan = buf.AsSpan(0, di);
        }
        else
        {
            while (_position < _data.Length
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r')
                _position++;
            _valueSpan = Trim(_data[valStart.._position]);
            // Strip inline comment
            var semiIdx = _valueSpan.IndexOf((byte)';');
            var hashIdx = _valueSpan.IndexOf((byte)'#');
            var commentIdx = semiIdx < 0 ? hashIdx
                : hashIdx < 0 ? semiIdx
                : Math.Min(semiIdx, hashIdx);
            if (commentIdx >= 0)
                _valueSpan = TrimEnd(_valueSpan[..commentIdx]);
        }

        SkipToNextLineSpan();
        _tokenType = IniTokenType.Key;
        return true;
    }

    private void SkipToNextLineSpan()
    {
        while (_position < _data.Length
            && _data[_position] != (byte)'\n'
            && _data[_position] != (byte)'\r')
            _position++;
        if (_position < _data.Length)
        {
            if (_data[_position] == (byte)'\r'
                && _position + 1 < _data.Length
                && _data[_position + 1] == (byte)'\n')
                _position += 2;
            else
                _position++;
        }
    }

    private bool ReadSeq()
    {
        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)'\n' or (byte)'\r')
            {
                _seqReader.Advance(1);
                if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
                    _seqReader.Advance(1);
                _tokenType = IniTokenType.Blank;
                return true;
            }
            if (b is (byte)' ' or (byte)'\t') { _seqReader.Advance(1); continue; }
            if (b == (byte)'[') return ReadSectionSeq();
            if (b is (byte)';' or (byte)'#') return ReadCommentSeq();
            return ReadKeyValueSeq();
        }
        return false;
    }

    private bool ReadSectionSeq()
    {
        _seqReader.Advance(1); // skip '['
        var buf = ArrayPool<byte>.Shared.Rent(64);
        _rentedBuffer = buf;
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        if (_seqReader.End) throw new FormatException("Unterminated section");
        _seqReader.Advance(1); // skip ']'
        _sectionName = buf.AsSpan(0, di);
        SkipToNextLineSeq();
        _tokenType = IniTokenType.SectionStart;
        return true;
    }

    private bool ReadCommentSeq()
    {
        _seqReader.Advance(1); // skip ; or #
        var buf = ArrayPool<byte>.Shared.Rent(256);
        _rentedBuffer = buf;
        int di = 0;
        while (!_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _commentText = buf.AsSpan(0, di);
        SkipToNextLineSeq();
        _tokenType = IniTokenType.Comment;
        return true;
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
        _key = TrimEnd(buf.AsSpan(0, di));
        _seqReader.Advance(1); // skip '='

        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        di = 0;
        while (!_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _valueSpan = buf.AsSpan(0, di);
        SkipToNextLineSeq();
        _tokenType = IniTokenType.Key;
        return true;
    }

    private void SkipToNextLineSeq()
    {
        while (!_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r')
            _seqReader.Advance(1);
        if (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
            if (!_seqReader.End && b == (byte)'\r'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
                _seqReader.Advance(1);
        }
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> span)
    {
        int end = span.Length;
        while (end > 0 && (span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t'))
            end--;
        return span[..end];
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span)
    {
        int start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t'))
            start++;
        int end = span.Length;
        while (end > start && (span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t'))
            end--;
        return span[start..end];
    }
}
