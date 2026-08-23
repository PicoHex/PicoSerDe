namespace PicoYaml;

/// <summary>Format-specific marker; shares behavior with the PicoSerDe.Core base.</summary>
public sealed class YamlDateTimeFormatAttribute : PicoDateTimeFormatAttribute
{
    public YamlDateTimeFormatAttribute(string format)
        : base(format) { }
}
