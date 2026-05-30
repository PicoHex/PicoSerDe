namespace PicoMsgPack;

[AttributeUsage(AttributeTargets.Property)]
public sealed class MsgPackKeyAttribute : Attribute
{
    public int Key { get; }

    public MsgPackKeyAttribute(int key) => Key = key;
}
