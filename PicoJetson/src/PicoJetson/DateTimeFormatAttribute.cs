namespace PicoJetson;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class DateTimeFormatAttribute : PicoDateTimeFormatAttribute
{
    public DateTimeFormatAttribute(string format)
        : base(format) { }
}
