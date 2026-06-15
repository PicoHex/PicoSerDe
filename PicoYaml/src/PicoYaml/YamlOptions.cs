namespace PicoYaml;

public enum YamlIgnoreCondition
{
    Never = 0,
    WhenWritingNull = 1,
}

public class YamlOptions
{
    public bool Indented { get; set; } = false;
    public YamlIgnoreCondition DefaultIgnoreCondition { get; set; } = YamlIgnoreCondition.Never;

    [ThreadStatic]
    internal static YamlOptions? Current;
}
