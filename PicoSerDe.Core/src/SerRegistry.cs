namespace PicoSerDe.Core;

/// <summary>
/// Cross-format serializer registry. <typeparamref name="TFormat"/> is a
/// per-format marker type (e.g. <c>PicoJetson.JsonFormat</c>) that isolates
/// registrations: the same <typeparamref name="T"/> can carry independent
/// serializers for each format. Static-generic fields keep lookups at
/// field-read cost (JIT/AOT friendly, zero reflection).
/// </summary>
public static class SerRegistry<TFormat, T>
    where T : allows ref struct
{
    /// <summary>The active top-level serializer (SG-registered or user-registered).</summary>
    public static SerDelegate<T>? Handler;

    /// <summary>
    /// User serializer registered via a format's <c>RegisterCustom</c>: also
    /// overrides SG-generated serialization for nested occurrences of T.
    /// </summary>
    public static SerDelegate<T>? CustomHandler;
}

/// <summary>
/// Cross-format deserializer registry, isolated per format marker like
/// <see cref="SerRegistry{TFormat, T}"/>.
/// </summary>
public static class DesRegistry<TFormat, T>
{
    /// <summary>The active top-level deserializer.</summary>
    public static IDeserializer<T>? Deserializer;
}
