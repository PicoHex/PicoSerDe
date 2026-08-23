namespace PicoToml;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class TomlConverterAttribute : PicoConverterAttribute
{
    public TomlConverterAttribute(Type converterType)
        : base(converterType) { }
}
