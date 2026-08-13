namespace PicoSerDe.Core.Tests;

/// <summary>
/// H7 regression: DeserializeFromPipeAsync must assemble the complete payload
/// across multiple pipe segments before deserializing.
/// </summary>
public class PipeDeserializeTests
{
    private struct StringDeserializer : IDeserializer<string>
    {
        public string Deserialize(ReadOnlySpan<byte> data)
        {
            return Encoding.UTF8.GetString(data);
        }
    }

    [Test]
    public async Task DeserializeFromPipeAsync_AssemblesMultipleSegments()
    {
        var pipe = new Pipe();
        var payload = "{\"value\":42}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var half = bytes.Length / 2;
        await pipe.Writer.WriteAsync(bytes.AsMemory(0, half));
        await pipe.Writer.WriteAsync(bytes.AsMemory(half));
        await pipe.Writer.CompleteAsync();

        var deserializer = new StringDeserializer();
        var result = await deserializer.DeserializeFromPipeAsync(pipe.Reader);
        await Assert.That(result).IsEqualTo(payload);
    }

    [Test]
    public async Task DeserializeFromPipeAsync_EmptyPayload_ThrowsFormatException()
    {
        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();

        var deserializer = new StringDeserializer();
        var threw = false;
        try
        {
            await deserializer.DeserializeFromPipeAsync(pipe.Reader);
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }
}
