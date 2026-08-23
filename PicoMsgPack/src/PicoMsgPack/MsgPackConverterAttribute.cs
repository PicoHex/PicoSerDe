namespace PicoMsgPack;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class MsgPackConverterAttribute : PicoConverterAttribute
{
    public MsgPackConverterAttribute(Type converterType)
        : base(converterType) { }
}
