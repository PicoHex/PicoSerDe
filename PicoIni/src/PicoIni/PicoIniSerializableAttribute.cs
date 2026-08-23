namespace PicoIni;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class PicoIniSerializableAttribute : PicoSerializableAttribute
{
    public PicoIniSerializableAttribute() { }

    public PicoIniSerializableAttribute(Type type)
        : base(type) { }
}
