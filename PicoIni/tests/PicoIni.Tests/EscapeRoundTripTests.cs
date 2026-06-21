namespace PicoIni.Tests;

public class EscapeRoundTripTests
{
    [Test]
    public async Task BackslashInValue_RoundTripsCorrectly()
    {
        // Writer: NeedsQuoting detects space → uses WriteQuotedValue
        // BUG: WriteQuotedValue only escapes '"', not '\'
        // So "C:\Program Files" is written as "C:\Program Files" (bare backslash)
        // Reader: sees '\P' → escape handler fires → backslash consumed, 'P' passes through
        // Result: "C:Program Files" (backslash lost)
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        var original = "C:\\Program Files";

        writer.WriteKeyValue("Path"u8, Encoding.UTF8.GetBytes(original));
        // Now writer escapes backslash -> "C:\\Program Files"
        var written = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(written).IsEqualTo("Path = \"C:\\\\Program Files\"\r\n");

        // Read back
        var reader = new IniReader(buf.WrittenSpan);
        reader.Read(); // first read → PropertyName("Path")
        reader.ReadValue(); // second read → value string

        var roundTripped = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(roundTripped).IsEqualTo(original);
    }

    [Test]
    public async Task TabInQuotedValue_RoundTripsCorrectly()
    {
        // Writer: NeedsQuoting detects tab → uses WriteQuotedValue
        // BUG: WriteQuotedValue doesn't escape '\t', writes raw tab inside quotes
        // Many INI parsers treat raw tab inside quotes as literal tab
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        var original = "col1\tcol2"; // tab-separated

        writer.WriteKeyValue("Row"u8, Encoding.UTF8.GetBytes(original));
        var written = buf.WrittenSpan;

        var reader = new IniReader(written);
        reader.Read();
        reader.ReadValue();
        var roundTripped = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(roundTripped).IsEqualTo(original);
    }

    [Test]
    public async Task NewlineInQuotedValue_RoundTripsCorrectly()
    {
        // BUG: Writer doesn't escape '\n' → writes raw newline inside quoted value
        // INI lines are line-oriented → raw newline breaks the format
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        var original = "line1\nline2";

        writer.WriteKeyValue("Multi"u8, Encoding.UTF8.GetBytes(original));
        var written = buf.WrittenSpan;

        var reader = new IniReader(written);
        reader.Read();
        reader.ReadValue();
        var roundTripped = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(roundTripped).IsEqualTo(original);
    }

    [Test]
    public async Task EscapeSequences_AllRoundTrip()
    {
        // Test all escape sequences that the reader supports
        var buf = new ArrayBufferWriter<byte>(256);
        var writer = new IniWriter(buf);
        var original = "hello\nworld\ttest\rreturn\nnewline\\backslash\"quote";

        writer.WriteKeyValue("Esc"u8, Encoding.UTF8.GetBytes(original));
        var written = buf.WrittenSpan;

        var reader = new IniReader(written);
        reader.Read();
        reader.ReadValue();
        var roundTripped = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(roundTripped).IsEqualTo(original);
    }
}
