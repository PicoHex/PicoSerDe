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
        await Assert.That(result).IsEqualTo(
            "server:\n  host: localhost\n  port: 8080\n");
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
}
