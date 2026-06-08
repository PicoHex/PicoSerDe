namespace PicoJetson;

public static partial class JsonSerializer
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

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonOptions? options = null)
    {
        if (Cache<T>.Serializer is { } s)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var writer = SerializerExtensions.RentWriter();
                s.Serialize(writer, value);
                return writer.WrittenSpan.ToArray();
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return default!;
    }

    public static string Serialize<T>(T value, JsonOptions? options = null)
    {
        if (Cache<T>.Serializer is { } s)
            return s.SerializeToString(value);
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return "";
    }

    public static void Serialize<T>(
        IBufferWriter<byte> writer,
        T value,
        JsonOptions? options = null
    )
    {
        if (Cache<T>.Serializer is { } s)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                s.Serialize(writer, value);
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data, JsonOptions? options = null)
    {
        if (Cache<T>.Deserializer is { } d)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                return d.Deserialize(data);
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return default;
    }
}
