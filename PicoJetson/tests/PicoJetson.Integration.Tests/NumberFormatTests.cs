using System.Globalization;

namespace PicoJetson.Tests;

public class DecimalCultureDto
{
    public decimal V { get; set; }
}

public class FloatPrecisionDto
{
    public float V { get; set; }
}

public class HalfPrecisionDto
{
    public Half H { get; set; }
}

/// <summary>
/// Review C-14 / I-24: end-to-end number formatting through the source-generated
/// serializer, where the generator (not the test) chooses the writer calls.
/// <para>
/// Culture is process-wide mutable state and TUnit runs tests in parallel, so
/// every culture-mutating test opts out with <see cref="NotInParallelAttribute"/>.
/// </para>
/// </summary>
public class NumberFormatTests
{
    [Test]
    [NotInParallel]
    public async Task Decimal_UnderDeDe_SerializesInvariantAndRoundTrips()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var json = JsonSerializer.Serialize(new DecimalCultureDto { V = 1.5m });

            // Full-string equality, not a substring probe: "1,5", "1.50" and
            // "11.5" must all fail, so only an exact payload counts as invariant.
            await Assert.That(json).IsEqualTo("""{"V":1.5}""");
            await Assert.That(json).DoesNotContain("1,5");

            var back = JsonSerializer.Deserialize<DecimalCultureDto>(Encoding.UTF8.GetBytes(json));
            await Assert.That(back!.V).IsEqualTo(1.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    /// <summary>
    /// A <c>float</c> carries only ~7 significant digits, so JSON must emit the
    /// shortest text that round-trips back to the same <c>float</c> — <c>0.1</c>,
    /// never the widening artifact <c>0.10000000149011612</c>.
    /// </summary>
    [Test]
    public async Task Float_EmitsShortestRoundTripText()
    {
        var json = JsonSerializer.Serialize(new FloatPrecisionDto { V = 0.1f });

        // Full-string equality pins the "shortest round-trip" contract: "0.10",
        // "0.100000" and "1E-01" must fail, not just the widening artifact.
        await Assert.That(json).IsEqualTo("""{"V":0.1}""");
        await Assert.That(json).DoesNotContain("0.10000000149011612");

        var back = JsonSerializer.Deserialize<FloatPrecisionDto>(Encoding.UTF8.GetBytes(json));
        await Assert.That(back!.V).IsEqualTo(0.1f);
    }

    /// <summary>
    /// <see cref="Half"/> has an 11-bit significand; writing it through the
    /// <c>double</c> overload widened the text to the exact binary expansion
    /// (<c>0.0999755859375</c>). The payload must be the <c>Half</c>'s own
    /// shortest round-trip text, <c>0.1</c>, and must read back unchanged.
    /// </summary>
    [Test]
    public async Task Half_EmitsShortestRoundTripText()
    {
        var json = JsonSerializer.Serialize(new HalfPrecisionDto { H = (Half)0.1 });

        await Assert.That(json).IsEqualTo("""{"H":0.1}""");
        await Assert.That(json).DoesNotContain("0.0999755859375");

        var back = JsonSerializer.Deserialize<HalfPrecisionDto>(Encoding.UTF8.GetBytes(json));
        await Assert.That(back!.H).IsEqualTo((Half)0.1);
    }
}
