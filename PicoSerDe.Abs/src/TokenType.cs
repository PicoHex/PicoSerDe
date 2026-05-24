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
    Int8, Int16, Int32, Int64,
    UInt8, UInt16, UInt32, UInt64,
    Float16, Float32, Float64,
    String,
    Bytes,
    Extension,
}
