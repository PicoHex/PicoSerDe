namespace PicoYaml.Tests;

public class YamlWriterTests
{
    [Test]
    public async Task WritePropertyName_And_WriteString()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("name"u8);
        w.WriteString(Encoding.UTF8.GetBytes("Alice"));
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("name: Alice\n");
    }

    [Test]
    public async Task NestedMapping_WithIndentation()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("server"u8);
        w.WriteStartMapping();
        w.WritePropertyName("host"u8);
        w.WriteString(Encoding.UTF8.GetBytes("localhost"));
        w.WritePropertyName("port"u8);
        w.WriteNumber(8080);
        w.WriteEndMapping();
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("server:\n  host: localhost\n  port: 8080\n");
    }

    [Test]
    public async Task WriteComment()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WriteComment("config section");
        w.WritePropertyName("key"u8);
        w.WriteString(Encoding.UTF8.GetBytes("value"));
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("# config section\nkey: value\n");
    }

    // ── NeedsQuoting: YAML literals ──

    [Test]
    public async Task WriteString_BooleanLiteral_IsQuoted()
    {
        foreach (var literal in new[] { "true", "false", "yes", "no", "on", "off" })
        {
            var buf = new ArrayBufferWriter<byte>(256);
            var w = new YamlWriter(buf);
            w.WritePropertyName("key"u8);
            w.WriteString(Encoding.UTF8.GetBytes(literal));
            var result = Encoding.UTF8.GetString(buf.WrittenSpan);
            await Assert.That(result).Contains("\"" + literal + "\"");
        }
    }

    [Test]
    public async Task WriteString_NullLiteral_IsQuoted()
    {
        foreach (var literal in new[] { "null", "~" })
        {
            var buf = new ArrayBufferWriter<byte>(256);
            var w = new YamlWriter(buf);
            w.WritePropertyName("key"u8);
            w.WriteString(Encoding.UTF8.GetBytes(literal));
            var result = Encoding.UTF8.GetString(buf.WrittenSpan);
            await Assert.That(result).Contains("\"" + literal + "\"");
        }
    }

    [Test]
    public async Task WriteString_NumericString_IsQuoted()
    {
        foreach (var literal in new[] { "123", "1.5", "-42", "0x1f" })
        {
            var buf = new ArrayBufferWriter<byte>(256);
            var w = new YamlWriter(buf);
            w.WritePropertyName("key"u8);
            w.WriteString(Encoding.UTF8.GetBytes(literal));
            var result = Encoding.UTF8.GetString(buf.WrittenSpan);
            await Assert.That(result).Contains("\"" + literal + "\"");
        }
    }

    [Test]
    public async Task WriteSequenceItem_IntList_NotQuoted()
    {
        // Numeric sequence items use WriteSequenceItem(int), bypassing NeedsQuoting
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WriteSequenceItem(10);
        w.WriteSequenceItem(20L);
        w.WriteSequenceItem(3.14);
        w.WriteSequenceItem(1.5f);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("- 10");
        await Assert.That(result).Contains("- 20");
        await Assert.That(result).Contains("- 3.14");
        await Assert.That(result).Contains("- 1.5");
        await Assert.That(result).DoesNotContain("\"10\"");
    }

    [Test]
    public async Task WriteString_MultipleDots_NotQuoted()
    {
        // "1.2.3" is not a valid YAML number — don't false-positive quote it
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("ver"u8);
        w.WriteString("1.2.3"u8);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("ver: 1.2.3");
        await Assert.That(result).DoesNotContain("\"1.2.3\"");
    }

    [Test]
    public async Task WriteString_LeadingIndicator_IsQuoted()
    {
        foreach (var literal in new[] { "?query", "@at", "%pct", "`bt" })
        {
            var buf = new ArrayBufferWriter<byte>(256);
            var w = new YamlWriter(buf);
            w.WritePropertyName("key"u8);
            w.WriteString(Encoding.UTF8.GetBytes(literal));
            var result = Encoding.UTF8.GetString(buf.WrittenSpan);
            await Assert.That(result).Contains("\"" + literal + "\"");
        }
    }

    [Test]
    public async Task WriteString_SpaceBoundary_IsQuoted()
    {
        foreach (var literal in new[] { " leading", "trailing ", " both " })
        {
            var buf = new ArrayBufferWriter<byte>(256);
            var w = new YamlWriter(buf);
            w.WritePropertyName("key"u8);
            w.WriteString(Encoding.UTF8.GetBytes(literal));
            var result = Encoding.UTF8.GetString(buf.WrittenSpan);
            await Assert.That(result).Contains("\"" + literal + "\"");
        }
    }

    // ── WriteEscaped: control characters ──

    [Test]
    public async Task WriteString_WithCarriageReturn_IsEscaped()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("key"u8);
        w.WriteString(Encoding.UTF8.GetBytes("line1\rline2"));
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        // \r should be escaped, not raw
        await Assert.That(result).DoesNotContain("\r");
        await Assert.That(result).Contains("\\r");
    }

    [Test]
    public async Task WriteString_WithNullChar_IsEscaped()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("key"u8);
        w.WriteString(new byte[] { (byte)'a', 0, (byte)'b' });
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("\\0");
    }
}
