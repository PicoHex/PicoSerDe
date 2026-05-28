namespace PicoYaml;

/// <summary>Specifies the YAML key name for a property, overriding the property name.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class YamlKeyAttribute : Attribute
{
    public string Key { get; }

    public YamlKeyAttribute(string key) => Key = key;
}
