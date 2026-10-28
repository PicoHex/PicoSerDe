namespace PicoMsgPack.Tests;

/// <summary>
/// Pins the <c>TryGetInt32</c> / <c>TryGetInt64</c> contract for every integer
/// encoding a MsgPack document can use, and for every non-integer token.
/// <para>
/// Why this file exists: those two accessors no longer re-derive the value from
/// <c>TokenType</c> + <c>_valueSpan.Length</c> — <see cref="MsgPackReader"/> derives
/// it inside the tokenizer and the accessors are a range check on the cached
/// value. The observable contract must be exactly the old one, including the
/// failure paths a fast path could quietly change: a <c>uint64</c> above
/// <c>long.MaxValue</c> must not fit a <c>long</c> target, an <c>int64</c> outside
/// the <c>int</c> range must not fit an <c>int</c> target, a non-integer token must
/// report false, a token that is not an integer must never see the previous
/// token's value, and an accessor called before the first <c>Read()</c> must
/// report false. Each token is probed in span mode and in sequence mode, because
/// the two tokenizers are separate code (see
/// <see cref="MsgPackReaderReadSpanDerivationTests"/>).
/// </para>
/// <para>
/// Reader work happens in the synchronous helpers below: a <c>ref struct</c> cannot
/// live across an <c>await</c>, so the tests only await plain values.
/// </para>
/// </summary>
public class MsgPackReaderIntegerCacheTests
{
    private readonly record struct Probe(
        bool SpanRead,
        bool SeqRead,
        TokenType SpanType,
        TokenType SeqType,
        bool SpanInt32Ok,
        int SpanInt32,
        bool SeqInt32Ok,
        int SeqInt32,
        bool SpanInt64Ok,
        long SpanInt64,
        bool SeqInt64Ok,
        long SeqInt64
    );

    /// <summary>Reads the single token in <paramref name="token"/> in both modes and probes both accessors.</summary>
    private static Probe ReadBoth(byte[] token)
    {
        var span = new MsgPackReader(token);
        var spanRead = span.Read();
        var spanInt32Ok = span.TryGetInt32(out var spanInt32);
        var spanInt64Ok = span.TryGetInt64(out var spanInt64);
        var spanType = span.TokenType;

        var seq = new MsgPackReader(new ReadOnlySequence<byte>(token));
        var seqRead = seq.Read();
        var seqInt32Ok = seq.TryGetInt32(out var seqInt32);
        var seqInt64Ok = seq.TryGetInt64(out var seqInt64);
        var seqType = seq.TokenType;

        return new Probe(
            spanRead,
            seqRead,
            spanType,
            seqType,
            spanInt32Ok,
            spanInt32,
            seqInt32Ok,
            seqInt32,
            spanInt64Ok,
            spanInt64,
            seqInt64Ok,
            seqInt64
        );
    }

    /// <summary>Reads the single token and reports both modes' int64 verdicts.</summary>
    private static async Task AssertToken(
        byte[] token,
        TokenType expectedType,
        bool int64Ok,
        long value
    )
    {
        var p = ReadBoth(token);
        await Assert.That(p.SpanRead).IsTrue();
        await Assert.That(p.SeqRead).IsTrue();
        await Assert.That(p.SpanType).IsEqualTo(expectedType);
        await Assert.That(p.SeqType).IsEqualTo(expectedType);
        await Assert.That(p.SpanInt64Ok).IsEqualTo(int64Ok);
        await Assert.That(p.SeqInt64Ok).IsEqualTo(int64Ok);
        if (int64Ok)
        {
            await Assert.That(p.SpanInt64).IsEqualTo(value);
            await Assert.That(p.SeqInt64).IsEqualTo(value);
        }
        // span mode and sequence mode must agree about every verdict and value
        await Assert.That(p.SpanInt32Ok).IsEqualTo(p.SeqInt32Ok);
        if (p.SpanInt32Ok)
            await Assert.That(p.SpanInt32).IsEqualTo(p.SeqInt32);
    }

    [Test]
    public async Task Fixint_Positive_ReadsAsInt32()
    {
        await AssertToken([0x7F], TokenType.Int32, true, 127);
        await Assert.That(ReadBoth([0x7F]).SpanInt32).IsEqualTo(127);
    }

    [Test]
    public async Task Fixint_Negative_SignExtends()
    {
        await AssertToken([0xFF], TokenType.Int32, true, -1);
        await AssertToken([0xE0], TokenType.Int32, true, -32);
        await Assert.That(ReadBoth([0xFF]).SpanInt32).IsEqualTo(-1);
    }

    [Test]
    public async Task UInt8_And_Int8()
    {
        await AssertToken([0xCC, 0xFF], TokenType.UInt8, true, 255);
        await AssertToken([0xD0, 0x80], TokenType.Int32, true, -128);
        await Assert.That(ReadBoth([0xCC, 0xFF]).SpanInt32).IsEqualTo(255);
        await Assert.That(ReadBoth([0xD0, 0x80]).SpanInt32).IsEqualTo(-128);
    }

    [Test]
    public async Task UInt16_And_Int16()
    {
        await AssertToken([0xCD, 0xFF, 0xFF], TokenType.UInt16, true, 65535);
        await AssertToken([0xD1, 0x80, 0x00], TokenType.Int32, true, -32768);
    }

    [Test]
    public async Task UInt32_AboveIntMax_FitsLongButNotInt()
    {
        var p = ReadBoth([0xCE, 0x80, 0x00, 0x00, 0x00]); // 2147483648
        await AssertToken([0xCE, 0x80, 0x00, 0x00, 0x00], TokenType.UInt32, true, 2147483648L);
        await Assert.That(p.SpanInt32Ok).IsFalse();
        await Assert.That(p.SeqInt32Ok).IsFalse();
    }

    [Test]
    public async Task Int64_OutsideIntRange_FitsLongButNotInt()
    {
        var p = ReadBoth([0xD3, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]); // long.MinValue
        await AssertToken(
            [0xD3, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            TokenType.Int64,
            true,
            long.MinValue
        );
        await Assert.That(p.SpanInt32Ok).IsFalse();
        await Assert.That(p.SeqInt32Ok).IsFalse();
    }

    [Test]
    public async Task UInt64_AboveLongMax_FitsNeitherIntNorLong()
    {
        var token = new byte[] { 0xCF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }; // ulong.MaxValue
        var p = ReadBoth(token);
        await AssertToken(token, TokenType.UInt64, false, 0);
        await Assert.That(p.SpanInt32Ok).IsFalse();
        await Assert.That(p.SeqInt32Ok).IsFalse();
        // ... while the unsigned accessor still reads it in full
        await Assert.That(ReadUInt64(token)).IsEqualTo(ulong.MaxValue);
    }

    private static ulong ReadUInt64(byte[] token)
    {
        var r = new MsgPackReader(token);
        r.Read();
        return r.TryGetUInt64(out var v) ? v : 0;
    }

    [Test]
    public async Task UInt64_AtLongMax_FitsLong()
    {
        var p = ReadBoth([0xCF, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        await AssertToken(
            [0xCF, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            TokenType.UInt64,
            true,
            long.MaxValue
        );
        await Assert.That(p.SpanInt32Ok).IsFalse(); // still outside the int range
    }

    [Test]
    public async Task NonIntegerTokens_ReportFalse()
    {
        await AssertToken([0xC0], TokenType.Null, false, 0);
        await AssertToken([0xC3], TokenType.Bool, false, 0);
        await AssertToken([0xC2], TokenType.Bool, false, 0);
        await AssertToken([0xA1, 0x61], TokenType.String, false, 0);
        await AssertToken([0xC4, 0x01, 0x00], TokenType.Bytes, false, 0);
        await AssertToken([0xD4, 0x00, 0x01], TokenType.Extension, false, 0);
        await AssertToken([0xCB, 0x3F, 0xF0, 0, 0, 0, 0, 0, 0], TokenType.Float64, false, 0); // 1.0
    }

    [Test]
    public async Task AccessorBeforeFirstRead_ReportsFalse()
    {
        await Assert.That(BeforeReadInt32Ok([0x2A])).IsFalse();
        await Assert.That(BeforeReadInt64Ok([0x2A])).IsFalse();
    }

    private static bool BeforeReadInt32Ok(byte[] data)
    {
        var r = new MsgPackReader(data);
        return r.TryGetInt32(out _);
    }

    private static bool BeforeReadInt64Ok(byte[] data)
    {
        var r = new MsgPackReader(new ReadOnlySequence<byte>(data));
        return r.TryGetInt64(out _);
    }

    private readonly record struct LeakProbe(
        int IntValue,
        bool IntOk,
        bool AfterStringInt32Ok,
        bool AfterStringInt64Ok,
        TokenType EndType,
        bool AfterEndInt32Ok,
        bool AfterEndInt64Ok
    );

    /// <summary>int, then a string, then the container end — probing the accessors at each step.</summary>
    private static LeakProbe ProbeLeak()
    {
        var r = new MsgPackReader(new byte[] { 0x92, 0x2A, 0xA1, 0x61 });
        r.Read(); // array(2)
        r.Read(); // 42
        var intOk = r.TryGetInt32(out var intValue);
        r.Read(); // "a"
        var afterString32 = r.TryGetInt32(out _);
        var afterString64 = r.TryGetInt64(out _);
        r.Read(); // ArrayEnd
        var endType = r.TokenType;
        var afterEnd32 = r.TryGetInt32(out _);
        var afterEnd64 = r.TryGetInt64(out _);
        return new LeakProbe(
            intValue,
            intOk,
            afterString32,
            afterString64,
            endType,
            afterEnd32,
            afterEnd64
        );
    }

    [Test]
    public async Task CachedValue_DoesNotLeakIntoTheNextToken()
    {
        // A stale integer must never be reported for a token that is not an integer.
        var p = ProbeLeak();
        await Assert.That(p.IntOk).IsTrue();
        await Assert.That(p.IntValue).IsEqualTo(42);
        await Assert.That(p.AfterStringInt32Ok).IsFalse();
        await Assert.That(p.AfterStringInt64Ok).IsFalse();
        await Assert.That(p.EndType).IsEqualTo(TokenType.ArrayEnd);
        await Assert.That(p.AfterEndInt32Ok).IsFalse();
        await Assert.That(p.AfterEndInt64Ok).IsFalse();
    }

    [Test]
    public async Task IntTokensInASequenceMatchSpanMode()
    {
        await Assert.That(Mismatches([0x93, 0x00, 0x7F, 0xFF])).IsEqualTo(0);
    }

    /// <summary>Counts (verdict, value) disagreements between the two modes.</summary>
    private static int Mismatches(byte[] data)
    {
        var span = new MsgPackReader(data);
        var seq = new MsgPackReader(new ReadOnlySequence<byte>(data));
        span.Read();
        seq.Read();
        var mismatches = 0;
        while (true)
        {
            var spanRead = span.Read();
            var seqRead = seq.Read();
            if (spanRead != seqRead)
                return mismatches + 1;
            if (!spanRead)
                return mismatches;
            var spanOk = span.TryGetInt32(out var a);
            var seqOk = seq.TryGetInt32(out var b);
            if (spanOk != seqOk || (spanOk && a != b))
                mismatches++;
        }
    }
}
