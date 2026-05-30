namespace PicoSerDe.Core;

public static class SerializerExtensions
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _sharedWriter;

    public static ArrayBufferWriter<byte> RentWriter()
    {
        var w = _sharedWriter ??= new ArrayBufferWriter<byte>(1024);
        w.Clear();
        return w;
    }

    public static byte[] SerializeToBytes<T>(this ISerializer<T> serializer, T value)
    {
        var writer = RentWriter();
        serializer.Serialize(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    public static string SerializeToString<T>(this ISerializer<T> serializer, T value)
    {
        var writer = RentWriter();
        serializer.Serialize(writer, value);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    public static void SerializeToStream<T>(this ISerializer<T> serializer, Stream stream, T value)
    {
        var writer = RentWriter();
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
        var writer = RentWriter();
        serializer.Serialize(writer, value);
        await stream.WriteAsync(writer.WrittenSpan.ToArray(), ct);
    }

    /// <summary>Throws a descriptive InvalidOperationException when no SG is registered for a type.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void ThrowNoSerializer<T>(string formatPackage) =>
        throw new InvalidOperationException(
            $"No serializer/deserializer registered for {typeof(T)}. Ensure {formatPackage} is referenced and the type is used with serializer methods."
        );
}
