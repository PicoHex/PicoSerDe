namespace PicoJson;

public static class JsonSerializer
{
    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        throw new NotImplementedException(
            "Source Generator not yet built. Use ISerializer<T> implementations directly."
        );
    }

    public static string Serialize<T>(T value)
    {
        var bytes = SerializeToUtf8Bytes(value);
        return Encoding.UTF8.GetString(bytes);
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        throw new NotImplementedException("Source Generator not yet built.");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        throw new NotImplementedException("Source Generator not yet built.");
    }
}
