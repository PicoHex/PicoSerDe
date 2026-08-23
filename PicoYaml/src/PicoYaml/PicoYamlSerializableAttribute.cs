namespace PicoYaml;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class PicoYamlSerializableAttribute : PicoSerializableAttribute
{
    public PicoYamlSerializableAttribute() { }

    public PicoYamlSerializableAttribute(Type type)
        : base(type) { }
}
