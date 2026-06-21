namespace PicoJetson;

public static partial class JsonSerializer
{
    /// <summary>HTTP Content-Type header value for JSON.</summary>
    public const string ContentType = "application/json";

    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    /// <summary>Delegate for streaming deserialization via PipeReader.</summary>
    public delegate ReadStatus StreamingFunc<T>(ref JsonReader reader, out T? result);

    private static class StreamingCache<T>
    {
        internal static StreamingFunc<T>? Func;
    }

    public static void RegisterStreaming<T>(StreamingFunc<T> func)
        where T : notnull
    {
        StreamingCache<T>.Func = func;
    }

    /// <summary>True when a streaming deserializer has been registered for T.</summary>
    public static bool HasStreamingDelegate<T>() => StreamingCache<T>.Func is not null;

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonOptions? options = null)
    {
        if (Cache<T>.Serializer is { } s)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var writer = SerializerExtensions.RentWriter();
                s.Serialize(writer, value);
                return writer.WrittenSpan.ToArray();
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return default!;
    }

    public static string Serialize<T>(T value, JsonOptions? options = null)
    {
        if (Cache<T>.Serializer is { } s)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                return s.SerializeToString(value);
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return "";
    }

    public static void Serialize<T>(
        IBufferWriter<byte> writer,
        T value,
        JsonOptions? options = null
    )
    {
        if (Cache<T>.Serializer is { } s)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                s.Serialize(writer, value);
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data, JsonOptions? options = null)
    {
        if (Cache<T>.Deserializer is { } d)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                return d.Deserialize(data);
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return default;
    }

    /// <summary>
    /// Deserializes asynchronously from a Stream using PipeReader-based streaming.
    /// Requires the source generator to have registered a streaming deserializer
    /// for the target type (auto-registered via ModuleInitializer when the type
    /// is used with JsonSerializer).
    /// </summary>
    public static async ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        JsonOptions? options = null,
        CancellationToken ct = default
    )
        where T : notnull
    {
        var func =
            StreamingCache<T>.Func
            ?? throw new InvalidOperationException(
                $"No streaming deserializer registered for {typeof(T)}."
            );

        var prev = JsonOptions.Current;
        JsonOptions.Current = options;
        try
        {
            var pipe = PipeReader.Create(stream);
            var state = default(JsonReaderState);

            while (true)
            {
                var r = await pipe.ReadAsync(ct);
                var reader = new JsonReader(r.Buffer, r.IsCompleted, state);

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
        finally
        {
            JsonOptions.Current = prev;
        }
    }
}
