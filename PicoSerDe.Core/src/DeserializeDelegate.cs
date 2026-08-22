namespace PicoSerDe.Core;

/// <summary>
/// Universal deserialization delegate; carries per-call options explicitly.
/// Used as the hot-path dispatch target for DesRegistry{TFormat, T}.
/// </summary>
public delegate T DeserializeDelegate<T>(ReadOnlySpan<byte> data, SerOptions? options)
    where T : allows ref struct;
