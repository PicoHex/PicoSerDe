namespace PicoJetson;

public static partial class JsonSerializer
{
    /// <summary>HTTP Content-Type header value for JSON.</summary>
    public const string ContentType = "application/json";

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

    /// <summary>Register a delegate-based serializer (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> handler)
        where T : allows ref struct
    {
        SerCache<T>.Handler = handler;
    }

    /// <summary>
    /// Register serializer + deserializer (compat path for hand-written ISerializer/IDeserializer).
    /// The ISerializer&lt;T&gt; is wrapped into a SerDelegate&lt;T&gt; internally.
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

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonOptions? options = null)
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var writer = SerializerExtensions.RentWriter();
                h(writer, value);
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
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var writer = SerializerExtensions.RentWriter();
                h(writer, value);
                return Encoding.UTF8.GetString(writer.WrittenSpan);
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
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                h(writer, value);
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

    /// <summary>
    /// Serializes each value in <paramref name="values"/> as a JSON line,
    /// separated by <c>'\n'</c>. Returns a JSONL byte array.
    /// </summary>
    public static byte[] SerializeLines<T>(
        IEnumerable<T> values,
        JsonOptions? options = null
    )
        where T : allows ref struct
    {
        var prev = JsonOptions.Current;
        JsonOptions.Current = options;
        try
        {
            var buf = new ArrayBufferWriter<byte>(1024);
            foreach (var v in values)
            {
                if (SerCache<T>.Handler is { } h)
                {
                    h(buf, v);
                    buf.Write("\n"u8);
                }
                else
                {
                    SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
                }
            }
            return buf.WrittenSpan.ToArray();
        }
        finally
        {
            JsonOptions.Current = prev;
        }
    }

    /// <summary>
    /// Deserializes each line of a JSONL byte span into an array of <typeparamref name="T"/>.
    /// Empty lines are skipped. Each line must be a complete, valid JSON value.
    /// </summary>
    public static T?[] DeserializeLines<T>(
        ReadOnlySpan<byte> data,
        JsonOptions? options = null
    )
    {
        if (data.IsEmpty)
            return [];

        var prev = JsonOptions.Current;
        JsonOptions.Current = options;
        try
        {
            var results = new List<T?>();
            var remaining = data;

            while (remaining.Length > 0)
            {
                int newlineIdx = remaining.IndexOf((byte)'\n');
                ReadOnlySpan<byte> line;
                if (newlineIdx >= 0)
                {
                    line = remaining[..newlineIdx];
                    remaining = remaining[(newlineIdx + 1)..];
                }
                else
                {
                    line = remaining;
                    remaining = default;
                }

                if (line.IsEmpty)
                    continue;

                if (Cache<T>.Deserializer is { } d)
                {
                    results.Add(d.Deserialize(line));
                }
                else
                {
                    SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
                }
            }

            return results.ToArray();
        }
        finally
        {
            JsonOptions.Current = prev;
        }
    }

    /// <summary>
    /// Wraps a UTF-8 stream into an <see cref="IAsyncEnumerable{T}"/> that yields one
    /// <typeparamref name="T"/> per top-level JSON value (JSONL mode, <c>topLevelValues = true</c>)
    /// or per root-level array element (JSON array mode, <c>topLevelValues = false</c>).
    /// Requires a deserializer to be registered for <typeparamref name="T"/>
    /// (auto-registered by the SG via <c>ModuleInitializer</c>).
    /// </summary>
    public static IAsyncEnumerable<T?> DeserializeAsyncEnumerable<T>(
        Stream stream,
        bool topLevelValues = true,
        JsonOptions? options = null,
        CancellationToken ct = default
    )
    {
        return DeserializeAsyncEnumerableImpl<T>(stream, topLevelValues, options, ct);
    }

    private static async IAsyncEnumerable<T?> DeserializeAsyncEnumerableImpl<T>(
        Stream stream,
        bool topLevelValues,
        JsonOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        // Verify deserializer is registered before entering the loop
        if (Cache<T>.Deserializer is not { } deserializer)
        {
            SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
            yield break;
        }

        var prev = JsonOptions.Current;
        JsonOptions.Current = options;
        try
        {
            if (topLevelValues)
            {
                var readBuf = new byte[4096];
                var accum = new List<byte>(4096);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int bytesRead = await stream.ReadAsync(readBuf, ct);
                    if (bytesRead == 0)
                    {
                        if (accum.Count > 0)
                        {
                            // Copy remaining to array before yielding (Span can't cross yield boundary)
                            var lastLine = accum.ToArray();
                            yield return deserializer.Deserialize(lastLine);
                        }
                        yield break;
                    }

                    accum.AddRange(readBuf.AsSpan(0, bytesRead));

                    int consumed = 0;
                    while (consumed < accum.Count)
                    {
                        int nlPos = accum.IndexOf((byte)'\n', consumed);
                        if (nlPos < 0)
                            break;

                        int lineStart = consumed;
                        int lineEnd = nlPos;
                        consumed = lineEnd + 1;

                        if (lineEnd > lineStart)
                        {
                            // Copy line bytes; List<T>.GetRange avoids Span crossing yield
                            var lineBytes = accum.GetRange(lineStart, lineEnd - lineStart).ToArray();
                            yield return deserializer.Deserialize(lineBytes);
                        }
                    }

                    if (consumed > 0)
                    {
                        if (consumed < accum.Count)
                        {
                            accum.RemoveRange(0, consumed);
                        }
                        else
                        {
                            accum.Clear();
                        }
                    }
                }
            }
            else
            {
                // ── Array mode: bracket-counting value extraction ──
                var readBuf = new byte[4096];
                var accum = new List<byte>(4096);
                bool sawOpeningBracket = false;
                int depth = 0;
                int valueStart = -1;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int bytesRead = await stream.ReadAsync(readBuf, ct);
                    if (bytesRead == 0)
                    {
                        if (!sawOpeningBracket)
                            throw new FormatException("Expected '[' at start of JSON array stream.");
                        if (depth > 0)
                            throw new FormatException("Unexpected end of stream inside JSON array.");
                        yield break;
                    }

                    accum.AddRange(readBuf.AsSpan(0, bytesRead));

                    int i = 0;
                    while (i < accum.Count)
                    {
                        byte b = accum[i];

                        if (!sawOpeningBracket)
                        {
                            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
                            { i++; continue; }
                            if (b == (byte)'[')
                            { sawOpeningBracket = true; depth = 1; i++; continue; }
                            throw new FormatException("Expected '[' at start of JSON array stream.");
                        }

                        if (b == (byte)'"')
                        {
                            i++;
                            while (i < accum.Count)
                            {
                                if (accum[i] == (byte)'\\')
                                { i += 2; continue; }
                                if (accum[i] == (byte)'"')
                                { i++; break; }
                                i++;
                            }
                            continue;
                        }

                        if (b is (byte)'{' or (byte)'[')
                        {
                            if (depth == 1)
                                valueStart = i;
                            depth++;
                            i++;
                            continue;
                        }

                        if (b is (byte)'}' or (byte)']')
                        {
                            depth--;
                            if (depth == 0 && b == (byte)']')
                            {
                                if (valueStart >= 0)
                                {
                                    var valBytes = accum.GetRange(valueStart, i - valueStart).ToArray();
                                    yield return deserializer.Deserialize(valBytes);
                                }
                                yield break;
                            }
                            if (depth == 1 && b == (byte)'}')
                            {
                                var valBytes = accum.GetRange(valueStart, i + 1 - valueStart).ToArray();
                                yield return deserializer.Deserialize(valBytes);
                                valueStart = -1;
                                int consumed = i + 1;
                                while (consumed < accum.Count && accum[consumed] <= 32)
                                    consumed++;
                                if (consumed < accum.Count && accum[consumed] == (byte)',')
                                    consumed++;
                                accum.RemoveRange(0, consumed);
                                i = 0;
                                continue;
                            }
                            i++;
                            continue;
                        }

                        i++;
                    }

                    if (valueStart < 0 && depth == 1)
                    {
                        int trim = 0;
                        while (trim < accum.Count && accum[trim] <= 32)
                            trim++;
                        if (trim < accum.Count && accum[trim] == (byte)',')
                            trim++;
                        if (trim > 0)
                            accum.RemoveRange(0, trim);
                    }
                }
            }
        }
        finally
        {
            JsonOptions.Current = prev;
        }
    }
}
