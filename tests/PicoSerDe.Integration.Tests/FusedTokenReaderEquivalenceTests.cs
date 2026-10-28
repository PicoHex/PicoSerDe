namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Task <b>T1b</b> (fused token read, spec §3.1/§3.2, plan §5 T1b): the four text readers —
/// <see cref="JsonReader"/>, <see cref="TomlReader"/>, <see cref="YamlReader"/>, <see cref="IniReader"/> —
/// implement <see cref="IFusedTokenReader"/> with <b>forwarding only</b>. No format optimizes a fused read
/// here (that is T14/T18's job), so each of the four <c>TryReadX</c> members must be *exactly* the
/// reference combination <c>Read() &amp;&amp; TryGetX(out value)</c> of spec §3.2 — verified against a
/// hand-written pair running on an independent reader over the same bytes.
/// <para>
/// <b>Why eight readers walk in lockstep</b>: every fused read advances exactly <b>one</b> token
/// (<see cref="ITokenReader.Read"/> plus a value decode, spec §3.1), so readers started on the same bytes
/// stay aligned regardless of which accessor drives them. One walk therefore covers all four accessors and
/// a plain <see cref="ITokenReader.Read"/>-driven <paramref name="probe"/> reader becomes the loop's clock.
/// </para>
/// <para>
/// <b>What is compared</b> (task T1b acceptance 3 + spec §3.2.2): the boolean result, the value, and every
/// observable — <see cref="ITokenReader.TokenType"/>, <see cref="ITokenReader.Depth"/>,
/// <see cref="ITokenReader.NeedsMoreData"/> and <c>BytesConsumed</c>. Failure is lossless on both sides, so
/// a type-mismatch step is compared too — the token stays current and the walk simply continues.
/// </para>
/// </summary>
public class FusedTokenReaderEquivalenceTests
{
    // BytesConsumed is not on ITokenReader (it is a per-format convenience today), so the witness reaches it
    // through a `allows ref struct` delegate instead of widening the contract. Static so no allocation per
    // walk beyond the cached delegate instance.
    private delegate long ConsumedProbe<TReader>(ref TReader reader)
        where TReader : allows ref struct;

    // A ref struct local cannot be lifted into an array or a generic list, so the witness has to declare its
    // nine readers one by one — but the *caller* only needs to say how to build one. Without this factory each
    // format would repeat the same nine-line construction block.
    private delegate TReader ReaderFactory<TReader>()
        where TReader : allows ref struct;

    private readonly record struct Snapshot(
        bool Ok,
        long Bits,
        TokenType Type,
        int Depth,
        bool NeedsMore,
        long Consumed
    );

    [Test]
    [Arguments("json")]
    [Arguments("toml")]
    [Arguments("yaml")]
    [Arguments("ini")]
    public async Task FusedReads_MatchTheHandWrittenPair(string format)
    {
        var problems = format switch
        {
            "json" => JsonProblems(),
            "toml" => TomlProblems(),
            "yaml" => YamlProblems(),
            "ini" => IniProblems(),
            _ => $"unknown format '{format}'",
        };
        await Assert.That(problems).IsEqualTo("");
    }

    // ── per-format probes: span mode, sequence mode, and a truncated sequence (chunk-boundary) ──

    private static string JsonProblems()
    {
        var payload = """{"a":1,"b":1.5,"c":true,"d":"s","e":[2,3]}"""u8.ToArray();
        var problems = new List<string>
        {
            JsonWitness(payload, streaming: false),
            JsonWitness(payload, streaming: true),
            // Cut mid-collection: the streaming path must report the same NeedsMoreData Read() does.
            JsonWitness("""{"a":1,"b":[2"""u8.ToArray(), streaming: true),
        };
        problems.RemoveAll(p => p.Length == 0);
        return string.Join("; ", problems);
    }

    private static string TomlProblems()
    {
        var payload = "a = 1\nb = 1.5\nc = true\nd = \"s\"\n[e]\nf = 2\n"u8.ToArray();
        var problems = new List<string>
        {
            TomlWitness(payload, streaming: false),
            TomlWitness(payload, streaming: true),
            TomlWitness("a = [1, 2"u8.ToArray(), streaming: true),
        };
        problems.RemoveAll(p => p.Length == 0);
        return string.Join("; ", problems);
    }

    private static string YamlProblems()
    {
        var payload = "a: 1\nb: 1.5\nc: true\nd: x\n"u8.ToArray();
        var problems = new List<string>
        {
            YamlWitness(payload, streaming: false),
            YamlWitness(payload, streaming: true),
            YamlWitness("a: 1\nb: "u8.ToArray(), streaming: true),
        };
        problems.RemoveAll(p => p.Length == 0);
        return string.Join("; ", problems);
    }

    private static string IniProblems()
    {
        var payload = "a=1\nb=1.5\nc=true\n[d]\ne=2\n"u8.ToArray();
        var problems = new List<string>
        {
            IniWitness(payload, streaming: false),
            IniWitness(payload, streaming: true),
            IniWitness("a=1\nb="u8.ToArray(), streaming: true),
        };
        problems.RemoveAll(p => p.Length == 0);
        return string.Join("; ", problems);
    }

    private static string JsonWitness(byte[] bytes, bool streaming) =>
        Witness($"json(streaming={streaming})", () => MakeJson(bytes, streaming), JsonConsumed);

    private static JsonReader MakeJson(byte[] bytes, bool streaming) =>
        streaming
            ? new JsonReader(new ReadOnlySequence<byte>(bytes), 256, isFinalBlock: false)
            : new JsonReader(bytes);

    private static string TomlWitness(byte[] bytes, bool streaming) =>
        Witness($"toml(streaming={streaming})", () => MakeToml(bytes, streaming), TomlConsumed);

    private static TomlReader MakeToml(byte[] bytes, bool streaming) =>
        streaming
            ? new TomlReader(new ReadOnlySequence<byte>(bytes), isFinalBlock: false)
            : new TomlReader(bytes);

    private static string YamlWitness(byte[] bytes, bool streaming) =>
        Witness($"yaml(streaming={streaming})", () => MakeYaml(bytes, streaming), YamlConsumed);

    private static YamlReader MakeYaml(byte[] bytes, bool streaming) =>
        streaming
            ? new YamlReader(new ReadOnlySequence<byte>(bytes), isFinalBlock: false)
            : new YamlReader(bytes);

    private static string IniWitness(byte[] bytes, bool streaming) =>
        Witness($"ini(streaming={streaming})", () => MakeIni(bytes, streaming), IniConsumed);

    private static IniReader MakeIni(byte[] bytes, bool streaming) =>
        streaming
            ? new IniReader(new ReadOnlySequence<byte>(bytes), isFinalBlock: false)
            : new IniReader(bytes);

    private static long JsonConsumed(ref JsonReader reader) => reader.BytesConsumed;

    private static long TomlConsumed(ref TomlReader reader) => reader.BytesConsumed;

    private static long YamlConsumed(ref YamlReader reader) => reader.BytesConsumed;

    private static long IniConsumed(ref IniReader reader) => reader.BytesConsumed;

    /// <summary>
    /// Walks <paramref name="probe"/> with plain <see cref="ITokenReader.Read"/> and, at every step, makes each
    /// hand-written reader do <c>Read() &amp;&amp; TryGetX()</c> while its fused twin does <c>TryReadX()</c>,
    /// then compares the two observations. The walk runs one step past the last token so a failed
    /// <c>Read</c> is compared as well (spec §3.2.2 (b): <c>NeedsMoreData</c> must agree there).
    /// </summary>
    private static string Witness<TReader>(
        string label,
        ReaderFactory<TReader> make,
        ConsumedProbe<TReader> consumed
    )
        where TReader : IFusedTokenReader, allows ref struct
    {
        var probe = make();
        var handInt32 = make();
        var fusedInt32 = make();
        var handInt64 = make();
        var fusedInt64 = make();
        var handFloat64 = make();
        var fusedFloat64 = make();
        var handBool = make();
        var fusedBool = make();

        var problems = new List<string>();
        var steps = 0;

        while (problems.Count == 0 && steps < 8192)
        {
            var probeOk = probe.Read();
            steps++;

            bool hand32Ok = handInt32.Read();
            int hand32Value = 0;
            if (hand32Ok)
                hand32Ok = handInt32.TryGetInt32(out hand32Value);
            if (!hand32Ok)
                hand32Value = 0;
            bool fused32Ok = fusedInt32.TryReadInt32(out int fused32Value);
            if (!fused32Ok)
                fused32Value = 0;

            bool hand64Ok = handInt64.Read();
            long hand64Value = 0;
            if (hand64Ok)
                hand64Ok = handInt64.TryGetInt64(out hand64Value);
            if (!hand64Ok)
                hand64Value = 0;
            bool fused64Ok = fusedInt64.TryReadInt64(out long fused64Value);
            if (!fused64Ok)
                fused64Value = 0;

            bool handFloatOk = handFloat64.Read();
            double handFloatValue = 0;
            if (handFloatOk)
                handFloatOk = handFloat64.TryGetFloat64(out handFloatValue);
            if (!handFloatOk)
                handFloatValue = 0;
            bool fusedFloatOk = fusedFloat64.TryReadFloat64(out double fusedFloatValue);
            if (!fusedFloatOk)
                fusedFloatValue = 0;

            bool handBoolOk = handBool.Read();
            bool handBoolValue = false;
            if (handBoolOk)
                handBoolOk = handBool.TryGetBool(out handBoolValue);
            if (!handBoolOk)
                handBoolValue = false;
            bool fusedBoolOk = fusedBool.TryReadBool(out bool fusedBoolValue);
            if (!fusedBoolOk)
                fusedBoolValue = false;

            Compare(
                problems,
                label,
                steps,
                "TryReadInt32",
                Take(hand32Ok, hand32Value, ref handInt32, consumed),
                Take(fused32Ok, fused32Value, ref fusedInt32, consumed)
            );
            Compare(
                problems,
                label,
                steps,
                "TryReadInt64",
                Take(hand64Ok, hand64Value, ref handInt64, consumed),
                Take(fused64Ok, fused64Value, ref fusedInt64, consumed)
            );
            // Floats compare by bit pattern: NaN, ±Inf and -0.0 must survive the fused path unchanged.
            Compare(
                problems,
                label,
                steps,
                "TryReadFloat64",
                Take(
                    handFloatOk,
                    BitConverter.DoubleToInt64Bits(handFloatValue),
                    ref handFloat64,
                    consumed
                ),
                Take(
                    fusedFloatOk,
                    BitConverter.DoubleToInt64Bits(fusedFloatValue),
                    ref fusedFloat64,
                    consumed
                )
            );
            Compare(
                problems,
                label,
                steps,
                "TryReadBool",
                Take(handBoolOk, handBoolValue ? 1 : 0, ref handBool, consumed),
                Take(fusedBoolOk, fusedBoolValue ? 1 : 0, ref fusedBool, consumed)
            );

            if (!probeOk)
                break;
        }

        if (steps >= 8192)
            problems.Add($"{label}: the walk did not terminate (8192 steps)");

        return string.Join("; ", problems);
    }

    private static Snapshot Take<TReader>(
        bool ok,
        long bits,
        ref TReader reader,
        ConsumedProbe<TReader> consumed
    )
        where TReader : IFusedTokenReader, allows ref struct =>
        new(ok, bits, reader.TokenType, reader.Depth, reader.NeedsMoreData, consumed(ref reader));

    private static void Compare(
        List<string> problems,
        string label,
        int step,
        string accessor,
        Snapshot hand,
        Snapshot fused
    )
    {
        if (hand.Ok != fused.Ok)
            problems.Add($"{label} step {step} {accessor}: ok {hand.Ok} != {fused.Ok}");
        if (hand.Bits != fused.Bits)
            problems.Add($"{label} step {step} {accessor}: value {hand.Bits} != {fused.Bits}");
        if (hand.Type != fused.Type)
            problems.Add($"{label} step {step} {accessor}: token {hand.Type} != {fused.Type}");
        if (hand.Depth != fused.Depth)
            problems.Add($"{label} step {step} {accessor}: depth {hand.Depth} != {fused.Depth}");
        if (hand.NeedsMore != fused.NeedsMore)
            problems.Add(
                $"{label} step {step} {accessor}: needsMoreData {hand.NeedsMore} != {fused.NeedsMore}"
            );
        if (hand.Consumed != fused.Consumed)
            problems.Add(
                $"{label} step {step} {accessor}: bytesConsumed {hand.Consumed} != {fused.Consumed}"
            );
    }
}
