namespace PicoJetson;

/// <summary>
/// Options for <see cref="JsonSerializer"/>.
/// Default mode is compact (no indentation) — optimal for data transfer.
/// Set <see cref="Indented"/> to <c>true</c> for human-readable output.
/// </summary>
public class JsonOptions
{
    /// <summary>
    /// Gets or sets whether to indent the JSON output.
    /// Default: <c>false</c> (compact).
    /// </summary>
    public bool Indented { get; set; } = false;

    /// <summary>Thread-local current options, used by SG-generated code to configure JsonWriter.</summary>
    [ThreadStatic]
    internal static JsonOptions? Current;
}
