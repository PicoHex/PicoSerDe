namespace PicoIni;

[AttributeUsage(AttributeTargets.Property)]
public sealed class IniDateTimeFormatAttribute : Attribute
{
    public string Format { get; }

    public IniDateTimeFormatAttribute(string format) => Format = format;
}
