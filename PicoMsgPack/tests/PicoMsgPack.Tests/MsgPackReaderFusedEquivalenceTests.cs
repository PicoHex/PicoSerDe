namespace PicoMsgPack.Tests;

/// <summary>
/// Task T3 (fused token read, plan §3.5 / spec §5.2): the <b>"reference vs fused" dual-walk guard</b> for
/// MessagePack, plus the negative control that proves the guard bites.
/// <para>
/// <b>Why the guard exists before any fused read is implemented.</b> A fused read is designed to be
/// behaviour-preserving, so "same behaviour after the overwrite" is already green before the overwrite —
/// it cannot supply the red TDD needs (plan §3.5). The red has to come from the guard itself: make one
/// member return a wrong value and the guard must go red. That is what
/// <see cref="InjectedFault_ReddensTheGuard"/> asserts, permanently, for every payload the fault can
/// reach.
/// </para>
/// <para>
/// <b>Two fused arms, since T5.</b> The <b>real reader arm</b> drives
/// <c>DrainFused&lt;MsgPackReader, RealReaderCapture&gt;</c> over every corpus entry: it is the arm that
/// can see a real overwrite (and, since <see cref="MsgPackReader"/> now declares
/// <see cref="IFusedTokenReader.HasCountedCollections"/>, the arm that turns the dumped collection-count
/// column into a live comparison against counts derived from the reference token stream). That arm is what
/// plan §3.6 requires T5 to switch on — "the stand-in being green does not count".
/// </para>
/// <para>
/// The <b>stand-in arm</b> (<see cref="StandInFusedReader"/>, a <see cref="ref struct"/> holding a real
/// <see cref="MsgPackReader"/> and forwarding all sixteen contract members to it, plus an injectable fault)
/// stays for the negative control: a real reader cannot be made to return a wrong value, and the guard is
/// only worth having if it bites when one does. It also declares <i>no</i> counted collections, so the
/// count column's fallback branch stays covered by the same comparison.
/// </para>
/// <para>
/// <b>Count column, un-derivable entries.</b> A malformed payload can throw before the end token of a
/// collection it opened; the reference token stream alone cannot derive that collection's count. Those
/// entries are marked <see cref="MsgPackFusedTokenDump.OpenCount"/> and skipped; every closed collection is
/// compared against the arithmetic derivation.
/// </para>
/// </summary>
public class MsgPackReaderFusedEquivalenceTests
{
    /// <summary>The named matrix, each payload in both block modes (spec §5.3 "流式分块" row).</summary>
    public static IEnumerable<(string Name, bool IsFinalBlock)> NamedCases() =>
        MsgPackSpanTokenDump
            .Payloads()
            .SelectMany(p => new[] { (p.Name, IsFinalBlock: true), (p.Name, IsFinalBlock: false) });

    public static IEnumerable<string> MalformedNames() =>
        MsgPackSpanTokenDump.MalformedPayloads().Select(p => p.Name);

    /// <summary>Every integer accessor, because T6 and T7 overwrite different members.</summary>
    public static IEnumerable<MsgPackFusedTokenDump.IntAccessor> IntAccessors() =>
        [MsgPackFusedTokenDump.IntAccessor.Int32, MsgPackFusedTokenDump.IntAccessor.Int64];

    public static IEnumerable<FusedFault> Faults() =>
        [
            FusedFault.WrongInt32Value,
            FusedFault.NoAdvanceOnInt32,
            FusedFault.WrongInt64Value,
            FusedFault.WrongBoolValue,
        ];

    /// <summary>The guard, per named payload: reference arm vs fused arm, both block modes, both int accessors.</summary>
    [Test]
    [MethodDataSource(nameof(NamedCases))]
    public async Task NamedPayloads_ReferenceAndFusedDumpsAreIdentical(
        string name,
        bool isFinalBlock
    )
    {
        var payload = MsgPackSpanTokenDump.PayloadByName(name);
        foreach (var ints in IntAccessors())
        {
            var reference = MsgPackFusedTokenDump.DrainReference(payload, isFinalBlock, ints);
            var standIn = DrainOnStandIn(payload, reference, FusedFault.None, isFinalBlock, ints);
            await AssertDumpsMatchAsync(
                $"{name} stand-in arm (final={isFinalBlock}, {ints})",
                payload,
                reference,
                standIn
            );
            var real = DrainOnRealReader(payload, reference, isFinalBlock, ints);
            await AssertDumpsMatchAsync(
                $"{name} real reader arm (final={isFinalBlock}, {ints})",
                payload,
                reference,
                real
            );
        }
    }

    /// <summary>The four entries of spec §5.3's "流式分块" row that need no chunk size (plan §3.6 D-5).</summary>
    public static IEnumerable<(string Label, MsgPackFusedTokenDump.DumpEntry Entry)> EntryCases() =>
        [
            ("SPAN", MsgPackFusedTokenDump.DumpEntry.Span),
            ("SPAN-NONFINAL", MsgPackFusedTokenDump.DumpEntry.SpanNonFinal),
            ("SEQ", MsgPackFusedTokenDump.DumpEntry.Sequence),
            ("SEQ-NONFINAL", MsgPackFusedTokenDump.DumpEntry.SequenceNonFinal),
        ];

    /// <summary>Chunk widths of spec §5.3's "流式分块" row.</summary>
    public static IEnumerable<int> ChunkSizes() => [1, 2, 3, 5, 7];

    /// <summary>
    /// The <b>entry matrix</b> (task T6, spec §5.3, plan §3.6 D-5): the same dual walk over span and
    /// sequence input, in both block modes, on the named corpus and the 2000-payload random corpus.
    /// <para>
    /// The sequence entries are not decoration. <see cref="MsgPackReader.TryReadInt32"/> takes its own
    /// shortcut only in span mode; on a sequence it must forward to the reference semantics. If it ever
    /// took the span path there, <c>_data</c> would be empty while <c>_position</c> advanced, and this
    /// test is what notices — the token stream would stop matching the reference at the first token.
    /// </para>
    /// </summary>
    [Test]
    [MethodDataSource(nameof(EntryCases))]
    public async Task EveryEntry_ReferenceAndFusedDumpsAreIdentical(
        string label,
        MsgPackFusedTokenDump.DumpEntry entry
    )
    {
        var payloads = 0;
        var tokens = 0;
        string firstDifference = "";
        var corpus = MsgPackSpanTokenDump.Payloads().Concat(MsgPackSpanTokenDump.RandomPayloads());
        foreach (var (name, payload) in corpus)
        {
            var reference = MsgPackFusedTokenDump.DrainReference(payload, entry);
            var real = DrainOnRealReaderEntry(payload, reference, entry);
            var difference = FirstDifference(reference, real);
            if (difference is not null && firstDifference.Length == 0)
                firstDifference = $"{name}: {difference}";
            payloads++;
            tokens += reference.Lines.Count;
        }

        await Assert
            .That(firstDifference)
            .IsEmpty()
            .Because($"{label}: every payload must dump identically on both arms");
        await Assert
            .That(payloads)
            .IsEqualTo(MsgPackSpanTokenDump.Payloads().Count() + 2000)
            .Because($"{label}: the whole corpus was walked");
        await Assert.That(tokens).IsGreaterThan(20000).Because($"{label}: not degenerate");
    }

    /// <summary>
    /// The <b>chunked entries</b> (task T6, spec §5.3 "chunk = 1/2/3/5/7"): every payload of at most
    /// 512 bytes is streamed in chunks, the document is re-parsed from the accumulated prefix until it
    /// completes (the shape <c>MsgPackSerializer.DeserializeStreamingCore</c> uses), and the fused arm
    /// walks the completing prefix with the completing block mode. A token consumed at a block boundary
    /// — or a <c>NeedsMoreData</c> that one arm reports and the other does not — shows up here.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ChunkSizes))]
    public async Task ChunkedEntry_ReferenceAndFusedDumpsAreIdentical(int chunk)
    {
        var payloads = 0;
        var tokens = 0;
        string firstDifference = "";
        foreach (var (name, payload) in MsgPackSpanTokenDump.SmallPayloads())
        {
            var (reference, prefix, isFinalBlock) = MsgPackFusedTokenDump.DrainReferenceChunked(
                payload,
                chunk
            );
            var real = DrainOnRealReaderSequence(prefix, reference, isFinalBlock);
            var difference = FirstDifference(reference, real);
            if (difference is not null && firstDifference.Length == 0)
                firstDifference = $"{name}: {difference}";
            payloads++;
            tokens += reference.Lines.Count;
        }

        await Assert
            .That(firstDifference)
            .IsEmpty()
            .Because($"chunk {chunk}: streaming in {chunk}-byte blocks must dump identically");
        await Assert
            .That(payloads)
            .IsEqualTo(MsgPackSpanTokenDump.SmallPayloads().Count())
            .Because($"chunk {chunk}: every small payload was streamed");
        await Assert.That(tokens).IsGreaterThan(100).Because($"chunk {chunk}: not degenerate");
    }

    /// <summary>
    /// The guard across the deterministic random corpus: the part that reaches token combinations the
    /// named matrix does not enumerate.
    /// </summary>
    [Test]
    public async Task RandomWellFormedPayloads_ReferenceAndFusedDumpsAreIdentical()
    {
        var payloads = 0;
        var tokens = 0;
        foreach (var (name, payload) in MsgPackSpanTokenDump.RandomPayloads())
        {
            var reference = MsgPackFusedTokenDump.DrainReference(payload);
            var standIn = DrainOnStandIn(payload, reference, FusedFault.None);
            await AssertDumpsMatchAsync(name + " stand-in arm", payload, reference, standIn);
            var real = DrainOnRealReader(payload, reference);
            await AssertDumpsMatchAsync(name + " real reader arm", payload, reference, real);
            payloads++;
            tokens += reference.Lines.Count;
        }

        await Assert.That(payloads).IsEqualTo(2000).Because("the corpus is fixed-size");
        await Assert.That(tokens).IsGreaterThan(2000).Because("the corpus is not degenerate");
    }

    /// <summary>
    /// Failure paths: a malformed payload must fail with the same exception type <b>and</b> message on
    /// both arms (spec §5.1 lists exception type and message as part of equivalence) <i>after</i> the
    /// same partial token stream — so the fused arm is walked over the tokens the reference arm walked
    /// before it threw, not over an empty script.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(MalformedNames))]
    public async Task MalformedPayloads_FailIdentically(string name)
    {
        var payload = MsgPackSpanTokenDump.MalformedPayloads().First(p => p.Name == name).Payload;
        var (partial, referenceFailure) = MsgPackFusedTokenDump.ReferenceUntilFailure(payload);

        await Assert
            .That(referenceFailure)
            .IsNotNull()
            .Because($"{name}: the corpus entry must actually be malformed");

        var expected = referenceFailure!.GetType().Name + ": " + referenceFailure.Message;
        var because =
            $"{name}: failure type and message must match ({partial.Lines.Count} tokens first)";

        string standInFailure;
        try
        {
            DrainOnStandIn(payload, partial, FusedFault.None);
            standInFailure = "<no throw>";
        }
        catch (Exception ex)
        {
            standInFailure = ex.GetType().Name + ": " + ex.Message;
        }

        await Assert.That(standInFailure).IsEqualTo(expected).Because("stand-in arm — " + because);

        string realFailure;
        try
        {
            DrainOnRealReader(payload, partial);
            realFailure = "<no throw>";
        }
        catch (Exception ex)
        {
            realFailure = ex.GetType().Name + ": " + ex.Message;
        }

        await Assert.That(realFailure).IsEqualTo(expected).Because("real reader arm — " + because);
    }

    /// <summary>
    /// Pins the reference arm to the token stream the reader already produces
    /// (<see cref="MsgPackSpanTokenDump.DrainSpan"/>): if the reference arm drifts, every comparison in
    /// this file would be comparing the fused path with something that is not the reference.
    /// </summary>
    [Test]
    public async Task ReferenceArm_MatchesTheEstablishedSpanDrain()
    {
        var compared = 0;
        foreach (var (name, payload) in MsgPackSpanTokenDump.Payloads())
        {
            var expected = MsgPackSpanTokenDump.DrainSpan(payload);
            var actual = MsgPackFusedTokenDump.DrainReference(payload);
            await Assert.That(actual.Lines.Count).IsEqualTo(expected.Count).Because(name);
            for (int i = 0; i < expected.Count; i++)
                await Assert
                    .That(actual.Lines[i].Snapshot.ToString())
                    .IsEqualTo(expected[i].ToString())
                    .Because($"{name}: token {i}");
            compared++;
        }

        await Assert.That(compared).IsEqualTo(MsgPackSpanTokenDump.Payloads().Count());
    }

    /// <summary>
    /// The corpus must actually carry the families the guard is supposed to pin down (plan §3.5, T3 step
    /// 3): three timestamp widths, counted arrays and maps at every header width, nesting, empty and
    /// single-element collections, and the malformed set including unknown tag <c>0xC1</c> and depth
    /// overflow.
    /// </summary>
    [Test]
    public async Task Corpus_CoversTheRequiredTokenFamilies()
    {
        var named = MsgPackSpanTokenDump.Payloads().Select(p => p.Name).ToHashSet();
        string[] required =
        [
            "timestamp32",
            "timestamp64",
            "timestamp96",
            "fixarray-empty",
            "fixarray-3",
            "fixarray-15-nil",
            "array16-300-nil",
            "array32-65536-nil",
            "fixmap-empty",
            "fixmap-2",
            "map16-20",
            "map32-65536-nil",
            "nested-map-array",
            "nested-array-depth-60",
        ];
        await Assert
            .That(string.Join(",", required.Where(one => !named.Contains(one))))
            .IsEqualTo("")
            .Because("named payload matrix");

        var malformed = MsgPackSpanTokenDump.MalformedPayloads().Select(p => p.Name).ToHashSet();
        string[] requiredMalformed =
        [
            "unknown-byte-c1",
            "trailing-garbage",
            "truncated-int32",
            "truncated-length-bytes-str16",
            "hostile-bin32-length",
            "truncated-fixext-tag",
            "truncated-str8-body",
            "depth-overflow-65",
        ];
        await Assert
            .That(string.Join(",", requiredMalformed.Where(one => !malformed.Contains(one))))
            .IsEqualTo("")
            .Because("malformed corpus");

        await Assert.That(MsgPackSpanTokenDump.RandomPayloads().Count()).IsEqualTo(2000);
    }

    /// <summary>
    /// <b>The task's real acceptance criterion</b> (plan §3.5): the guard is worth having only if a wrong
    /// member turns it red. One member of the stand-in is deliberately wrong; for every payload whose
    /// dump can show that member's answer, the comparison must find a difference. A payload the guard
    /// lets through with a wrong member is a hole in the guard and fails this test by name.
    /// <para>
    /// The four faults cover the four ways a fused overwrite can go wrong: a wrong <i>value</i> with
    /// correct state (only the dumped fused-read column can catch it, and the state half of the dump
    /// stays identical), a missed <i>advance</i> (the token stream itself diverges), and a wrong member
    /// <i>routing</i> for <c>TryReadInt64</c> and <c>TryReadBool</c>.
    /// </para>
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Faults))]
    public async Task InjectedFault_ReddensTheGuard(FusedFault fault)
    {
        var touched = 0;
        var missed = new List<string>();
        var firstDifference = "";

        var corpus = MsgPackSpanTokenDump.Payloads().Concat(MsgPackSpanTokenDump.RandomPayloads());
        foreach (var (name, payload) in corpus)
        {
            var ints =
                fault == FusedFault.WrongInt64Value
                    ? MsgPackFusedTokenDump.IntAccessor.Int64
                    : MsgPackFusedTokenDump.IntAccessor.Int32;
            var reference = MsgPackFusedTokenDump.DrainReference(payload, ints: ints);
            if (!reference.Lines.Exists(line => fault.ManifestsIn(line)))
                continue;
            touched++;

            var faulty = DrainOnStandIn(payload, reference, fault, ints: ints);
            var difference = FirstDifference(reference, faulty);
            if (difference is null)
            {
                missed.Add(name);
            }
            else if (firstDifference.Length == 0)
            {
                firstDifference = name + ": " + difference;
            }
        }

        await Assert
            .That(touched)
            .IsGreaterThan(100)
            .Because($"{fault}: the corpus must reach this fault");
        await Assert
            .That(string.Join(",", missed))
            .IsEqualTo("")
            .Because($"{fault}: payloads the guard let through with a wrong member");
        await Assert
            .That(firstDifference)
            .IsNotEmpty()
            .Because($"{fault}: the first difference the guard reported");
    }

    // ── helpers ──

    private static MsgPackFusedTokenDump.FusedDrain DrainOnStandIn(
        byte[] payload,
        MsgPackFusedTokenDump.FusedDrain reference,
        FusedFault fault,
        bool isFinalBlock = true,
        MsgPackFusedTokenDump.IntAccessor ints = MsgPackFusedTokenDump.IntAccessor.Int32
    )
    {
        // Not a `using` variable: DrainFused takes the stand-in by ref, which the language forbids for
        // using-locals.
        var standIn = new StandInFusedReader(
            new MsgPackReader(payload.AsSpan(), isFinalBlock),
            fault
        );
        try
        {
            return MsgPackFusedTokenDump.DrainFused<StandInFusedReader, StandInCapture>(
                ref standIn,
                MsgPackFusedTokenDump.ScriptOf(reference),
                default,
                ints
            );
        }
        finally
        {
            standIn.Dispose();
        }
    }

    /// <summary>
    /// The fused arm on the <b>real</b> reader (plan §3.6: T5 must make this arm live, and "the stand-in
    /// being green does not count"). Every fused call goes through <see cref="MsgPackReader"/>'s own
    /// <see cref="IFusedTokenReader"/> members, so a wrong overwrite — and, since T5, a wrong collection
    /// count — shows up in the dump.
    /// </summary>
    private static MsgPackFusedTokenDump.FusedDrain DrainOnRealReader(
        byte[] payload,
        MsgPackFusedTokenDump.FusedDrain reference,
        bool isFinalBlock = true,
        MsgPackFusedTokenDump.IntAccessor ints = MsgPackFusedTokenDump.IntAccessor.Int32
    )
    {
        var reader = new MsgPackReader(payload.AsSpan(), isFinalBlock);
        try
        {
            return MsgPackFusedTokenDump.DrainFused<MsgPackReader, RealReaderCapture>(
                ref reader,
                MsgPackFusedTokenDump.ScriptOf(reference),
                default,
                ints
            );
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// The fused arm over the input entry the caller asked for (span or sequence, final or non-final).
    /// A sequence reader is a different state machine, so this is the only way to prove the fused member
    /// forwards there instead of assuming span-shaped state (spec §2.2 N4) — with an empty <c>_data</c>,
    /// a span-shaped fast path would walk off the end of the buffer at the first token.
    /// </summary>
    private static MsgPackFusedTokenDump.FusedDrain DrainOnRealReaderEntry(
        byte[] payload,
        MsgPackFusedTokenDump.FusedDrain reference,
        MsgPackFusedTokenDump.DumpEntry entry
    ) =>
        entry
        is MsgPackFusedTokenDump.DumpEntry.Sequence
            or MsgPackFusedTokenDump.DumpEntry.SequenceNonFinal
            ? DrainOnRealReaderSequence(
                payload,
                reference,
                entry == MsgPackFusedTokenDump.DumpEntry.Sequence
            )
            : DrainOnRealReader(
                payload,
                reference,
                entry == MsgPackFusedTokenDump.DumpEntry.Span
            );

    /// <summary>The fused arm over a single-segment sequence: the streaming entry.</summary>
    private static MsgPackFusedTokenDump.FusedDrain DrainOnRealReaderSequence(
        byte[] payload,
        MsgPackFusedTokenDump.FusedDrain reference,
        bool isFinalBlock
    )
    {
        var reader = new MsgPackReader(new ReadOnlySequence<byte>(payload), isFinalBlock);
        try
        {
            return MsgPackFusedTokenDump.DrainFused<MsgPackReader, RealReaderCapture>(
                ref reader,
                MsgPackFusedTokenDump.ScriptOf(reference),
                default
            );
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static async Task AssertDumpsMatchAsync(
        string label,
        byte[] payload,
        MsgPackFusedTokenDump.FusedDrain expected,
        MsgPackFusedTokenDump.FusedDrain actual
    )
    {
        await Assert
            .That(FirstDifference(expected, actual))
            .IsNull()
            .Because($"{label}: the two arms diverged on a {payload.Length}-byte payload");
    }

    /// <summary>
    /// <b>Plan §3.6 evidence</b>: the real-reader arm is not decoration — it walks the named corpus through
    /// <see cref="MsgPackReader"/>'s own fused members and compares a live collection count on every start
    /// token against the count derived from the reference token stream. Both numbers come from different
    /// places (the reader's element stack vs. arithmetic over the dump), so this is not self-certification.
    /// </summary>
    [Test]
    public async Task RealReaderArm_ComparesCountsOnTheRealReader()
    {
        var payloads = 0;
        var countComparisons = 0;
        var problems = new List<string>();
        foreach (var (name, payload) in MsgPackSpanTokenDump.Payloads())
        {
            var reference = MsgPackFusedTokenDump.DrainReference(payload);
            var real = DrainOnRealReader(payload, reference);
            await AssertDumpsMatchAsync(name + " real reader arm", payload, reference, real);

            if (!real.DeclaresCounts)
                problems.Add($"{name}: the real reader did not declare counted collections");
            foreach (var line in real.Lines)
            {
                if (!MsgPackFusedTokenDump.IsCollectionStart(line.Snapshot.Type))
                    continue;
                if (
                    line.Count == "count=fallback"
                    || !line.Count.StartsWith("count=", StringComparison.Ordinal)
                )
                    problems.Add($"{name}: start token {line.Index} answered <{line.Count}>");
                countComparisons++;
            }
            payloads++;
        }

        await Assert.That(string.Join("; ", problems)).IsEqualTo("").Because("real reader arm");
        await Assert
            .That(payloads)
            .IsEqualTo(MsgPackSpanTokenDump.Payloads().Count())
            .Because("every named payload was walked by the real reader arm");
        await Assert
            .That(countComparisons)
            .IsGreaterThan(20)
            .Because("the named corpus carries arrays and maps at every header width");
    }

    /// <summary>
    /// The whole comparison in one place, returning the <b>first</b> difference (or null): token count,
    /// every dumped line, the terminal state, then the two capability rules — a reader that declares
    /// <see cref="IFusedTokenReader.HasCountedCollections"/> may not fall back to a <c>Read</c>-driven
    /// loop, and a count it does report must equal the count derived from the reference token stream.
    /// Reporting the first difference keeps the guard O(n) instead of paying an assertion per token on
    /// 65 536-token payloads.
    /// </summary>
    private static string? FirstDifference(
        MsgPackFusedTokenDump.FusedDrain expected,
        MsgPackFusedTokenDump.FusedDrain actual
    )
    {
        if (actual.Lines.Count != expected.Lines.Count)
            return $"token count {expected.Lines.Count} vs {actual.Lines.Count}";
        for (int i = 0; i < expected.Lines.Count; i++)
        {
            var want = expected.Lines[i];
            var got = actual.Lines[i];
            if (got.Snapshot.ToString() != want.Snapshot.ToString())
                return $"token {i}: style/state <{want.Snapshot}> vs <{got.Snapshot}>";
            if (got.Read != want.Read)
                return $"token {i}: fused read <{want.Read}> vs <{got.Read}>";
            if (want.Count == MsgPackFusedTokenDump.OpenCount)
            {
                // The reference walk ended inside this collection (malformed payload): there is no
                // independent count to compare against. The reader's answer is still required to be an
                // answer when it declares counted collections (the fallback branch below).
                if (got.Count == "count=fallback" && actual.DeclaresCounts)
                    return $"token {i}: declares counted collections but answered fallback";
                continue;
            }
            if (got.Count == "count=fallback")
            {
                if (actual.DeclaresCounts)
                    return $"token {i}: declares counted collections but answered fallback";
                continue;
            }
            if (got.Count != want.Count)
                return $"token {i}: collection count <{want.Count}> vs <{got.Count}>";
        }
        if (actual.NeedsMoreData != expected.NeedsMoreData)
            return $"NeedsMoreData {expected.NeedsMoreData} vs {actual.NeedsMoreData}";
        if (actual.Depth != expected.Depth)
            return $"Depth {expected.Depth} vs {actual.Depth}";
        return null;
    }
}

/// <summary>The way a fused overwrite can be wrong; used to prove the guard detects each one.</summary>
public enum FusedFault
{
    /// <summary>No fault: the stand-in forwards every member to the real reader.</summary>
    None = 0,

    /// <summary><c>TryReadInt32</c> advances and materialises correctly but reports a wrong value.</summary>
    WrongInt32Value = 1,

    /// <summary><c>TryReadInt32</c> reports failure without advancing: the token stream itself diverges.</summary>
    NoAdvanceOnInt32 = 2,

    /// <summary><c>TryReadInt64</c> reports a wrong value.</summary>
    WrongInt64Value = 3,

    /// <summary><c>TryReadBool</c> reports the negated value.</summary>
    WrongBoolValue = 4,
}

/// <summary>Which dumped lines a fault is supposed to change.</summary>
public static class FusedFaultExtensions
{
    /// <summary>
    /// A fault is injected into one member, so it can only show up where that member's answer is
    /// observable: a wrong <c>TryReadInt32</c> value only exists on tokens that decode as an
    /// <see cref="int"/> (a <c>uint64-max</c> token reports <c>&lt;no i32&gt;</c> on both arms, fault or
    /// not), a missed advance exists on every integer-family token, and so on.
    /// </summary>
    public static bool ManifestsIn(this FusedFault fault, MsgPackFusedTokenDump.FusedLine line) =>
        fault switch
        {
            FusedFault.WrongInt32Value => line.Read.StartsWith("i32=", StringComparison.Ordinal),
            FusedFault.WrongInt64Value => line.Read.StartsWith("i64=", StringComparison.Ordinal),
            FusedFault.NoAdvanceOnInt32 => MsgPackFusedTokenDump.IsIntFamily(line.Snapshot.Type),
            FusedFault.WrongBoolValue => line.Read.StartsWith("bool=", StringComparison.Ordinal),
            _ => false,
        };
}

/// <summary>
/// A <see cref="ref struct"/> stand-in for a fused <see cref="MsgPackReader"/>: it holds a real reader and
/// forwards all sixteen contract members to it, with an injectable fault on one of the fused reads. The
/// fused reads forward to <see cref="TokenReaderFused"/>, i.e. the literal
/// <c>Read() &amp;&amp; TryGetX()</c> combination — the same one-line body T1b adds to the readers.
/// </summary>
public ref struct StandInFusedReader : IFusedTokenReader
{
    private MsgPackReader _inner;
    private readonly FusedFault _fault;

    public StandInFusedReader(MsgPackReader inner, FusedFault fault = FusedFault.None)
    {
        _inner = inner;
        _fault = fault;
    }

    /// <summary>A copy of the real reader, taken when the guard snapshots a token.</summary>
    public readonly MsgPackReader Snapshot => _inner;

    // ── ITokenReader: pure delegation ──
    public bool Read() => _inner.Read();

    public readonly TokenType TokenType => _inner.TokenType;
    public readonly bool NeedsMoreData => _inner.NeedsMoreData;
    public readonly int Depth => _inner.Depth;

    public bool TryGetInt32(out int value) => _inner.TryGetInt32(out value);

    public bool TryGetInt64(out long value) => _inner.TryGetInt64(out value);

    public bool TryGetFloat64(out double value) => _inner.TryGetFloat64(out value);

    public bool TryGetBool(out bool value) => _inner.TryGetBool(out value);

    public bool TrySkip() => _inner.TrySkip();

    // ── IFusedTokenReader: a MessagePack scalar walk needs no count ──
    public readonly bool HasCountedCollections => false;

    public readonly bool TryGetCollectionCount(out int count)
    {
        count = 0;
        return false;
    }

    // ── IFusedTokenReader: the reference semantics, with the fault applied ──
    public bool TryReadInt32(out int value)
    {
        if (_fault == FusedFault.NoAdvanceOnInt32)
        {
            value = 0;
            return false;
        }
        bool ok = TokenReaderFused.TryReadInt32(ref _inner, out value);
        if (ok && _fault == FusedFault.WrongInt32Value)
            value++;
        return ok;
    }

    public bool TryReadInt64(out long value)
    {
        bool ok = TokenReaderFused.TryReadInt64(ref _inner, out value);
        if (ok && _fault == FusedFault.WrongInt64Value)
            value++;
        return ok;
    }

    public bool TryReadFloat64(out double value) =>
        TokenReaderFused.TryReadFloat64(ref _inner, out value);

    public bool TryReadBool(out bool value)
    {
        bool ok = TokenReaderFused.TryReadBool(ref _inner, out value);
        if (ok && _fault == FusedFault.WrongBoolValue)
            value = !value;
        return ok;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Snapshot strategy for the <b>real</b> <see cref="MsgPackReader"/>: the same capture the reference arm
/// uses, so the two arms' snapshots are the same computation on the same type.
/// </summary>
public struct RealReaderCapture : MsgPackFusedTokenDump.IMsgPackFusedCapture<MsgPackReader>
{
    public readonly MsgPackTokenSnapshot Capture(int index, ref MsgPackReader reader) =>
        MsgPackSpanTokenDump.Capture(index, ref reader);
}

/// <summary>Snapshot strategy for <see cref="StandInFusedReader"/>: snapshot the reader it wraps.</summary>
public struct StandInCapture : MsgPackFusedTokenDump.IMsgPackFusedCapture<StandInFusedReader>
{
    public readonly MsgPackTokenSnapshot Capture(int index, ref StandInFusedReader reader)
    {
        var inner = reader.Snapshot;
        return MsgPackSpanTokenDump.Capture(index, ref inner);
    }
}
