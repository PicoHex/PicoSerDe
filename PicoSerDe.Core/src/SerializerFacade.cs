namespace PicoSerDe.Core;

/// <summary>
/// Cross-format serializer facade core. Each format's public static class
/// (PicoIni.IniSerializer, PicoJetson.JsonSerializer, ...) forwards to this
/// generic implementation, isolated per format via the <typeparamref name="TFormat"/>
/// marker type. Options are explicit per call — no ambient state.
/// </summary>
public static class SerializerFacade<TFormat>
    where TFormat : struct
{
    /// <summary>Register a delegate-based serializer (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> handler)
        where T : allows ref struct
    {
        SerRegistry<TFormat, T>.Handler = handler;
    }

    /// <summary>Register serializer + deserializer delegates (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> serializer, DeserializeDelegate<T> deserializer)
        where T : allows ref struct
    {
        SerRegistry<TFormat, T>.Handler = serializer;
        DesRegistry<TFormat, T>.Deserializer = deserializer;
    }

    /// <summary>
    /// Register serializer + deserializer (compat path for hand-written
    /// ISerializer/IDeserializer). Options are not forwarded to hand-written
    /// implementations.
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        SerRegistry<TFormat, T>.Handler = (writer, value, _) =>
            serializer.Serialize(writer, value);
        DesRegistry<TFormat, T>.Deserializer = (data, _) => deserializer.Deserialize(data);
    }

    /// <summary>Register a deserializer delegate only.</summary>
    public static void RegisterDeserializer<T>(DeserializeDelegate<T> deserializer)
        where T : allows ref struct
    {
        DesRegistry<TFormat, T>.Deserializer = deserializer;
    }

    /// <summary>Register a deserializer (hand-written compat path).</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer)
    {
        DesRegistry<TFormat, T>.Deserializer = (data, _) => deserializer.Deserialize(data);
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value, SerOptions? options = null)
        where T : allows ref struct
    {
        if (SerRegistry<TFormat, T>.Handler is { } h)
        {
            var writer = SerializerExtensions.RentWriter();
            h(writer, value, options);
            return writer.WrittenSpan.ToArray();
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoSerDe format package");
        return default!;
    }

    public static string Serialize<T>(T value, SerOptions? options = null)
        where T : allows ref struct
    {
        if (SerRegistry<TFormat, T>.Handler is { } h)
        {
            var writer = SerializerExtensions.RentWriter();
            h(writer, value, options);
            return Encoding.UTF8.GetString(writer.WrittenSpan);
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoSerDe format package");
        return "";
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value, SerOptions? options = null)
        where T : allows ref struct
    {
        if (SerRegistry<TFormat, T>.Handler is { } h)
            h(writer, value, options);
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoSerDe format package");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data, SerOptions? options = null)
    {
        if (DesRegistry<TFormat, T>.Deserializer is { } d)
            return d(data, options);
        SerializerExtensions.ThrowNoSerializer<T>("PicoSerDe format package");
        return default;
    }
}
