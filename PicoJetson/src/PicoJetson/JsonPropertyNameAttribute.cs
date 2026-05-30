namespace PicoJetson;

[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonPropertyNameAttribute : Attribute
{
    public string Name { get; }

    public JsonPropertyNameAttribute(string name) => Name = name;
}
