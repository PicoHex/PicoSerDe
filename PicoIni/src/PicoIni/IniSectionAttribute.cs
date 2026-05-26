namespace PicoIni;

/// <summary>Overrides the INI section name for a class or property.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class IniSectionAttribute : Attribute
{
    /// <summary>The section name to use in the INI file.</summary>
    public string Name { get; }
    /// <param name="name">The section name to use.</param>
    public IniSectionAttribute(string name) => Name = name;
}
