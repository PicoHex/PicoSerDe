namespace PicoSerDe.Abs;

public static class SerializerExtensions
{
    public static byte[] SerializeToBytes<T>(this ISerializer<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    public static string SerializeToString<T>(this ISerializer<T> serializer, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    public static void SerializeToStream<T>(this ISerializer<T> serializer, Stream stream, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        stream.Write(writer.WrittenSpan);
    }

    public static async ValueTask SerializeToStreamAsync<T>(
        this ISerializer<T> serializer,
        T value,
        Stream stream,
        CancellationToken ct = default
    )
    {
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(writer, value);
        await stream.WriteAsync(writer.WrittenSpan.ToArray(), ct);
    }
}
