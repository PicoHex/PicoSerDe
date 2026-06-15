namespace PicoIni;

/// <summary>Controls when properties are skipped during INI serialization.</summary>
public enum IniIgnoreCondition
{
    /// <summary>Always serialize the property.</summary>
    Never = 0,

    /// <summary>Skip properties whose value is null.</summary>
    WhenWritingNull = 1,
}

/// <summary>Options for <see cref="IniSerializer"/>.</summary>
public class IniOptions
{
    /// <summary>Whether to indent the INI output. Default: false (compact).</summary>
    public bool Indented { get; set; } = false;

    /// <summary>Controls when properties are skipped based on their value. Default: Never.</summary>
    public IniIgnoreCondition DefaultIgnoreCondition { get; set; } = IniIgnoreCondition.Never;

    [ThreadStatic]
    internal static IniOptions? Current;
}
