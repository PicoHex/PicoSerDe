namespace PicoYaml;

public static partial class YamlSerializer
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
        SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
        return default!;
    }

    public static string Serialize<T>(T value)
    {
        if (Cache<T>.Serializer is { } s)
        {
            var writer = SerializerExtensions.RentWriter();
            s.Serialize(writer, value);
            return Encoding.UTF8.GetString(writer.WrittenSpan);
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
        return "";
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        if (Cache<T>.Serializer is { } s)
            s.Serialize(writer, value);
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (Cache<T>.Deserializer is { } d)
            return d.Deserialize(data);
        SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
        return default;
    }
}
