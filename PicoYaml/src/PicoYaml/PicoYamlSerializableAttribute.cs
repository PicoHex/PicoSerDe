namespace PicoYaml;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoYamlSerializableAttribute : PicoSerDe.Core.PicoSerializableAttribute
{
    public PicoYamlSerializableAttribute() { }
    public PicoYamlSerializableAttribute(Type type) : base(type) { }
}
