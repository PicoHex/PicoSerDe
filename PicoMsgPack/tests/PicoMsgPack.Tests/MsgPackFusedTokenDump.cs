using System.Runtime.ExceptionServices;

namespace PicoMsgPack.Tests;

/// <summary>
/// Task T3 (fused token read, spec §5.2): the <b>"reference vs fused" dual-walk dump</b>.
/// <para>
/// <b>What it proves.</b> The same payload is walked twice <i>in the same build</i>: once by the
/// reference path (a <see cref="MsgPackReader.Read"/> loop plus the <c>TryGetX</c> family, i.e. the shape
/// the generator emits today) and once by the fused path (one <c>TryReadX</c> call per token, which
/// advances <b>and</b> decodes in a single dispatch). Every token is dumped as
/// <c>index|TokenType|BytesConsumed|Depth|value bytes|all getters|fused-read result|collection count</c>
/// and the two dumps must be identical (spec §5.1).
/// </para>
/// <para>
/// <b>Why the reference arm is written out inline instead of calling <see cref="TokenReaderFused"/>.</b>
/// The reference must not come from the implementation under test (spec §3.2: "正本管样板，内联管对拍").
/// Reusing the reference semantics would compare the fused path with itself.
/// </para>
/// <para>
/// <b>Why the dump carries the fused read's answer and the collection count.</b> A snapshot only sees
/// the reader's <i>state</i>. A fused member that consumed the right token and materialised the right
/// state but <i>returned a wrong value</i> — or a count query that returned the wrong number — would
/// leave every state-only dump identical. So the value each fused read returns, and the answer each
/// count query gives, are dumped next to the reference arm's inline answer for the same token.
/// </para>
/// <para>
/// <b>Where this file lives.</b> Deliberately not inside <see cref="MsgPackSpanTokenDump"/>: that file is
/// compiled by the out-of-tree golden dump harness of task 4e
/// (<c>.superpowers/sdd/perf-mp-readspan/harness/DumpTokens</c>), which must keep compiling against its
/// own older source set. Everything reusable is still reused from there
/// (<see cref="MsgPackSpanTokenDump.Payloads"/>, <see cref="MsgPackSpanTokenDump.Capture"/>, …).
/// </para>
/// </summary>
public static class MsgPackFusedTokenDump
{
    /// <summary>Which fused read the walker uses for the integer family (T6 and T7 are separate overwrites).</summary>
    public enum IntAccessor
    {
        /// <summary>What generated int elements call: <c>TryReadInt32</c>.</summary>
        Int32 = 0,

        /// <summary>What generated long elements call: <c>TryReadInt64</c>.</summary>
        Int64 = 1,
    }

    /// <summary>
    /// The format-specific half of a fused walk: how to snapshot a reader whose concrete type the walker
    /// does not know. Generic so that a deliberately-wrong stand-in reader is walked by the very same
    /// code as the real one — that is what makes the guard's bite provable.
    /// </summary>
    public interface IMsgPackFusedCapture<TReader>
        where TReader : allows ref struct
    {
        MsgPackTokenSnapshot Capture(int index, ref TReader reader);
    }

    /// <summary>One dumped token: the snapshot, what the walker's read returned, and the count query's answer.</summary>
    public readonly record struct FusedLine(
        int Index,
        MsgPackTokenSnapshot Snapshot,
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
    /// A whole walk: the dumped lines, the terminal state, and whether the fused reader claims the
    /// counted-collection capability.
    /// </summary>
    public readonly record struct FusedDrain(
        List<FusedLine> Lines,
        bool NeedsMoreData,
        int Depth,
        bool DeclaresCounts = false
    );

    /// <summary>
    /// The reference arm: a literal <see cref="MsgPackReader.Read"/> loop, the accessor answer read
    /// inline from the <c>TryGetX</c> family, and <see cref="MsgPackSpanTokenDump.Capture"/> for the
    /// snapshot (every getter, the value span, <c>BytesConsumed</c>, <c>Depth</c>).
    /// <para>Throws exactly what the reader throws; <see cref="ReferenceUntilFailure"/> keeps the tokens
    /// walked up to that point, which is the script a fused walk of a malformed payload needs.</para>
    /// </summary>
    public static FusedDrain DrainReference(
        byte[] payload,
        bool isFinalBlock = true,
        IntAccessor ints = IntAccessor.Int32
    )
    {
        var (drain, failure) = ReferenceUntilFailure(payload, isFinalBlock, ints);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return drain;
    }

    /// <summary>
    /// Which input entry a dual walk drives (spec §5.3 "流式分块" row, plan §3.6 D-5 — this matrix
    /// belongs to T6, because T6 is the first task whose member has a fast path of its own to
    /// compare). The fused read is span-only, so the two sequence entries are also the guard that
    /// the fused member really does forward to the reference semantics there instead of taking a
    /// span-shaped shortcut.
    /// </summary>
    public enum DumpEntry
    {
        /// <summary>Span mode, final block: every non-streaming deserialize.</summary>
        Span = 0,

        /// <summary>Span mode, non-final block: the block boundary must report <c>NeedsMoreData</c>.</summary>
        SpanNonFinal = 1,

        /// <summary>Single-segment sequence, final block: the streaming state machine.</summary>
        Sequence = 2,

        /// <summary>Single-segment sequence, non-final block.</summary>
        SequenceNonFinal = 3,
    }

    /// <summary>
    /// The reference arm over the entry the caller asked for. <see cref="DumpEntry"/> has no
    /// <c>Chunked</c> value on purpose: a chunked walk needs a chunk size, so it goes through
    /// <see cref="DrainReferenceChunked"/>.
    /// </summary>
    public static FusedDrain DrainReference(
        byte[] payload,
        DumpEntry entry,
        IntAccessor ints = IntAccessor.Int32
    ) =>
        entry switch
        {
            DumpEntry.Span => DrainReference(payload, isFinalBlock: true, ints),
            DumpEntry.SpanNonFinal => DrainReference(payload, isFinalBlock: false, ints),
            DumpEntry.Sequence => DrainReferenceSequence(payload, isFinalBlock: true, ints),
            DumpEntry.SequenceNonFinal => DrainReferenceSequence(payload, isFinalBlock: false, ints),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "chunked has its own entry point"),
        };

    /// <summary>
    /// The reference arm over a single-segment <see cref="ReadOnlySequence{T}"/>: the shape
    /// <see cref="MsgPackSpanTokenDump.DrainSequence"/> uses, with the fused dump's extra columns.
    /// </summary>
    public static FusedDrain DrainReferenceSequence(
        byte[] payload,
        bool isFinalBlock = true,
        IntAccessor ints = IntAccessor.Int32
    )
    {
        var lines = new List<FusedLine>();
        var reader = new MsgPackReader(new ReadOnlySequence<byte>(payload), isFinalBlock);
        try
        {
            while (reader.Read())
                lines.Add(
                    new FusedLine(
                        lines.Count,
                        MsgPackSpanTokenDump.Capture(lines.Count, ref reader),
                        ReferenceReadResult(ref reader, ints)
                    )
                );
            return new FusedDrain(WithDerivedCounts(lines), reader.NeedsMoreData, reader.Depth);
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// The reference arm over <b>chunked streaming</b>: accumulate one chunk at a time and re-parse
    /// the accumulated prefix from the start, exactly as
    /// <see cref="MsgPackSpanTokenDump.DrainStreaming"/> does, until the document parses completely.
    /// Returns that final prefix together with its dump, so the fused arm can be walked over the very
    /// same bytes and the very same block mode.
    /// </summary>
    public static (FusedDrain Drain, byte[] Prefix, bool IsFinalBlock) DrainReferenceChunked(
        byte[] payload,
        int chunkSize,
        IntAccessor ints = IntAccessor.Int32
    )
    {
        for (int taken = Math.Min(chunkSize, payload.Length); ; taken += chunkSize)
        {
            var isFinalBlock = taken >= payload.Length;
            var prefix = payload.AsSpan(0, Math.Min(taken, payload.Length)).ToArray();
            var lines = new List<FusedLine>();
            var reader = new MsgPackReader(new ReadOnlySequence<byte>(prefix), isFinalBlock);
            bool complete;
            try
            {
                while (reader.Read())
                    lines.Add(
                        new FusedLine(
                            lines.Count,
                            MsgPackSpanTokenDump.Capture(lines.Count, ref reader),
                            ReferenceReadResult(ref reader, ints)
                        )
                    );
                complete =
                    !reader.NeedsMoreData
                    && reader.Depth == 0
                    && reader.BytesConsumed == prefix.Length;
                if (complete)
                    return (
                        new FusedDrain(WithDerivedCounts(lines), reader.NeedsMoreData, reader.Depth),
                        prefix,
                        isFinalBlock
                    );
            }
            finally
            {
                reader.Dispose();
            }
            if (isFinalBlock)
                throw new FormatException(
                    $"streaming never completed the {payload.Length}-byte payload "
                        + $"(chunk {chunkSize}, {lines.Count} tokens)"
                );
        }
    }

    /// <summary>The reference arm on a payload that may throw: the partial dump plus the exception.</summary>
    public static (FusedDrain Drain, Exception? Failure) ReferenceUntilFailure(
        byte[] payload,
        bool isFinalBlock = true,
        IntAccessor ints = IntAccessor.Int32
    )
    {
        var lines = new List<FusedLine>();
        var reader = new MsgPackReader(payload.AsSpan(), isFinalBlock);
        try
        {
            while (reader.Read())
                lines.Add(
                    new FusedLine(
                        lines.Count,
                        MsgPackSpanTokenDump.Capture(lines.Count, ref reader),
                        ReferenceReadResult(ref reader, ints)
                    )
                );
            return (
                new FusedDrain(WithDerivedCounts(lines), reader.NeedsMoreData, reader.Depth),
                null
            );
        }
        catch (Exception ex)
        {
            // The tokens walked before the failure are the whole point: they are the script a fused
            // walk of the same malformed payload has to reproduce.
            return (
                new FusedDrain(WithDerivedCounts(lines), reader.NeedsMoreData, reader.Depth),
                ex
            );
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// The fused arm: for every token the reference arm saw, one fused call decides advance + value.
    /// <para>
    /// The accessor is chosen from the <b>reference</b> token type (spec §3.3 / plan §3.3 item 5) — not
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
        where TCapture : struct, IMsgPackFusedCapture<TReader>
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
                new FusedLine(
                    i,
                    capture.Capture(i, ref reader),
                    read,
                    CountAnswer(reader.TokenType, ref reader)
                )
            );
        }

        if (reader.Read())
            lines.Add(
                new FusedLine(
                    lines.Count,
                    capture.Capture(lines.Count, ref reader),
                    "unexpected extra token",
                    CountAnswer(reader.TokenType, ref reader)
                )
            );
        return new FusedDrain(
            lines,
            reader.NeedsMoreData,
            reader.Depth,
            reader.HasCountedCollections
        );
    }

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

    /// <summary>True for the two tokens that open a counted-or-fallback collection.</summary>
    public static bool IsCollectionStart(TokenType type) =>
        type is TokenType.ArrayStart or TokenType.ObjectStart;

    /// <summary>
    /// The reference arm's inline answer to "what would <c>TryReadX</c> have returned for this token",
    /// written with the <c>TryGetX</c> family only (never with <see cref="TokenReaderFused"/>).
    /// </summary>
    private static string ReferenceReadResult(ref MsgPackReader reader, IntAccessor ints)
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
    /// The fused reader's answer to the count query, dumped so a wrong count cannot hide. A reader that
    /// declares <see cref="IFusedTokenReader.HasCountedCollections"/> may not answer "fallback" here;
    /// that is checked by the caller (see <c>FirstDifference</c> in the test).
    /// </summary>
    private static string CountAnswer<TReader>(TokenType type, ref TReader reader)
        where TReader : IFusedTokenReader, allows ref struct
    {
        if (!IsCollectionStart(type))
            return "";
        return reader.TryGetCollectionCount(out var count)
            ? "count=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "count=fallback";
    }

    /// <summary>
    /// The dump column value for a collection the reference walk ended <i>inside</i>: a malformed payload
    /// can throw before the matching end token, and then no count can be derived from the token stream
    /// alone. Writing <c>0</c> would be a fabricated number, so the entry is marked and the comparison
    /// skips exactly these (see <c>FirstDifference</c> in <c>MsgPackReaderFusedEquivalenceTests</c>).
    /// Every closed collection is still derived and compared.
    /// </summary>
    public const string OpenCount = "count=open";

    /// <summary>
    /// Per-<c>Start</c> element counts derived <b>from the reference token stream alone</b> (arrays: their
    /// children; maps: their key-value pairs). This is the independent side of the count comparison: it
    /// is arithmetic over the dump, not a second call into the reader under test.
    /// </summary>
    private static List<FusedLine> WithDerivedCounts(List<FusedLine> lines)
    {
        var counts = new int[lines.Count];
        var open = new List<(int Index, int Children, bool IsMap)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var type = lines[i].Snapshot.Type;
            if (IsCollectionStart(type))
            {
                if (open.Count > 0)
                    open[^1] = (open[^1].Index, open[^1].Children + 1, open[^1].IsMap);
                open.Add((i, 0, type == TokenType.ObjectStart));
            }
            else if (type is TokenType.ArrayEnd or TokenType.ObjectEnd)
            {
                var top = open[^1];
                open.RemoveAt(open.Count - 1);
                counts[top.Index] = top.IsMap ? top.Children / 2 : top.Children;
            }
            else if (open.Count > 0)
            {
                open[^1] = (open[^1].Index, open[^1].Children + 1, open[^1].IsMap);
            }
        }

        // A collection the walk ended inside is un-derivable: mark it instead of guessing 0.
        foreach (var stillOpen in open)
            counts[stillOpen.Index] = -1;

        var derived = new List<FusedLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            derived.Add(
                IsCollectionStart(lines[i].Snapshot.Type)
                    ? lines[i] with
                    {
                        Count =
                            counts[i] < 0
                                ? OpenCount
                                : "count="
                                    + counts[i]
                                        .ToString(
                                            System.Globalization.CultureInfo.InvariantCulture
                                        ),
                    }
                    : lines[i]
            );
        }
        return derived;
    }

    private static string Format<T>(string prefix, bool ok, T value)
        where T : IFormattable =>
        ok
            ? prefix + "=" + value.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
            : "<no " + prefix + ">";

    /// <summary>Floats are compared by bit pattern (the same <c>X16</c> form the snapshot uses).</summary>
    private static string FormatBits(bool ok, double value) =>
        ok
            ? "f64="
                + BitConverter
                    .DoubleToInt64Bits(value)
                    .ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            : "<no f64>";
}
