namespace PicoJson;

public ref struct JsonWriter
{
    private IBufferWriter<byte> _buffer;
    private long _bytesWritten;
    private readonly bool _indented;
    private int _depth;
    private long _needsComma;
    private bool _afterPropertyName;

    public long BytesWritten => _bytesWritten;

    public JsonWriter(IBufferWriter<byte> buffer, bool indented = false)
    {
        _buffer = buffer;
        _bytesWritten = 0;
        _indented = indented;
        _depth = 0;
        _needsComma = 0;
        _afterPropertyName = false;
    }

    public void Flush() { }

    public void WriteNull()
    {
        BeforeWriteValue();
        WriteRaw("null"u8);
    }

    public void WriteBoolean(bool value)
    {
        BeforeWriteValue();
        WriteRaw(value ? "true"u8 : "false"u8);
    }

    public void WriteNumber(int value)
    {
        BeforeWriteValue();
        Span<byte> buf = stackalloc byte[16];
        value.TryFormat(buf, out var w);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteNumber(long value)
    {
        BeforeWriteValue();
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var w);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteNumber(double value)
    {
        BeforeWriteValue();
        Span<byte> buf = stackalloc byte[32];
        value.TryFormat(buf, out var w);
        _buffer.Write(buf[..w]);
        _bytesWritten += w;
    }

    public void WriteString(ReadOnlySpan<byte> utf8Value)
    {
        BeforeWriteValue();
        WriteByte((byte)'"');
        WriteRaw(utf8Value);
        WriteByte((byte)'"');
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        if ((_needsComma & (1L << _depth)) != 0)
            WriteByte((byte)',');
        _needsComma |= (1L << _depth);
        if (_indented)
            WriteIndent();
        WriteByte((byte)'"');
        WriteRaw(utf8Name);
        WriteByte((byte)'"');
        WriteRaw(_indented ? ": "u8 : ":"u8);
        _afterPropertyName = true;
    }

    public void WriteStartObject()
    {
        BeforeWriteValue();
        WriteByte((byte)'{');
        _depth++;
        _needsComma &= ~(1L << _depth);
    }

    public void WriteEndObject()
    {
        _depth--;
        if (_indented)
            WriteIndent();
        WriteByte((byte)'}');
    }

    public void WriteStartArray()
    {
        BeforeWriteValue();
        WriteByte((byte)'[');
        _depth++;
        _needsComma &= ~(1L << _depth);
    }

    public void WriteEndArray()
    {
        _depth--;
        if (_indented)
            WriteIndent();
        WriteByte((byte)']');
    }

    private void BeforeWriteValue()
    {
        if (_afterPropertyName)
        {
            _afterPropertyName = false;
            return;
        }
        if ((_needsComma & (1L << _depth)) != 0)
            WriteByte((byte)',');
        _needsComma |= (1L << _depth);
    }

    private void WriteRaw(ReadOnlySpan<byte> utf8)
    {
        _buffer.Write(utf8);
        _bytesWritten += utf8.Length;
    }

    private void WriteByte(byte value)
    {
        var s = _buffer.GetSpan(1);
        s[0] = value;
        _buffer.Advance(1);
        _bytesWritten++;
    }

    private void WriteIndent()
    {
        WriteByte((byte)'\n');
        for (var i = 0; i < _depth; i++)
            WriteRaw("  "u8);
    }
}
