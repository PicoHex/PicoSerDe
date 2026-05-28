namespace PicoToml;

/// <summary>Specifies a custom converter to use for serializing a property.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class TomlConverterAttribute : Attribute
{
    public Type ConverterType { get; }

    public TomlConverterAttribute(Type converterType) => ConverterType = converterType;
}
