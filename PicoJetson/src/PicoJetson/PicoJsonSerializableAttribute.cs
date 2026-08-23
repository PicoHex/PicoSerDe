namespace PicoJetson;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class PicoJsonSerializableAttribute : PicoSerializableAttribute
{
    public PicoJsonSerializableAttribute() { }

    public PicoJsonSerializableAttribute(Type type)
        : base(type) { }
}
