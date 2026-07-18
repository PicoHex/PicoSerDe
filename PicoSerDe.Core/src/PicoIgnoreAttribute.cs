namespace PicoSerDe.Core;

/// <summary>Controls when a property marked with <see cref="PicoIgnoreAttribute"/> is skipped during serialization.</summary>
public enum PicoIgnoreCondition
{
    /// <summary>Always ignore the property (default). The property participates in neither serialization nor deserialization.</summary>
    Always = 0,

    /// <summary>Never ignore the property: it is exempt from the format's global DefaultIgnoreCondition. Formats without a null literal (TOML/INI/YAML) still omit null values.</summary>
    Never = 1,

    /// <summary>Skip the property when its value is null, regardless of the global DefaultIgnoreCondition. Serialization only — deserialization still maps the property.</summary>
    WhenWritingNull = 2,

    /// <summary>Skip the property when its value is null or the type's default, regardless of the global DefaultIgnoreCondition. Serialization only.</summary>
    WhenWritingDefault = 3,
}

/// <summary>
/// Cross-format per-property ignore control, honored by all PicoSerDe format
/// generators (JSON, YAML, TOML, INI, MessagePack).
///
/// <code>
/// public class Message
/// {
///     [PicoIgnore]                                                  // stripped everywhere
///     public string Internal { get; set; } = "";
///
///     [PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)] // omitted only when null
///     public string? Note { get; set; }
///
///     [PicoIgnore(Condition = PicoIgnoreCondition.Never)]           // exempt from global condition
///     public string? Pinned { get; set; }
/// }
/// </code>
///
/// Format-specific markers ([JsonIgnore], [YamlIgnore], ...) remain single-format
/// unconditional ignores.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PicoIgnoreAttribute : Attribute
{
    /// <summary>The condition under which the property is ignored. Default: <see cref="PicoIgnoreCondition.Always"/>.</summary>
    public PicoIgnoreCondition Condition { get; set; } = PicoIgnoreCondition.Always;
}
