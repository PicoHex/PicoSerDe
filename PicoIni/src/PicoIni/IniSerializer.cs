namespace PicoIni;

/// <summary>Format marker isolating SerRegistry/DesRegistry entries for INI.</summary>
public readonly struct IniFormat { }

public static partial class IniSerializer
{
    /// <summary>HTTP Content-Type header value for INI.</summary>
    public const string ContentType = "text/plain";

    // Serialization/deserialization registries live in PicoSerDe.Core
    // (SerRegistry/DesRegistry), isolated per format via IniFormat.
    // All shared methods forward to SerializerFacade<IniFormat>.

    private static class StreamingCache<T>
        where T : notnull
    {
        internal static StreamingFunc<IniReader, T>? Func;
    }

    /// <summary>
    /// Registers the streaming deserializer delegate emitted by the source
    /// generator (also callable by hand-written code).
    /// </summary>
    /// <param name="func">Resumable per-chunk deserialization step.</param>
    /// <typeparam name="T">DTO type; must be non-nullable (<c>notnull</c>).</typeparam>
    public static void RegisterStreaming<T>(StreamingFunc<IniReader, T> func)
        where T : notnull => StreamingCache<T>.Func = func;

    /// <summary>True when a streaming deserializer has been registered for T.</summary>
    public static bool HasStreamingDelegate<T>()
        where T : notnull => StreamingCache<T>.Func is not null;

    /// <summary>Register a delegate-based serializer (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> handler)
        where T : allows ref struct => SerializerFacade<IniFormat>.Register(handler);

    /// <summary>
    /// Register serializer + deserializer (compat path for hand-written ISerializer/IDeserializer).
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer) =>
        SerializerFacade<IniFormat>.Register(serializer, deserializer);

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer) =>
        SerializerFacade<IniFormat>.RegisterDeserializer(deserializer);

    public static byte[] SerializeToUtf8Bytes<T>(T value, IniOptions? options = null)
        where T : allows ref struct =>
        SerializerFacade<IniFormat>.SerializeToUtf8Bytes(value, options);

    public static string Serialize<T>(T value, IniOptions? options = null)
        where T : allows ref struct => SerializerFacade<IniFormat>.Serialize(value, options);

    public static void Serialize<T>(IBufferWriter<byte> writer, T value, IniOptions? options = null)
        where T : allows ref struct =>
        SerializerFacade<IniFormat>.Serialize(writer, value, options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> data, IniOptions? options = null) =>
        SerializerFacade<IniFormat>.Deserialize<T>(data, options);

    public static ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        CancellationToken ct = default
    )
        where T : notnull => DeserializeFromStreamAsync<T>(stream, null, ct);

    public static async ValueTask<T> DeserializeFromStreamAsync<T>(
        Stream stream,
        IniOptions? options,
        CancellationToken ct = default
    )
        where T : notnull
    {
        var func = StreamingCache<T>.Func;
        if (func is not null)
            return await StreamingRunner.RunAsync<IniReader, IniReaderState, T>(
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
        IniReaderState state,
        SerOptions? options,
        StreamingFunc<IniReader, T> func,
        T? partial,
        out T? result,
        out IniReaderState next,
        out SequencePosition advanceTo
    )
        where T : notnull
    {
        var reader = new IniReader(buffer, isFinalBlock, state);
        var status = func(ref reader, partial, out result);
        next = reader.ExportState();
        advanceTo = next.Position;
        reader.Dispose();
        return status;
    }
}
