namespace PicoMsgPack;

public static partial class MsgPackSerializer
{
    /// <summary>HTTP Content-Type header value for MsgPack.</summary>
    public const string ContentType = "application/msgpack";

    // Serialization cache — allows ref struct via delegate
    private static class SerCache<T> where T : allows ref struct
    {
        internal static SerDelegate<T>? Handler;
    }

    // Deserialization cache — unchanged (no ref struct support)
    private static class Cache<T>
    {
        internal static IDeserializer<T>? Deserializer;
    }

    /// <summary>Delegate for streaming deserialization via PipeReader.</summary>
    public delegate ReadStatus StreamingFunc<T>(ref MsgPackReader reader, out T? result);

    private static class StreamingCache<T>
    {
        internal static StreamingFunc<T>? Func;
    }

    public static void RegisterStreaming<T>(StreamingFunc<T> func)
        where T : notnull
    {
        StreamingCache<T>.Func = func;
    }

    public static bool HasStreamingDelegate<T>() => StreamingCache<T>.Func is not null;

    /// <summary>Register a delegate-based serializer (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> handler)
        where T : allows ref struct
    {
        SerCache<T>.Handler = handler;
    }

    /// <summary>
    /// Register serializer + deserializer (compat path).
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        SerCache<T>.Handler = (writer, value) => serializer.Serialize(writer, value);
        Cache<T>.Deserializer = deserializer;
    }

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer)
    {
        Cache<T>.Deserializer = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
        {
            var writer = SerializerExtensions.RentWriter();
            h(writer, value);
            return writer.WrittenSpan.ToArray();
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoMsgPack.Gen");
        return default!;
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
            h(writer, value);
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoMsgPack.Gen");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (Cache<T>.Deserializer is { } d)
            return d.Deserialize(data);
        SerializerExtensions.ThrowNoSerializer<T>("PicoMsgPack.Gen");
        return default;
    }

    /// <summary>
    /// Deserializes asynchronously from a Stream.
    /// When a streaming delegate is registered (via SG), uses PipeReader-based
    /// streaming. Otherwise falls back to loading the entire stream into memory.
    /// </summary>
    public static async ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        CancellationToken ct = default
    )
        where T : notnull
    {
        var func = StreamingCache<T>.Func;
        if (func is not null)
        {
            return await DeserializeStreamingCore(func, stream, ct);
        }

        // Fallback: load all data then deserialize
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return Deserialize<T>(ms.GetBuffer().AsSpan(0, (int)ms.Length))!;
    }

    private static async ValueTask<T> DeserializeStreamingCore<T>(
        StreamingFunc<T> func,
        Stream stream,
        CancellationToken ct
    )
        where T : notnull
    {
        var pipe = PipeReader.Create(stream);
        var state = default(MsgPackReaderState);

        while (true)
        {
            var r = await pipe.ReadAsync(ct);
            var reader = new MsgPackReader(r.Buffer, r.IsCompleted, state);

            var status = func(ref reader, out var result);

            if (status == ReadStatus.Success)
            {
                pipe.AdvanceTo(r.Buffer.End);
                return result!;
            }

            if (status == ReadStatus.NeedMoreData)
            {
                if (r.IsCompleted)
                    throw new FormatException("Unexpected end of stream while parsing.");
                state = reader.ExportState();
                pipe.AdvanceTo(state.Position, r.Buffer.End);
                continue;
            }

            throw new FormatException("Unexpected parser state.");
        }
    }
}
