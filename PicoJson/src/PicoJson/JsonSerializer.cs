namespace PicoJson;

public static partial class JsonSerializer
{
    public static readonly Dictionary<Type, object> _serializers = new();
    public static readonly Dictionary<Type, object> _deserializers = new();

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        if (_serializers.TryGetValue(typeof(T), out var s))
        {
            var writer = new ArrayBufferWriter<byte>();
            ((ISerializer<T>)s).Serialize(writer, value);
            return writer.WrittenSpan.ToArray();
        }
        ThrowNoSerializer<T>();
        return default;
    }

    public static string Serialize<T>(T value)
    {
        var bytes = SerializeToUtf8Bytes(value);
        return Encoding.UTF8.GetString(bytes);
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        if (_serializers.TryGetValue(typeof(T), out var s))
            ((ISerializer<T>)s).Serialize(writer, value);
        else
            ThrowNoSerializer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (_deserializers.TryGetValue(typeof(T), out var d))
            return ((IDeserializer<T>)d).Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSerializer<T>()
    {
        throw new InvalidOperationException(
            $"No serializer/deserializer registered for {typeof(T)}. "
                + "Ensure PicoJson.Gen is referenced and the type is used with JsonSerializer methods."
        );
    }
}
