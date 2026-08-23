namespace PicoMsgPack;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class PicoMsgPackSerializableAttribute : PicoSerializableAttribute
{
    public PicoMsgPackSerializableAttribute() { }

    public PicoMsgPackSerializableAttribute(Type type)
        : base(type) { }
}
