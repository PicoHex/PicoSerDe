namespace PicoJetson.Tests;

/// <summary>
/// Task T12 (fused token read, plan §3.5 / spec §5.2): the <b>"reference vs fused" dual-walk guard</b> for
/// JSON, plus the negative control that proves the guard bites.
/// <para>
/// <b>Why the guard exists before any JSON fused read is implemented.</b> JSON's fused members are the
/// one-line forwards T1b added (spec §3.3 marks only <c>TryReadInt32</c> for a T14 override), so "same
/// behaviour as the reference" is already green and cannot supply the red TDD needs (plan §3.5). The red
/// has to come from the guard itself: make one member return a wrong value and the guard must go red. That
/// is what <see cref="InjectedFault_ReddensTheGuard"/> asserts, permanently, for every payload the fault
/// can reach. What the guard proves today is that the forward channel is wired and that the dump sees what
/// the fused path answers; the real equivalence proof lands on T14's override.
/// </para>
/// <para>
/// <b>Two fused arms.</b> The <b>real reader arm</b> drives
/// <c>DrainFused&lt;JsonReader, JsonReaderCapture&gt;</c> over every corpus entry through
/// <see cref="JsonReader"/>'s own <see cref="IFusedTokenReader"/> members (plan §3.6 D-2: the contract
/// arm must call the interface, never the reference class, or a T14 override would be invisible here).
/// The <b>stand-in arm</b> (<see cref="StandInJsonReader"/>, a <see cref="ref struct"/> holding a real
/// <see cref="JsonReader"/>, forwarding all contract members and carrying an injectable fault) exists for
/// the negative control: a real reader cannot be made to return a wrong value, and the guard is only worth
/// having if it bites when one does.
/// </para>
/// </summary>
public class JsonReaderFusedEquivalenceTests
{
    /// <summary>The named matrix, each payload in all four reader modes (spec §5.3 "流式分块" row).</summary>
    public static IEnumerable<(
        string Name,
        JsonFusedTokenDump.ReaderMode Mode,
        bool IsFinalBlock
    )> NamedCases() =>
        JsonSpanTokenDump
            .Payloads()
            .SelectMany(p =>
                new[]
                {
                    (p.Name, JsonFusedTokenDump.ReaderMode.Span, true),
                    (p.Name, JsonFusedTokenDump.ReaderMode.Span, false),
                    (p.Name, JsonFusedTokenDump.ReaderMode.Sequence, true),
                    (p.Name, JsonFusedTokenDump.ReaderMode.Sequence, false),
                }
            );

    /// <summary>Every integer accessor, because T14 overwrites one member and leaves the other forwarded.</summary>
    public static IEnumerable<JsonFusedTokenDump.IntAccessor> IntAccessors() =>
        [JsonFusedTokenDump.IntAccessor.Int32, JsonFusedTokenDump.IntAccessor.Int64];

    public static IEnumerable<string> MalformedNames() =>
        JsonSpanTokenDump.MalformedPayloads().Select(m => m.Name);

    /// <summary>The chunk sizes spec §5.2 requires, over the payloads short enough to slice that thinly.</summary>
    public static IEnumerable<(string Name, int Chunk)> ChunkedCases() =>
        JsonSpanTokenDump
            .SmallPayloads()
            .SelectMany(p => new[] { 1, 2, 3, 5, 7 }.Select(c => (p.Name, c)));

    public static IEnumerable<FusedFault> Faults() =>
        [
            FusedFault.WrongInt32Value,
            FusedFault.NoAdvanceOnInt32,
            FusedFault.WrongInt64Value,
            FusedFault.WrongBoolValue,
        ];

    /// <summary>The guard, per named payload: reference arm vs fused arm, every reader mode, both accessors.</summary>
    [Test]
    [MethodDataSource(nameof(NamedCases))]
    public async Task NamedPayloads_ReferenceAndFusedDumpsAreIdentical(
        string name,
        JsonFusedTokenDump.ReaderMode mode,
        bool isFinalBlock
    )
    {
        var payload = JsonSpanTokenDump.PayloadByName(name);
        foreach (var ints in IntAccessors())
        {
            var reference = JsonFusedTokenDump.DrainReference(payload, mode, isFinalBlock, ints);
            var label = $"{name} ({mode}, final={isFinalBlock}, {ints})";

            var standIn = DrainOnStandIn(
                payload,
                reference,
                FusedFault.None,
                mode,
                isFinalBlock,
                ints
            );
            await Assert
                .That(FirstDifference(reference, standIn))
                .IsNull()
                .Because(label + ": stand-in arm diverged");

            var real = DrainOnRealReader(payload, reference, mode, isFinalBlock, ints);
            await Assert
                .That(FirstDifference(reference, real))
                .IsNull()
                .Because(label + ": real reader arm diverged");
        }
    }

    /// <summary>
    /// The guard across the deterministic random corpus: the part that reaches token and nesting
    /// combinations the named matrix does not enumerate.
    /// </summary>
    [Test]
    public async Task RandomWellFormedPayloads_ReferenceAndFusedDumpsAreIdentical()
    {
        var payloads = 0;
        var tokens = 0;
        foreach (var (name, payload) in JsonSpanTokenDump.RandomPayloads())
        {
            foreach (
                var mode in new[]
                {
                    JsonFusedTokenDump.ReaderMode.Span,
                    JsonFusedTokenDump.ReaderMode.Sequence,
                }
            )
            {
                var reference = JsonFusedTokenDump.DrainReference(payload, mode);
                await Assert
                    .That(
                        FirstDifference(
                            reference,
                            DrainOnStandIn(payload, reference, FusedFault.None, mode)
                        )
                    )
                    .IsNull()
                    .Because($"{name} ({mode}): stand-in arm diverged");
                await Assert
                    .That(FirstDifference(reference, DrainOnRealReader(payload, reference, mode)))
                    .IsNull()
                    .Because($"{name} ({mode}): real reader arm diverged");
                tokens += reference.Lines.Count;
            }
            payloads++;
        }

        await Assert.That(payloads).IsEqualTo(300).Because("the corpus is fixed-size");
        await Assert.That(tokens).IsGreaterThan(600).Because("the corpus is not degenerate");
    }

    /// <summary>
    /// Chunked streaming (spec §5.2 "流式分块"): every chunk-aligned prefix the PipeReader strategy can hold
    /// — with <c>isFinalBlock</c> telling the reader whether more bytes may arrive — is walked by both arms
    /// and compared, terminal state included. Comparing only the completing attempt would let a fused read
    /// that ignores a chunk boundary pass.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ChunkedCases))]
    public async Task ChunkedStreaming_ReferenceAndFusedDumpsAreIdentical(string name, int chunk)
    {
        var payload = JsonSpanTokenDump.PayloadByName(name);
        var attempts = 0;
        var completed = false;
        foreach (
            var (_, isFinal, prefix, reference, failure) in JsonFusedTokenDump.ChunkedPrefixes(
                payload,
                chunk
            )
        )
        {
            var label = $"{name} chunk={chunk} prefix={prefix.Length} final={isFinal}";
            var expected = JsonFusedTokenDump.ExecutionString(failure);

            // The drain is caught, the assertion is not: a guard assertion swallowed by the fault-catching
            // `catch` would look like a pass.
            JsonFusedTokenDump.FusedDrain? standIn = null;
            var standInFailure = JsonFusedTokenDump.ExecutionString(null);
            try
            {
                standIn = DrainOnStandIn(
                    prefix,
                    reference,
                    FusedFault.None,
                    JsonFusedTokenDump.ReaderMode.Sequence,
                    isFinal
                );
            }
            catch (Exception ex)
            {
                standInFailure = JsonFusedTokenDump.ExecutionString(ex);
            }
            if (standIn is { } standInDrain)
                await Assert
                    .That(FirstDifference(reference, standInDrain))
                    .IsNull()
                    .Because(label + ": stand-in arm diverged");
            await Assert
                .That(standInFailure)
                .IsEqualTo(expected)
                .Because(label + ": stand-in arm failure");

            JsonFusedTokenDump.FusedDrain? real = null;
            var realFailure = JsonFusedTokenDump.ExecutionString(null);
            try
            {
                real = DrainOnRealReader(
                    prefix,
                    reference,
                    JsonFusedTokenDump.ReaderMode.Sequence,
                    isFinal
                );
            }
            catch (Exception ex)
            {
                realFailure = JsonFusedTokenDump.ExecutionString(ex);
            }
            if (real is { } realDrain)
                await Assert
                    .That(FirstDifference(reference, realDrain))
                    .IsNull()
                    .Because(label + ": real reader arm diverged");
            await Assert
                .That(realFailure)
                .IsEqualTo(expected)
                .Because(label + ": real reader arm failure");

            attempts++;
            completed |= JsonFusedTokenDump.IsComplete(reference, failure, prefix.Length);
        }

        await Assert.That(attempts).IsGreaterThan(0).Because(name);
        await Assert
            .That(completed)
            .IsTrue()
            .Because($"{name}: a well-formed payload must complete at some chunk boundary");
    }

    /// <summary>
    /// Failure paths: a malformed or truncated payload must fail with the same exception type <b>and</b>
    /// message on both arms (spec §5.1 lists exception type and message as part of equivalence), after the
    /// same partial token stream — the fused arm is walked over the tokens the reference arm walked before
    /// it threw, not over an empty script.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(MalformedNames))]
    public async Task MalformedPayloads_FailIdentically(string name)
    {
        var entry = JsonSpanTokenDump.MalformedPayloads().First(m => m.Name == name);
        foreach (
            var mode in new[]
            {
                JsonFusedTokenDump.ReaderMode.Span,
                JsonFusedTokenDump.ReaderMode.Sequence,
            }
        )
        {
            foreach (var isFinalBlock in new[] { true, false })
            {
                var (partial, referenceFailure) = JsonFusedTokenDump.ReferenceUntilFailure(
                    entry.Payload,
                    mode,
                    isFinalBlock
                );
                if (isFinalBlock && mode == JsonFusedTokenDump.ReaderMode.Span)
                    await Assert
                        .That(referenceFailure is not null)
                        .IsEqualTo(entry.ThrowsInSpan)
                        .Because(
                            $"{name}: the corpus flag must record what the strict span path actually does"
                        );

                var expected = JsonFusedTokenDump.ExecutionString(referenceFailure);
                var because =
                    $"{name} ({mode}, final={isFinalBlock}): failure type and message must match "
                    + $"({partial.Lines.Count} tokens first)";

                var standInFailure = JsonFusedTokenDump.ExecutionString(null);
                try
                {
                    DrainOnStandIn(entry.Payload, partial, FusedFault.None, mode, isFinalBlock);
                }
                catch (Exception ex)
                {
                    standInFailure = JsonFusedTokenDump.ExecutionString(ex);
                }
                await Assert
                    .That(standInFailure)
                    .IsEqualTo(expected)
                    .Because("stand-in arm — " + because);

                var realFailure = JsonFusedTokenDump.ExecutionString(null);
                try
                {
                    DrainOnRealReader(entry.Payload, partial, mode, isFinalBlock);
                }
                catch (Exception ex)
                {
                    realFailure = JsonFusedTokenDump.ExecutionString(ex);
                }
                await Assert
                    .That(realFailure)
                    .IsEqualTo(expected)
                    .Because("real reader arm — " + because);
            }
        }
    }

    /// <summary>
    /// Pins the reference arm to the token stream the dump fixture itself produces: if the reference arm
    /// drifts, every comparison in this file would be comparing the fused path with something that is not
    /// the reference.
    /// </summary>
    [Test]
    public async Task ReferenceArm_MatchesTheEstablishedDrains()
    {
        var compared = 0;
        foreach (var (name, payload) in JsonSpanTokenDump.Payloads())
        {
            await AssertPinnedAsync(
                name + " span",
                JsonSpanTokenDump.DrainSpan(payload),
                payload,
                JsonFusedTokenDump.ReaderMode.Span
            );
            await AssertPinnedAsync(
                name + " seq",
                JsonSpanTokenDump.DrainSequence(payload),
                payload,
                JsonFusedTokenDump.ReaderMode.Sequence
            );
            compared++;
        }

        await Assert.That(compared).IsEqualTo(JsonSpanTokenDump.Payloads().Count());
    }

    /// <summary>
    /// The corpus must actually carry the families the guard is supposed to pin down (plan T12 step 2):
    /// every JSON token type, both integer boundaries, escapes and surrogate pairs, empty and single-element
    /// containers, whitespace-heavy layout, and the malformed set.
    /// </summary>
    [Test]
    public async Task Corpus_CoversTheRequiredJsonTokenFamilies()
    {
        var named = JsonSpanTokenDump.Payloads().Select(p => p.Name).ToHashSet();
        string[] required =
        [
            "null",
            "bool-true",
            "bool-false",
            "int-negative",
            "int32-min",
            "int32-max",
            "int64-min",
            "int64-max",
            "beyond-uint64",
            "float-simple",
            "float-neg-zero",
            "float-exponent-plus",
            "string-escapes",
            "string-unicode-escapes",
            "string-surrogate-pair",
            "string-raw-emoji",
            "object-empty",
            "object-nested-3",
            "array-empty",
            "array-one",
            "array-mixed-families",
            "whitespace-everywhere",
            "nested-arrays-depth-32",
            "flat-object-32-members",
        ];
        await Assert
            .That(string.Join(",", required.Where(one => !named.Contains(one))))
            .IsEqualTo("")
            .Because("named payload matrix");

        // Every TokenType the JSON reader can emit must appear somewhere in the named corpus.
        var emitted = JsonSpanTokenDump
            .Payloads()
            .SelectMany(p => JsonSpanTokenDump.DrainSpan(p.Payload))
            .Select(t => t.Type)
            .ToHashSet();
        TokenType[] requiredTypes =
        [
            TokenType.ObjectStart,
            TokenType.ObjectEnd,
            TokenType.ArrayStart,
            TokenType.ArrayEnd,
            TokenType.PropertyName,
            TokenType.String,
            TokenType.Int32,
            TokenType.Int64,
            TokenType.UInt64,
            TokenType.Float64,
            TokenType.Bool,
            TokenType.Null,
        ];
        await Assert
            .That(string.Join(",", requiredTypes.Where(one => !emitted.Contains(one))))
            .IsEqualTo("")
            .Because("named corpus, token types emitted by JsonReader");

        var malformed = JsonSpanTokenDump.MalformedPayloads().Select(m => m.Name).ToHashSet();
        string[] requiredMalformed =
        [
            "unclosed-object",
            "unclosed-array",
            "unclosed-string",
            "trailing-comma-array",
            "double-comma-array",
            "leading-comma-array",
            "invalid-escape",
            "raw-control-char-in-string",
            "bad-literal-tru",
            "unexpected-byte",
            "trailing-garbage",
            "two-roots",
            "leading-zero",
            "depth-overflow-257",
        ];
        await Assert
            .That(string.Join(",", requiredMalformed.Where(one => !malformed.Contains(one))))
            .IsEqualTo("")
            .Because("malformed corpus");

        await Assert.That(JsonSpanTokenDump.RandomPayloads().Count()).IsEqualTo(300);
    }

    /// <summary>
    /// <b>Plan §3.6 evidence</b>: the real-reader arm is not decoration. It walks the whole named corpus
    /// through <see cref="JsonReader"/>'s own fused members — the arm T14's override will be judged on.
    /// </summary>
    [Test]
    public async Task RealReaderArm_WalksEveryNamedPayload()
    {
        var payloads = 0;
        var tokens = 0;
        var collectionStarts = 0;
        var problems = new List<string>();
        foreach (var (name, payload) in JsonSpanTokenDump.Payloads())
        {
            var reference = JsonFusedTokenDump.DrainReference(payload);
            var real = DrainOnRealReader(payload, reference);
            var difference = FirstDifference(reference, real);
            if (difference is not null)
                problems.Add($"{name}: {difference}");
            if (real.DeclaresCounts)
                problems.Add($"{name}: the JSON reader declared counted collections");
            tokens += real.Lines.Count;
            collectionStarts += real.Lines.Count(line =>
                JsonFusedTokenDump.IsCollectionStart(line.Snapshot.Type)
            );
            payloads++;
        }

        await Assert.That(string.Join("; ", problems)).IsEqualTo("").Because("real reader arm");
        await Assert
            .That(payloads)
            .IsEqualTo(JsonSpanTokenDump.Payloads().Count())
            .Because("every named payload was walked by the real reader arm");
        await Assert.That(tokens).IsGreaterThan(300).Because("the named corpus is not degenerate");
        await Assert
            .That(collectionStarts)
            .IsGreaterThan(15)
            .Because("the named corpus carries objects and arrays, including empty ones");
    }

    /// <summary>
    /// <b>The JSON half of the count contract, pinned</b> (plan T12 step 4, spec §3.3): JSON's wire form
    /// carries no element count, so the reader must declare <see cref="IFusedTokenReader.HasCountedCollections"/>
    /// = <see langword="false"/> <b>and</b> answer <see langword="false"/> (with <c>count = 0</c>) for every
    /// token of every corpus payload — before the first token, on each start token, and after the walk.
    /// The declaration and the answer are asserted together on purpose: a drift between them is exactly
    /// what a single-member contract could not catch.
    /// </summary>
    [Test]
    public async Task JsonReader_DeclaresNoCountedCollections_OnEveryCorpusPayload()
    {
        var problems = new List<string>();
        foreach (
            var (name, payload) in JsonSpanTokenDump
                .Payloads()
                .Concat(JsonSpanTokenDump.RandomPayloads())
        )
        {
            var span = CountWitness(new JsonReader(payload.AsSpan()));
            if (span.Length != 0)
                problems.Add($"{name} (span): {span}");
            var seq = CountWitness(new JsonReader(new ReadOnlySequence<byte>(payload)));
            if (seq.Length != 0)
                problems.Add($"{name} (sequence): {seq}");
        }

        await Assert
            .That(string.Join(" | ", problems))
            .IsEqualTo("")
            .Because("counted-collection contract");
    }

    // Sync helper: ref structs cannot cross into async methods, and TUnit assertions only execute when
    // awaited — the checks run here and the single awaited assertion reports the result.
    private static string CountWitness<TReader>(TReader reader)
        where TReader : IFusedTokenReader, allows ref struct
    {
        var problems = new List<string>();
        if (reader.HasCountedCollections)
            problems.Add(
                "HasCountedCollections must be false: JSON's wire form carries no element count (spec §3.3)"
            );

        if (reader.TryGetCollectionCount(out var before))
            problems.Add($"before the first token: answered true with count {before}");
        else if (before != 0)
            problems.Add($"before the first token: answered false with count {before}");

        int tokens = 0;
        while (reader.Read())
        {
            tokens++;
            if (reader.TryGetCollectionCount(out var count))
                problems.Add(
                    $"token {tokens} ({reader.TokenType}): answered true with count {count}"
                );
            else if (count != 0)
                problems.Add(
                    $"token {tokens} ({reader.TokenType}): answered false with count {count}"
                );
        }

        if (reader.TryGetCollectionCount(out var after))
            problems.Add($"after the walk: answered true with count {after}");
        else if (after != 0)
            problems.Add($"after the walk: answered false with count {after}");

        return string.Join("; ", problems);
    }

    /// <summary>
    /// <b>The task's real acceptance criterion</b> (plan §3.5): the guard is worth having only if a wrong
    /// member turns it red. One member of the stand-in is deliberately wrong; for every payload whose dump
    /// can show that member's answer, the comparison must find a difference. A payload the guard lets
    /// through with a wrong member is a hole in the guard and fails this test by name.
    /// <para>
    /// The four faults cover the four ways a fused member can go wrong: a wrong <i>value</i> with correct
    /// state (only the dumped fused-read column can catch it, and the state half of the dump stays
    /// identical), a missed <i>advance</i> (the token stream itself diverges), and a wrong member
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

        var corpus = JsonSpanTokenDump.Payloads().Concat(JsonSpanTokenDump.RandomPayloads());
        foreach (var (name, payload) in corpus)
        {
            var ints =
                fault == FusedFault.WrongInt64Value
                    ? JsonFusedTokenDump.IntAccessor.Int64
                    : JsonFusedTokenDump.IntAccessor.Int32;
            var reference = JsonFusedTokenDump.DrainReference(payload, ints: ints);
            if (!reference.Lines.Exists(line => fault.ManifestsIn(line)))
                continue;
            touched++;

            var faulty = DrainOnStandIn(payload, reference, fault, ints: ints);
            var difference = FirstDifference(reference, faulty);
            if (difference is null)
                missed.Add(name);
            else if (firstDifference.Length == 0)
                firstDifference = name + ": " + difference;
        }

        await Assert
            .That(touched)
            .IsGreaterThan(30)
            .Because($"{fault}: the corpus must reach this fault (reached {touched} payloads)");
        await Assert
            .That(string.Join(",", missed))
            .IsEqualTo("")
            .Because($"{fault}: payloads the guard let through with a wrong member");
        await Assert
            .That(firstDifference)
            .IsNotEmpty()
            .Because($"{fault}: the first difference the guard reported");
    }

    /// <summary>
    /// The scale of the dump, pinned so it cannot quietly shrink: the number of reference-side lines the
    /// guard compares (named corpus in all four reader modes, the chunked sweep over every chunk-aligned
    /// prefix, and the random corpus in both modes). Each of those lines is compared against two fused arms,
    /// so the real comparison count is twice this. Measured on delivery: <b>4 378</b> reference-side lines
    /// (8 756 line comparisons); the bound is a floor, so adding corpus is always allowed and removing it is
    /// not.
    /// </summary>
    [Test]
    public async Task DumpScale_IsNotDegenerate()
    {
        var lines = 0;
        foreach (var (_, payload) in JsonSpanTokenDump.Payloads())
        foreach (
            var mode in new[]
            {
                JsonFusedTokenDump.ReaderMode.Span,
                JsonFusedTokenDump.ReaderMode.Sequence,
            }
        )
            lines += JsonFusedTokenDump.DrainReference(payload, mode).Lines.Count;

        foreach (var (name, chunk) in ChunkedCases())
        foreach (
            var attempt in JsonFusedTokenDump.ChunkedPrefixes(
                JsonSpanTokenDump.PayloadByName(name),
                chunk
            )
        )
            lines += attempt.Reference.Lines.Count;

        foreach (var (_, payload) in JsonSpanTokenDump.RandomPayloads())
        foreach (
            var mode in new[]
            {
                JsonFusedTokenDump.ReaderMode.Span,
                JsonFusedTokenDump.ReaderMode.Sequence,
            }
        )
            lines += JsonFusedTokenDump.DrainReference(payload, mode).Lines.Count;

        await Assert
            .That(lines)
            .IsGreaterThan(4300)
            .Because("reference-side dump lines (measured: 4 378)");
    }

    // ── helpers ──

    private static async Task AssertPinnedAsync(
        string label,
        List<JsonTokenSnapshot> expected,
        byte[] payload,
        JsonFusedTokenDump.ReaderMode mode
    )
    {
        var actual = JsonFusedTokenDump.DrainReference(payload, mode);
        await Assert.That(actual.Lines.Count).IsEqualTo(expected.Count).Because(label);
        for (int i = 0; i < expected.Count; i++)
            await Assert
                .That(actual.Lines[i].Snapshot.ToString())
                .IsEqualTo(expected[i].ToString())
                .Because($"{label}: token {i}");
    }

    private static JsonFusedTokenDump.FusedDrain DrainOnStandIn(
        byte[] payload,
        JsonFusedTokenDump.FusedDrain reference,
        FusedFault fault,
        JsonFusedTokenDump.ReaderMode mode = JsonFusedTokenDump.ReaderMode.Span,
        bool isFinalBlock = true,
        JsonFusedTokenDump.IntAccessor ints = JsonFusedTokenDump.IntAccessor.Int32
    )
    {
        var script = JsonFusedTokenDump.ScriptOf(reference);
        if (mode == JsonFusedTokenDump.ReaderMode.Sequence)
        {
            var standIn = new StandInJsonReader(
                new JsonReader(
                    new ReadOnlySequence<byte>(payload),
                    maxDepth: 256,
                    isFinalBlock: isFinalBlock
                ),
                fault
            );
            try
            {
                return JsonFusedTokenDump.DrainFused<StandInJsonReader, StandInCapture>(
                    ref standIn,
                    script,
                    default,
                    ints
                );
            }
            finally
            {
                standIn.Dispose();
            }
        }

        var spanStandIn = new StandInJsonReader(
            new JsonReader(payload.AsSpan(), maxDepth: 256, isFinalBlock: isFinalBlock),
            fault
        );
        try
        {
            return JsonFusedTokenDump.DrainFused<StandInJsonReader, StandInCapture>(
                ref spanStandIn,
                script,
                default,
                ints
            );
        }
        finally
        {
            spanStandIn.Dispose();
        }
    }

    /// <summary>
    /// The fused arm on the <b>real</b> reader (plan §3.6 D-2): every fused call goes through
    /// <see cref="JsonReader"/>'s own <see cref="IFusedTokenReader"/> members, so an override that is wrong
    /// (T14) shows up in the dump instead of hiding behind a green stand-in.
    /// </summary>
    private static JsonFusedTokenDump.FusedDrain DrainOnRealReader(
        byte[] payload,
        JsonFusedTokenDump.FusedDrain reference,
        JsonFusedTokenDump.ReaderMode mode = JsonFusedTokenDump.ReaderMode.Span,
        bool isFinalBlock = true,
        JsonFusedTokenDump.IntAccessor ints = JsonFusedTokenDump.IntAccessor.Int32
    )
    {
        var script = JsonFusedTokenDump.ScriptOf(reference);
        if (mode == JsonFusedTokenDump.ReaderMode.Sequence)
        {
            var reader = new JsonReader(
                new ReadOnlySequence<byte>(payload),
                maxDepth: 256,
                isFinalBlock: isFinalBlock
            );
            try
            {
                return JsonFusedTokenDump.DrainFused<JsonReader, JsonReaderCapture>(
                    ref reader,
                    script,
                    default,
                    ints
                );
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
            return JsonFusedTokenDump.DrainFused<JsonReader, JsonReaderCapture>(
                ref spanReader,
                script,
                default,
                ints
            );
        }
        finally
        {
            spanReader.Dispose();
        }
    }

    /// <summary>
    /// The whole comparison in one place, returning the <b>first</b> difference (or null): token count,
    /// every dumped line (state, fused-read answer, count answer), then the terminal state. Reporting the
    /// first difference keeps the guard O(n) instead of paying an assertion per token.
    /// </summary>
    private static string? FirstDifference(
        JsonFusedTokenDump.FusedDrain expected,
        JsonFusedTokenDump.FusedDrain actual
    )
    {
        if (actual.Lines.Count != expected.Lines.Count)
            return $"token count {expected.Lines.Count} vs {actual.Lines.Count}";
        for (int i = 0; i < expected.Lines.Count; i++)
        {
            var want = expected.Lines[i];
            var got = actual.Lines[i];
            if (got.Snapshot.ToString() != want.Snapshot.ToString())
                return $"token {i}: state <{want.Snapshot}> vs <{got.Snapshot}>";
            if (got.Read != want.Read)
                return $"token {i}: fused read <{want.Read}> vs <{got.Read}>";
            if (got.Count != want.Count)
                return $"token {i}: collection count <{want.Count}> vs <{got.Count}>";
        }
        if (actual.NeedsMoreData != expected.NeedsMoreData)
            return $"NeedsMoreData {expected.NeedsMoreData} vs {actual.NeedsMoreData}";
        if (actual.Depth != expected.Depth)
            return $"Depth {expected.Depth} vs {actual.Depth}";
        if (actual.Consumed != expected.Consumed)
            return $"BytesConsumed {expected.Consumed} vs {actual.Consumed}";
        if (actual.DeclaresCounts != expected.DeclaresCounts)
            return $"HasCountedCollections {expected.DeclaresCounts} vs {actual.DeclaresCounts}";
        return null;
    }
}

/// <summary>The way a fused member can be wrong; used to prove the guard detects each one.</summary>
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
    /// observable: a wrong <c>TryReadInt32</c> value only exists on tokens that decode as an <c>int</c>
    /// (a <c>uint64-max</c> token reports <c>&lt;no i32&gt;</c> on both arms, fault or not), a missed
    /// advance exists on every integer-family token, and so on.
    /// </summary>
    public static bool ManifestsIn(this FusedFault fault, JsonFusedTokenDump.FusedLine line) =>
        fault switch
        {
            FusedFault.WrongInt32Value => line.Read.StartsWith("i32=", StringComparison.Ordinal),
            FusedFault.WrongInt64Value => line.Read.StartsWith("i64=", StringComparison.Ordinal),
            FusedFault.NoAdvanceOnInt32 => JsonFusedTokenDump.IsIntFamily(line.Snapshot.Type),
            FusedFault.WrongBoolValue => line.Read.StartsWith("bool=", StringComparison.Ordinal),
            _ => false,
        };
}

/// <summary>
/// A <see cref="ref struct"/> stand-in for a fused <see cref="JsonReader"/>: it holds a real reader and
/// forwards every contract member to it, with an injectable fault on one of the fused reads. The fused reads
/// forward to <see cref="TokenReaderFused"/>, i.e. the literal <c>Read() &amp;&amp; TryGetX()</c>
/// combination — the same one-line body T1b adds to the readers.
/// </summary>
public ref struct StandInJsonReader : IFusedTokenReader
{
    private JsonReader _inner;
    private readonly FusedFault _fault;

    public StandInJsonReader(JsonReader inner, FusedFault fault = FusedFault.None)
    {
        _inner = inner;
        _fault = fault;
    }

    /// <summary>A copy of the real reader, taken when the guard snapshots a token.</summary>
    public readonly JsonReader Snapshot => _inner;

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

    // ── IFusedTokenReader: a JSON walk needs no count (the wire carries none) ──
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

/// <summary>Snapshot strategy for <see cref="StandInJsonReader"/>: snapshot the reader it wraps.</summary>
public struct StandInCapture : JsonFusedTokenDump.IJsonFusedCapture<StandInJsonReader>
{
    public readonly JsonTokenSnapshot Capture(int index, ref StandInJsonReader reader)
    {
        var inner = reader.Snapshot;
        return JsonSpanTokenDump.Capture(index, ref inner);
    }

    public readonly long Consumed(ref StandInJsonReader reader)
    {
        var inner = reader.Snapshot;
        return inner.BytesConsumed;
    }
}
