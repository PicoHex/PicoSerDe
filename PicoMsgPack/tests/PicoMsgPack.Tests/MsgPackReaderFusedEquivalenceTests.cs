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
/// <b>State at T3 (deliberate; recorded in the task report).</b> <see cref="MsgPackReader"/> does not
/// implement <see cref="IFusedTokenReader"/> yet — that is T1b, which T3 does not do. Pointing
/// <see cref="MsgPackFusedTokenDump.DrainFused{TReader,TCapture}"/> at <c>MsgPackReader</c> is therefore
/// a compile error today (<c>CS0315</c>, reproduced in the T3 report). Until then the fused arm runs on
/// <see cref="StandInFusedReader"/>: a <see cref="ref struct"/> holding a real <see cref="MsgPackReader"/>
/// and forwarding all sixteen contract members to it — exactly the six-member shape T1b adds to the
/// reader — with an injectable fault on top. When T1b lands, the <see cref="FusedFault.None"/> use
/// collapses to <c>DrainFused&lt;MsgPackReader, …&gt;</c> and the stand-in stays only as the fault injector.
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
            var fused = DrainOnStandIn(payload, reference, FusedFault.None, isFinalBlock, ints);
            await AssertDumpsMatchAsync(
                $"{name} (final={isFinalBlock}, {ints})",
                payload,
                reference,
                fused
            );
        }
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
            var fused = DrainOnStandIn(payload, reference, FusedFault.None);
            await AssertDumpsMatchAsync(name, payload, reference, fused);
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

        string fusedFailure;
        try
        {
            DrainOnStandIn(payload, partial, FusedFault.None);
            fusedFailure = "<no throw>";
        }
        catch (Exception ex)
        {
            fusedFailure = ex.GetType().Name + ": " + ex.Message;
        }

        await Assert
            .That(fusedFailure)
            .IsEqualTo(referenceFailure!.GetType().Name + ": " + referenceFailure.Message)
            .Because(
                $"{name}: failure type and message must match ({partial.Lines.Count} tokens first)"
            );
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

/// <summary>Snapshot strategy for <see cref="StandInFusedReader"/>: snapshot the reader it wraps.</summary>
public struct StandInCapture : MsgPackFusedTokenDump.IMsgPackFusedCapture<StandInFusedReader>
{
    public readonly MsgPackTokenSnapshot Capture(int index, ref StandInFusedReader reader)
    {
        var inner = reader.Snapshot;
        return MsgPackSpanTokenDump.Capture(index, ref inner);
    }
}
