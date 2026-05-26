namespace PicoIni.Tests;

public class IniWriterTests
{
    // ── Comments ──

    [Test]
    public async Task WriteComment_Semicolon_WritesCorrectly()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteComment("hello world");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("; hello world\r\n");
    }

    [Test]
    public async Task WriteComment_Hash_WritesCorrectly()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteComment("#", "hash comment");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("# hash comment\r\n");
    }

    // ── Sections ──

    [Test]
    public async Task WriteSection_WritesBracketedName()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteSection("Server");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("[Server]\r\n");
    }

    [Test]
    public async Task WriteSection_WithSpaces()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteSection("My Section");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("[My Section]\r\n");
    }

    // ── Key-Value (primitives) ──

    [Test]
    public async Task WriteKeyValue_String_WritesWithoutQuotes()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Host", "localhost");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Host = localhost\r\n");
    }

    [Test]
    public async Task WriteKeyValue_Int32()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Port", 8080);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Port = 8080\r\n");
    }

    [Test]
    public async Task WriteKeyValue_Int64()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Big", 9_223_372_036_854_775_807L);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Big = 9223372036854775807\r\n");
    }

    [Test]
    public async Task WriteKeyValue_Bool_True()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Enabled", true);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Enabled = true\r\n");
    }

    [Test]
    public async Task WriteKeyValue_Bool_False()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Enabled", false);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Enabled = false\r\n");
    }

    [Test]
    public async Task WriteKeyValue_Double()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Pi", 3.14);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).Contains("Pi = ");
        await Assert.That(output).Contains("3.14");
    }

    [Test]
    public async Task WriteKeyValue_Decimal()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Price", 49.99m);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).Contains("49.99");
    }

    // ── Value Quoting ──

    [Test]
    public async Task WriteKeyValue_WithSpaces_QuotesValue()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Name", "hello world");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Name = \"hello world\"\r\n");
    }

    [Test]
    public async Task WriteKeyValue_WithEquals_QuotesValue()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Conn", "server=.;db=mydb");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Conn = \"server=.;db=mydb\"\r\n");
    }

    [Test]
    public async Task WriteKeyValue_WithSemicolon_QuotesValue()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Path", "C:\\dir;D:\\dir");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Path = \"C:\\dir;D:\\dir\"\r\n");
    }

    [Test]
    public async Task WriteKeyValue_WithHash_QuotesValue()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Color", "#FF0000");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Color = \"#FF0000\"\r\n");
    }

    [Test]
    public async Task WriteKeyValue_WithQuote_EscapesQuote()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Text", "say \"hello\"");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Text = \"say \\\"hello\\\"\"\r\n");
    }

    [Test]
    public async Task WriteKeyValue_LeadingSpace_QuotesValue()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Indent", "  indented");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Indent = \"  indented\"\r\n");
    }

    [Test]
    public async Task WriteKeyValue_TrailingSpace_QuotesValue()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Suffix", "trailing  ");
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Suffix = \"trailing  \"\r\n");
    }

    // ── Blank Line ──

    [Test]
    public async Task WriteBlankLine()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteBlankLine();
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("\r\n");
    }

    // ── Multiple operations ──

    [Test]
    public async Task FullSection_ProducesCorrectIni()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteComment("Server config");
        writer.WriteSection("Server");
        writer.WriteKeyValue("Host", "localhost");
        writer.WriteKeyValue("Port", 8080);
        writer.WriteKeyValue("Enabled", true);
        writer.WriteBlankLine();
        writer.WriteSection("Database");
        writer.WriteKeyValue("ConnStr", "server=.;db=mydb");

        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).Contains("; Server config\r\n");
        await Assert.That(output).Contains("[Server]\r\n");
        await Assert.That(output).Contains("Host = localhost\r\n");
        await Assert.That(output).Contains("Port = 8080\r\n");
        await Assert.That(output).Contains("Enabled = true\r\n");
        await Assert.That(output).Contains("\r\n[Database]\r\n");
        await Assert.That(output).Contains("ConnStr = \"server=.;db=mydb\"\r\n");
    }

    // ── UTF-8 overloads ──

    [Test]
    public async Task WriteSection_Utf8()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteSection("Server"u8);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("[Server]\r\n");
    }

    [Test]
    public async Task WriteKeyValue_Utf8()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("Name"u8, "value"u8);
        var output = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(output).IsEqualTo("Name = value\r\n");
    }

    [Test]
    public async Task BytesWritten_TracksOutput()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        writer.WriteKeyValue("A", "b");
        var len = Encoding.UTF8.GetByteCount("A = b\r\n");
        await Assert.That(writer.BytesWritten).IsEqualTo(len);
    }
}
