namespace PicoSerDe.Core;

/// <summary>
/// Transactional extension of <see cref="ITokenReader"/> for chunked parsing.
/// Shared by every built-in text reader (JSON/TOML/YAML/INI) and used by the
/// generated streaming delegates. The contract:
/// <list type="bullet">
/// <item><see cref="Mark"/> snapshots the reader's <b>position and parser
/// state</b> (record the shape only the reader knows: indent stacks, section
/// context, array depth, ...).</item>
/// <item><see cref="RewindToMark"/> restores that snapshot exactly, so a caller
/// can re-parse the current member from its start after a chunk boundary.</item>
/// <item>While a read reports <see cref="ITokenReader.NeedsMoreData"/>, the
/// reader can be resumed from the position reported by the exported state;
/// callers that need a member-level retry use <see cref="Mark"/> /
/// <see cref="RewindToMark"/> instead of assuming anything about the internal
/// position.</item>
/// <item>Token spans obtained before a rollback must not be used afterwards.</item>
/// </list>
/// </summary>
public interface ITransactionalTokenReader : ITokenReader
{
    /// <summary>Bytes consumed from the current buffer (span-relative).</summary>
    long BytesConsumed { get; }

    /// <summary>Start offset of the token most recently produced by <see cref="ITokenReader.Read"/>.</summary>
    long TokenStart { get; }

    /// <summary>Rewinds the position to an earlier buffer offset (low-level escape hatch).</summary>
    void RewindTo(long consumedOffset);

    /// <summary>
    /// Snapshots position + parser state. Single-level: a nested Mark overwrites
    /// the previous snapshot.
    /// </summary>
    void Mark();

    /// <summary>Restores the state captured by the last <see cref="Mark"/> call.</summary>
    void RewindToMark();
}
