namespace PicoJetson;

[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonPropertyNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
