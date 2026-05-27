namespace PicoIni;

/// <summary>Token types produced by <see cref="IniReader"/>.</summary>
public enum IniTokenType
{
    /// <summary>No token — initial state or end of input.</summary>
    None = 0,

    /// <summary>A section header: [SectionName].</summary>
    SectionStart,

    /// <summary>A key-value pair: Key = Value.</summary>
    Key,

    /// <summary>A standalone value token (unused in INI; reserved).</summary>
    Value,

    /// <summary>A comment line starting with ; or #.</summary>
    Comment,

    /// <summary>An empty or whitespace-only line.</summary>
    Blank,
}
