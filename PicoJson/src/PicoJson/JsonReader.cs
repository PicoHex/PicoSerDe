using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PicoJson;

public ref struct JsonReader
{
    // Span mode fields
    private ReadOnlySpan<byte> _data;
    private int _position;

    // Sequence mode fields
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;

    // Common
    private TokenType _tokenType;
    private int _depth;
    private readonly int _maxDepth;
    private ReadOnlySpan<byte> _valueSpan;
    private byte[]? _rentedBuffer;

    public JsonReader(ReadOnlySpan<byte> data, int maxDepth = 256)
    {
        _data = data;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = maxDepth;
        _valueSpan = default;
        _rentedBuffer = null;
    }

    public JsonReader(ReadOnlySequence<byte> data, int maxDepth = 256)
    {
        _data = default;
        _position = 0;
        _seqReader = new SequenceReader<byte>(data);
        _isSequence = true;
        _tokenType = TokenType.None;
        _depth = 0;
        _maxDepth = maxDepth;
        _valueSpan = default;
        _rentedBuffer = null;
    }

    public TokenType TokenType => _tokenType;
    public int Depth => _depth;

    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;

    public ReadOnlySpan<byte> ValueSpan => _valueSpan;

    public bool Read()
    {
        SkipWhitespace();
        if (IsAtEnd())
            return false;

        if (PeekByte() == (byte)',')
        {
            AdvanceByte();
            SkipWhitespace();
            if (IsAtEnd())
                return false;
        }

        var b = PeekByte();
        switch (b)
        {
            case (byte)'{':
                if (_depth >= _maxDepth)
                    throw new FormatException(
                        $"Maximum depth of {_maxDepth} exceeded at offset {BytesConsumed}"
                    );
                _tokenType = TokenType.ObjectStart;
                AdvanceByte();
                _depth++;
                return true;
            case (byte)'}':
                _tokenType = TokenType.ObjectEnd;
                AdvanceByte();
                _depth--;
                return true;
            case (byte)'[':
                if (_depth >= _maxDepth)
                    throw new FormatException(
                        $"Maximum depth of {_maxDepth} exceeded at offset {BytesConsumed}"
                    );
                _tokenType = TokenType.ArrayStart;
                AdvanceByte();
                _depth++;
                return true;
            case (byte)']':
                _tokenType = TokenType.ArrayEnd;
                AdvanceByte();
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
                throw new FormatException($"Unexpected byte 0x{b:X2} at offset {BytesConsumed}");
        }
    }

    public void Skip()
    {
        if (!TrySkip())
            throw new FormatException($"Failed to skip at offset {BytesConsumed}");
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

    /// <summary>
    /// Returns the decoded UTF-8 bytes of the current string or property name token.
    /// Escape sequences (\n, \r, \t, \\, \", \uXXXX) are resolved.
    /// </summary>
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

    public bool TryReadNextInt32(out int v)
    {
        if (_isSequence)
            return TryReadNextInt32Seq(out v);
        return TryReadNextInt32Span(out v);
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
        if (_tokenType is not (TokenType.Float64 or TokenType.Int32 or TokenType.Int64))
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

    private bool TryReadNextInt32Span(out int v)
    {
        var len = _data.Length;
        if (_position >= len) { v = 0; return false; }
        if (_data[_position] == (byte)',') _position++;
        if (_position < len && _data[_position] <= 32) SkipWhitespaceSpan();
        if (_position >= len || _data[_position] is (byte)']' or (byte)'}') { v = 0; return false; }

        bool neg = false;
        if (_data[_position] == (byte)'-') { neg = true; _position++; }
        if (_position >= len || !IsDigit(_data[_position])) { v = 0; return false; }

        int result = 0;
        do
        {
            result = result * 10 + (_data[_position] - (byte)'0');
            _position++;
        } while (_position < len && IsDigit(_data[_position]));
        v = neg ? -result : result;
        return true;
    }

    private bool TryReadNextInt32Seq(out int v)
    {
        SkipWhitespaceSeq();
        if (_seqReader.End) { v = 0; return false; }
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)',')
        {
            _seqReader.Advance(1);
            SkipWhitespaceSeq();
        }
        if (_seqReader.End) { v = 0; return false; }
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'-')
            _seqReader.Advance(1);
        var buf = ArrayPool<byte>.Shared.Rent(16);
        _rentedBuffer = buf;
        int di = 0;
        while (!_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex]))
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        return Utf8Parser.TryParse(buf.AsSpan(0, di), out v, out _);
    }

    // ── Mode-agnostic helpers ──

    private bool IsAtEnd() => _isSequence ? _seqReader.End : _position >= _data.Length;

    private byte PeekByte() =>
        _isSequence ? _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] : _data[_position];

    private void AdvanceByte()
    {
        if (_isSequence)
            _seqReader.Advance(1);
        else
            _position++;
    }

    private void SkipWhitespace()
    {
        if (_isSequence)
            SkipWhitespaceSeq();
        else
            SkipWhitespaceSpan();
    }

    private void SkipWhitespaceSpan()
    {
        // SIMD: process 16 bytes at a time
        if (Vector128.IsHardwareAccelerated)
        {
            var spaceVec = Vector128.Create((byte)0x20);
            var tabVec = Vector128.Create((byte)0x09);
            var newlineVec = Vector128.Create((byte)0x0A);
            var crVec = Vector128.Create((byte)0x0D);

            while (_position + 16 <= _data.Length)
            {
                ref readonly var src = ref _data[_position];
                var chunk = Vector128.LoadUnsafe(in src);
                var isWs = Vector128.Equals(chunk, spaceVec)
                    | Vector128.Equals(chunk, tabVec)
                    | Vector128.Equals(chunk, newlineVec)
                    | Vector128.Equals(chunk, crVec);
                var bits = isWs.ExtractMostSignificantBits();

                if (bits == 0xFFFF)
                {
                    _position += 16;
                    continue;
                }

                _position += BitOperations.TrailingZeroCount(~bits);
                return;
            }
        }

        // Scalar fallback: remaining bytes or no SIMD
        while (
            _position < _data.Length
            && _data[_position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'
        )
        {
            _position++;
        }
    }

    private void SkipWhitespaceSeq()
    {
        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                return;
            _seqReader.Advance(1);
        }
    }

    // ── String / Property reading ──

    private bool ReadStringOrProperty()
    {
        if (_isSequence)
            return ReadStringOrPropertySeq();
        return ReadStringOrPropertySpan();
    }

    private bool ReadStringOrPropertySpan()
    {
        _position++;
        var start = _position;
        while (_position < _data.Length)
        {
            if (_data[_position] == (byte)'"')
                break;
            if (_data[_position] == (byte)'\\' && _position + 1 < _data.Length)
                _position++;
            _position++;
        }
        if (_position >= _data.Length)
            throw new FormatException($"Unterminated string at offset {_position}");

        _valueSpan = _data[start.._position];
        _position++;
        UnescapeIfNeeded();

        var saved = _position;
        SkipWhitespaceSpan();
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

    private bool ReadStringOrPropertySeq()
    {
        _seqReader.Advance(1); // skip opening "

        var buf = ArrayPool<byte>.Shared.Rent(256);
        _rentedBuffer = buf;
        int di = 0;
        long bytesBeforeString = _seqReader.Consumed;

        try
        {
            while (!_seqReader.End)
            {
                var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                if (b == (byte)'"')
                {
                    _seqReader.Advance(1);
                    break;
                }
                if (b == (byte)'\\')
                {
                    _seqReader.Advance(1);
                    if (_seqReader.End)
                        throw new FormatException(
                            $"Unterminated escape sequence at offset {_seqReader.Consumed}"
                        );
                    b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                    switch (b)
                    {
                        case (byte)'"':
                            buf[di++] = (byte)'"';
                            break;
                        case (byte)'\\':
                            buf[di++] = (byte)'\\';
                            break;
                        case (byte)'/':
                            buf[di++] = (byte)'/';
                            break;
                        case (byte)'n':
                            buf[di++] = (byte)'\n';
                            break;
                        case (byte)'r':
                            buf[di++] = (byte)'\r';
                            break;
                        case (byte)'t':
                            buf[di++] = (byte)'\t';
                            break;
                        case (byte)'u':
                            di = ReadUnicodeEscapeSeq(buf, di);
                            break;
                        default:
                            buf[di++] = b;
                            break;
                    }
                    if (di >= buf.Length)
                    {
                        var newBuf = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        buf.AsSpan(0, di).CopyTo(newBuf);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = newBuf;
                        _rentedBuffer = buf;
                    }
                }
                else
                {
                    buf[di++] = b;
                    _seqReader.Advance(1);
                    if (di >= buf.Length)
                    {
                        var newBuf = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        buf.AsSpan(0, di).CopyTo(newBuf);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = newBuf;
                        _rentedBuffer = buf;
                    }
                }
            }

            _valueSpan = buf.AsSpan(0, di);
        }
        catch
        {
            ReturnBuffer();
            throw;
        }

        SkipWhitespaceSeq();
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)':')
        {
            _tokenType = TokenType.PropertyName;
            _seqReader.Advance(1);
        }
        else
        {
            _tokenType = TokenType.String;
        }
        return true;
    }

    private int ReadUnicodeEscapeSeq(byte[] buf, int di)
    {
        int codepoint = 0;
        for (int j = 0; j < 4; j++)
        {
            if (_seqReader.End)
                throw new FormatException(
                    $"Incomplete unicode escape at offset {_seqReader.Consumed}"
                );
            var hex = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
            codepoint <<= 4;
            if (hex is >= (byte)'0' and <= (byte)'9')
                codepoint |= hex - (byte)'0';
            else if (hex is >= (byte)'A' and <= (byte)'F')
                codepoint |= hex - (byte)'A' + 10;
            else if (hex is >= (byte)'a' and <= (byte)'f')
                codepoint |= hex - (byte)'a' + 10;
            else
                throw new FormatException(
                    $"Invalid unicode escape character '{(char)hex}' at offset {_seqReader.Consumed}"
                );
        }

        if (codepoint is >= 0xD800 and <= 0xDBFF)
        {
            if (_seqReader.End || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\\')
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
                );
            _seqReader.Advance(1);
            if (_seqReader.End || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'u')
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
                );
            _seqReader.Advance(1);

            int lowSurrogate = 0;
            for (int j = 0; j < 4; j++)
            {
                if (_seqReader.End)
                    throw new FormatException(
                        $"Incomplete unicode escape at offset {_seqReader.Consumed}"
                    );
                var hex = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
                lowSurrogate <<= 4;
                if (hex is >= (byte)'0' and <= (byte)'9')
                    lowSurrogate |= hex - (byte)'0';
                else if (hex is >= (byte)'A' and <= (byte)'F')
                    lowSurrogate |= hex - (byte)'A' + 10;
                else if (hex is >= (byte)'a' and <= (byte)'f')
                    lowSurrogate |= hex - (byte)'a' + 10;
                else
                    throw new FormatException(
                        $"Invalid unicode escape character '{(char)hex}' at offset {_seqReader.Consumed}"
                    );
            }

            if (lowSurrogate is < 0xDC00 or > 0xDFFF)
                throw new FormatException(
                    $"Invalid low surrogate U+{lowSurrogate:X4} at offset {_seqReader.Consumed}"
                );

            codepoint = 0x10000 + ((codepoint - 0xD800) << 10) + (lowSurrogate - 0xDC00);
        }
        else if (codepoint is >= 0xDC00 and <= 0xDFFF)
        {
            throw new FormatException(
                $"Lone low surrogate U+{codepoint:X4} at offset {_seqReader.Consumed}"
            );
        }

        if (codepoint < 0x80)
        {
            buf[di++] = (byte)codepoint;
        }
        else if (codepoint < 0x800)
        {
            buf[di++] = (byte)(0xC0 | (codepoint >> 6));
            buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else if (codepoint < 0x10000)
        {
            buf[di++] = (byte)(0xE0 | (codepoint >> 12));
            buf[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else
        {
            buf[di++] = (byte)(0xF0 | (codepoint >> 18));
            buf[di++] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
            buf[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            buf[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }

        return di;
    }

    private static bool ContainsBackslash(ReadOnlySpan<byte> span)
    {
        if (Vector128.IsHardwareAccelerated && span.Length >= 16)
        {
            var slash = Vector128.Create((byte)'\\');
            ref var ptr = ref MemoryMarshal.GetReference(span);
            int i = 0;
            for (; i + 16 <= span.Length; i += 16)
            {
                var chunk = Vector128.LoadUnsafe(ref Unsafe.Add(ref ptr, i));
                if (Vector128.Equals(chunk, slash) != Vector128<byte>.Zero)
                    return true;
            }
            span = span[i..];
        }
        return span.IndexOf((byte)'\\') >= 0;
    }

    private void UnescapeIfNeeded()
    {
        if (ContainsBackslash(_valueSpan))
        {
            // Design note: the token-layer spec calls for stackalloc on short
            // escaped strings. C# escape analysis prevents assigning stackalloc
            // Span<byte> to a ref struct field (CS8352). We mitigate by renting
            // a tight pool buffer: short strings use ArrayPool directly, which
            // is still cheaper than the old Rent(_valueSpan.Length) for large
            // strings.  For ≤256 bytes we rent exactly di (decoded size).
            var decoded = ArrayPool<byte>.Shared.Rent(_valueSpan.Length);
            _rentedBuffer = decoded;
            try
            {
                int di = 0;
                for (int si = 0; si < _valueSpan.Length; si++)
                {
                    if (_valueSpan[si] == (byte)'\\' && si + 1 < _valueSpan.Length)
                    {
                        si++;
                        switch (_valueSpan[si])
                        {
                            case (byte)'"': decoded[di++] = (byte)'"'; break;
                            case (byte)'\\': decoded[di++] = (byte)'\\'; break;
                            case (byte)'/': decoded[di++] = (byte)'/'; break;
                            case (byte)'n': decoded[di++] = (byte)'\n'; break;
                            case (byte)'r': decoded[di++] = (byte)'\r'; break;
                            case (byte)'t': decoded[di++] = (byte)'\t'; break;
                            case (byte)'u':
                                si = ReadUnicodeEscapeSpan(decoded, ref di, si);
                                break;
                            default: decoded[di++] = _valueSpan[si]; break;
                        }
                    }
                    else
                        decoded[di++] = _valueSpan[si];
                }
                _valueSpan = decoded.AsSpan(0, di);
            }
            catch
            {
                ReturnBuffer();
                throw;
            }
        }
    }

    // ── Number reading ──

    private bool ReadNumber()
    {
        long start;
        if (_isSequence)
        {
            start = _seqReader.Consumed;
            ReadNumberSeq();
            return true;
        }
        start = _position;
        ReadNumberSpan();
        return true;
    }

    private void ReadNumberSpan()
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
    }

    private void ReadNumberSeq()
    {
        var buf = ArrayPool<byte>.Shared.Rent(32);
        _rentedBuffer = buf;
        int di = 0;
        try
        {
            bool isFloat = false;
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'-')
            {
                buf[di++] = (byte)'-';
                _seqReader.Advance(1);
            }
            while (!_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex]))
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'.')
            {
                isFloat = true;
                buf[di++] = (byte)'.';
                _seqReader.Advance(1);
                while (
                    !_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex])
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
            }
            if (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] is (byte)'e' or (byte)'E'
            )
            {
                isFloat = true;
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] is (byte)'+' or (byte)'-'
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                while (
                    !_seqReader.End && IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex])
                )
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
            }
            _valueSpan = buf.AsSpan(0, di);
            _tokenType = isFloat ? TokenType.Float64 : TokenType.Int32;
        }
        catch
        {
            ReturnBuffer();
            throw;
        }
    }

    // ── Literal reading ──

    private bool ReadLiteral(ReadOnlySpan<byte> expected, TokenType token)
    {
        if (_isSequence)
            return ReadLiteralSeq(expected, token);
        return ReadLiteralSpan(expected, token);
    }

    private bool ReadLiteralSpan(ReadOnlySpan<byte> expected, TokenType token)
    {
        if (_position + expected.Length > _data.Length)
            throw new FormatException($"Unexpected EOF at offset {_position}");
        if (!_data.Slice(_position, expected.Length).SequenceEqual(expected))
            throw new FormatException($"Invalid literal at offset {_position}");
        _valueSpan = _data.Slice(_position, expected.Length);
        _tokenType = token;
        _position += expected.Length;
        return true;
    }

    private bool ReadLiteralSeq(ReadOnlySpan<byte> expected, TokenType token)
    {
        var buf = ArrayPool<byte>.Shared.Rent(expected.Length);
        _rentedBuffer = buf;
        try
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (
                    _seqReader.End
                    || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != expected[i]
                )
                    throw new FormatException($"Invalid literal at offset {_seqReader.Consumed}");
                buf[i] = expected[i];
                _seqReader.Advance(1);
            }
            _valueSpan = buf.AsSpan(0, expected.Length);
            _tokenType = token;
            return true;
        }
        catch
        {
            ReturnBuffer();
            throw;
        }
    }

    // ── Unicode escape (span path) ──

    private int ReadUnicodeEscapeSpan(byte[] decoded, ref int di, int si)
    {
        if (si + 4 >= _valueSpan.Length)
            throw new FormatException($"Incomplete unicode escape at offset {_position}");

        int codepoint = 0;
        for (int j = 0; j < 4; j++)
        {
            si++;
            var hex = _valueSpan[si];
            codepoint <<= 4;
            if (hex is >= (byte)'0' and <= (byte)'9')
                codepoint |= hex - (byte)'0';
            else if (hex is >= (byte)'A' and <= (byte)'F')
                codepoint |= hex - (byte)'A' + 10;
            else if (hex is >= (byte)'a' and <= (byte)'f')
                codepoint |= hex - (byte)'a' + 10;
            else
                throw new FormatException(
                    $"Invalid unicode escape character '{(char)hex}' at offset {_position}"
                );
        }

        if (codepoint is >= 0xD800 and <= 0xDBFF)
        {
            if (
                si + 6 >= _valueSpan.Length
                || _valueSpan[si + 1] != (byte)'\\'
                || _valueSpan[si + 2] != (byte)'u'
            )
                throw new FormatException(
                    $"Lone high surrogate U+{codepoint:X4} at offset {_position}"
                );

            si += 2;
            int lowSurrogate = 0;
            for (int j = 0; j < 4; j++)
            {
                si++;
                var hex = _valueSpan[si];
                lowSurrogate <<= 4;
                if (hex is >= (byte)'0' and <= (byte)'9')
                    lowSurrogate |= hex - (byte)'0';
                else if (hex is >= (byte)'A' and <= (byte)'F')
                    lowSurrogate |= hex - (byte)'A' + 10;
                else if (hex is >= (byte)'a' and <= (byte)'f')
                    lowSurrogate |= hex - (byte)'a' + 10;
                else
                    throw new FormatException(
                        $"Invalid unicode escape character '{(char)hex}' at offset {_position}"
                    );
            }

            if (lowSurrogate is < 0xDC00 or > 0xDFFF)
                throw new FormatException(
                    $"Invalid low surrogate U+{lowSurrogate:X4} at offset {_position}"
                );

            codepoint = 0x10000 + ((codepoint - 0xD800) << 10) + (lowSurrogate - 0xDC00);
        }
        else if (codepoint is >= 0xDC00 and <= 0xDFFF)
        {
            throw new FormatException($"Lone low surrogate U+{codepoint:X4} at offset {_position}");
        }

        if (codepoint < 0x80)
        {
            decoded[di++] = (byte)codepoint;
        }
        else if (codepoint < 0x800)
        {
            decoded[di++] = (byte)(0xC0 | (codepoint >> 6));
            decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else if (codepoint < 0x10000)
        {
            decoded[di++] = (byte)(0xE0 | (codepoint >> 12));
            decoded[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }
        else
        {
            decoded[di++] = (byte)(0xF0 | (codepoint >> 18));
            decoded[di++] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
            decoded[di++] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            decoded[di++] = (byte)(0x80 | (codepoint & 0x3F));
        }

        return si;
    }

    private void ReturnBuffer()
    {
        if (_rentedBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBuffer);
            _rentedBuffer = null;
        }
    }

    public void Dispose()
    {
        ReturnBuffer();
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}
