namespace PicoJetson.Tests;

// ── Models ──

public class DoubleRangeModel
{
    public double V { get; set; }
}

public class FloatRangeModel
{
    public float V { get; set; }
}

/// <summary>
/// JSON number range (review 2026-10-23, "Strict 接受 <c>1e400</c> → ∞"): a number literal whose value
/// is not representable as a <see cref="double"/> must not come back as
/// <see cref="double.PositiveInfinity"/>.
/// <para>
/// <b>Why this is a defect at all, measured rather than assumed.</b> <c>Utf8Parser.TryParse</c>
/// answers <c>true</c> with <c>∞</c> for <c>"1e400"</c> on this runtime, so the reader's only range
/// check — the token-type dispatch in <see cref="JsonReader.TryGetFloat64"/> — never fires and a
/// literal the writer itself refuses to emit (<c>JsonWriter</c> throws for <c>∞</c> in Strict mode)
/// comes out of the read side as a value. Strict JSON is documented as "NaN/Infinity throw during
/// serialization <i>and</i> deserialization" (<see cref="JsonOptions.NumberHandling"/>), so deriving
/// <c>∞</c> from digits contradicts the mode the reader is in: the named literal <c>Infinity</c> is
/// refused in Strict, and the digits <c>1e400</c> must not be a back door to the same value.
/// </para>
/// <para>
/// <b>The lax half, and why it is not a second policy.</b> <see
/// cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/> is the one option that lets a
/// non-finite value out of the reader; an out-of-range literal is a non-finite value, so the same
/// option governs it — <c>±∞</c> on read, no new switch and no clamping to
/// <see cref="double.MaxValue"/>. Named literals are untouched in both modes: the range check sits on
/// the <c>Utf8Parser</c> arm, so <c>Infinity</c>/<c>-Infinity</c>/<c>NaN</c> keep answering what
/// <see cref="JsonReader.Read"/> already decided for them.
/// </para>
/// <para>
/// Coverage is per <b>read path</b>, not just per value: the span reader
/// (<see cref="JsonReader"/>'s <c>ReadNumberSpan</c>), the sequence reader (<c>ReadNumberSeq</c>, the
/// streaming mode), the source-generated member read and the streaming deserializer all have to
/// answer the same way — a guard on one arm is exactly the "fast path and reference path disagree"
/// class of bug.
/// </para>
/// </summary>
public class JsonFloatRangeTests
{
    private static JsonOptions Lax() =>
        new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    // Reader-level probes: a ref struct cannot be captured by a lambda, so the failure is captured in
    // a helper that returns it (null = the read answered a value).

    private static Exception? SpanRead(string literal, JsonOptions? options = null)
    {
        try
        {
            var reader = new JsonReader(Encoding.UTF8.GetBytes(literal), options: options);
            reader.Read();
            if (!reader.TryGetFloat64(out var value))
                return new FormatException($"TryGetFloat64 answered false for '{literal}'.");
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? SequenceRead(string literal, JsonOptions? options = null)
    {
        try
        {
            // Three segments: the sequence arm must not depend on the literal sitting in one buffer.
            var bytes = Encoding.UTF8.GetBytes(literal);
            var last = new Segment(new ReadOnlyMemory<byte>(bytes, bytes.Length - 1, 1), null);
            var middle = new Segment(new ReadOnlyMemory<byte>(bytes, 1, bytes.Length - 2), last);
            var head = new Segment(new ReadOnlyMemory<byte>(bytes, 0, 1), middle);
            var sequence = new ReadOnlySequence<byte>(head, 0, last, 1);
            var reader = new JsonReader(sequence, options: options);
            reader.Read();
            if (!reader.TryGetFloat64(out var value))
                return new FormatException($"TryGetFloat64 answered false for '{literal}'.");
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static double SpanReadValue(string literal, JsonOptions? options = null)
    {
        var reader = new JsonReader(Encoding.UTF8.GetBytes(literal), options: options);
        reader.Read();
        if (!reader.TryGetFloat64(out var value))
            throw new FormatException($"TryGetFloat64 answered false for '{literal}'.");
        return value;
    }

    /// <summary>Minimal <see cref="ReadOnlySequenceSegment{T}"/> so a literal can straddle buffers.</summary>
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, Segment? next)
        {
            Memory = memory;
            if (next is not null)
                next.RunningIndex = RunningIndex + memory.Length;
            Next = next;
        }
    }

    // ── Strict (the default) ──

    [Test]
    public async Task Strict_Double_1e400_Throws()
    {
        var ex = Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":1e400}"""u8)
        );
        await Assert.That(ex!.Message).Contains("out of range");
    }

    [Test]
    public async Task Strict_Double_Negative1e400_Throws()
    {
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":-1e400}"""u8)
        );
    }

    [Test]
    public async Task Strict_Double_1e309_Throws()
    {
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":1e309}"""u8)
        );
    }

    /// <summary>
    /// The literal does not have to look like a float to be out of range: <c>1e999999</c> is an
    /// exponent past any double, and a 400-digit integer is a magnitude past any double — both reach
    /// <see cref="JsonReader.TryGetFloat64"/> through a different token (<c>Float64</c> vs the
    /// <c>Int64</c> fallback <c>ReadNumberSpan</c> uses when int/long/ulong all decline) and must be
    /// refused on both.
    /// </summary>
    [Test]
    public async Task Strict_Double_1e999999_Throws()
    {
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":1e999999}"""u8)
        );
    }

    [Test]
    public async Task Strict_Double_HugeIntegerLiteral_Throws()
    {
        var digits = new string('9', 400);
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<DoubleRangeModel>(
                Encoding.UTF8.GetBytes($"{{\"V\":{digits}}}")
            )
        );
    }

    [Test]
    public async Task Strict_Double_1_7e308_ReadsTheValue()
    {
        var model = JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":1.7e308}"""u8)!;
        await Assert.That(model.V).IsEqualTo(1.7e308);
    }

    [Test]
    public async Task Strict_Double_MaxValueLiteral_ReadsMaxValue()
    {
        var model = JsonSerializer.Deserialize<DoubleRangeModel>(
            """{"V":1.7976931348623157e308}"""u8
        )!;
        await Assert.That(model.V).IsEqualTo(double.MaxValue);
    }

    [Test]
    public async Task Strict_Float_1e400_Throws()
    {
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<FloatRangeModel>("""{"V":1e400}"""u8)
        );
    }

    [Test]
    public async Task Strict_SpanReader_1e400_Throws()
    {
        var ex = SpanRead("1e400");
        await Assert.That(ex).IsNotNull().Because("the span arm must reject the literal");
        await Assert.That(ex!.GetType()).IsEqualTo(typeof(FormatException));
    }

    [Test]
    public async Task Strict_SequenceReader_1e400_Throws()
    {
        var ex = SequenceRead("1e400");
        await Assert.That(ex).IsNotNull().Because("the sequence arm must reject the literal");
        await Assert.That(ex!.GetType()).IsEqualTo(typeof(FormatException));
    }

    [Test]
    public async Task Strict_Streaming_1e400_Throws()
    {
        using var stream = new MemoryStream("""{"V":1e400}"""u8.ToArray());
        Exception? thrown = null;
        try
        {
            await JsonSerializer.DeserializeFromStreamAsync<DoubleRangeModel>(stream);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull().Because("the chunked path must reject it too");
        await Assert.That(thrown!.GetType()).IsEqualTo(typeof(FormatException));
    }

    // ── Lax (AllowNamedFloatingPointLiterals): the one mode that admits a non-finite value ──

    [Test]
    public async Task Lax_Double_1e400_ReadsInfinity()
    {
        var model = JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":1e400}"""u8, Lax())!;
        await Assert.That(double.IsPositiveInfinity(model.V)).IsTrue();
    }

    [Test]
    public async Task Lax_Double_Negative1e400_ReadsNegativeInfinity()
    {
        var model = JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":-1e400}"""u8, Lax())!;
        await Assert.That(double.IsNegativeInfinity(model.V)).IsTrue();
    }

    [Test]
    public async Task Lax_Double_1e309_ReadsInfinity()
    {
        var model = JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":1e309}"""u8, Lax())!;
        await Assert.That(double.IsPositiveInfinity(model.V)).IsTrue();
    }

    [Test]
    public async Task Lax_Double_1_7e308_ReadsTheValue()
    {
        var model = JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":1.7e308}"""u8, Lax())!;
        await Assert.That(model.V).IsEqualTo(1.7e308);
    }

    /// <summary>
    /// The named literals are a different construct: <see cref="JsonReader.Read"/> decides them before
    /// any number digit is parsed, so the range check must not reach them. Both edges are pinned — lax
    /// keeps answering <c>∞</c>, Strict keeps refusing the same payload.
    /// </summary>
    [Test]
    public async Task Lax_NamedInfinityLiteral_StillReadsInfinity()
    {
        var model = JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":Infinity}"""u8, Lax())!;
        await Assert.That(double.IsPositiveInfinity(model.V)).IsTrue();
    }

    [Test]
    public async Task Strict_NamedInfinityLiteral_StillThrows()
    {
        Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<DoubleRangeModel>("""{"V":Infinity}"""u8)
        );
    }

    /// <summary>
    /// Underflow is not overflow: <c>1e-400</c> rounds to <c>0</c> — the same value
    /// <see cref="double.TryParse(string, out double)"/> answers — so it must not start throwing.
    /// </summary>
    [Test]
    public async Task Strict_Double_1e_400_ReadsZero()
    {
        await Assert.That(SpanReadValue("1e-400")).IsEqualTo(0d);
    }
}
