namespace PicoMsgPack;

[AttributeUsage(AttributeTargets.Property)]
public sealed class MsgPackExtensionTagAttribute : Attribute
{
    public byte Tag { get; }

    public MsgPackExtensionTagAttribute(byte tag) => Tag = tag;
}
