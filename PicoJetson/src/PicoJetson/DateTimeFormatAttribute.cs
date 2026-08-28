namespace PicoJetson;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class DateTimeFormatAttribute(string format) : PicoDateTimeFormatAttribute(format);
