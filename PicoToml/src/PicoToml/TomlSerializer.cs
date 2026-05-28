namespace PicoToml;

public static partial class TomlSerializer
{
    private static readonly ConcurrentDictionary<Type, object> _serializers = new();
    private static readonly ConcurrentDictionary<Type, object> _deserializers = new();

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        _serializers[typeof(T)] = serializer;
        _deserializers[typeof(T)] = deserializer;
    }

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
    private static void ThrowNoSerializer<T>() =>
        throw new InvalidOperationException(
            $"No serializer registered for {typeof(T)}. Ensure PicoToml.Gen is referenced."
        );
}
