namespace PicoJson;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class JsonConverterAttribute : Attribute
{
    public Type ConverterType { get; }

    public JsonConverterAttribute(Type converterType) => ConverterType = converterType;
}
