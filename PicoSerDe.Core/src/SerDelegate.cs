namespace PicoSerDe.Core;

/// <summary>
/// Universal serialization delegate that accepts any type including ref structs.
/// Used as the hot-path dispatch target for SerRegistry{TFormat, T}.
/// Carries per-call options explicitly — no ambient state.
/// </summary>
public delegate void SerDelegate<T>(IBufferWriter<byte> writer, T value, SerOptions? options)
    where T : allows ref struct;
