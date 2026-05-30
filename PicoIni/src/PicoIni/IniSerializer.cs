namespace PicoIni;

/// <summary>High-level INI serialization facade. Uses source-generated serializers
/// registered via <see cref="Register{T}"/> (auto-registered by PicoIni.Gen).</summary>
public static partial class IniSerializer
{
    // Generic static cache — avoids dictionary lookup + cast on hot path
    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    /// <summary>Registers a manual serializer/deserializer pair for a type.</summary>
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
            var writer = SerializerExtensions.RentWriter();
            s.Serialize(writer, value);
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
        var s = Cache<T>.Serializer;
        if (s is not null)
            s.Serialize(writer, value);
        else
            ThrowNoSerializer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null)
            return d.Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSerializer<T>() =>
        throw new InvalidOperationException(
            $"No serializer/deserializer registered for {typeof(T)}. "
                + "Ensure PicoIni.Gen is referenced and the type is used with IniSerializer methods."
        );
}
