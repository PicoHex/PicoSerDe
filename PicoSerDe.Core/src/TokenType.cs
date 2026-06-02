namespace PicoSerDe.Core;

public enum TokenType
{
    None = 0,
    ObjectStart,
    ObjectEnd,
    ArrayStart,
    ArrayEnd,
    PropertyName,
    Null,
    Bool,

    // ── Reserved for future format support ──
    // These enum values are placeholders for potential Int8/Int16/Float16
    // support (e.g., MsgPack). Not emitted by any current reader/writer.
    // If unused by v2.1, consider removal or consolidation.
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Float16,
    Float32,
    Float64,

    // ── End reserved block ──

    String,
    Bytes,
    Extension,
}
