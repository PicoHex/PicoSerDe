using System.Buffers;
using System.Text;

namespace PicoSerDe.Abs.Tests;

public class ExtensionsTests
{
    private struct StringSerializer : ISerializer<string>
    {
        public void Serialize(IBufferWriter<byte> writer, string value)
        {
            var span = writer.GetSpan(Encoding.UTF8.GetByteCount(value));
            var len = Encoding.UTF8.GetBytes(value, span);
            writer.Advance(len);
        }
    }

    private struct StringDeserializer : IDeserializer<string>
    {
        public string Deserialize(ReadOnlySpan<byte> data)
        {
            return Encoding.UTF8.GetString(data);
        }
    }

    [Test]
    public async Task SerializeToBytes_ReturnsByteArray()
    {
        var serializer = new StringSerializer();
        var bytes = serializer.SerializeToBytes("hello");
        var result = Encoding.UTF8.GetString(bytes);
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task SerializeToString_ReturnsString()
    {
        var serializer = new StringSerializer();
        var result = serializer.SerializeToString("hello");
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task SerializeToStream_WritesBytes()
    {
        var serializer = new StringSerializer();
        using var stream = new MemoryStream();
        serializer.SerializeToStream(stream, "hello");
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var result = reader.ReadToEnd();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Deserialize_FromByteArray()
    {
        var deserializer = new StringDeserializer();
        var bytes = Encoding.UTF8.GetBytes("hello");
        var result = deserializer.Deserialize(bytes);
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task Deserialize_FromString()
    {
        var deserializer = new StringDeserializer();
        var result = deserializer.Deserialize("hello");
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task DeserializeFromStream_ReadsBytes()
    {
        var deserializer = new StringDeserializer();
        var bytes = Encoding.UTF8.GetBytes("hello");
        using var stream = new MemoryStream(bytes);
        var result = deserializer.DeserializeFromStream(stream);
        await Assert.That(result).IsEqualTo("hello");
    }
}
