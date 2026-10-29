using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// Equivalence proof for the writer's escape path. Whatever route the writer takes to decide
/// that a payload needs escaping — the vectorized plain-string fast path, the scalar escape
/// table behind it, or the char overload's stack buffer — the bytes it emits must be exactly
/// what a byte-at-a-time scalar escaper emits for the same payload.
/// </summary>
/// <remarks>
/// The reference escaper below is written out in the test and shares no code with
/// <c>JsonWriter</c>, so a change to the writer's escaping table (a shorthand added, a
/// control byte dropped, the hex case flipped) fails here instead of only in a full pipeline
/// test. It also pins the two decisions this writer has always made: <c>\b</c> and <c>\f</c>
/// travel as <c>\u0008</c> / <c>\u000C</c> (legal JSON, no shorthand emitted), and <c>/</c>
/// as well as every byte at or above 0x20 that is not <c>"</c> or <c>\</c> is written
/// verbatim (escaped forward slashes are optional; non-ASCII stays raw).
/// </remarks>
public class JsonWriterEscapeEquivalenceTests
{
    /// <summary>Every length that crosses a scan width (16/32/64) or the 256-byte line.</summary>
    private static readonly int[] Lengths =
    [
        0,
        1,
        2,
        3,
        15,
        16,
        17,
        31,
        32,
        33,
        63,
        64,
        65,
        127,
        128,
        129,
        // 254 is the last length the single-pass ASCII string path takes; 255 and up are
        // produced by the encoding route, so the boundary itself has to be pinned.
        253,
        254,
        255,
        256,
        257,
        300,
        511,
        512,
        1024,
    ];

    /// <summary>The bytes the escaping table reacts to, one of each class.</summary>
    private static readonly byte[] StopBytes = [(byte)'"', (byte)'\\', 0x00, 0x01, 0x09, 0x1F];

    // ── the reference: a scalar escaper, no production code involved ──────────

    private static byte[] Reference(ReadOnlySpan<byte> payload)
    {
        // Bytes out, never a StringBuilder of chars: a payload byte at or above 0x20 is
        // copied through untouched, including bytes that are not valid UTF-8 on their own.
        var output = new List<byte>(payload.Length + 8) { (byte)'"' };
        foreach (var b in payload)
        {
            switch (b)
            {
                case (byte)'"':
                    output.Add((byte)'\\');
                    output.Add((byte)'"');
                    break;
                case (byte)'\\':
                    output.Add((byte)'\\');
                    output.Add((byte)'\\');
                    break;
                case (byte)'\n':
                    output.Add((byte)'\\');
                    output.Add((byte)'n');
                    break;
                case (byte)'\r':
                    output.Add((byte)'\\');
                    output.Add((byte)'r');
                    break;
                case (byte)'\t':
                    output.Add((byte)'\\');
                    output.Add((byte)'t');
                    break;
                default:
                    if (b < 0x20)
                    {
                        output.Add((byte)'\\');
                        output.Add((byte)'u');
                        output.Add((byte)'0');
                        output.Add((byte)'0');
                        var hex = b.ToString("X2", CultureInfo.InvariantCulture);
                        output.Add((byte)hex[0]);
                        output.Add((byte)hex[1]);
                    }
                    else
                    {
                        output.Add(b);
                    }
                    break;
            }
        }
        output.Add((byte)'"');
        return [.. output];
    }

    // ── the writer, one helper per entry point ───────────────────────────────

    private static byte[] WriteViaBytes(ReadOnlySpan<byte> payload)
    {
        var buf = new ArrayBufferWriter<byte>(1024);
        var w = new JsonWriter(buf);
        w.WriteString(payload);
        _ = w.Buffer; // hand-off point: commits the pending span
        return buf.WrittenSpan.ToArray();
    }

    private static byte[] WriteViaChars(string payload)
    {
        var buf = new ArrayBufferWriter<byte>(1024);
        var w = new JsonWriter(buf);
        w.WriteString(payload.AsSpan());
        _ = w.Buffer;
        return buf.WrittenSpan.ToArray();
    }

    private static byte[] WriteViaPropertyName(ReadOnlySpan<byte> payload)
    {
        var buf = new ArrayBufferWriter<byte>(1024);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName(payload);
        w.WriteNumber(1);
        w.WriteEndObject();
        _ = w.Buffer;
        var rendered = buf.WrittenSpan.ToArray();
        // {"<name>":1} -> the name's own bytes, quotes included
        return rendered.AsSpan(1, rendered.Length - 4).ToArray();
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [Test]
    public async Task EverySingleByteValue_MatchesTheScalarReference()
    {
        for (var b = 0; b <= 0xFF; b++)
        {
            byte[] payload = [(byte)b];
            await Assert.That(Hex(WriteViaBytes(payload))).IsEqualTo(Hex(Reference(payload)));
        }
    }

    [Test]
    public async Task EveryStopByteAtEveryPositionOfEveryLength_MatchesTheScalarReference()
    {
        // The vectorized scan returns as soon as it finds a hit, so the interesting cases
        // are a hit in the first vector, mid-vector, in a later vector, and in the scalar
        // tail. Every (length, position, byte) combination below covers all of them.
        foreach (var length in Lengths)
        {
            foreach (var stop in StopBytes)
            {
                for (var pos = 0; pos < length; pos++)
                {
                    var payload = Filled(length, (byte)'x');
                    payload[pos] = stop;
                    var actual = WriteViaBytes(payload);
                    var expected = Reference(payload);
                    if (!actual.AsSpan().SequenceEqual(expected))
                        throw new InvalidOperationException(
                            $"length={length} pos={pos} byte=0x{stop:X2}\n"
                                + $"  actual  ={Hex(actual)}\n  expected={Hex(expected)}"
                        );
                }
            }
        }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task PlainPayloadsOfEveryLength_AreWrittenVerbatimBetweenQuotes()
    {
        foreach (var length in Lengths)
        {
            var payload = Filled(length, (byte)'x');
            await Assert.That(Hex(WriteViaBytes(payload))).IsEqualTo(Hex(Reference(payload)));
        }
    }

    [Test]
    public async Task RepeatedEscapesAndMixedPayloads_MatchTheScalarReference()
    {
        byte[] payload =
        [
            (byte)'a',
            (byte)'"',
            (byte)'b',
            (byte)'\\',
            (byte)'c',
            (byte)'\t',
            (byte)'d',
            (byte)'\r',
            (byte)'e',
            (byte)'\n',
            (byte)'f',
            0x00,
            (byte)'g',
            0x1F,
            (byte)'h',
            (byte)'/',
            (byte)'i',
            0x7F,
        ];
        await Assert.That(Hex(WriteViaBytes(payload))).IsEqualTo(Hex(Reference(payload)));
    }

    [Test]
    public async Task EscapeShorthandsAndControlTable_AreUnchanged()
    {
        // Named expectations, so a rewrite of the table cannot quietly redefine "correct".
        await Assert.That(WriteText("\b"u8)).IsEqualTo("\"\\u0008\"");
        await Assert.That(WriteText("\f"u8)).IsEqualTo("\"\\u000C\"");
        await Assert.That(WriteText("\n"u8)).IsEqualTo("\"\\n\"");
        await Assert.That(WriteText("\r"u8)).IsEqualTo("\"\\r\"");
        await Assert.That(WriteText("\t"u8)).IsEqualTo("\"\\t\"");
        await Assert.That(WriteText("\""u8)).IsEqualTo("\"\\\"\"");
        await Assert.That(WriteText("\\"u8)).IsEqualTo("\"\\\\\"");
        await Assert.That(WriteText("/"u8)).IsEqualTo("\"/\"");
        await Assert.That(WriteText("\u0000"u8)).IsEqualTo("\"\\u0000\"");
        await Assert.That(WriteText("\u001F"u8)).IsEqualTo("\"\\u001F\"");
        await Assert.That(WriteText("\u007F"u8)).IsEqualTo("\"\u007F\"");
        await Assert.That(WriteText("é"u8)).IsEqualTo("\"é\"");
    }

    [Test]
    public async Task NonAsciiSurrogatePairsAndEmoji_AreWrittenVerbatim()
    {
        foreach (
            var payload in new[]
            {
                "非 ASCII 漢字 · Ω · ñ",
                "emoji 😀🎉",
                "zwj 👨‍👩‍👧‍👦 family",
                "surrogate pair 𐒠𝄞 𝕏",
                "mixed \"q\" \u0001 漢字 😀",
            }
        )
        {
            var utf8 = Encoding.UTF8.GetBytes(payload);
            await Assert.That(Hex(WriteViaChars(payload))).IsEqualTo(Hex(Reference(utf8)));
            await Assert.That(Hex(WriteViaBytes(utf8))).IsEqualTo(Hex(Reference(utf8)));
        }
    }

    [Test]
    public async Task InvalidUtf8IsWrittenVerbatimAndNotValidated()
    {
        // Documented behaviour: the byte overload escapes but does not validate, so an
        // invalid sequence passes through unchanged rather than throwing or being replaced.
        byte[] payload = [0xC3, 0x28, 0xFF, 0x80, (byte)'"', 0xF0, 0x9F];
        await Assert.That(Hex(WriteViaBytes(payload))).IsEqualTo(Hex(Reference(payload)));
    }

    [Test]
    public async Task CharAndByteOverloads_ProduceTheSameBytes()
    {
        foreach (var length in Lengths)
        {
            var plain = new string('x', length);
            await Assert
                .That(Hex(WriteViaChars(plain)))
                .IsEqualTo(Hex(WriteViaBytes(Encoding.UTF8.GetBytes(plain))));
            if (length == 0)
                continue;
            foreach (var (ch, at) in new[] { ('"', 0), ('\\', length / 2), ('\u001f', length - 1) })
            {
                var chars = new char[length];
                Array.Fill(chars, 'x');
                chars[at] = ch;
                var text = new string(chars);
                await Assert
                    .That(Hex(WriteViaChars(text)))
                    .IsEqualTo(Hex(WriteViaBytes(Encoding.UTF8.GetBytes(text))));
            }
        }
    }

    [Test]
    public async Task PropertyNamesEscapeExactlyLikeValues()
    {
        foreach (var stop in StopBytes)
        {
            var name = Filled(40, (byte)'n');
            name[7] = stop;
            await Assert.That(Hex(WriteViaPropertyName(name))).IsEqualTo(Hex(Reference(name)));
        }
        var longName = Filled(512, (byte)'n');
        await Assert.That(Hex(WriteViaPropertyName(longName))).IsEqualTo(Hex(Reference(longName)));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string WriteText(ReadOnlySpan<byte> payload) =>
        Encoding.UTF8.GetString(WriteViaBytes(payload));

    private static byte[] Filled(int length, byte value)
    {
        var buf = new byte[length];
        buf.AsSpan().Fill(value);
        return buf;
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
