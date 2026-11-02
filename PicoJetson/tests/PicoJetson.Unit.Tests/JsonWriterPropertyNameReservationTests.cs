using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// A buffer writer that hands out the <em>smallest</em> span the
/// <see cref="IBufferWriter{T}"/> contract allows, so that a write reserving one byte too few
/// runs off the end of the span instead of disappearing into a rounded-up allocation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArrayBufferWriter{T}"/> hides an under-reservation: it rounds every request up and
/// keeps growing, so the writer's span is always larger than the token and an off-by-one in the
/// reservation lands in unused memory. The contract promises only <em>at least</em> the size
/// hint, so a buffer that honours the hint and nothing more is legal — and it converts a
/// reservation short by one byte into an <see cref="IndexOutOfRangeException"/> at exactly the
/// byte the token overruns.
/// </para>
/// <para>
/// Two grants are offered. <see cref="ExactHintBufferWriter"/> is the tightest legal one and is
/// the shape every claim in this file is made against. <see cref="CappedHintBufferWriter"/> goes
/// below the hint for spans larger than its cap, which the interface does not permit — it is an
/// adversarial capacity, useful only where a whole token still fits in one grant.
/// </para>
/// </remarks>
public abstract class HintOnlyBufferWriter : IBufferWriter<byte>
{
    private byte[] _buffer = [];
    private int _index;

    /// <summary>The bytes the writer committed, i.e. the document.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    public void Advance(int count)
    {
        if (count < 0 || _index + count > _buffer.Length)
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "Advance past the end of the buffer."
            );
        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        // Reserve may grow the backing array, so it has to run before the receiver is bound.
        var granted = Reserve(sizeHint);
        return _buffer.AsMemory(_index, granted);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        var granted = Reserve(sizeHint);
        return _buffer.AsSpan(_index, granted);
    }

    private int Reserve(int sizeHint)
    {
        var need = Math.Max(sizeHint, 1);
        if (_index + need > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_index + need, 64));
        return Grant(need);
    }

    /// <summary>How many bytes of the reserved <paramref name="need"/> are actually handed out.</summary>
    protected abstract int Grant(int need);
}

/// <summary>Grants exactly the size hint — the tightest span the contract allows.</summary>
public sealed class ExactHintBufferWriter : HintOnlyBufferWriter
{
    protected override int Grant(int need) => need;
}

/// <summary>
/// Grants <c>min(need, cap)</c>, i.e. less than the size hint once the hint exceeds
/// <paramref name="cap"/>.
/// </summary>
public sealed class CappedHintBufferWriter(int cap) : HintOnlyBufferWriter
{
    protected override int Grant(int need) => Math.Min(need, cap);
}

/// <summary>
/// The reservation <see cref="JsonWriter.WritePropertyName(ReadOnlySpan{byte})"/> makes for the
/// fused <c>"name":</c> token, proven against the tightest span a buffer may hand out.
/// </summary>
/// <remarks>
/// <para>
/// BU-1. The fused write stages <c>,</c> (optional) <c>"</c> name <c>"</c> <c>:</c> behind one
/// <c>Ensure(n + 3)</c>, but the comma arm is <c>n + 4</c> bytes. <c>Ensure</c> only refills when
/// the current span has <em>fewer</em> than <c>n + 3</c> bytes left, so a span with exactly
/// <c>n + 3</c> left passes the check and the colon lands one byte past the end. Hence a buffer
/// that grants the hint and no more.
/// </para>
/// <para>
/// Measured on <c>ea4f514</c>, before the fix. A 256-byte tight span fails for every name of
/// 252 bytes or more (255 of them for the name, one for the value's own byte, leaving exactly
/// <c>n + 3</c>); aiming the cursor, every length from 0 to 245 fails; the smallest trigger of
/// all is a 0-byte name with exactly three bytes left. The suite's existing buffers are blind
/// for most of that range — <see cref="TinyChunkBufferWriter"/> grants at least the hint (it
/// fails only from 252 up), and <see cref="ArrayBufferWriter{T}"/> grows by doubling, so it
/// lands on the window only at its own lengths: <c>new ArrayBufferWriter&lt;byte&gt;()</c> 252,
/// capacity 8 → 128 and 260, 64 → 156, 1024 → 508 and 1020, 4096 → none up to 1200.
/// </para>
/// </remarks>
public class JsonWriterPropertyNameReservationTests
{
    private delegate void WriterAction(ref JsonWriter w);

    private static (string Json, long Bytes) Through(
        HintOnlyBufferWriter buffer,
        WriterAction write
    )
    {
        var w = new JsonWriter(buffer);
        write(ref w);
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), w.BytesWritten);
    }

    private static byte[] Name(string s) => Encoding.UTF8.GetBytes(s);

    // ── 1. every name length, from a span that grants exactly what is asked ──

    [Test]
    public async Task EveryNameLength_FromATightSpan_IsWrittenByteForByte()
    {
        // 0..300 covers the empty name, the vectorized escape scan's edges (15/16/17), the
        // MinSpanSize line (255/256/257) and — from 253 up — the lengths where the name's own
        // reservation, not MinSpanSize, decides how large the span is.
        for (var n = 0; n <= 300; n++)
        {
            var name = new string('a', n);
            var (json, bytes) = Through(
                new ExactHintBufferWriter(),
                (ref JsonWriter w) =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName(Name(name));
                    w.WriteNumber(0);
                    w.WritePropertyName(Name(name));
                    w.WriteNumber(1);
                    w.WriteEndObject();
                }
            );

            await Assert
                .That(json)
                .IsEqualTo($"{{\"{name}\":0,\"{name}\":1}}")
                .Because($"a {n}-byte name must be written verbatim");
            await Assert
                .That(bytes)
                .IsEqualTo(Encoding.UTF8.GetByteCount(json))
                .Because($"BytesWritten must match the buffer for a {n}-byte name");
        }
    }

    // ── 2. the boundary aimed at the exact byte, for every short name length ──

    [Test]
    public async Task ANameAlignedExactlyOnItsReservation_StillFits()
    {
        // `{` is committed at depth 0, so the next write refills a 256-byte span; `"pad":`
        // takes 6 of it and a k-char value 2 more, leaving 248 - k. Padding the value to
        // k = 245 - n leaves exactly n + 3 bytes — the amount the old reservation asked for and
        // one byte short of `,"name":`. Every n from 0 to 245 is aimed at that byte rather than
        // sampled, so the window is proven for all of them and not for a lucky one.
        for (var n = 0; n <= 245; n++)
        {
            var name = new string('b', n);
            var pad = new string('p', 245 - n);
            var (json, bytes) = Through(
                new ExactHintBufferWriter(),
                (ref JsonWriter w) =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName("pad"u8);
                    w.WriteString(pad.AsSpan());
                    w.WritePropertyName(name);
                    w.WriteNumber(1);
                    w.WriteEndObject();
                }
            );

            await Assert
                .That(json)
                .IsEqualTo($"{{\"pad\":\"{pad}\",\"{name}\":1}}")
                .Because($"a {n}-byte name starting with one byte less than it needs must refill");
            await Assert
                .That(bytes)
                .IsEqualTo(Encoding.UTF8.GetByteCount(json))
                .Because($"BytesWritten must match the buffer for a {n}-byte name");
        }
    }

    // ── 3. exhaustion: keep writing names until the span has to be refilled ──

    [Test]
    public async Task NamesWrittenUntilTheSpanRefills_StayByteIdentical()
    {
        // About 25 KB of members — roughly a hundred 256-byte spans — with the name lengths
        // chosen to sweep the whole boundary cycle (0, 1, 2, 3, 7, … 300): every refill lands on
        // a different byte of a token, so this walks the boundaries rather than picking one.
        int[] lengths = [0, 1, 2, 3, 7, 15, 16, 17, 31, 63, 64, 127, 253, 254, 255, 256, 257, 300];
        var expected = new StringBuilder("{");
        var count = 0;
        for (var round = 0; round < 12; round++)
        {
            foreach (var length in lengths)
            {
                var name = new string((char)('a' + round % 26), length);
                if (count++ > 0)
                    expected.Append(',');
                expected
                    .Append('"')
                    .Append(name)
                    .Append("\":")
                    .Append((round * 1000 + length).ToString(CultureInfo.InvariantCulture));
            }
        }
        expected.Append('}');

        var (json, bytes) = Through(
            new ExactHintBufferWriter(),
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                for (var round = 0; round < 12; round++)
                    foreach (var length in lengths)
                    {
                        w.WritePropertyName(Name(new string((char)('a' + round % 26), length)));
                        w.WriteNumber(round * 1000 + length);
                    }
                w.WriteEndObject();
            }
        );

        await Assert
            .That(json)
            .IsEqualTo(expected.ToString())
            .Because("a refill between two members must not move a single byte");
        await Assert.That(bytes).IsEqualTo(Encoding.UTF8.GetByteCount(json));
    }

    // ── 4. spans capped below the hint, as far as a token can still fit ──

    [Test]
    public async Task ASpanCappedBelowTheHint_IsByteIdenticalWhileTheTokenFits()
    {
        // `,"name":` is staged in one go, so it needs n + 4 bytes in a single grant: that is the
        // window this matrix covers (n + 4 <= cap). A cap below it is not reachable by any
        // implementation of this write — the buffer will never hand out more than `cap` bytes,
        // and a buffer that returns less than the size hint breaks IBufferWriter<T> anyway.
        int[] caps = [4, 8, 16, 32, 64];
        foreach (var cap in caps)
        {
            for (var n = 0; n + 4 <= cap; n++)
            {
                var name = new string('c', n);
                var (json, bytes) = Through(
                    new CappedHintBufferWriter(cap),
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WritePropertyName(Name(name));
                        w.WriteNumber(0);
                        w.WritePropertyName(Name(name));
                        w.WriteNumber(1);
                        w.WriteEndObject();
                    }
                );

                await Assert
                    .That(json)
                    .IsEqualTo($"{{\"{name}\":0,\"{name}\":1}}")
                    .Because($"cap {cap}, name length {n}: the token fits in one grant");
                await Assert
                    .That(bytes)
                    .IsEqualTo(Encoding.UTF8.GetByteCount(json))
                    .Because($"cap {cap}, name length {n}: BytesWritten must match the buffer");
            }
        }
    }

    // ── 5. the buffer every caller already uses, aimed at the same byte ──

    [Test]
    public async Task TheDefaultArrayBufferWriter_IsNotWideEnoughForA252ByteName()
    {
        // The claim "ArrayBufferWriter rounds the request up, so it hides this" is only half
        // true: it grows by doubling, so a span can land on exactly the reservation. Measured
        // on ea4f514 — `new ArrayBufferWriter<byte>()` grants 256, then 511 bytes — the window
        // is reached at n = 252, and every other initial capacity has its own n (8 -> 128,
        // 64 -> 156, 1024 -> 508 and 1020, 4096 -> none up to 1200). The tight-span tests above
        // are the alignment-independent proof; this one keeps the reach in a buffer the
        // library itself hands out, so the defect cannot look like a test artefact.
        var name = new string('a', 252);
        var buffer = new ArrayBufferWriter<byte>();
        var w = new JsonWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName(Name(name));
        w.WriteNumber(0);
        w.WritePropertyName(Name(name));
        w.WriteNumber(1);
        w.WriteEndObject();

        await Assert
            .That(Encoding.UTF8.GetString(buffer.WrittenSpan))
            .IsEqualTo($"{{\"{name}\":0,\"{name}\":1}}");
    }
}
