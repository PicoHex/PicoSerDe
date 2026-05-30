namespace PicoSerDe.Abs;

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

    /// <summary>Reserved for future format support. Not currently emitted by any reader/writer.</summary>
    Int8,

    /// <summary>Reserved for future format support. Not currently emitted by any reader/writer.</summary>
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,

    /// <summary>Reserved for future format support. Not currently emitted by any reader/writer.</summary>
    Float16,
    Float32,
    Float64,
    String,
    Bytes,
    Extension,
}
