namespace PicoIni;

/// <summary>Specifies a custom <see cref="IIniConverter{T}"/> for a property.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IniConverterAttribute : Attribute
{
    /// <summary>The converter type (must implement <see cref="IIniConverter{T}"/>).</summary>
    public Type ConverterType { get; }

    /// <param name="converterType">The converter type.</param>
    public IniConverterAttribute(Type converterType) => ConverterType = converterType;
}
