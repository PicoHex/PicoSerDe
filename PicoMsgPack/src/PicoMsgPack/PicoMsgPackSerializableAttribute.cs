namespace PicoMsgPack;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoMsgPackSerializableAttribute : PicoSerDe.Core.PicoSerializableAttribute
{
    public PicoMsgPackSerializableAttribute() { }

    public PicoMsgPackSerializableAttribute(Type type)
        : base(type) { }
}
