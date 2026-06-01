namespace PicoYaml;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class YamlTagAttribute : Attribute
{
    public string Tag { get; }

    public YamlTagAttribute(string tag) => Tag = tag;
}
