namespace PicoYaml;

[AttributeUsage(AttributeTargets.Property)]
public sealed class YamlDateTimeFormatAttribute : Attribute
{
    public string Format { get; }

    public YamlDateTimeFormatAttribute(string format) => Format = format;
}
