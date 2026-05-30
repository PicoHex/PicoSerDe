namespace PicoSerDe.Core.Tests;

public class InterfaceTests
{
    private struct Int32Serializer : ISerializer<int>
    {
        public void Serialize(IBufferWriter<byte> writer, int value)
        {
            var span = writer.GetSpan(4);
            _ = BitConverter.TryWriteBytes(span, value);
            writer.Advance(4);
        }
    }

    private struct Int32Deserializer : IDeserializer<int>
    {
        public int Deserialize(ReadOnlySpan<byte> data)
        {
            return BitConverter.ToInt32(data);
        }
    }

    [Test]
    public async Task Serializer_WritesToBuffer()
    {
        var buf = new ArrayBufferWriter<byte>(64);
        var serializer = new Int32Serializer();
        serializer.Serialize(buf, 42);
        await Assert.That(buf.WrittenCount).IsEqualTo(4);
    }

    [Test]
    public async Task Deserializer_ReadsFromSpan()
    {
        var data = BitConverter.GetBytes(42);
        var deserializer = new Int32Deserializer();
        var result = deserializer.Deserialize(data);
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task Serialize_Deserialize_RoundTrip()
    {
        var serializer = new Int32Serializer();
        var deserializer = new Int32Deserializer();
        var buf = new ArrayBufferWriter<byte>(64);
        serializer.Serialize(buf, 12345);
        var result = deserializer.Deserialize(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo(12345);
    }

    [Test]
    public async Task Serializer_InterfaceAssignment_Compiles()
    {
        ISerializer<int> serializer = new Int32Serializer();
        var buf = new ArrayBufferWriter<byte>(64);
        serializer.Serialize(buf, 10);
        await Assert.That(buf.WrittenCount).IsEqualTo(4);
    }

    [Test]
    public async Task Deserializer_InterfaceAssignment_Compiles()
    {
        IDeserializer<int> deserializer = new Int32Deserializer();
        var result = deserializer.Deserialize(BitConverter.GetBytes(99));
        await Assert.That(result).IsEqualTo(99);
    }
}
