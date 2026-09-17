namespace PicoToml;

/// <summary>Format marker isolating SerRegistry/DesRegistry entries for PicoToml.</summary>
public readonly struct TomlFormat { }

public static partial class TomlSerializer
{
    /// <summary>HTTP Content-Type header value for PicoToml.</summary>
    public const string ContentType = "application/toml";

    // Serialization/deserialization registries live in PicoSerDe.Core
    // (SerRegistry/DesRegistry), isolated per format via TomlFormat.
    // All shared methods forward to SerializerFacade<TomlFormat>.

    private static class StreamingCache<T>
        where T : notnull
    {
        internal static StreamingFunc<TomlReader, T>? Func;
    }

    public static void RegisterStreaming<T>(StreamingFunc<TomlReader, T> func)
        where T : notnull => StreamingCache<T>.Func = func;

    public static bool HasStreamingDelegate<T>()
        where T : notnull => StreamingCache<T>.Func is not null;

    /// <summary>Register a delegate-based serializer (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> handler)
        where T : allows ref struct => SerializerFacade<TomlFormat>.Register(handler);

    /// <summary>
    /// Register serializer + deserializer (compat path for hand-written ISerializer/IDeserializer).
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer) =>
        SerializerFacade<TomlFormat>.Register(serializer, deserializer);

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer) =>
        SerializerFacade<TomlFormat>.RegisterDeserializer(deserializer);

    public static byte[] SerializeToUtf8Bytes<T>(T value, TomlOptions? options = null)
        where T : allows ref struct =>
        SerializerFacade<TomlFormat>.SerializeToUtf8Bytes(value, options);

    public static string Serialize<T>(T value, TomlOptions? options = null)
        where T : allows ref struct => SerializerFacade<TomlFormat>.Serialize(value, options);

    public static void Serialize<T>(
        IBufferWriter<byte> writer,
        T value,
        TomlOptions? options = null
    )
        where T : allows ref struct =>
        SerializerFacade<TomlFormat>.Serialize(writer, value, options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> data, TomlOptions? options = null) =>
        SerializerFacade<TomlFormat>.Deserialize<T>(data, options);

    public static ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        CancellationToken ct = default
    )
        where T : notnull => DeserializeFromStreamAsync<T>(stream, null, ct);

    public static async ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        TomlOptions? options,
        CancellationToken ct = default
    )
        where T : notnull
    {
        var func = StreamingCache<T>.Func;
        if (func is not null)
            return await StreamingRunner.RunAsync<TomlReader, TomlReaderState, T>(
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
        TomlReaderState state,
        SerOptions? options,
        StreamingFunc<TomlReader, T> func,
        T? partial,
        out T? result,
        out TomlReaderState next,
        out SequencePosition advanceTo
    )
        where T : notnull
    {
        var reader = new TomlReader(buffer, isFinalBlock, state);
        var status = func(ref reader, partial, out result);
        next = reader.ExportState();
        advanceTo = next.Position;
        reader.Dispose();
        return status;
    }
}
