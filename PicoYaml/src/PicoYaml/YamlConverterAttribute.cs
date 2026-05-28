namespace PicoYaml;

/// <summary>Specifies a custom converter to use for serializing a property.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class YamlConverterAttribute : Attribute
{
    public Type ConverterType { get; }

    public YamlConverterAttribute(Type converterType) => ConverterType = converterType;
}
