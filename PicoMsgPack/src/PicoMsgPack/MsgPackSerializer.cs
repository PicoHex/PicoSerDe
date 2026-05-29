namespace PicoMsgPack;

public static partial class MsgPackSerializer
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
        var s = Cache<T>.Serializer;
        if (s is not null)
        {
            var writer = new ArrayBufferWriter<byte>();
            s.Serialize(writer, value);
            return writer.WrittenSpan.ToArray();
        }
        ThrowNoSerializer<T>();
        return default;
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null) s.Serialize(writer, value);
        else ThrowNoSerializer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null) return d.Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowNoSerializer<T>() =>
        throw new InvalidOperationException(
            $"No serializer registered for {typeof(T)}. Ensure PicoMsgPack.Gen is referenced.");
}
