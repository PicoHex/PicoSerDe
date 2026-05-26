namespace PicoIni;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class IniCommentAttribute : Attribute
{
    public string Text { get; }
    public IniCommentAttribute(string text) => Text = text;
}
