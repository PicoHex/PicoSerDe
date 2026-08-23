namespace PicoYaml;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class YamlConverterAttribute : PicoConverterAttribute
{
    public YamlConverterAttribute(Type converterType)
        : base(converterType) { }
}
