namespace PicoToml;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class TomlDateTimeFormatAttribute : PicoDateTimeFormatAttribute
{
    public TomlDateTimeFormatAttribute(string format)
        : base(format) { }
}
