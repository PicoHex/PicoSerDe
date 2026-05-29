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
    private byte[]?[] _rentedBuffers;
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
        _rentedBuffers = new byte[]?[8];
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
        _rentedBuffers = new byte[]?[8];
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

    private void TrackBuffer(byte[] buf)
    {
        if (_bufCount < _rentedBuffers.Length)
            _rentedBuffers[_bufCount++] = buf;
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
                _position++;
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
        while (
            !_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
        )
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _pendingValue = Trim(buf.AsSpan(0, di));
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

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> s)
    {
        int e = s.Length;
        while (e > 0 && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t'))
            e--;
        return s[..e];
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int st = 0,
            e = s.Length;
        while (st < e && (s[st] == (byte)' ' || s[st] == (byte)'\t'))
            st++;
        while (e > st && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t'))
            e--;
        return s[st..e];
    }
}
