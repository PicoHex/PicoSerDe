using System.Globalization;
using System.Text;

namespace PicoJetson.Tests;

/// <summary>
/// Task T14 (spec §3.3, plan T14 acceptance 2–3): the <see cref="JsonReader.TryReadInt32"/> override against
/// the reference semantics <c>Read() &amp;&amp; TryGetInt32(out value)</c>, over a <b>run of consecutive fused
/// reads</b>.
/// <para>
/// <b>Why this file exists next to the dual-walk dump.</b> The dump
/// (<see cref="JsonReaderFusedEquivalenceTests"/>) picks one accessor per dumped token, so every fused call it
/// makes starts where the <i>reference</i> said an integer was — it never calls the fused read at a <c>]</c>,
/// at an out-of-int32 number, or twice in a row across a comma. Generated code does exactly that
/// (<c>while (TryReadInt32(out var x)) …</c>), so the equivalence a caller relies on is <b>step-for-step over a
/// whole walk</b>, not token-for-token over a script. Both arms are compared on every call: answer, value,
/// <see cref="JsonReader.TokenType"/>, <see cref="JsonReader.BytesConsumed"/>, <see cref="JsonReader.Depth"/>,
/// <see cref="JsonReader.NeedsMoreData"/> and the value span (spec §5.1's "all getters" column).
/// </para>
/// <para>
/// <b>The reference arm is inline</b> (<c>Read()</c> then <c>TryGetInt32()</c>), never
/// <see cref="TokenReaderFused"/>: the reference must not come from the implementation under test
/// (spec §3.2, "正本管样板，内联管对拍").
/// </para>
/// </summary>
public class JsonReaderTryReadInt32FusedTests
{
    /// <summary>How many calls each arm makes: enough to cross a document, its end token, and past it.</summary>
    private const int Steps = 24;

    /// <summary>
    /// The payload matrix: exactly the shapes the override has to answer for, including the ones it must
    /// <b>decline</b> (and then reproduce the reference's failure on).
    /// </summary>
    public static IEnumerable<(string Name, string Json)> Payloads() =>
        [
            ("flat-int", "123"),
            ("negative-int", "-123"),
            ("int32-max", "2147483647"),
            ("int32-min", "-2147483648"),
            ("one-past-int32", "2147483648"),
            ("one-below-int32", "-2147483649"),
            ("int64-max", "9223372036854775807"),
            ("leading-zero-padded", "[000123]"),
            ("leading-zero", "[0123]"),
            ("bare-minus", "[-]"),
            ("array-ints", "[1,2,3]"),
            ("array-negatives", "[-1,-2147483648,2147483647]"),
            ("array-single", "[7]"),
            ("array-empty", "[]"),
            ("object-ints", """{"a":1,"b":-2}"""),
            ("object-empty", "{}"),
            ("nested-arrays", "[[1,2],[3,-4]]"),
            ("whitespace-everywhere", " [ 1 ,\t2\n,\r\n-3 ] "),
            ("mixed-families", """[1,"x",true,null,2.5,7]"""),
            ("float-after-int", "[1,2.5]"),
            ("string-only", """["x"]"""),
            ("bool-object", """{"k":true}"""),
            ("negative-zero", "[-0]"),
            ("trailing-comma", "[1,2,]"),
        ];

    /// <summary>
    /// Step-for-step: the fused walk and the reference walk must be indistinguishable at every call, in both
    /// reader constructions (the sequence path is a one-line forward by design — spec §2.2 N4 — and this pins
    /// that it really is one).
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Payloads))]
    public async Task EveryIntreadOfAWalk_MatchesTheReference(string name, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var fusedSpan = FusedWalk(payload, sequence: false);
        var referenceSpan = ReferenceWalk(payload, sequence: false);
        await Assert
            .That(Join(fusedSpan))
            .IsEqualTo(Join(referenceSpan))
            .Because($"{name} span: fused walk must equal Read() && TryGetInt32() at every step");

        var fusedSeq = FusedWalk(payload, sequence: true);
        var referenceSeq = ReferenceWalk(payload, sequence: true);
        await Assert
            .That(Join(fusedSeq))
            .IsEqualTo(Join(referenceSeq))
            .Because($"{name} sequence: the fused read must forward, not read a span it does not own");
    }

    /// <summary>
    /// <b>Plan T14 acceptance 2 (F-3)</b>: the fusion materialises the token, it does not skip it. After a fused
    /// int read the token must still answer the whole <c>TryGet*</c> family exactly as the reference's token
    /// does — including <c>GetStringRaw()</c>, which reads the very span the fused arm wrote.
    /// </summary>
    [Test]
    public async Task FusedIntToken_AnswersTheWholeGetterFamily()
    {
        var payload = "[42,-2147483648]"u8.ToArray();
        var fused = new JsonReader(payload.AsSpan());
        var reference = new JsonReader(payload.AsSpan());
        var problems = new List<string>();
        try
        {
            // Both arms walk the same call sequence; every getter answer is compared on the int token.
            for (int i = 0; i < 6; i++)
            {
                bool fusedOk = fused.TryReadInt32(out var fusedValue);
                bool referenceOk = reference.Read();
                int referenceValue = 0;
                if (referenceOk)
                    referenceOk = reference.TryGetInt32(out referenceValue);

                var label = $"step {i}";
                if (fusedOk != referenceOk || fusedValue != referenceValue)
                    problems.Add(
                        $"{label}: answer {referenceOk}/{referenceValue} vs {fusedOk}/{fusedValue}"
                    );
                if (fused.TokenType != reference.TokenType)
                    problems.Add($"{label}: TokenType {reference.TokenType} vs {fused.TokenType}");
                if (fused.BytesConsumed != reference.BytesConsumed)
                    problems.Add($"{label}: consumed {reference.BytesConsumed} vs {fused.BytesConsumed}");
                if (fused.Depth != reference.Depth)
                    problems.Add($"{label}: depth {reference.Depth} vs {fused.Depth}");
                if (fused.NeedsMoreData != reference.NeedsMoreData)
                    problems.Add($"{label}: NeedsMoreData {reference.NeedsMoreData} vs {fused.NeedsMoreData}");
                if (Hex(fused.GetStringRaw()) != Hex(reference.GetStringRaw()))
                    problems.Add(
                        $"{label}: value bytes {Hex(reference.GetStringRaw())} vs {Hex(fused.GetStringRaw())}"
                    );
                if (fused.TryGetInt64(out var fusedI64) != reference.TryGetInt64(out var referenceI64)
                    || fusedI64 != referenceI64)
                    problems.Add($"{label}: TryGetInt64 disagrees");
                if (
                    fused.TryGetFloat64(out var fusedF64) != reference.TryGetFloat64(out var referenceF64)
                    || BitConverter.DoubleToInt64Bits(fusedF64)
                        != BitConverter.DoubleToInt64Bits(referenceF64)
                )
                    problems.Add($"{label}: TryGetFloat64 disagrees");
                if (fused.TryGetBool(out var fusedB) != reference.TryGetBool(out var referenceB)
                    || fusedB != referenceB)
                    problems.Add($"{label}: TryGetBool disagrees");
            }
        }
        finally
        {
            fused.Dispose();
            reference.Dispose();
        }

        await Assert.That(string.Join("; ", problems)).IsEqualTo("").Because("all-getter equivalence");
    }

    /// <summary>
    /// <b>Never truncate</b> (spec §5.3 "overflow"): a number outside <see cref="int"/> is not an int32 read.
    /// The token is consumed and classified exactly as the reference classifies it
    /// (<see cref="TokenType.Int64"/>/<see cref="TokenType.UInt64"/>), the answer is <see langword="false"/>
    /// and the value is 0 — never <c>(int)2147483648</c>.
    /// </summary>
    [Test]
    public async Task OutOfInt32_AnswersFalse_AndIsNeverTruncated()
    {
        string[] payloads = ["[2147483648]", "[-2147483649]", "[9223372036854775807]", "[18446744073709551615]"];
        var problems = new List<string>();
        foreach (var json in payloads)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var reader = new JsonReader(payload.AsSpan());
            try
            {
                reader.TryReadInt32(out _); // '[' → false, ArrayStart
                var ok = reader.TryReadInt32(out var value);
                if (ok)
                    problems.Add($"{json}: answered true with {value} (truncated)");
                if (value != 0)
                    problems.Add($"{json}: answered false with value {value}, must be 0");
                if (reader.TokenType is not (TokenType.Int64 or TokenType.UInt64))
                    problems.Add($"{json}: token type {reader.TokenType}, the reference classifies Int64/UInt64");
                if (reader.BytesConsumed != payload.Length - 1)
                    problems.Add($"{json}: consumed {reader.BytesConsumed}, the whole number must be consumed");
            }
            finally
            {
                reader.Dispose();
            }
        }

        await Assert.That(string.Join("; ", problems)).IsEqualTo("").Because("no truncation");
    }

    /// <summary>
    /// <b>Plan T14 acceptance 3 (F-4), as the contract actually states it.</b> When the next byte is
    /// <c>]</c> or <c>}</c>, the fused read must be the reference: <see cref="JsonReader.Read"/> produces the
    /// end token and <c>TryGetInt32</c> refuses it, so the call answers <see langword="false"/>
    /// <b>after consuming that end token</b> (spec §3.2.2 (a)/(c): failure is lossless, the token
    /// <c>Read()</c> produced stays current — it is not rolled back).
    /// <para>
    /// The plan's literal wording ("returns false and the position does not move") would be a second,
    /// diverging path — the very defect class F-4 names — so it is pinned here as equality with the
    /// reference instead: no move would leave the end token unread and the caller's next
    /// <c>Read()</c> would produce it, i.e. a different token stream from the reference's.
    /// </para>
    /// </summary>
    [Test]
    public async Task ArrayEndAndObjectEnd_MatchTheReference_InsteadOfANoMove()
    {
        var problems = new List<string>();
        foreach (var json in new[] { "[1]", """{"a":1}""" })
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var fused = new JsonReader(payload.AsSpan());
            var reference = new JsonReader(payload.AsSpan());
            try
            {
                // Walk up to and including the first call whose next byte is the closing bracket.
                for (int i = 0; i < Steps; i++)
                {
                    bool fusedOk = fused.TryReadInt32(out var fusedValue);
                    bool referenceOk = reference.Read();
                    int referenceValue = 0;
                    if (referenceOk)
                        referenceOk = reference.TryGetInt32(out referenceValue);
                    if (
                        fusedOk != referenceOk
                        || fusedValue != referenceValue
                        || fused.TokenType != reference.TokenType
                        || fused.BytesConsumed != reference.BytesConsumed
                    )
                    {
                        problems.Add(
                            $"{json} step {i}: {referenceOk}/{referenceValue}/{reference.TokenType}/{reference.BytesConsumed} "
                                + $"vs {fusedOk}/{fusedValue}/{fused.TokenType}/{fused.BytesConsumed}"
                        );
                        break;
                    }
                    if (reference.TokenType is TokenType.ArrayEnd or TokenType.ObjectEnd)
                        break;
                }
            }
            finally
            {
                fused.Dispose();
                reference.Dispose();
            }
        }

        // And the shape itself: the end token is consumed, not parked in front of the caller.
        var onlyArray = "[1]"u8.ToArray();
        var reader = new JsonReader(onlyArray.AsSpan());
        try
        {
            reader.TryReadInt32(out _); // '['
            reader.TryReadInt32(out var one);
            if (one != 1)
                problems.Add($"element read gave {one}");
            if (reader.TryReadInt32(out var past))
                problems.Add($"past the element the fused read answered true with {past}");
            if (reader.TokenType != TokenType.ArrayEnd)
                problems.Add($"past the element TokenType is {reader.TokenType}, expected ArrayEnd");
            if (reader.BytesConsumed != onlyArray.Length)
                problems.Add($"past the element consumed {reader.BytesConsumed}, expected {onlyArray.Length}");
        }
        finally
        {
            reader.Dispose();
        }

        await Assert.That(string.Join("; ", problems)).IsEqualTo("").Because("F-4 as the contract states it");
    }

    // ── helpers ──

    /// <summary>The fused arm: <see cref="JsonReader.TryReadInt32"/> and nothing else.</summary>
    private static List<string> FusedWalk(byte[] payload, bool sequence)
    {
        var steps = new List<string>();
        var reader = sequence
            ? new JsonReader(new ReadOnlySequence<byte>(payload))
            : new JsonReader(payload.AsSpan());
        try
        {
            for (int i = 0; i < Steps; i++)
            {
                bool ok;
                int value;
                try
                {
                    ok = reader.TryReadInt32(out value);
                }
                catch (Exception ex)
                {
                    // Exception type and message are part of the equivalence (spec §5.1).
                    steps.Add($"{i}: throw {ex.GetType().FullName}: {ex.Message}");
                    return steps;
                }
                steps.Add(Step(i, ok, value, ref reader));
            }
            return steps;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>The reference arm, inline: <c>Read()</c>, then <c>TryGetInt32()</c> — never the reference class.</summary>
    private static List<string> ReferenceWalk(byte[] payload, bool sequence)
    {
        var steps = new List<string>();
        var reader = sequence
            ? new JsonReader(new ReadOnlySequence<byte>(payload))
            : new JsonReader(payload.AsSpan());
        try
        {
            for (int i = 0; i < Steps; i++)
            {
                bool ok;
                int value = 0;
                try
                {
                    ok = reader.Read();
                    if (ok)
                        ok = reader.TryGetInt32(out value);
                }
                catch (Exception ex)
                {
                    steps.Add($"{i}: throw {ex.GetType().FullName}: {ex.Message}");
                    return steps;
                }
                steps.Add(Step(i, ok, ok ? value : 0, ref reader));
            }
            return steps;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static string Step(int index, bool ok, int value, ref JsonReader reader) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{index}: {ok}/{value} {reader.TokenType} pos={reader.BytesConsumed} d={reader.Depth} "
                + $"more={reader.NeedsMoreData} span={Hex(reader.ValueSpan)}"
        );

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return "-";
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string Join(List<string> steps) => string.Join(" | ", steps);
}
