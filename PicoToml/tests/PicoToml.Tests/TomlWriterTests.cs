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
    public async Task WriteArray_Ints()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteStartArray("scores"u8);
        w.WriteArrayValue(1);
        w.WriteArrayValue(2);
        w.WriteArrayValue(3);
        w.WriteEndArray();
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("scores = [1, 2, 3]\n");
    }

    [Test]
    public async Task WriteArray_Strings()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteStartArray("tags"u8);
        w.WriteArrayValue("dev");
        w.WriteArrayValue("runner");
        w.WriteEndArray();
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("tags = [\"dev\", \"runner\"]\n");
    }

    [Test]
    public async Task WriteArray_Nested()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new TomlWriter(buf);
        w.WriteStartArray("matrix"u8);
        w.WriteStartArray(default);
        w.WriteArrayValue(1);
        w.WriteArrayValue(2);
        w.WriteEndArray();
        w.WriteStartArray(default);
        w.WriteArrayValue(3);
        w.WriteArrayValue(4);
        w.WriteEndArray();
        w.WriteEndArray();
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("matrix = [[1, 2], [3, 4]]\n");
    }
}
