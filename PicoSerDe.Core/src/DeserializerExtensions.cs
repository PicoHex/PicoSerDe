namespace PicoSerDe.Core;

public static class DeserializerExtensions
{
    public static T Deserialize<T>(this IDeserializer<T> deserializer, byte[] data)
    {
        return deserializer.Deserialize(data.AsSpan());
    }

    public static T Deserialize<T>(this IDeserializer<T> deserializer, string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        return deserializer.Deserialize(bytes.AsSpan());
    }

    public static T DeserializeFromStream<T>(this IDeserializer<T> deserializer, Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return deserializer.Deserialize(ms.ToArray().AsSpan());
    }

    public static async ValueTask<T> DeserializeFromStreamAsync<T>(
        this IDeserializer<T> deserializer,
        Stream stream,
        CancellationToken ct = default
    )
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return deserializer.Deserialize(ms.ToArray().AsSpan());
    }

    public static async ValueTask<T> DeserializeFromPipeAsync<T>(
        this IDeserializer<T> deserializer,
        PipeReader reader,
        CancellationToken ct = default
    )
    {
        ReadResult result;
        do
        {
            ct.ThrowIfCancellationRequested();
            result = await reader.ReadAsync(ct);
        } while (!result.IsCompleted && result.Buffer.Length == 0);

        var buffer = result.Buffer;
        byte[] data;

        if (buffer.IsSingleSegment)
        {
            data = buffer.FirstSpan.ToArray();
        }
        else
        {
            data = new byte[buffer.Length];
            buffer.CopyTo(data);
        }

        reader.AdvanceTo(buffer.End);
        return deserializer.Deserialize(data);
    }
}
