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

    // Inline buffer tracking — avoids heap array allocation
    private byte[]? _rb0,
        _rb1,
        _rb2,
        _rb3,
        _rb4,
        _rb5,
        _rb6,
        _rb7;
    private int _bufCount;
    private int _depth;
    private readonly int _maxDepth;

    // Pending value: when Read() returns PropertyName, the value token is emitted
    // on the next Read() call. Type detection is deferred to TryGet* methods.
    private ReadOnlySpan<byte> _pendingValue;
    private bool _hasPendingValue;

    // Section tracking
    private bool _inSection;
    private bool _hasPendingSectionStart;
    private ReadOnlySpan<byte> _pendingSectionName;

    public IniReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _currentValue = default;
        _rentedBuffer = null;
        _rb0 = _rb1 = _rb2 = _rb3 = _rb4 = _rb5 = _rb6 = _rb7 = null;
        _bufCount = 0;
        _pendingValue = default;
        _hasPendingValue = false;
        _inSection = false;
        _hasPendingSectionStart = false;
        _pendingSectionName = default;
        _depth = 0;
        _maxDepth = 256;
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
        _rb0 = _rb1 = _rb2 = _rb3 = _rb4 = _rb5 = _rb6 = _rb7 = null;
        _bufCount = 0;
        _pendingValue = default;
        _hasPendingValue = false;
        _inSection = false;
        _hasPendingSectionStart = false;
        _pendingSectionName = default;
        _depth = 0;
        _maxDepth = 256;
    }

    public TokenType TokenType => _tokenType;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    public ReadOnlySpan<byte> GetStringRaw() => _currentValue;

    /// <summary>Optimized fast path: consume the pending value after a PropertyName read,
    /// without checking for section transitions.</summary>
    public void ReadValue()
    {
        _currentValue = _pendingValue;
        _hasPendingValue = false;
        _tokenType = TokenType.String;
    }

    public bool Read()
    {
        // Emit pending section start (from section transition)
        if (_hasPendingSectionStart)
        {
            _currentValue = _pendingSectionName;
            _hasPendingSectionStart = false;
            _tokenType = TokenType.ObjectStart;
            _inSection = true;
            if (++_depth > _maxDepth)
                throw new FormatException(
                    $"Maximum depth of {_maxDepth} exceeded at offset {BytesConsumed}"
                );
            return true;
        }

        // Emit pending value from previous PropertyName read
        if (_hasPendingValue)
        {
            _currentValue = _pendingValue;
            _hasPendingValue = false;
            // Value always starts as String — TryGet* methods parse on demand
            _tokenType = TokenType.String;
            return true;
        }

        return _isSequence ? ReadSeq() : ReadSpan();
    }

    // ── Lazy type accessors (accept String token, parse on demand) ──

    public bool TryGetInt32(out int v)
    {
        if (_tokenType is TokenType.Int32 or TokenType.String)
            return Utf8Parser.TryParse(_currentValue, out v, out _);
        v = 0;
        return false;
    }

    public bool TryGetInt64(out long v)
    {
        if (_tokenType is TokenType.Int32 or TokenType.Int64 or TokenType.String)
            return Utf8Parser.TryParse(_currentValue, out v, out _);
        v = 0;
        return false;
    }

    public bool TryGetFloat64(out double v)
    {
        if (
            _tokenType
            is TokenType.Int32
                or TokenType.Int64
                or TokenType.Float64
                or TokenType.String
        )
            return Utf8Parser.TryParse(_currentValue, out v, out _);
        v = 0;
        return false;
    }

    public bool TryGetBool(out bool v)
    {
        if (_tokenType is TokenType.Bool or TokenType.String)
        {
            if (_currentValue.SequenceEqual("true"u8))
            {
                v = true;
                return true;
            }
            if (_currentValue.SequenceEqual("false"u8))
            {
                v = false;
                return true;
            }
            return Utf8Parser.TryParse(_currentValue, out v, out _);
        }
        v = false;
        return false;
    }

    public void Skip() { }

    public bool TrySkip() => true;

    public void Dispose()
    {
        ReturnBuf(ref _rb0);
        ReturnBuf(ref _rb1);
        ReturnBuf(ref _rb2);
        ReturnBuf(ref _rb3);
        ReturnBuf(ref _rb4);
        ReturnBuf(ref _rb5);
        ReturnBuf(ref _rb6);
        ReturnBuf(ref _rb7);
        _bufCount = 0;
        _rentedBuffer = null;
    }

    private static void ReturnBuf(ref byte[]? buf)
    {
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
            buf = null;
        }
    }

    private void TrackBuffer(byte[] buf)
    {
        switch (_bufCount++)
        {
            case 0:
                _rb0 = buf;
                break;
            case 1:
                _rb1 = buf;
                break;
            case 2:
                _rb2 = buf;
                break;
            case 3:
                _rb3 = buf;
                break;
            case 4:
                _rb4 = buf;
                break;
            case 5:
                _rb5 = buf;
                break;
            case 6:
                _rb6 = buf;
                break;
            default:
                ReturnBuf(ref _rb7);
                _rb7 = buf;
                break;
        }
        _rentedBuffer = buf;
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
                if (
                    _position < _data.Length
                    && _data[_position] == (byte)'\n'
                    && _data[_position - 1] == (byte)'\r'
                )
                    _position++;
                continue;
            }
            if (b is (byte)' ' or (byte)'\t')
            {
                _position = PicoSerDe.Core.SimdHelpers.SkipSpacesAndTabs(_data, _position);
                continue;
            }
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
            _tokenType = TokenType.ObjectEnd;
            _inSection = false;
            _depth--;
            _pendingSectionName = ReadSectionNameSpan();
            _hasPendingSectionStart = true;
            return true;
        }
        _currentValue = ReadSectionNameSpan();
        _inSection = true;
        if (++_depth > _maxDepth)
            throw new FormatException(
                $"Maximum depth of {_maxDepth} exceeded at offset {BytesConsumed}"
            );
        _tokenType = TokenType.ObjectStart;
        return true;
    }

    private ReadOnlySpan<byte> ReadSectionNameSpan()
    {
        _position++;
        var start = _position;
        while (_position < _data.Length && _data[_position] != (byte)']')
            _position++;
        if (_position >= _data.Length)
            throw new FormatException("Unterminated section at end of input");
        var name = _data[start.._position];
        _position++;
        SkipToNextLineSpan();
        return name;
    }

    private bool ReadKeyValueSpan()
    {
        var keyStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _currentValue = TrimEnd(_data[keyStart.._position]);
        _tokenType = TokenType.PropertyName;
        _position++;
        while (
            _position < _data.Length
            && (_data[_position] == (byte)' ' || _data[_position] == (byte)'\t')
        )
            _position++;

        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            var buf = ArrayPool<byte>.Shared.Rent(256);
            TrackBuffer(buf);
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
            if (_position < _data.Length)
                _position++;
            _pendingValue = buf.AsSpan(0, di);
        }
        else
        {
            var valStart = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            var raw = _data[valStart.._position];
            var semiIdx = raw.IndexOf((byte)';');
            var hashIdx = raw.IndexOf((byte)'#');
            var commentIdx =
                semiIdx < 0
                    ? hashIdx
                    : hashIdx < 0
                        ? semiIdx
                        : Math.Min(semiIdx, hashIdx);
            if (commentIdx >= 0)
                raw = TrimEnd(raw[..commentIdx]);
            _pendingValue = Trim(raw);
        }

        SkipToNextLineSpan();
        _hasPendingValue = true;
        return true;
    }

    // ── Sequence-mode read ──

    private bool ReadSeq()
    {
        if (_hasPendingValue)
        {
            _currentValue = _pendingValue;
            _hasPendingValue = false;
            _tokenType = TokenType.String;
            return true;
        }
        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)'\n' or (byte)'\r')
            {
                _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                )
                    _seqReader.Advance(1);
                continue;
            }
            if (b is (byte)' ' or (byte)'\t')
            {
                _seqReader.Advance(1);
                continue;
            }
            if (b == (byte)'[')
                return ReadSectionSeq();
            if (b is (byte)';' or (byte)'#')
            {
                SkipToNextLineSeq();
                continue;
            }
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
            _depth--;
            _pendingSectionName = ReadSectionNameSeq();
            _hasPendingSectionStart = true;
            return true;
        }
        _currentValue = ReadSectionNameSeq();
        _inSection = true;
        if (++_depth > _maxDepth)
            throw new FormatException(
                $"Maximum depth of {_maxDepth} exceeded at offset {BytesConsumed}"
            );
        _tokenType = TokenType.ObjectStart;
        return true;
    }

    private ReadOnlySpan<byte> ReadSectionNameSeq()
    {
        _seqReader.Advance(1);
        var buf = ArrayPool<byte>.Shared.Rent(64);
        TrackBuffer(buf);
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        if (_seqReader.End)
            throw new FormatException("Unterminated section");
        _seqReader.Advance(1);
        SkipToNextLineSeq();
        return buf.AsSpan(0, di);
    }

    private bool ReadKeyValueSeq()
    {
        var buf = ArrayPool<byte>.Shared.Rent(256);
        TrackBuffer(buf);

        // Read key up to '='
        if (
            !_seqReader.TryReadTo(
                out ReadOnlySpan<byte> keyBytes,
                (byte)'=',
                advancePastDelimiter: true
            )
        )
        {
            // No '=' found: consume rest as value-less key (or error)
            _currentValue = default;
            _tokenType = TokenType.PropertyName;
            _hasPendingValue = false;
            SkipToNextLineSeq();
            return true;
        }
        _currentValue = TextHelpers.TrimEnd(keyBytes);
        _tokenType = TokenType.PropertyName;

        // Skip whitespace after '='
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        // Quoted value with escape handling
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            int di = 0;
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"'
            )
            {
                if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\\')
                {
                    _seqReader.Advance(1);
                    if (_seqReader.End)
                        break;
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] switch
                    {
                        (byte)'n' => (byte)'\n',
                        (byte)'t' => (byte)'\t',
                        (byte)'r' => (byte)'\r',
                        (byte)'\\' => (byte)'\\',
                        (byte)'"' => (byte)'"',
                        var c => c,
                    };
                    _seqReader.Advance(1);
                }
                else
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
            }
            if (!_seqReader.End)
                _seqReader.Advance(1); // skip closing quote
            _pendingValue = buf.AsSpan(0, di);
        }
        else
        {
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
            var raw = (ReadOnlySpan<byte>)buf.AsSpan(0, di);
            var semiIdx = raw.IndexOf((byte)';');
            var hashIdx = raw.IndexOf((byte)'#');
            var commentIdx =
                semiIdx < 0
                    ? hashIdx
                    : hashIdx < 0
                        ? semiIdx
                        : Math.Min(semiIdx, hashIdx);
            if (commentIdx >= 0)
                raw = TextHelpers.TrimEnd(raw[..commentIdx]);
            _pendingValue = TextHelpers.Trim(raw);
        }
        SkipToNextLineSeq();
        _hasPendingValue = true;
        return true;
    }

    // ── Helpers ──

    private void SkipToNextLineSpan()
    {
        while (
            _position < _data.Length
            && _data[_position] != (byte)'\n'
            && _data[_position] != (byte)'\r'
        )
            _position++;
        if (_position < _data.Length)
        {
            if (
                _data[_position] == (byte)'\r'
                && _position + 1 < _data.Length
                && _data[_position + 1] == (byte)'\n'
            )
                _position += 2;
            else
                _position++;
        }
    }

    private void SkipToNextLineSeq()
    {
        while (
            !_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
        )
            _seqReader.Advance(1);
        if (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
            if (
                !_seqReader.End
                && b == (byte)'\r'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
            )
                _seqReader.Advance(1);
        }
    }
}
