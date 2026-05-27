namespace PicoToml.Tests;

public class TomlWriterTests
{
    [Test]
    public async Task WriteKeyValue_String()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteKeyValue("name", "Alice");
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("name = \"Alice\"\n");
    }

    [Test]
    public async Task WriteKeyValue_Int()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteKeyValue("port", 8080);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("port = 8080\n");
    }

    [Test]
    public async Task WriteTable()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteTable("server");
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("[server]\n");
    }

    [Test]
    public async Task WriteComment()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteComment("config");
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("# config\n");
    }
}
