namespace PicoToml;

[AttributeUsage(AttributeTargets.Property)]
public sealed class TomlDateTimeFormatAttribute : Attribute
{
    public string Format { get; }

    public TomlDateTimeFormatAttribute(string format) => Format = format;
}
