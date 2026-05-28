namespace PicoYaml;

public static partial class YamlSerializer
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
            var w = new ArrayBufferWriter<byte>();
            ((ISerializer<T>)s).Serialize(w, value);
            return w.WrittenSpan.ToArray();
        }
        ThrowNoSer<T>();
        return default;
    }

    public static string Serialize<T>(T value) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(value));

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        if (_serializers.TryGetValue(typeof(T), out var s))
            ((ISerializer<T>)s).Serialize(writer, value);
        else
            ThrowNoSer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (_deserializers.TryGetValue(typeof(T), out var d))
            return ((IDeserializer<T>)d).Deserialize(data);
        ThrowNoSer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSer<T>() =>
        throw new InvalidOperationException(
            $"No serializer registered for {typeof(T)}. Ensure PicoYaml.Gen is referenced."
        );
}
