namespace PicoIni;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoIniSerializableAttribute : PicoSerDe.Core.PicoSerializableAttribute
{
    public PicoIniSerializableAttribute() { }

    public PicoIniSerializableAttribute(Type type)
        : base(type) { }
}
