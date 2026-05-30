namespace PicoToml;

public static partial class TomlSerializer
{
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
        if (Cache<T>.Serializer is { } s)
        {
            var writer = SerializerExtensions.RentWriter();
            s.Serialize(writer, value);
            return writer.WrittenSpan.ToArray();
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoToml.Gen");
        return default!;
    }

    public static string Serialize<T>(T value) => Encoding.UTF8.GetString(SerializeToUtf8Bytes(value));

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        if (Cache<T>.Serializer is { } s) s.Serialize(writer, value);
        else SerializerExtensions.ThrowNoSerializer<T>("PicoToml.Gen");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (Cache<T>.Deserializer is { } d) return d.Deserialize(data);
        SerializerExtensions.ThrowNoSerializer<T>("PicoToml.Gen");
        return default;
    }
}
