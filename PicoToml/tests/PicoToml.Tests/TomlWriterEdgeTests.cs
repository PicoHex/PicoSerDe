namespace PicoToml.Tests;

public class TomlWriterEdgeTests
{
    [Test]
    public async Task WriteKeyValue_Bool()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteKeyValue("enabled", true);
        w.WriteKeyValue("disabled", false);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("enabled = true");
        await Assert.That(result).Contains("disabled = false");
    }

    [Test]
    public async Task WriteKeyValue_Long()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteKeyValue("big", 9_999_999_999L);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("9" + new string('9', 9));
    }

    [Test]
    public async Task WriteKeyValue_QuotedKeyWhenNeeded()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteKeyValue("key with spaces", "value");
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("\"key with spaces\" = \"value\"");
    }

    [Test]
    public async Task WriteBlankLine_BetweenTables()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteTable("alpha");
        w.WriteKeyValue("x", 1);
        w.WriteBlankLine();
        w.WriteTable("beta");
        w.WriteKeyValue("y", 2);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("[alpha]\nx = 1\n\n[beta]\ny = 2");
    }

    [Test]
    public async Task BytesWritten_TracksOutput()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        var initial = w.BytesWritten;
        w.WriteKeyValue("x", 1);
        var afterWrite = w.BytesWritten;
        await Assert.That(initial).IsEqualTo(0);
        await Assert.That(afterWrite).IsGreaterThan(0);
    }
}
