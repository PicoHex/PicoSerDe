namespace PicoIni;

/// <summary>Excludes a property from INI serialization and deserialization.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IniIgnoreAttribute : Attribute { }
