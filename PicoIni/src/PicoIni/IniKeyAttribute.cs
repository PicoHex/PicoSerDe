namespace PicoIni;

/// <summary>Overrides the INI key name for a property.</summary>
public sealed class IniKeyAttribute : Attribute
{
    /// <summary>The key name to use in the INI file.</summary>
    public string Key { get; }

    /// <summary>Backwards-compatible alias for <see cref="Key"/>.</summary>
    [Obsolete("Use Key instead.")]
    public string Name => Key;

    /// <param name="key">The key name to use.</param>
    public IniKeyAttribute(string key) => Key = key;
}
