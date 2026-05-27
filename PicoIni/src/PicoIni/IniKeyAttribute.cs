namespace PicoIni;

/// <summary>Overrides the INI key name for a property.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IniKeyAttribute : Attribute
{
    /// <summary>The key name to use in the INI file.</summary>
    public string Name { get; }

    /// <param name="name">The key name to use.</param>
    public IniKeyAttribute(string name) => Name = name;
}
