namespace PicoYaml;

/// <summary>Format marker isolating SerRegistry/DesRegistry entries for YAML.</summary>
public readonly struct YamlFormat { }

public static partial class YamlSerializer
{
    /// <summary>HTTP Content-Type header value for YAML.</summary>
    public const string ContentType = "application/yaml";

    // Serialization/deserialization registries live in PicoSerDe.Core
    // (SerRegistry/DesRegistry), isolated per format via YamlFormat.

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
        where T : allows ref struct
    {
        SerRegistry<YamlFormat, T>.Handler = handler;
    }

    /// <summary>
    /// Register serializer + deserializer (compat path).
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        SerRegistry<YamlFormat, T>.Handler = (writer, value) => serializer.Serialize(writer, value);
        DesRegistry<YamlFormat, T>.Deserializer = deserializer;
    }

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer)
    {
        DesRegistry<YamlFormat, T>.Deserializer = deserializer;
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
        where T : allows ref struct
    {
        if (SerRegistry<YamlFormat, T>.Handler is { } h)
        {
            var writer = SerializerExtensions.RentWriter();
            h(writer, value);
            return writer.WrittenSpan.ToArray();
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
        return default!;
    }

    public static string Serialize<T>(T value)
        where T : allows ref struct
    {
        if (SerRegistry<YamlFormat, T>.Handler is { } h)
        {
            var writer = SerializerExtensions.RentWriter();
            h(writer, value);
            return Encoding.UTF8.GetString(writer.WrittenSpan);
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
        return "";
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
        where T : allows ref struct
    {
        if (SerRegistry<YamlFormat, T>.Handler is { } h)
            h(writer, value);
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (DesRegistry<YamlFormat, T>.Deserializer is { } d)
            return d.Deserialize(data);
        SerializerExtensions.ThrowNoSerializer<T>("PicoYaml.Gen");
        return default;
    }

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
