namespace PicoYaml;

public ref struct YamlWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private readonly int _maxDepth;
    private int _depth;
    private bool _afterKey;

    public long BytesWritten => _bytesWritten;

    public YamlWriter(IBufferWriter<byte> buffer, int maxDepth = 256)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _maxDepth = maxDepth;
        _depth = 0;
        _afterKey = false;
    }

    public void WriteComment(string text)
    {
        for (int i = 0; i < _depth; i++)
            WriteRaw("  "u8);
        WriteRaw("# "u8);
        WriteRaw(Encoding.UTF8.GetBytes(text));
        WriteNewLine();
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        for (int i = 0; i < _depth; i++)
            WriteRaw("  "u8);
        WriteRaw(utf8Name);
        WriteRaw(":"u8);
        _afterKey = true;
    }

    public void WritePropertyName(scoped ReadOnlySpan<char> name)
    {
        int max = Encoding.UTF8.GetMaxByteCount(name.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(name, buf);
            for (int i = 0; i < _depth; i++)
            {
                _buffer.Write("  "u8);
                _bytesWritten += 2;
            }
            _buffer.Write(buf[..w]);
            _bytesWritten += w;
            _buffer.Write(":"u8);
            _bytesWritten++;
            _afterKey = true;
        }
        else
        {
            WritePropertyName(Encoding.UTF8.GetBytes(name.ToArray()));
        }
    }

    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        if (NeedsQuoting(utf8Value))
        {
            WriteByte((byte)'"');
            WriteEscaped(utf8Value);
            WriteByte((byte)'"');
        }
        else
            WriteRaw(utf8Value);
        WriteNewLine();
    }

    public void WriteString(scoped ReadOnlySpan<char> value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (max <= 256)
        {
            Span<byte> buf = stackalloc byte[max];
            int w = Encoding.UTF8.GetBytes(value, buf);
            var slice = buf[..w];
            if (NeedsQuoting(slice))
            {
                _buffer.Write("\""u8);
                _bytesWritten++;
                _buffer.Write(slice);
                _bytesWritten += w;
                _buffer.Write("\""u8);
                _bytesWritten++;
            }
            else
            {
                _buffer.Write(slice);
                _bytesWritten += w;
            }
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToArray());
            if (NeedsQuoting(bytes))
            {
                _buffer.Write("\""u8);
                _bytesWritten++;
                WriteEscaped(bytes);
                _buffer.Write("\""u8);
                _bytesWritten++;
            }
            else
            {
                _buffer.Write(bytes);
                _bytesWritten += bytes.Length;
            }
        }
        WriteNewLine();
    }

    public void WriteNumber(int value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        Span<byte> buf = _buffer.GetSpan(16);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteBoolean(bool value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        WriteRaw(value ? "true"u8 : "false"u8);
        WriteNewLine();
    }

    public void WriteInt64(long value)
    {
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException(
                "NaN and Infinity cannot be written as YAML",
                nameof(value)
            );
        if (_afterKey)
        {
            WriteByte((byte)' ');
            _afterKey = false;
        }
        Span<byte> buf = _buffer.GetSpan(32);
        value.TryFormat(buf, out var w);
        _buffer.Advance(w);
        _bytesWritten += w;
        WriteNewLine();
    }

    public void WriteStartMapping()
    {
        if (_depth >= _maxDepth)
            throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
        if (_afterKey)
        {
            WriteNewLine();
            _afterKey = false;
        }
        _depth++;
    }

    public void WriteEndMapping()
    {
        _depth--;
    }

    public void WriteSequenceItem(ReadOnlySpan<byte> utf8Value)
    {
        if (_afterKey)
        {
            WriteNewLine();
            _afterKey = false;
        }
        for (var i = 0; i < _depth; i++)
            WriteRaw("  "u8);
        WriteRaw("- "u8);
        if (NeedsQuoting(utf8Value))
        {
            WriteByte((byte)'"');
            WriteRaw(utf8Value);
            WriteByte((byte)'"');
        }
        else
            WriteRaw(utf8Value);
        WriteNewLine();
    }

    /// <summary>Writes a block sequence item that starts a mapping (-\n indented properties).</summary>
    public void WriteStartSequenceBlock()
    {
        if (_afterKey)
        {
            WriteNewLine();
            _afterKey = false;
        }
        for (var i = 0; i < _depth; i++)
            WriteRaw("  "u8);
        WriteRaw("- "u8);
        WriteNewLine();
        _depth++;
    }

    public void WriteEndSequenceBlock()
    {
        _depth--;
    }

    /// <summary>Writes an explicit tag like !person on its own line before a block mapping.</summary>
    public void WriteTag(string tag)
    {
        WriteRaw(Encoding.UTF8.GetBytes(tag));
        WriteByte((byte)'\n');
    }

    private void WriteRaw(ReadOnlySpan<byte> utf8)
    {
        _buffer.Write(utf8);
        _bytesWritten += utf8.Length;
    }

    private void WriteByte(byte v)
    {
        var s = _buffer.GetSpan(1);
        s[0] = v;
        _buffer.Advance(1);
        _bytesWritten++;
    }

    private void WriteNewLine()
    {
        WriteByte((byte)'\n');
    }

    private void WriteEscaped(ReadOnlySpan<byte> u)
    {
        for (int i = 0; i < u.Length; i++)
        {
            byte b = u[i];
            if (b == (byte)'"')
                WriteRaw("\\\""u8);
            else if (b == (byte)'\\')
                WriteRaw("\\\\"u8);
            else if (b == (byte)'\n')
                WriteRaw("\\n"u8);
            else if (b == (byte)'\r')
                WriteRaw("\\r"u8);
            else if (b == 0)
                WriteRaw("\\0"u8);
            else if (b < 0x20 && b != (byte)'\t')
            {
                WriteByte((byte)'\\');
                WriteByte((byte)'x');
                WriteByte((byte)(b / 16 < 10 ? '0' + b / 16 : 'A' + (b / 16 - 10)));
                WriteByte((byte)(b % 16 < 10 ? '0' + b % 16 : 'A' + (b % 16 - 10)));
            }
            else
                WriteByte(b);
        }
    }

    private static bool LooksLikeNumber(ReadOnlySpan<byte> u)
    {
        if (u.IsEmpty)
            return false;
        int start = 0;
        if (u[0] == (byte)'-')
        {
            if (u.Length < 2 || u[1] < (byte)'0' || u[1] > (byte)'9')
                return false;
            start = 1;
        }
        else if (u[0] < (byte)'0' || u[0] > (byte)'9')
            return false;
        // Handle "0x" / "0X" hex prefix
        if (
            start + 1 < u.Length
            && u[start] == (byte)'0'
            && (u[start + 1] == (byte)'x' || u[start + 1] == (byte)'X')
        )
        {
            for (int i = start + 2; i < u.Length; i++)
            {
                byte b = u[i];
                if (
                    !(
                        (b >= (byte)'0' && b <= (byte)'9')
                        || (b >= (byte)'a' && b <= (byte)'f')
                        || (b >= (byte)'A' && b <= (byte)'F')
                    )
                )
                    return false;
            }
            return u.Length > start + 2;
        }
        for (int i = start; i < u.Length; i++)
        {
            byte b = u[i];
            if (
                !(
                    (b >= (byte)'0' && b <= (byte)'9')
                    || b == (byte)'.'
                    || b == (byte)'e'
                    || b == (byte)'E'
                    || b == (byte)'+'
                    || b == (byte)'-'
                )
            )
                return false;
        }
        return true;
    }

    private static bool NeedsQuoting(ReadOnlySpan<byte> u)
    {
        if (u.IsEmpty)
            return true;

        // YAML literal keywords (true/false/null/yes/no/on/off)
        if (u.Length <= 5)
        {
            Span<char> tmp = stackalloc char[u.Length];
            int len = Encoding.UTF8.GetChars(u, tmp);
            var s = tmp[..len].ToString();
            if (
                s
                is "true"
                    or "false"
                    or "null"
                    or "~"
                    or "yes"
                    or "no"
                    or "on"
                    or "off"
                    or "True"
                    or "False"
                    or "Null"
                    or "Yes"
                    or "No"
                    or "On"
                    or "Off"
                    or "YES"
                    or "NO"
                    or "ON"
                    or "OFF"
            )
                return true;
        }

        // Leading indicator characters (except '-' which is a valid YAML list item marker)
        byte first = u[0];
        if (first is (byte)'?' or (byte)'@' or (byte)'%' or (byte)'`')
            return true;

        // Leading/trailing whitespace
        if (first == (byte)' ' || u[^1] == (byte)' ')
            return true;

        for (int i = 0; i < u.Length; i++)
        {
            var b = u[i];
            // C0 control characters (except \t and \n which are handled below)
            if (b < 0x20 && b != (byte)'\t' && b != (byte)'\n')
                return true;
            if (
                b == (byte)':'
                || b == (byte)'#'
                || b == (byte)'{'
                || b == (byte)'}'
                || b == (byte)'['
                || b == (byte)']'
                || b == (byte)','
                || b == (byte)'&'
                || b == (byte)'*'
                || b == (byte)'!'
                || b == (byte)'|'
                || b == (byte)'>'
                || b == (byte)'"'
                || b == (byte)'\''
                || b == (byte)'\n'
                || b == (byte)'\t'
            )
                return true;
        }
        return false;
    }
}
