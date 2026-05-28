namespace PicoToml;

/// <summary>Specifies the TOML key name for a property, overriding the property name.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TomlKeyAttribute : Attribute
{
    public string Key { get; }

    public TomlKeyAttribute(string key) => Key = key;
}
