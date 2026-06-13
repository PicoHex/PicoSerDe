namespace PicoSerDe.Core;

/// <summary>Return status for streaming-aware reader operations.</summary>
public enum ReadStatus
{
    /// <summary>A token was successfully read.</summary>
    Success = 0,

    /// <summary>End of the document has been reached (isFinalBlock was true).</summary>
    EndOfInput,

    /// <summary>More data is required to complete the current token (isFinalBlock was false).</summary>
    NeedMoreData,
}
