namespace PicoIni;

[AttributeUsage(AttributeTargets.Property)]
public sealed class IniKeyAttribute : Attribute
{
    public string Name { get; }
    public IniKeyAttribute(string name) => Name = name;
}
