namespace PicoMsgPack;

/// <summary>Format marker isolating SerRegistry/DesRegistry entries for MessagePack.</summary>
public readonly struct MsgPackFormat { }

public static partial class MsgPackSerializer
{
    /// <summary>HTTP Content-Type header value for MsgPack.</summary>
    public const string ContentType = "application/msgpack";

    // Serialization/deserialization registries live in PicoSerDe.Core
    // (SerRegistry/DesRegistry), isolated per format via MsgPackFormat.

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
        SerRegistry<MsgPackFormat, T>.Handler = handler;
    }

    /// <summary>
    /// Register serializer + deserializer delegates (SG primary path).
    /// </summary>
    public static void Register<T>(SerDelegate<T> serializer, DeserializeDelegate<T> deserializer)
        where T : allows ref struct
    {
        SerRegistry<MsgPackFormat, T>.Handler = serializer;
        DesRegistry<MsgPackFormat, T>.Deserializer = deserializer;
    }

    /// <summary>
    /// Register serializer + deserializer (compat path).
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        SerRegistry<MsgPackFormat, T>.Handler = (writer, value, _) =>
            serializer.Serialize(writer, value);
        DesRegistry<MsgPackFormat, T>.Deserializer = (data, _) => deserializer.Deserialize(data);
    }

    /// <summary>
    /// Register a user serializer pair that ALSO overrides SG-generated
    /// serialization wherever T appears as a nested value (object property,
    /// list element). Deserialization override applies at the top level only.
    /// </summary>
    public static void RegisterCustom<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Register(serializer, deserializer);
        SerRegistry<MsgPackFormat, T>.CustomHandler = (writer, value, _) =>
            serializer.Serialize(writer, value);
    }

    /// <summary>True when a custom serializer overriding nested occurrences of T is registered.</summary>
    public static bool HasCustomSerializer<T>()
        where T : allows ref struct => SerRegistry<MsgPackFormat, T>.CustomHandler is not null;

    /// <summary>Invokes the custom serializer registered via <see cref="RegisterCustom{T}"/>. Called by SG-generated nested emit paths.</summary>
    public static void SerializeCustom<T>(
        IBufferWriter<byte> writer,
        T value,
        MsgPackOptions? options = null
    )
        where T : allows ref struct
    {
        if (SerRegistry<MsgPackFormat, T>.CustomHandler is { } h)
            h(writer, value, options);
        else
            SerializerExtensions.ThrowNoSerializer<T>("RegisterCustom");
    }

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer)
    {
        DesRegistry<MsgPackFormat, T>.Deserializer = (data, _) => deserializer.Deserialize(data);
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value, MsgPackOptions? options = null)
        where T : allows ref struct
    {
        if (SerRegistry<MsgPackFormat, T>.Handler is { } h)
        {
            var writer = SerializerExtensions.RentWriter();
            h(writer, value, options);
            return writer.WrittenSpan.ToArray();
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoMsgPack.Gen");
        return default!;
    }

    public static void Serialize<T>(
        IBufferWriter<byte> writer,
        T value,
        MsgPackOptions? options = null
    )
        where T : allows ref struct
    {
        if (SerRegistry<MsgPackFormat, T>.Handler is { } h)
            h(writer, value, options);
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoMsgPack.Gen");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (DesRegistry<MsgPackFormat, T>.Deserializer is { } d)
            return d(data, null);
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
        // StreamPipeReader may reuse its internal buffer, so previously
        // returned data is not guaranteed to persist across ReadAsync calls.
        // Copy every chunk into our own accumulator.
        var accum = new ArrayBufferWriter<byte>(4096);

        while (true)
        {
            var r = await pipe.ReadAsync(ct);
            foreach (var seg in r.Buffer)
                accum.Write(seg.Span);
            pipe.AdvanceTo(r.Buffer.End);

            // Re-parse from the beginning on every attempt: streaming
            // deserializers do not preserve partially parsed state across
            // NeedMoreData returns, so restarting is the only correct
            // strategy. The reader is in sequence mode so partial tokens at
            // the tail signal NeedMoreData instead of throwing.
            var reader = new MsgPackReader(
                new ReadOnlySequence<byte>(accum.WrittenMemory),
                isFinalBlock: r.IsCompleted
            );

            ReadStatus status;
            try
            {
                status = func(ref reader, out var result);
                if (status == ReadStatus.Success)
                    return result!;
            }
            finally
            {
                reader.Dispose();
            }

            if (status == ReadStatus.NeedMoreData)
            {
                if (r.IsCompleted)
                    throw new FormatException("Unexpected end of stream while parsing.");
                continue;
            }

            throw new FormatException("Unexpected parser state.");
        }
    }
}
