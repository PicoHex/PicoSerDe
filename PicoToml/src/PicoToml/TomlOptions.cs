namespace PicoToml;

public enum TomlIgnoreCondition
{
    Never = 0,
    WhenWritingNull = 1,
}

public class TomlOptions
{
    public bool Indented { get; set; } = false;
    public TomlIgnoreCondition DefaultIgnoreCondition { get; set; } = TomlIgnoreCondition.Never;

    [ThreadStatic]
    internal static TomlOptions? Current;
}
