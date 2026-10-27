namespace PicoJetson.Tests;

/// <summary>
/// Equivalence proof for the vectorized string-token scan (P0-3): the SIMD helper
/// must return exactly what the scalar byte-at-a-time predicate returns, for every
/// length, every hit position and every combination of the three stop bytes.
/// </summary>
public class JsonStringScanHelperTests
{
    /// <summary>The reference: the pre-optimization per-byte predicate.</summary>
    private static int ScalarReference(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is (byte)'"' or (byte)'\\' or < 0x20)
                return i;
        }
        return -1;
    }

    private static byte[] Filled(int length, byte value)
    {
        var buf = new byte[length];
        buf.AsSpan().Fill(value);
        return buf;
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(17)]
    [Arguments(31)]
    [Arguments(32)]
    [Arguments(33)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(1000)]
    public async Task PlainSpan_HasNoHit(int length)
    {
        var scan = JsonReader.IndexOfQuoteEscapeOrControl(Filled(length, (byte)'a'));
        await Assert.That(scan).IsEqualTo(-1);
    }

    [Test]
    public async Task EveryHitPosition_AndEveryStopByte_IsFound()
    {
        // Covers all three SIMD widths, their tails, and the scalar-only case.
        foreach (var length in new[] { 1, 2, 3, 5, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 129 })
        {
            foreach (var stop in new byte[] { (byte)'"', (byte)'\\', 0x00, 0x09, 0x1F })
            {
                for (var hit = 0; hit < length; hit++)
                {
                    var buf = Filled(length, (byte)'a');
                    buf[hit] = stop;
                    var scan = JsonReader.IndexOfQuoteEscapeOrControl(buf);
                    if (scan != hit)
                        throw new Exception(
                            $"length={length} stop=0x{stop:X2} hit={hit} scan={scan}"
                        );
                }
            }
        }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task FirstHitWins_WhenSeveralStopBytesArePresent()
    {
        var buf = Filled(200, (byte)'a');
        buf[100] = (byte)'\\';
        buf[150] = (byte)'"';
        buf[180] = 0x01;
        var scan = JsonReader.IndexOfQuoteEscapeOrControl(buf);
        await Assert.That(scan).IsEqualTo(100);
    }

    [Test]
    public async Task HighBytes_AreNotStopBytes()
    {
        // Multi-byte UTF-8 continuation bytes are >= 0x80: plain string data.
        var buf = Filled(300, (byte)'a');
        for (var i = 0; i < buf.Length; i += 3)
        {
            buf[i] = (byte)'é';
            buf[i + 1] = 0x80;
            buf[i + 2] = 0xFF;
        }
        var scan = JsonReader.IndexOfQuoteEscapeOrControl(buf);
        await Assert.That(scan).IsEqualTo(-1);
    }

    [Test]
    public async Task RandomSpans_MatchTheScalarReference()
    {
        // Deterministic xorshift so a failure is reproducible; the alphabet mixes
        // plain bytes, all three stop bytes and high bytes.
        uint state = 0x9E3779B9;
        byte NextByte()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            var pick = state % 10;
            return pick switch
            {
                0 => (byte)'"',
                1 => (byte)'\\',
                2 => 0x00,
                3 => 0x1F,
                4 => 0xFF,
                5 => (byte)'é',
                _ => (byte)'a',
            };
        }

        for (var length = 0; length <= 300; length++)
        {
            for (var rep = 0; rep < 3; rep++)
            {
                var buf = new byte[length];
                for (var i = 0; i < length; i++)
                    buf[i] = NextByte();
                var expected = ScalarReference(buf);
                var actual = JsonReader.IndexOfQuoteEscapeOrControl(buf);
                if (actual != expected)
                    throw new Exception(
                        $"length={length} rep={rep} expected={expected} actual={actual}"
                    );
            }
        }
        await Assert.That(true).IsTrue();
    }
}
