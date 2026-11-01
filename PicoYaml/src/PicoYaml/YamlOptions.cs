namespace PicoYaml;

public class YamlOptions : SerOptions
{
    public bool Indented { get; set; } = false;

    /// <summary>
    /// Controls when properties are skipped based on their value. Default:
    /// <see cref="PicoIgnoreCondition.Never"/>. A null <b>member</b> is omitted
    /// whatever this is set to; null elements and null dictionary values keep their
    /// <c>null</c> literal. <see cref="PicoIgnoreCondition.Always"/> has no global meaning
    /// either: as a default it behaves like <see cref="PicoIgnoreCondition.Never"/>.
    /// </summary>
    public PicoIgnoreCondition DefaultIgnoreCondition { get; set; } = PicoIgnoreCondition.Never;
}
