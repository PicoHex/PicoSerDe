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

    private static class StreamingCache<T>
        where T : notnull
    {
        internal static StreamingFunc<YamlReader, T>? Func;
    }

    public static void RegisterStreaming<T>(StreamingFunc<YamlReader, T> func)
        where T : notnull => StreamingCache<T>.Func = func;

    public static bool HasStreamingDelegate<T>()
        where T : notnull => StreamingCache<T>.Func is not null;

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

    public static byte[] SerializeToUtf8Bytes<T>(T value, YamlOptions? options = null)
        where T : allows ref struct =>
        SerializerFacade<YamlFormat>.SerializeToUtf8Bytes(value, options);

    public static string Serialize<T>(T value, YamlOptions? options = null)
        where T : allows ref struct => SerializerFacade<YamlFormat>.Serialize(value, options);

    public static void Serialize<T>(
        IBufferWriter<byte> writer,
        T value,
        YamlOptions? options = null
    )
        where T : allows ref struct =>
        SerializerFacade<YamlFormat>.Serialize(writer, value, options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> data, YamlOptions? options = null) =>
        SerializerFacade<YamlFormat>.Deserialize<T>(data, options);

    public static ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        CancellationToken ct = default
    )
        where T : notnull => DeserializeFromStreamAsync<T>(stream, null, ct);

    public static async ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        YamlOptions? options,
        CancellationToken ct = default
    )
        where T : notnull
    {
        var func = StreamingCache<T>.Func;
        if (func is not null)
            return await StreamingRunner.RunAsync<YamlReader, YamlReaderState, T>(
                stream,
                func,
                options,
                Step<T>,
                ct
            );

        // No streaming delegate (struct/propertyless/constructor types): buffer
        // the payload and use the synchronous path, which is always correct.
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return Deserialize<T>(ms.GetBuffer().AsSpan(0, (int)ms.Length))!;
    }

    private static ReadStatus Step<T>(
        ReadOnlySequence<byte> buffer,
        bool isFinalBlock,
        YamlReaderState state,
        SerOptions? options,
        StreamingFunc<YamlReader, T> func,
        T? partial,
        out T? result,
        out YamlReaderState next,
        out SequencePosition advanceTo
    )
        where T : notnull
    {
        var reader = new YamlReader(buffer, isFinalBlock, state);
        var status = func(ref reader, partial, out result);
        next = reader.ExportState();
        advanceTo = next.Position;
        reader.Dispose();
        return status;
    }
}
