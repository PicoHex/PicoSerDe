namespace PicoToml;

/// <summary>Marks a property to be ignored during TOML serialization and deserialization.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TomlIgnoreAttribute : Attribute { }
