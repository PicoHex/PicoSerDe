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

            await Assert.That(json).Contains("1.5");
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

        await Assert.That(json).Contains("0.1");
        await Assert.That(json).DoesNotContain("0.10000000149011612");

        var back = JsonSerializer.Deserialize<FloatPrecisionDto>(Encoding.UTF8.GetBytes(json));
        await Assert.That(back!.V).IsEqualTo(0.1f);
    }
}
