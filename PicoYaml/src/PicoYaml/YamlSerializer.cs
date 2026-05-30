namespace PicoYaml;

public static partial class YamlSerializer
{
    // Generic static cache — avoids dictionary lookup + cast on hot path
    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
        {
            var w = SerializerExtensions.RentWriter();
            s.Serialize(w, value);
            return w.WrittenSpan.ToArray();
        }
        ThrowNoSer<T>();
        return default;
    }

    public static string Serialize<T>(T value) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(value));

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
            s.Serialize(writer, value);
        else
            ThrowNoSer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null)
            return d.Deserialize(data);
        ThrowNoSer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSer<T>() =>
        throw new InvalidOperationException(
            $"No serializer registered for {typeof(T)}. Ensure PicoYaml.Gen is referenced."
        );
}
