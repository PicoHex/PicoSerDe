namespace PicoSerDe.Core;

/// <summary>
/// Cross-format token-stream contract implemented by all format readers
/// (PicoJetson.JsonReader, PicoIni.IniReader, ...). All readers are ref
/// structs; generic consumers must constrain with `allows ref struct` and
/// never box a reader into this interface.
/// </summary>
public interface ITokenReader
{
    /// <summary>Advances to the next token. Returns false at end of input or
    /// (in streaming mode) when a chunk boundary requires more data.</summary>
    bool Read();

    /// <summary>The token returned by the most recent <see cref="Read"/>.</summary>
    TokenType TokenType { get; }

    bool TryGetInt32(out int value);
    bool TryGetInt64(out long value);
    bool TryGetFloat64(out double value);
    bool TryGetBool(out bool value);
    bool TrySkip();

    /// <summary>True when the last failed <see cref="Read"/> hit a chunk boundary (streaming).</summary>
    bool NeedsMoreData { get; }

    /// <summary>Current nesting depth.</summary>
    int Depth { get; }
}
