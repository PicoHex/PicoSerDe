using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// The writer's long-payload path — a payload whose worst-case UTF-8 size does not fit the
/// 256-byte stack buffer, i.e. a member string of 85 characters or more. It encodes straight
/// into the writer's own span (quotes included) instead of going through
/// <c>value.ToArray()</c> and a full-length <see cref="Encoding.GetBytes(ReadOnlySpan{char})"/>.
/// </summary>
/// <remarks>
/// Two properties are pinned here, and they are independent of each other:
/// <list type="bullet">
/// <item>the <em>bytes</em> are exactly what the buffered path emits — the reference escaper
/// below is written out in the test and shares no code with the writer, so an escape table that
/// drifts, a lost byte or a cursor one short of the payload cannot pass;</item>
/// <item>the <em>allocation</em> is exactly zero for a payload that needs no escaping, measured
/// as an exact thread ledger rather than an average.</item>
/// </list>
/// </remarks>
public class JsonWriterLongPayloadTests
{
    // ── the payloads ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every length that crosses the 256-byte stack-buffer line (in <em>bytes</em>, which for
    /// multi-byte text is not the character count), plus every character class the encoder
    /// can turn into a different number of bytes than the char count suggests.
    /// </summary>
    private static IEnumerable<(string Label, string Text)> LongPayloads()
    {
        // 3 * n + 3 > 256  <=>  n >= 85: 84 stays on the stack buffer, 85 is the first
        // length that takes the new path, and every one of them is ASCII (1 byte/char).
        yield return ("84 ascii — last stack-buffer length", new string('x', 84));
        yield return ("85 ascii — first long-path length", new string('x', 85));
        yield return ("86 ascii", new string('x', 86));
        yield return ("255 ascii", new string('x', 255));
        yield return ("256 ascii", new string('x', 256));
        yield return ("257 ascii", new string('x', 257));
        yield return ("300 ascii", new string('x', 300));
        yield return ("512 ascii", new string('x', 512));
        yield return ("1024 ascii", new string('x', 1024));
        yield return ("4096 ascii", new string('x', 4096));

        // Same lengths in wider characters: the char count and the byte count diverge, and the
        // byte count is what every boundary in the writer is measured in.
        yield return ("85 latin-1 (2 B/char)", new string('é', 85));
        yield return ("1024 latin-1 (2 B/char)", new string('é', 1024));
        yield return ("1024 cjk (3 B/char)", new string('漢', 1024));
        yield return ("1024 non-BMP (4 B/char)", string.Concat(Enumerable.Repeat("😀", 512)));
        yield return ("zwj family emoji", string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 64)));
        yield return ("combining marks", string.Concat(Enumerable.Repeat("e\u0301", 600)));

        // Escapable payloads: the path must not be taken at all, and the fallback must still be
        // byte-identical to the buffered path it shares.
        yield return (
            "escapes everywhere",
            string.Concat(Enumerable.Repeat("ab\\cd\"ef\tgh\u0001", 64))
        );
        yield return ("control run (\\u0001)", new string('\u0001', 300));
        yield return ("nul run (\\u0000)", new string('\0', 300));
        yield return (
            "every escape class",
            string.Concat(Enumerable.Repeat("\u0000\u001f\"\\\n\r\t", 40))
        );
        yield return ("quote first", "\"" + new string('x', 1023));
        yield return ("quote last", new string('x', 1023) + "\"");
        yield return ("quote middle", new string('x', 512) + "\"" + new string('x', 512));
        yield return ("backslash only", "\\" + new string('x', 1024));
        yield return (
            "escape past the first vector",
            new string('x', 40) + "\"" + new string('x', 1000)
        );
        yield return (
            "escape at the very end of a 4-byte-char payload",
            string.Concat(Enumerable.Repeat("😀", 200)) + "\""
        );

        // Surrogates the encoder replaces: the replacement character is 3 bytes and none of them
        // is escapable, so the payload must travel verbatim on both paths.
        yield return ("lone high surrogate", new string('\uD800', 300));
        yield return ("lone low surrogate", new string('\uDC00', 300));
        yield return ("surrogate then ascii", string.Concat(Enumerable.Repeat("\uD800z", 300)));
    }

    // ── the reference: a scalar escaper, no production code involved ─────────

    private static byte[] Reference(ReadOnlySpan<byte> payload)
    {
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

    // ── the three routes to the same bytes ───────────────────────────────────

    /// <summary>The char overload — the route under test.</summary>
    private static byte[] WriteViaChars(string text)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        var writer = new JsonWriter(buffer);
        writer.WriteString(text.AsSpan());
        _ = writer.Buffer; // hand-off point: commits the pending span
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The buffered route: UTF-8 encoded into a fresh <c>byte[]</c> and written through the byte
    /// overload. That is what the char overload's long branch did before the span path existed
    /// (main's <c>Encoding.UTF8.GetBytes(value.ToArray())</c> — a <c>ReadOnlySpan&lt;char&gt;</c>
    /// copy, the same UTF-16 units — then the quoted-string writer), so it is the before-image the
    /// new path has to reproduce byte for byte.
    /// </summary>
    private static byte[] WriteViaBufferedPath(string text)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        var writer = new JsonWriter(buffer);
        writer.WriteString(Encoding.UTF8.GetBytes(text));
        _ = writer.Buffer;
        return buffer.WrittenSpan.ToArray();
    }

    // ── 1. same bytes, every payload ─────────────────────────────────────────

    [Test]
    public async Task LongPayloads_AreByteIdenticalToTheBufferedPath()
    {
        foreach (var (label, text) in LongPayloads())
        {
            var expected = WriteViaBufferedPath(text);
            var actual = WriteViaChars(text);
            await Assert
                .That(Hex(actual))
                .IsEqualTo(Hex(expected))
                .Because($"{label}: the span path must emit what the buffered path emitted");

            // ... and both must be what an independent scalar escaper says, so "identical"
            // cannot mean "identically wrong".
            await Assert
                .That(Hex(actual))
                .IsEqualTo(Hex(Reference(Encoding.UTF8.GetBytes(text))))
                .Because($"{label}: against the reference escaper");
        }
    }

    [Test]
    public async Task LongPayloadsWithoutEscapes_TravelVerbatimBetweenQuotes()
    {
        foreach (
            var text in new[]
            {
                new string('x', 85),
                new string('x', 1024),
                "漢" + new string('x', 1023),
            }
        )
        {
            var actual = Encoding.UTF8.GetString(WriteViaChars(text));
            await Assert.That(actual).IsEqualTo("\"" + text + "\"");
        }
    }

    [Test]
    public async Task LongPayloadWithEscapes_IsEscapedNotEmittedRaw()
    {
        // Named expectation rather than an equality against the other path: a fallback that
        // silently dropped the escapes would still agree with itself.
        var actual = Encoding.UTF8.GetString(WriteViaChars(new string('x', 512) + "\"\t\\"));
        await Assert.That(actual).IsEqualTo("\"" + new string('x', 512) + "\\\"\\t\\\\\"");
    }

    [Test]
    public async Task BytesWrittenCountsTheLongPayload()
    {
        var text = new string('x', 1024);
        var buffer = new ArrayBufferWriter<byte>(4096);
        var writer = new JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("Body"u8);
        writer.WriteString(text.AsSpan());
        writer.WriteEndObject();
        _ = writer.Buffer;
        await Assert.That(writer.BytesWritten).IsEqualTo((long)buffer.WrittenCount);
    }

    // ── 2. same bytes, every buffer boundary ─────────────────────────────────

    /// <summary>
    /// The block of the document written before the long payload, for the boundary sweep: one
    /// byte, so the writer's remaining space can be positioned to within a byte of what the
    /// payload needs.
    /// </summary>
    private static byte[] DocumentAtBoundary(int firstSpan, string text, int chunk = 256)
    {
        var buffer = new ExactSpanBufferWriter(firstSpan, chunk);
        var writer = new JsonWriter(buffer);
        writer.WriteStartObject(); // one byte: '{"Body":' puts 9 on its own, so start at '{'
        writer.WritePropertyName("Body"u8);
        writer.WriteString(text.AsSpan());
        writer.WriteEndObject();
        _ = writer.Buffer;
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The worst case the new path asks the buffer for, before the quotes: this is the size
    /// <see cref="JsonWriter"/> requests for the payload below, and every boundary case walks
    /// across it one byte at a time.
    /// </summary>
    private static int WorstCase(string text) => Encoding.UTF8.GetMaxByteCount(text.Length) + 2;

    [Test]
    public async Task LongPayloadSurvivesEveryBufferBoundary()
    {
        // "x" * 1024 is 1024 bytes; the writer asks for 3 * 1024 + 3 + 2 bytes. The prefix is
        // '{' plus '"Body":' = 9 bytes, so the remaining space lands on every offset around the
        // payload: short by 3, 2 and 1, exactly the payload, and 1..3 bytes spare.
        var text = new string('x', 1024);
        const int prefix = 9;
        var worst = WorstCase(text);

        var expected = Encoding.UTF8.GetString(WriteViaChars(text));

        for (var delta = -prefix - 3; delta <= 3; delta++)
        {
            var firstSpan = worst + delta;
            var bytes = DocumentAtBoundary(firstSpan, text);
            await Assert
                .That(Encoding.UTF8.GetString(bytes))
                .IsEqualTo($"{{\"Body\":{expected}}}")
                .Because(
                    $"first span {firstSpan} (= worst case {worst} {delta:+#;-#;+0}) must not change the bytes"
                );
        }
    }

    [Test]
    public async Task LongPayloadSurvivesEveryChunkSize()
    {
        var text = new string('x', 1024);
        var expected = $"{{\"Body\":{Encoding.UTF8.GetString(WriteViaChars(text))}}}";
        foreach (var chunk in new[] { 1, 2, 3, 7, 16, 17, 255, 256, 257, 1024, 1025, 4096 })
        {
            var bytes = DocumentAtBoundary(chunk, text, chunk);
            await Assert
                .That(Encoding.UTF8.GetString(bytes))
                .IsEqualTo(expected)
                .Because($"a buffer handing out {chunk}-byte chunks must not change the bytes");
        }
    }

    [Test]
    public async Task TwoLongPayloadsInARowAreBothComplete()
    {
        // The second payload starts with the span the first one left behind, which is shorter
        // than its own worst case: the writer has to flush and refetch, not write past the end.
        var first = new string('a', 1024);
        var second = new string('b', 1024);
        var buffer = new ExactSpanBufferWriter(WorstCase(first), 256);
        var writer = new JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("A"u8);
        writer.WriteString(first.AsSpan());
        writer.WritePropertyName("B"u8);
        writer.WriteString(second.AsSpan());
        writer.WriteEndObject();
        _ = writer.Buffer;
        await Assert
            .That(Encoding.UTF8.GetString(buffer.WrittenSpan))
            .IsEqualTo($"{{\"A\":\"{first}\",\"B\":\"{second}\"}}");
    }

    [Test]
    public async Task LongPayloadNeedingEscapesSurvivesAOneByteBuffer()
    {
        // The escaping fallback stages bytes it must never commit and then re-writes the payload
        // through the buffered path — on a buffer that hands out one byte at a time.
        var text = string.Concat(Enumerable.Repeat("a\"b\\c\td\u0001 ", 100));
        var buffer = new ExactSpanBufferWriter(256, 1);
        var writer = new JsonWriter(buffer);
        writer.WriteString(text.AsSpan());
        _ = writer.Buffer;
        await Assert
            .That(Hex(buffer.WrittenSpan.ToArray()))
            .IsEqualTo(Hex(Reference(Encoding.UTF8.GetBytes(text))));
    }

    // ── 3. a bare (depth 0) value still reaches the buffer ───────────────────

    [Test]
    public async Task TopLevelLongPayloadIsCommittedWithoutAHandOff()
    {
        // No container is open, so nothing will ever close and commit the tail: the bytes must
        // already be in the buffer when the call returns, exactly like the short path.
        var buffer = new ArrayBufferWriter<byte>(4096);
        var writer = new JsonWriter(buffer);
        writer.WriteString(new string('x', 1024).AsSpan());
        var committed = buffer.WrittenCount;
        await Assert.That(committed).IsEqualTo(1026);
    }

    // ── 4. allocation: zero for a payload that needs no escaping ─────────────

    private const int BatchIterations = 200;
    private const int MeasuredBatches = 9;
    private const int SettleBatches = 3;

    /// <summary>
    /// Bytes this thread allocated in each of <see cref="MeasuredBatches"/> batches, after the
    /// settle batches have paid for warm-up and any one-off buffer growth.
    /// </summary>
    private static long[] Batches(Action body)
    {
        for (var i = 0; i < SettleBatches * BatchIterations; i++)
            body();

        var batches = new long[MeasuredBatches];
        for (var b = 0; b < batches.Length; b++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < BatchIterations; i++)
                body();
            batches[b] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        return batches;
    }

    [Test]
    public async Task LongPayloadWithoutEscapesAllocatesNothing()
    {
        // The buffer is created once, outside the measured window: reusing it is what a caller
        // holding a writer does, and it makes the window's only possible allocation the
        // payload's own. (The rented-writer path a serializer takes is covered separately.)
        var text = new string('x', 1024);
        var buffer = new ArrayBufferWriter<byte>(4096);
        long smallest;
        long[] all;
        {
            var batches = Batches(() =>
            {
                var writer = new JsonWriter(buffer);
                writer.WriteString(text.AsSpan());
                _ = writer.Buffer;
                buffer.Clear();
            });
            smallest = batches.Min();
            all = batches;
        }

        await Assert
            .That(smallest)
            .IsEqualTo(0L)
            .Because(
                $"a 1 KB payload that needs no escaping must cost zero allocations; batches [{string.Join(", ", all)}]"
            );
    }

    [Test]
    public async Task TheAllocationProbeSeesTheBufferedPath()
    {
        // Sensitivity of the probe above: the route the writer used to take allocates its two
        // heap copies, and the same measurement must see them. Without this, "zero" could be a
        // probe that measures nothing.
        var text = new string('x', 1024);
        {
            var batches = Batches(() => _ = WriteViaBufferedPath(text));
            var perOp = batches.Min() / (double)BatchIterations;
            await Assert
                .That(perOp)
                .IsGreaterThan(1000.0)
                .Because(
                    $"the buffered path allocates a char[] and a byte[] per call; batches [{string.Join(", ", batches)}]"
                );
        }
    }

    [Test]
    public async Task TwoDocumentsThroughTheRentedBufferAreIdentical()
    {
        // The serializer rents one ArrayBufferWriter per thread and never shrinks it, while the
        // long path asks it for the worst case (3 * chars + 3 + 2). So the first document grows
        // the rented buffer and the second one runs on whatever capacity that left — a stale
        // span or a short tail would show up as a different second document.
        var text = new string('x', 1024);
        var first = WriteThroughRentedBuffer(text);
        var second = WriteThroughRentedBuffer(text);
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(first).IsEqualTo($"{{\"Body\":\"{text}\"}}");
    }

    private static string WriteThroughRentedBuffer(string text)
    {
        var buffer = SerializerExtensions.RentWriter();
        var writer = new JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("Body"u8);
        writer.WriteString(text.AsSpan());
        writer.WriteEndObject();
        _ = writer.Buffer;
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>
    /// A buffer writer whose first span is exactly <paramref name="firstSpan"/> bytes — so the
    /// writer's remaining space after the document prefix lands on a chosen offset — and whose
    /// later spans are <paramref name="chunk"/> bytes or whatever was asked for, whichever is
    /// larger. Both honour <see cref="IBufferWriter{T}.GetSpan"/>'s contract (never smaller than
    /// the hint), so a short span here is the writer's own boundary logic under test, not a
    /// contract violation.
    /// </summary>
    private sealed class ExactSpanBufferWriter : IBufferWriter<byte>
    {
        private readonly int _firstSpan;
        private readonly int _chunk;
        private byte[] _buffer;
        private int _index;
        private bool _first = true;

        public ExactSpanBufferWriter(int firstSpan, int chunk)
        {
            _firstSpan = firstSpan;
            _chunk = Math.Max(chunk, 1);
            _buffer = new byte[Math.Max(firstSpan, _chunk)];
        }

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

        public void Advance(int count)
        {
            if (count < 0 || _index + count > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "Advance past the end.");
            _index += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            var need = Math.Max(sizeHint, 1);
            int available;
            if (_first)
            {
                _first = false;
                // A hint larger than the configured first span wins: the contract is "at least
                // the hint", and the offset sweep only needs the span to be exact when the
                // document prefix is what has already been written into it.
                available = Math.Max(need, _firstSpan);
            }
            else
            {
                available = Math.Max(need, _chunk);
            }

            if (_index + available > _buffer.Length)
                Array.Resize(ref _buffer, _index + available);
            return _buffer.AsSpan(_index, available);
        }
    }
}
