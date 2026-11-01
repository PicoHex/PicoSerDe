namespace PicoIni;

/// <summary>Options for <see cref="IniSerializer"/>.</summary>
public class IniOptions : SerOptions
{
    /// <summary>Whether to indent the INI output. Default: false (compact).</summary>
    public bool Indented { get; set; } = false;

    /// <summary>
    /// Controls when properties are skipped based on their value. Default:
    /// <see cref="PicoIgnoreCondition.Never"/>. Accepted for API uniformity — INI has
    /// no null literal, so null members are omitted whatever this is set to.
    /// <see cref="PicoIgnoreCondition.Always"/> has no global meaning either: as a default it
    /// behaves like <see cref="PicoIgnoreCondition.Never"/>.
    /// </summary>
    public PicoIgnoreCondition DefaultIgnoreCondition { get; set; } = PicoIgnoreCondition.Never;
}
