namespace PicoSerDe.Core;

/// <summary>
/// Internal signal used by the built-in chunked readers: the current buffer
/// ends inside a token. Readers convert it to <c>NeedsMoreData</c>, so callers
/// never observe this exception. Shared across the format assemblies through
/// <c>InternalsVisibleTo</c>.
/// </summary>
internal sealed class IncompleteInputException : Exception
{
    public IncompleteInputException()
        : base("Incomplete input: the buffer ends inside a token.") { }
}
