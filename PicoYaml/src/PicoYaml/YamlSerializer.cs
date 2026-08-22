namespace PicoYaml;

/// <summary>Format marker isolating SerRegistry/DesRegistry entries for PicoYaml.</summary>
public readonly struct YamlFormat { }

public static partial class YamlSerializer
{
    /// <summary>HTTP Content-Type header value for PicoYaml.</summary>
    public const string ContentType = "text/yaml";

    // Serialization/deserialization registries live in PicoSerDe.Core
    // (SerRegistry/DesRegistry), isolated per format via YamlFormat.
    // All shared methods forward to SerializerFacade<YamlFormat>.

    /// <summary>Delegate for streaming deserialization via PipeReader.</summary>
    public delegate ReadStatus StreamingFunc<T>(ref YamlReader reader, out T? result);

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
        where T : allows ref struct => SerializerFacade<YamlFormat>.Register(handler);

    /// <summary>
    /// Register serializer + deserializer (compat path for hand-written ISerializer/IDeserializer).
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer) =>
        SerializerFacade<YamlFormat>.Register(serializer, deserializer);

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer) =>
        SerializerFacade<YamlFormat>.RegisterDeserializer(deserializer);

    public static byte[] SerializeToUtf8Bytes<T>(T value)
        where T : allows ref struct => SerializerFacade<YamlFormat>.SerializeToUtf8Bytes(value);

    public static string Serialize<T>(T value)
        where T : allows ref struct => SerializerFacade<YamlFormat>.Serialize(value);

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
        where T : allows ref struct => SerializerFacade<YamlFormat>.Serialize(writer, value);

    public static T? Deserialize<T>(ReadOnlySpan<byte> data) =>
        SerializerFacade<YamlFormat>.Deserialize<T>(data);

    public static async ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        CancellationToken ct = default
    )
        where T : notnull
    {
        var func = StreamingCache<T>.Func;
        if (func is not null)
            return await DeserializeStreamingCore(func, stream, ct);

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
        var state = default(YamlReaderState);

        while (true)
        {
            var r = await pipe.ReadAsync(ct);
            var reader = new YamlReader(r.Buffer, r.IsCompleted, state);

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
