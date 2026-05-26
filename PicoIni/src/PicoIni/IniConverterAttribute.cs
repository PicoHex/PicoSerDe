namespace PicoIni;

[AttributeUsage(AttributeTargets.Property)]
public sealed class IniConverterAttribute : Attribute
{
    public Type ConverterType { get; }
    public IniConverterAttribute(Type converterType) => ConverterType = converterType;
}
