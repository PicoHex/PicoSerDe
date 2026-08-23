namespace PicoIni;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class IniDateTimeFormatAttribute : PicoDateTimeFormatAttribute
{
    public IniDateTimeFormatAttribute(string format)
        : base(format) { }
}
