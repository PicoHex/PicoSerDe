using System.Text;

namespace PicoSerDe.Abs;

public static class DeserializerExtensions
{
    public static T Deserialize<T>(this IDeserializer<T> deserializer, byte[] data)
    {
        return deserializer.Deserialize(data.AsSpan());
    }

    public static T Deserialize<T>(this IDeserializer<T> deserializer, string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        return deserializer.Deserialize(bytes.AsSpan());
    }

    public static T DeserializeFromStream<T>(this IDeserializer<T> deserializer, Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return deserializer.Deserialize(ms.ToArray().AsSpan());
    }
}
