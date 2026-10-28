namespace PicoJetson.Tests;

/// <summary>
/// Task T13 (spec §4.3 <b>F-1</b>, spec §6 S2): the negative-number regression for
/// <see cref="JsonReader.TryReadNextInt32"/>.
/// <para>
/// <b>The defect.</b> The sequence arm of <c>TryReadNextInt32</c> (<c>TryReadNextInt32Seq</c>) advanced over
/// the minus sign and then decoded the bare digits, so <c>-123</c> came back as <c>123</c>; the span arm
/// (<c>TryReadNextInt32Span</c>) carries a <c>bool neg</c> and is correct. Two arms of one public method,
/// one of them wrong — which is why spec §4.3 rules the sequence arm must be deleted and the sequence path
/// must answer with the reference semantics (<see cref="TokenReaderFused"/>: <c>Read() &amp;&amp; TryGetInt32()</c>).
/// </para>
/// <para>
/// <b>Why these tests are red before the fix</b> (plan §3.5: assertion red, not compile red). The members
/// exist, so nothing fails to compile; what fails is the value (<c>-123</c> vs <c>123</c>) and, for the
/// sequence arm, the whole observable state — the old arm wrote no <c>TokenType</c>, so it was not the
/// reference semantics at all, it was a third path with its own behaviour.
/// </para>
/// </summary>
public class JsonReaderInt32SeqRegressionTests
{
    /// <summary>The span arm (whole payload, <see cref="JsonReader"/>'s <c>ReadOnlySpan</c> constructor).</summary>
    private const int SpanMode = -1;

    /// <summary>A sequence that happens to be one segment: sequence mode, no slicing.</summary>
    private const int SingleSegment = 0;

    /// <summary>Every sequence construction the regression must cover: one segment, then 1/2/3/5-byte slices.</summary>
    private static readonly int[] SequenceChunks = [SingleSegment, 1, 2, 3, 5];

    /// <summary>
    /// Negative literals at both <see cref="int"/> boundaries plus everyday values (spec §6 S2: "F-1 的回归
    /// 用例（负数的 span 与 seq 两条）").
    /// </summary>
    public static IEnumerable<(string Literal, int Expected)> NegativeLiterals() =>
        [
            ("-2", -2),
            ("-123", -123),
            ("-4567890", -4567890),
            ("-2147483647", -2147483647),
            ("-2147483648", int.MinValue),
        ];

    /// <summary>Every literal at every sequence construction (spec §5.2's chunk sizes).</summary>
    public static IEnumerable<(string Literal, int Expected, int Chunk)> ChunkedNegativeCases() =>
        NegativeLiterals()
            .SelectMany(one => SequenceChunks.Select(chunk => (one.Literal, one.Expected, chunk)));

    /// <summary>Integer arrays carrying negatives, so the separator/comma skipping is exercised too.</summary>
    public static IEnumerable<(string Json, int Chunk)> NegativeArrayCases() =>
        new[] { "[1,-123,7]", "[-5,-6]", "[0,-2147483648]" }.SelectMany(json =>
            SequenceChunks.Select(chunk => (json, chunk))
        );

    /// <summary>
    /// <b>F-1 itself.</b> The span arm is the authority for the value (it is the arm that was never wrong),
    /// and the sequence arm must agree with it — at every chunk boundary, because the old sequence arm's
    /// behaviour also depended on where the number fell relative to a segment.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ChunkedNegativeCases))]
    public async Task TryReadNextInt32_NegativeLiteral_BothModesDecodeTheSameValue(
        string literal,
        int expected,
        int chunk
    )
    {
        var span = ValueProbe(literal, SpanMode);
        var sequence = ValueProbe(literal, chunk);

        await Assert
            .That(span)
            .IsEqualTo(Verdict(expected))
            .Because($"span arm decodes '{literal}' correctly — F-1 is sequence-only");
        await Assert
            .That(sequence)
            .IsEqualTo(Verdict(expected))
            .Because($"F-1: sequence arm (chunk={Describe(chunk)}) must decode '{literal}'");
    }

    /// <summary>
    /// The sequence arm must <b>be</b> the reference semantics, not merely produce the right number: spec §4.3
    /// requires the sequence path to answer through <see cref="TokenReaderFused"/>, i.e. <c>Read()</c> then
    /// <c>TryGetInt32()</c>. So the arm's answer and the state it leaves behind are compared against a second
    /// reader over the same bytes driven by the literal <c>Read() &amp;&amp; TryGetInt32()</c> combination —
    /// value, <c>TokenType</c>, <c>BytesConsumed</c>, <c>Depth</c>, <c>NeedsMoreData</c>.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ChunkedNegativeCases))]
    public async Task TryReadNextInt32_SequenceArm_IsTheReferenceSemantics(
        string literal,
        int expected,
        int chunk
    )
    {
        var fused = StateProbe(literal, chunk, useNextInt32: true);
        var reference = StateProbe(literal, chunk, useNextInt32: false);

        await Assert
            .That(fused)
            .IsEqualTo(reference)
            .Because(
                $"F-1 fix: the sequence path (chunk={Describe(chunk)}) must be the reference semantics "
                    + $"(Read() && TryGetInt32()), so '{literal}' must report value {expected} with a "
                    + "materialised token, not a third set of side effects"
            );
    }

    /// <summary>
    /// The comma/whitespace skipping contract of <see cref="JsonReader.TryReadNextInt32"/> over arrays, in both
    /// modes: the same values in the same order, then <see langword="false"/> at the closing bracket.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(NegativeArrayCases))]
    public async Task TryReadNextInt32_IntegerArray_BothModesDecodeTheSameValues(
        string json,
        int chunk
    )
    {
        var span = ArrayProbe(json, SpanMode);
        var sequence = ArrayProbe(json, chunk);

        await Assert
            .That(sequence)
            .IsEqualTo(span)
            .Because(
                $"F-1: '{json}' (chunk={Describe(chunk)}) — span and sequence arms must agree"
            );
        await Assert
            .That(span)
            .Contains("=True")
            .Because($"'{json}': the span arm is the authority and must decode values");
    }

    /// <summary>
    /// The lock the deleted arm was violating by construction: the <b>fused contract</b>
    /// (<see cref="JsonReader.TryReadInt32"/>, the <see cref="IFusedTokenReader"/> member) answers identically
    /// on a span reader and on a chunked sequence reader — value, token, <c>BytesConsumed</c>, <c>Depth</c>,
    /// <c>NeedsMoreData</c> — token by token. Green before and after T13; it is the two-path equivalence the
    /// brief's step 2 requires, kept as a permanent guard.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(NegativeArrayCases))]
    public async Task FusedRead32_SpanAndSequenceReadersAgreeOnEveryElement(string json, int chunk)
    {
        var span = ElementWalk(json, SpanMode);
        var sequence = ElementWalk(json, chunk);

        await Assert
            .That(sequence)
            .IsEqualTo(span)
            .Because(
                $"fused read (TryReadInt32) parity for '{json}' at chunk={Describe(chunk)}: a span reader "
                    + "and a sequence reader must be the same instrument"
            );
        await Assert
            .That(span)
            .Contains("i32=")
            .Because($"'{json}': the walk must actually reach integer elements");
    }

    // ── probes ──
    //
    // Ref structs cannot be held across an await, so every probe is a synchronous helper that reduces the
    // reader to a string; the awaited assertion is the only async part.

    private static string Verdict(int value) => $"ok=True value={value}";

    private static string Describe(int chunk) =>
        chunk switch
        {
            SpanMode => "span",
            SingleSegment => "one-segment sequence",
            _ => $"sequence chunk={chunk}",
        };

    /// <summary>Answer of <see cref="JsonReader.TryReadNextInt32"/>: the value half only (the span arm leaves no token).</summary>
    private static string ValueProbe(string payload, int chunk)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var reader = MakeReader(bytes, chunk);
        try
        {
            var ok = reader.TryReadNextInt32(out var value);
            return $"ok={ok} value={(ok ? value : 0)}";
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// Answer plus the whole observable state, once — via the arm under test or via the reference
    /// <c>Read() &amp;&amp; TryGetInt32()</c> combination.
    /// </summary>
    private static string StateProbe(string payload, int chunk, bool useNextInt32)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var reader = MakeReader(bytes, chunk);
        try
        {
            int value = 0;
            bool ok;
            if (useNextInt32)
                ok = reader.TryReadNextInt32(out value);
            else
                ok = reader.Read() && reader.TryGetInt32(out value);
            if (!ok)
                value = 0;
            return $"ok={ok} value={value} token={reader.TokenType} consumed={reader.BytesConsumed}"
                + $" depth={reader.Depth} needsMore={reader.NeedsMoreData}";
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>Values of an integer array through <see cref="JsonReader.TryReadNextInt32"/>, until it says stop.</summary>
    private static string ArrayProbe(string json, int chunk)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = MakeReader(bytes, chunk);
        try
        {
            var steps = new List<string>();
            if (!reader.Read())
                return "no ArrayStart";
            steps.Add($"start={reader.TokenType}");
            for (int i = 0; i < 32; i++)
            {
                var ok = reader.TryReadNextInt32(out var value);
                steps.Add($"#{i} ok={ok} value={(ok ? value : 0)}");
                if (!ok)
                    break;
            }
            return string.Join(" ", steps);
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// The fused contract walked over an integer array's elements (the container start is stepped over by a
    /// plain <see cref="JsonReader.Read"/>, because <c>TryReadInt32</c> answers <see langword="false"/> on it by
    /// design and does not advance).
    /// </summary>
    private static string ElementWalk(string json, int chunk)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = MakeReader(bytes, chunk);
        try
        {
            if (!reader.Read())
                return "no ArrayStart";
            var steps = new List<string> { $"start={reader.TokenType}@{reader.BytesConsumed}" };
            for (int i = 0; i < 32; i++)
            {
                var ok = reader.TryReadInt32(out var value);
                if (!ok)
                {
                    steps.Add(
                        $"stop token={reader.TokenType}@{reader.BytesConsumed}"
                            + $" depth={reader.Depth} needsMore={reader.NeedsMoreData}"
                    );
                    break;
                }
                steps.Add(
                    $"i32={value} token={reader.TokenType}@{reader.BytesConsumed}"
                        + $" depth={reader.Depth} needsMore={reader.NeedsMoreData}"
                );
            }
            return string.Join(" ", steps);
        }
        finally
        {
            reader.Dispose();
        }
    }

    // ── reader construction ──

    private static JsonReader MakeReader(byte[] payload, int chunk) =>
        chunk == SpanMode
            ? new JsonReader(payload.AsSpan())
            : new JsonReader(Chunked(payload, chunk));

    /// <summary>A multi-segment <see cref="ReadOnlySequence{T}"/> of <paramref name="chunk"/>-byte slices.</summary>
    private static ReadOnlySequence<byte> Chunked(byte[] payload, int chunk)
    {
        if (chunk == SingleSegment || payload.Length <= chunk)
            return new ReadOnlySequence<byte>(payload);

        var first = new ChunkSegment<byte>(payload.AsMemory(0, chunk), 0);
        var last = first;
        for (int offset = chunk; offset < payload.Length; offset += chunk)
        {
            var take = Math.Min(chunk, payload.Length - offset);
            last = last.Append(payload.AsMemory(offset, take));
        }
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class ChunkSegment<T> : System.Buffers.ReadOnlySequenceSegment<T>
    {
        public ChunkSegment(ReadOnlyMemory<T> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public ChunkSegment<T> Append(ReadOnlyMemory<T> memory)
        {
            var segment = new ChunkSegment<T>(memory, RunningIndex + Memory.Length);
            Next = segment;
            return segment;
        }
    }
}
