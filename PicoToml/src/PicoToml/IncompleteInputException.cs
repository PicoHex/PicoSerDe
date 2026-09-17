namespace PicoToml;

/// <summary>
/// Internal signal: the current buffer ends inside a token. The streaming
/// reader converts it to <c>NeedsMoreData</c>; the caller retries with more
/// data starting at the token/member start.
/// </summary>
internal sealed class IncompleteInputException : Exception
{
    public IncompleteInputException()
        : base("Incomplete TOML input.") { }
}
