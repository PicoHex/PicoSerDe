namespace PicoToml;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class PicoTomlSerializableAttribute : PicoSerializableAttribute
{
    public PicoTomlSerializableAttribute() { }

    public PicoTomlSerializableAttribute(Type type)
        : base(type) { }
}
