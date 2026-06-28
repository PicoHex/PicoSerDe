namespace PicoSerDe.Core;

/// <summary>
/// Universal serialization delegate that accepts any type including ref structs.
/// Used as the hot-path dispatch target for SerCache{T}.
/// </summary>
public delegate void SerDelegate<T>(IBufferWriter<byte> writer, T value)
    where T : allows ref struct;
