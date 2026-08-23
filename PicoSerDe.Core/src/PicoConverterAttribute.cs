namespace PicoSerDe.Core;

/// <summary>Base for per-format converter attributes.</summary>
[AttributeUsage(AttributeTargets.Property)]
public class PicoConverterAttribute : Attribute
{
    /// <summary>The converter type applied to the property.</summary>
    public Type ConverterType { get; }

    public PicoConverterAttribute(Type converterType) => ConverterType = converterType;
}
