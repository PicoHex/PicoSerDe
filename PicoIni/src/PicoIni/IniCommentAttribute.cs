namespace PicoIni;

/// <summary>Emits a comment before the annotated class or property in the INI output.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class IniCommentAttribute : Attribute
{
    /// <summary>The comment text (without the ; or # prefix).</summary>
    public string Text { get; }

    /// <param name="text">The comment text.</param>
    public IniCommentAttribute(string text) => Text = text;
}
