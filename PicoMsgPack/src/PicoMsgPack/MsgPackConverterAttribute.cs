namespace PicoMsgPack;

[AttributeUsage(AttributeTargets.Property)]
public sealed class MsgPackConverterAttribute : Attribute
{
    public Type ConverterType { get; }
    public MsgPackConverterAttribute(Type converterType) => ConverterType = converterType;
}
