namespace PicoIni;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class IniSectionAttribute : Attribute
{
    public string Name { get; }
    public IniSectionAttribute(string name) => Name = name;
}
