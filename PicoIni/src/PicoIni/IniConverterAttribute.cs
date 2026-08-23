namespace PicoIni;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class IniConverterAttribute : PicoConverterAttribute
{
    public IniConverterAttribute(Type converterType)
        : base(converterType) { }
}
