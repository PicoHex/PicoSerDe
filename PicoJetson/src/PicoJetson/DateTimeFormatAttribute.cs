namespace PicoJetson;

[AttributeUsage(AttributeTargets.Property)]
public sealed class DateTimeFormatAttribute : Attribute
{
    public string Format { get; }

    public DateTimeFormatAttribute(string format) => Format = format;
}
