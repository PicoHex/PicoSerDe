namespace PicoYaml;

/// <summary>Marks a property to be ignored during YAML serialization and deserialization.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class YamlIgnoreAttribute : Attribute { }
