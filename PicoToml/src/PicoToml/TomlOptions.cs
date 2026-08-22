namespace PicoToml;

public enum TomlIgnoreCondition
{
    Never = 0,
    WhenWritingNull = 1,
}

public class TomlOptions : SerOptions
{
    public bool Indented { get; set; } = false;
    public TomlIgnoreCondition DefaultIgnoreCondition { get; set; } = TomlIgnoreCondition.Never;
}
