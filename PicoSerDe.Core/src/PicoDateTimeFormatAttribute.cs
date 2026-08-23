namespace PicoSerDe.Core;

/// <summary>Base for per-format date/time format attributes.</summary>
[AttributeUsage(AttributeTargets.Property)]
public class PicoDateTimeFormatAttribute : Attribute
{
    /// <summary>The format string applied to DateTime/DateOnly/TimeOnly values.</summary>
    public string Format { get; }

    public PicoDateTimeFormatAttribute(string format) => Format = format;
}
