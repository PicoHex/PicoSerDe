namespace PicoToml;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoTomlSerializableAttribute : PicoSerDe.Core.PicoSerializableAttribute
{
    public PicoTomlSerializableAttribute() { }
    public PicoTomlSerializableAttribute(Type type) : base(type) { }
}
