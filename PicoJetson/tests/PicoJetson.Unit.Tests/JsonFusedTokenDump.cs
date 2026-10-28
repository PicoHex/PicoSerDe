using System.Globalization;
using System.Runtime.ExceptionServices;

namespace PicoJetson.Tests;

/// <summary>
/// Task T12 (fused token read, spec §5.2 / §6 S2): the <b>"reference vs fused" dual-walk dump</b> for JSON.
/// <para>
/// <b>What it proves.</b> The same payload is walked twice <i>in the same build</i>: once by the reference
/// path (a <see cref="JsonReader.Read"/> loop plus the <c>TryGetX</c> family, i.e. the shape the generator
/// emits today) and once by the fused path (one <c>TryReadX</c> call per token, which advances <b>and</b>
/// decodes in a single dispatch). Every token is dumped as
/// <c>index|TokenType|BytesConsumed|Depth|value bytes|all getters|fused-read result|collection count</c>
/// and the two dumps must be identical (spec §5.1).
/// </para>
/// <para>
/// <b>Why the reference arm is written out inline instead of calling <see cref="TokenReaderFused"/>.</b>
/// The reference must not come from the implementation under test (spec §3.2: "正本管样板，内联管对拍").
/// Reusing the reference semantics would compare the fused path with itself.
/// </para>
/// <para>
/// <b>Why the dump carries the fused read's answer and the count query's answer.</b> A snapshot only sees
/// the reader's <i>state</i>. A fused member that consumed the right token and materialised the right
/// state but <i>returned a wrong value</i> would leave every state-only dump identical. So the value each
/// fused read returns, and the answer each count query gives, are dumped next to the reference arm's
/// inline answer for the same token.
/// </para>
/// <para>
/// <b>JSON answers no count.</b> Its wire form carries no element count (spec §3.3: the reader finds the
/// matching <c>]</c>/<c>}</c> by scanning bytes and keeps no counter), so the reference column for a
/// collection start is the constant <see cref="CountNotCarried"/> and a reader that answers a number there
/// is a contract violation caught by the comparison — the column is not decoration.
/// </para>
/// </summary>
public static class JsonFusedTokenDump
{
    /// <summary>Which fused read the walker uses for the integer family (T14 overwrites <c>TryReadInt32</c>).</summary>
    public enum IntAccessor
    {
        /// <summary>What generated int elements call: <c>TryReadInt32</c>.</summary>
        Int32 = 0,

        /// <summary>What generated long elements call: <c>TryReadInt64</c>.</summary>
        Int64 = 1,
    }

    /// <summary>The reader construction an arm walks: the whole payload as one span, or as one sequence.</summary>
    public enum ReaderMode
    {
        /// <summary><c>new JsonReader(ReadOnlySpan&lt;byte&gt;)</c> — the span path.</summary>
        Span = 0,

        /// <summary><c>new JsonReader(ReadOnlySequence&lt;byte&gt;)</c> — the sequence path.</summary>
        Sequence = 1,
    }

    /// <summary>
    /// The format-specific half of a fused walk: how to snapshot a reader (and read its
    /// <c>BytesConsumed</c>) whose concrete type the walker does not know. Generic so that a
    /// deliberately-wrong stand-in reader is walked by the very same code as the real one — that is what
    /// makes the guard's bite provable.
    /// </summary>
    public interface IJsonFusedCapture<TReader>
        where TReader : allows ref struct
    {
        JsonTokenSnapshot Capture(int index, ref TReader reader);

        long Consumed(ref TReader reader);
    }

    /// <summary>One dumped token: the snapshot, what the walker's read returned, and the count query's answer.</summary>
    public readonly record struct FusedLine(
        int Index,
        JsonTokenSnapshot Snapshot,
        string Read,
        string Count = ""
    )
    {
        public override string ToString() =>
            Count.Length == 0
                ? Snapshot.ToString() + "|" + Read
                : Snapshot.ToString() + "|" + Read + "|" + Count;
    }

    /// <summary>
    /// A whole walk: the dumped lines, the terminal state (<c>NeedsMoreData</c>, <c>Depth</c>,
    /// <c>BytesConsumed</c>), and whether the fused reader claims the counted-collection capability.
    /// </summary>
    public readonly record struct FusedDrain(
        List<FusedLine> Lines,
        bool NeedsMoreData,
        int Depth,
        long Consumed,
        bool DeclaresCounts = false
    );

    /// <summary>
    /// The reference column for a collection start: JSON's wire form has no count, so there is no number
    /// to write. A fused reader that answers a count here contradicts its own
    /// <see cref="IFusedTokenReader.HasCountedCollections"/> declaration for this format.
    /// </summary>
    public const string CountNotCarried = "count=not-carried";

    /// <summary>An exception (or its absence) rendered the way spec §5.1 compares it: type <b>and</b> message.</summary>
    public static string ExecutionString(Exception? failure) =>
        failure is null ? "<no throw>" : failure.GetType().Name + ": " + failure.Message;

    /// <summary>
    /// The reference arm: a literal <see cref="JsonReader.Read"/> loop, the accessor answer read inline
    /// from the <c>TryGetX</c> family, and <see cref="JsonSpanTokenDump.Capture"/> for the snapshot (every
    /// getter, the value span, <c>BytesConsumed</c>, <c>Depth</c>).
    /// </summary>
    public static FusedDrain DrainReference(
        byte[] payload,
        ReaderMode mode = ReaderMode.Span,
        bool isFinalBlock = true,
        IntAccessor ints = IntAccessor.Int32
    )
    {
        var (drain, failure) = ReferenceUntilFailure(payload, mode, isFinalBlock, ints);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return drain;
    }

    /// <summary>
    /// The reference arm on a payload that may throw: the partial dump plus the exception.
    /// <para>The tokens walked before the failure are the whole point: they are the script a fused walk of
    /// the same malformed or truncated payload has to reproduce.</para>
    /// </summary>
    public static (FusedDrain Drain, Exception? Failure) ReferenceUntilFailure(
        byte[] payload,
        ReaderMode mode = ReaderMode.Span,
        bool isFinalBlock = true,
        IntAccessor ints = IntAccessor.Int32
    )
    {
        if (mode == ReaderMode.Sequence)
        {
            var reader = new JsonReader(
                new ReadOnlySequence<byte>(payload),
                maxDepth: 256,
                isFinalBlock: isFinalBlock
            );
            try
            {
                return ReferenceOn<JsonReader, JsonReaderCapture>(ref reader, default, ints);
            }
            finally
            {
                reader.Dispose();
            }
        }

        var spanReader = new JsonReader(
            payload.AsSpan(),
            maxDepth: 256,
            isFinalBlock: isFinalBlock
        );
        try
        {
            return ReferenceOn<JsonReader, JsonReaderCapture>(ref spanReader, default, ints);
        }
        finally
        {
            spanReader.Dispose();
        }
    }

    private static (FusedDrain Drain, Exception? Failure) ReferenceOn<TReader, TCapture>(
        ref TReader reader,
        TCapture capture,
        IntAccessor ints
    )
        where TReader : ITokenReader, allows ref struct
        where TCapture : struct, IJsonFusedCapture<TReader>
    {
        var lines = new List<FusedLine>();
        try
        {
            while (reader.Read())
                lines.Add(
                    new FusedLine(
                        lines.Count,
                        capture.Capture(lines.Count, ref reader),
                        ReferenceReadResult(ref reader, ints)
                    )
                );
            return (Drain(lines, ref reader, capture, declaresCounts: false), null);
        }
        catch (Exception ex)
        {
            // The partial stream is kept: it is the script the fused arm has to reproduce before it may
            // fail with the same type and message.
            return (Drain(lines, ref reader, capture, declaresCounts: false), ex);
        }
    }

    /// <summary>
    /// The fused arm: for every token the reference arm saw, one fused call decides advance + value.
    /// <para>
    /// The accessor is chosen from the <b>reference</b> token type (spec §5.2 / plan §3.3 item 5) — not
    /// answer leakage: it mirrors what generated code knows at generation time (which scalar family the
    /// element is), while the value is decoded independently by the fused path and compared afterwards.
    /// </para>
    /// <para>
    /// After the script is exhausted the walker issues the reference loop's own terminating
    /// <c>Read()</c>, so a fused read that consumed too few (or too many) tokens shows up as an extra
    /// line, a different token count, or a different terminal state.
    /// </para>
    /// </summary>
    public static FusedDrain DrainFused<TReader, TCapture>(
        ref TReader reader,
        IReadOnlyList<TokenType> script,
        TCapture capture,
        IntAccessor ints = IntAccessor.Int32
    )
        where TReader : IFusedTokenReader, allows ref struct
        where TCapture : struct, IJsonFusedCapture<TReader>
    {
        var lines = new List<FusedLine>(script.Count);
        for (int i = 0; i < script.Count; i++)
        {
            var read = script[i] switch
            {
                var t when IsIntFamily(t) => ints == IntAccessor.Int64
                    ? Format<long>("i64", reader.TryReadInt64(out var i64), i64)
                    : Format("i32", reader.TryReadInt32(out var i32), i32),
                var t when IsFloatFamily(t) => FormatBits(reader.TryReadFloat64(out var f64), f64),
                TokenType.Bool => reader.TryReadBool(out var b)
                    ? "bool=" + (b ? "1" : "0")
                    : "<no bool>",
                _ => reader.Read() ? "read" : "<no read>",
            };
            lines.Add(
                new FusedLine(i, capture.Capture(i, ref reader), read, CountAnswer(ref reader))
            );
        }

        if (reader.Read())
            lines.Add(
                new FusedLine(
                    lines.Count,
                    capture.Capture(lines.Count, ref reader),
                    "unexpected extra token",
                    CountAnswer(ref reader)
                )
            );
        return Drain(lines, ref reader, capture, reader.HasCountedCollections);
    }

    /// <summary>
    /// The chunked-streaming sweep: every prefix of the payload that a chunked reader could hold, until one
    /// of them completes the document (the <c>PipeReader</c> refill strategy, chunk sizes 1/2/3/5/7).
    /// <para>
    /// Each prefix is drained by the <b>reference</b> arm with <c>isFinalBlock</c> telling the reader whether
    /// more bytes may arrive — that is the state a chunk boundary puts it in, and it is where
    /// <c>NeedsMoreData</c> and the rewind rules live. The fused arm is compared per prefix, so a fused read
    /// that ignores a chunk boundary cannot hide behind the final attempt.
    /// </para>
    /// <para>Well-formed payloads only: a malformed one never completes and would loop forever.</para>
    /// </summary>
    public static IEnumerable<(
        int Taken,
        bool IsFinal,
        byte[] Prefix,
        FusedDrain Reference,
        Exception? Failure
    )> ChunkedPrefixes(byte[] payload, int chunkSize, IntAccessor ints = IntAccessor.Int32)
    {
        for (int taken = Math.Min(chunkSize, payload.Length); ; taken += chunkSize)
        {
            int length = Math.Min(taken, payload.Length);
            bool isFinal = taken >= payload.Length;
            var prefix = payload.AsSpan(0, length).ToArray();
            var (reference, failure) = ReferenceUntilFailure(
                prefix,
                ReaderMode.Sequence,
                isFinal,
                ints
            );
            yield return (length, isFinal, prefix, reference, failure);
            if (IsComplete(reference, failure, prefix.Length))
                yield break;
            if (isFinal)
                yield break;
        }
    }

    /// <summary>
    /// "This prefix parses as a whole document": no pending chunk boundary, every container closed, and
    /// every byte consumed — the same completion test the streaming DSL applies before it stops refilling.
    /// </summary>
    public static bool IsComplete(FusedDrain drain, Exception? failure, int length) =>
        failure is null && !drain.NeedsMoreData && drain.Depth == 0 && drain.Consumed == length;

    /// <summary>The token types the reference arm produced — the fused arm's accessor script.</summary>
    public static List<TokenType> ScriptOf(FusedDrain reference) =>
        reference.Lines.ConvertAll(line => line.Snapshot.Type);

    /// <summary>Integer family: the tokens a generated int/long element site reads (spec §5.3).</summary>
    public static bool IsIntFamily(TokenType type) =>
        type
            is TokenType.Int32
                or TokenType.Int64
                or TokenType.UInt8
                or TokenType.UInt16
                or TokenType.UInt32
                or TokenType.UInt64;

    /// <summary>Float family: the tokens a generated double element site reads (spec §5.3).</summary>
    public static bool IsFloatFamily(TokenType type) =>
        type is TokenType.Float32 or TokenType.Float64;

    /// <summary>True for the two tokens that open a collection.</summary>
    public static bool IsCollectionStart(TokenType type) =>
        type is TokenType.ArrayStart or TokenType.ObjectStart;

    /// <summary>
    /// The reference arm's inline answer to "what would <c>TryReadX</c> have returned for this token",
    /// written with the <c>TryGetX</c> family only (never with <see cref="TokenReaderFused"/>).
    /// </summary>
    private static string ReferenceReadResult<TReader>(ref TReader reader, IntAccessor ints)
        where TReader : ITokenReader, allows ref struct
    {
        var type = reader.TokenType;
        if (IsIntFamily(type))
            return ints == IntAccessor.Int64
                ? Format<long>("i64", reader.TryGetInt64(out var i64), i64)
                : Format("i32", reader.TryGetInt32(out var i32), i32);
        if (IsFloatFamily(type))
            return FormatBits(reader.TryGetFloat64(out var f64), f64);
        if (type == TokenType.Bool)
            return reader.TryGetBool(out var b) ? "bool=" + (b ? "1" : "0") : "<no bool>";
        return "read";
    }

    /// <summary>
    /// The fused reader's answer to the count query, dumped so a wrong answer cannot hide. JSON carries no
    /// count, so the only admissible answer for a collection start is
    /// <see cref="CountNotCarried"/> — anything else fails the comparison.
    /// </summary>
    private static string CountAnswer<TReader>(ref TReader reader)
        where TReader : IFusedTokenReader, allows ref struct
    {
        if (!IsCollectionStart(reader.TokenType))
            return "";
        return reader.TryGetCollectionCount(out var count)
            ? "count=" + count.ToString(CultureInfo.InvariantCulture)
            : CountNotCarried;
    }

    private static FusedDrain Drain<TReader, TCapture>(
        List<FusedLine> lines,
        ref TReader reader,
        TCapture capture,
        bool declaresCounts
    )
        where TReader : ITokenReader, allows ref struct
        where TCapture : struct, IJsonFusedCapture<TReader>
    {
        var counted = new List<FusedLine>(lines.Count);
        foreach (var line in lines)
            counted.Add(
                IsCollectionStart(line.Snapshot.Type) && line.Count.Length == 0
                    ? line with
                    {
                        Count = CountNotCarried,
                    }
                    : line
            );
        return new FusedDrain(
            counted,
            reader.NeedsMoreData,
            reader.Depth,
            capture.Consumed(ref reader),
            declaresCounts
        );
    }

    private static string Format<T>(string prefix, bool ok, T value)
        where T : IFormattable =>
        ok
            ? prefix + "=" + value.ToString(null, CultureInfo.InvariantCulture)
            : "<no " + prefix + ">";

    /// <summary>Floats are compared by bit pattern (the same <c>X16</c> form the snapshot uses).</summary>
    private static string FormatBits(bool ok, double value) =>
        ok
            ? "f64="
                + BitConverter
                    .DoubleToInt64Bits(value)
                    .ToString("X16", CultureInfo.InvariantCulture)
            : "<no f64>";
}

/// <summary>
/// Snapshot strategy for the real <see cref="JsonReader"/>: the same capture the reference arm uses, so the
/// two arms' snapshots are the same computation on the same type.
/// </summary>
public struct JsonReaderCapture : JsonFusedTokenDump.IJsonFusedCapture<JsonReader>
{
    public readonly JsonTokenSnapshot Capture(int index, ref JsonReader reader) =>
        JsonSpanTokenDump.Capture(index, ref reader);

    public readonly long Consumed(ref JsonReader reader) => reader.BytesConsumed;
}
