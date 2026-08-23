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

    // ── Numeric tokens ──
    // Emitted by real readers/writers: MsgPack emits UInt8/16/32/64,
    // Int32/64, Float32/64; JSON/TOML/YAML/INI emit Int32/64, Float64.
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Float32,
    Float64,

    // ── End reserved block ──

    String,
    Bytes,
    Extension,
}
