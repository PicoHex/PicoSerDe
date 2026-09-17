namespace PicoJetson;

/// <summary>Format marker isolating SerRegistry/DesRegistry entries for JSON.</summary>
public readonly struct JsonFormat { }

public static partial class JsonSerializer
{
    /// <summary>HTTP Content-Type header value for JSON.</summary>
    public const string ContentType = "application/json";

    // Serialization/deserialization registries live in PicoSerDe.Core
    // (SerRegistry/DesRegistry), isolated per format via JsonFormat.

    private static class StreamingCache<T>
        where T : notnull
    {
        internal static StreamingFunc<JsonReader, T>? Func;
    }

    public static void RegisterStreaming<T>(StreamingFunc<JsonReader, T> func)
        where T : notnull
    {
        StreamingCache<T>.Func = func;
    }

    /// <summary>True when a streaming deserializer has been registered for T.</summary>
    public static bool HasStreamingDelegate<T>()
        where T : notnull => StreamingCache<T>.Func is not null;

    /// <summary>Register a delegate-based serializer (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> handler)
        where T : allows ref struct => SerializerFacade<JsonFormat>.Register(handler);

    /// <summary>
    /// Register serializer + deserializer delegates (SG primary path).
    /// </summary>
    public static void Register<T>(SerDelegate<T> serializer, DeserializeDelegate<T> deserializer)
        where T : allows ref struct =>
        SerializerFacade<JsonFormat>.Register(serializer, deserializer);

    /// <summary>
    /// Register serializer + deserializer (compat path for hand-written ISerializer/IDeserializer).
    /// Options are not forwarded to hand-written implementations.
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer) =>
        SerializerFacade<JsonFormat>.Register(serializer, deserializer);

    /// <summary>
    /// Register a user serializer pair that ALSO overrides SG-generated
    /// serialization wherever T appears as a nested value (object property,
    /// list element, dictionary value). Deserialization override applies at
    /// the top level only — nested reads still use the SG deserializer.
    /// </summary>
    public static void RegisterCustom<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Register(serializer, deserializer);
        SerRegistry<JsonFormat, T>.CustomHandler = (writer, value, _) =>
            serializer.Serialize(writer, value);
    }

    /// <summary>True when a custom serializer overriding nested occurrences of T is registered.</summary>
    public static bool HasCustomSerializer<T>()
        where T : allows ref struct => SerRegistry<JsonFormat, T>.CustomHandler is not null;

    /// <summary>Invokes the custom serializer registered via <see cref="RegisterCustom{T}"/>. Called by SG-generated nested emit paths.</summary>
    public static void SerializeCustom<T>(
        IBufferWriter<byte> writer,
        T value,
        JsonOptions? options = null
    )
        where T : allows ref struct
    {
        if (SerRegistry<JsonFormat, T>.CustomHandler is { } h)
            h(writer, value, (SerOptions?)options);
        else
            SerializerExtensions.ThrowNoSerializer<T>("RegisterCustom");
    }

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer) =>
        SerializerFacade<JsonFormat>.RegisterDeserializer(deserializer);

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonOptions? options = null)
        where T : allows ref struct =>
        SerializerFacade<JsonFormat>.SerializeToUtf8Bytes(value, options);

    public static string Serialize<T>(T value, JsonOptions? options = null)
        where T : allows ref struct => SerializerFacade<JsonFormat>.Serialize(value, options);

    public static void Serialize<T>(
        IBufferWriter<byte> writer,
        T value,
        JsonOptions? options = null
    )
        where T : allows ref struct =>
        SerializerFacade<JsonFormat>.Serialize(writer, value, options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> data, JsonOptions? options = null) =>
        SerializerFacade<JsonFormat>.Deserialize<T>(data, options);

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
        return await StreamingRunner.RunAsync<JsonReader, JsonReaderState, T>(
            stream,
            func,
            options,
            Step<T>,
            ct
        );
    }

    private static ReadStatus Step<T>(
        ReadOnlySequence<byte> buffer,
        bool isFinalBlock,
        JsonReaderState state,
        SerOptions? options,
        StreamingFunc<JsonReader, T> func,
        T? partial,
        out T? result,
        out JsonReaderState next,
        out SequencePosition advanceTo
    )
        where T : notnull
    {
        var reader = new JsonReader(buffer, isFinalBlock, state, (JsonOptions?)options);
        // partial is the previous chunk's partially built result (null on the first call).
        var status = func(ref reader, partial, out result);
        next = reader.ExportState();
        advanceTo = next.Position;
        // Return rented buffers (the result was already materialized by func).
        reader.Dispose();
        return status;
    }

    /// <summary>
    /// Serializes each value in <paramref name="values"/> as a JSON line,
    /// separated by <c>'\n'</c>. Returns a JSONL byte array.
    /// </summary>
    public static byte[] SerializeLines<T>(IEnumerable<T> values, JsonOptions? options = null)
        where T : allows ref struct
    {
        var buf = new ArrayBufferWriter<byte>(1024);
        foreach (var v in values)
        {
            if (SerRegistry<JsonFormat, T>.Handler is { } h)
            {
                h(buf, v, options);
                buf.Write("\n"u8);
            }
            else
            {
                SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
            }
        }
        return [.. buf.WrittenSpan];
    }

    /// <summary>
    /// Deserializes each line of a JSONL byte span into an array of <typeparamref name="T"/>.
    /// Empty lines are skipped. Each line must be a complete, valid JSON value.
    /// </summary>
    public static T?[] DeserializeLines<T>(ReadOnlySpan<byte> data, JsonOptions? options = null)
    {
        if (data.IsEmpty)
            return [];

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

            if (DesRegistry<JsonFormat, T>.Deserializer is { } d)
            {
                results.Add(d(line, options));
            }
            else
            {
                SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
            }
        }

        return results.ToArray();
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
        if (DesRegistry<JsonFormat, T>.Deserializer is not { } deserializer)
        {
            SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
            yield break;
        }

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
                        yield return deserializer(lastLine, options);
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
                    consumed = nlPos + 1;

                    if (nlPos <= lineStart)
                        continue;
                    // Copy line bytes; List<T>.GetRange avoids Span crossing yield
                    var lineBytes = accum.GetRange(lineStart, nlPos - lineStart).ToArray();
                    yield return deserializer(lineBytes, options);
                }

                if (consumed <= 0)
                    continue;
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
        else
        {
            await foreach (var item in ArrayModeImpl<T>(stream, options, ct))
                yield return item;
        }
    }

    /// <summary>
    /// Array-mode streaming (topLevelValues: false): parses a top-level JSON
    /// array incrementally and yields each element as soon as it is complete.
    /// Only the unconsumed bytes are retained (the buffer is compacted on every
    /// read), so memory stays bounded by the largest in-flight element.
    /// </summary>
    private static async IAsyncEnumerable<T?> ArrayModeImpl<T>(
        Stream stream,
        JsonOptions? options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        if (DesRegistry<JsonFormat, T>.Deserializer is not { } deserializer)
        {
            SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
            yield break;
        }

        var buffer = new byte[8192];
        int head = 0;
        int count = 0;
        bool done = false;
        bool sawOpen = false;
        bool expectValue = true; // true right after '[' or ','
        bool anyElement = false;

        while (true)
        {
            if (!sawOpen)
            {
                while (head < count && buffer[head] <= 32)
                    head++;
                if (head >= count)
                {
                    if (done)
                        throw new FormatException(
                            "Expected '[' at the start of a JSON array stream."
                        );
                    (head, count, done) = await FillAsync(stream, buffer, head, count, done, ct);
                    continue;
                }
                if (buffer[head] != (byte)'[')
                    throw new FormatException("Expected '[' at the start of a JSON array stream.");
                head++;
                sawOpen = true;
            }

            var items = new List<T?>();
            bool completed = false;
            int newHead = ParseArrayElements(
                buffer,
                head,
                count,
                done,
                options,
                deserializer,
                ref expectValue,
                ref anyElement,
                items,
                out completed
            );
            bool progressed = newHead != head;
            head = newHead;
            foreach (var item in items)
                yield return item;
            if (completed)
                yield break;
            if (done)
                throw new FormatException("Unexpected end of stream inside JSON array.");
            if (!progressed && items.Count == 0)
                (head, count, done) = await FillAsync(stream, buffer, head, count, done, ct);
        }
    }

    /// <summary>
    /// Compacts the unconsumed prefix, grows the buffer when full and reads the
    /// next block. Returns the updated (head, count, done) triple.
    /// </summary>
    private static async ValueTask<(int Head, int Count, bool Done)> FillAsync(
        Stream stream,
        byte[] buffer,
        int head,
        int count,
        bool done,
        CancellationToken ct
    )
    {
        if (head > 0)
        {
            int remaining = count - head;
            if (remaining > 0)
                Buffer.BlockCopy(buffer, head, buffer, 0, remaining);
            head = 0;
            count = remaining;
        }
        if (count == buffer.Length)
        {
            var bigger = new byte[buffer.Length * 2];
            Buffer.BlockCopy(buffer, 0, bigger, 0, count);
            buffer = bigger;
        }
        int read = await stream.ReadAsync(buffer.AsMemory(count), ct);
        if (read == 0)
            done = true;
        else
            count += read;
        return (head, count, done);
    }

    /// <summary>
    /// Parses as many complete array elements as the buffer allows. Element
    /// boundaries (comma / closing bracket) are enforced here; each element
    /// value is parsed by JsonReader and passed to the registered deserializer
    /// as raw bytes.
    /// </summary>
    private static int ParseArrayElements<T>(
        byte[] buffer,
        int head,
        int count,
        bool isFinal,
        JsonOptions? options,
        DeserializeDelegate<T> deserializer,
        ref bool expectValue,
        ref bool anyElement,
        List<T?> items,
        out bool completed
    )
    {
        completed = false;
        int pos = head;
        int maxDepth = options?.MaxDepth ?? 256;

        while (true)
        {
            while (pos < count && buffer[pos] <= 32)
                pos++;
            if (pos >= count)
                return pos;

            byte b = buffer[pos];
            if (b == (byte)']')
            {
                if (expectValue && anyElement && options?.AllowTrailingCommas != true)
                    throw new FormatException("Trailing comma before closing bracket");
                completed = true;
                return pos + 1;
            }
            if (b == (byte)',')
            {
                if (expectValue)
                    throw new FormatException($"Unexpected comma at offset {pos}");
                expectValue = true;
                pos++;
                continue;
            }
            if (!expectValue)
                throw new FormatException($"Missing comma between array elements at offset {pos}");

            var reader = new JsonReader(
                buffer.AsSpan(pos, count - pos),
                maxDepth,
                isFinal,
                options
            );
            try
            {
                bool readOk;
                try
                {
                    readOk = reader.Read();
                }
                catch (Exception ex) when (IsIncomplete(ex, isFinal))
                {
                    return pos;
                }
                if (!readOk)
                {
                    if (!isFinal && reader.NeedsMoreData)
                        return pos;
                    throw new FormatException(
                        $"Unexpected end of JSON array element at offset {pos}"
                    );
                }

                bool skipOk;
                try
                {
                    skipOk = reader.TrySkip();
                }
                catch (Exception ex) when (IsIncomplete(ex, isFinal))
                {
                    return pos;
                }
                if (!skipOk)
                {
                    if (!isFinal)
                        return pos;
                    throw new FormatException($"Malformed JSON array element at offset {pos}");
                }

                int len = (int)reader.BytesConsumed;
                var element = buffer.AsSpan(pos, len).ToArray();
                items.Add(deserializer(element, options));
                pos += len;
                anyElement = true;
                expectValue = false;
            }
            finally
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>True when the exception signals a truncated element that more
    /// data may complete (only meaningful while the stream is not finished).</summary>
    private static bool IsIncomplete(Exception ex, bool isFinal) =>
        !isFinal
        && ex is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException;
}
