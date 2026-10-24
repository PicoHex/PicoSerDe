using System.Globalization;

namespace PicoToml.Tests;

/// <summary>
/// Review P1-3: TOML numeric output must not depend on the ambient culture
/// (de-DE used to emit "1,5", which is invalid TOML).
/// </summary>
public class CultureTests
{
    [Test]
    public async Task Serialize_Double_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = TomlSerializer.Serialize(new CultureDoubleDto { Value = 1.5 });
            await Assert.That(text).Contains("1.5");
            await Assert.That(text).DoesNotContain("1,5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    [NotInParallel]
    public async Task Decimal_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = TomlSerializer.Serialize(new CultureDecimalDto { Price = 1.5m });
            await Assert.That(text).Contains("1.5");
            await Assert.That(text).DoesNotContain("1,5");
            var roundTripped = TomlSerializer.Deserialize<CultureDecimalDto>(
                Encoding.UTF8.GetBytes(text)
            );
            await Assert.That(roundTripped!.Price).IsEqualTo(1.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Test]
    [NotInParallel]
    public async Task TimeSpan_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = TomlSerializer.Serialize(
                new CultureTimeSpanDto { Duration = new TimeSpan(1, 30, 0) }
            );
            await Assert.That(text).Contains("01:30:00");
            var roundTripped = TomlSerializer.Deserialize<CultureTimeSpanDto>(
                Encoding.UTF8.GetBytes(text)
            );
            await Assert.That(roundTripped!.Duration).IsEqualTo(new TimeSpan(1, 30, 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }
}

public class CultureDoubleDto
{
    public double Value { get; set; }
}

public class CultureDecimalDto
{
    public decimal Price { get; set; }
}

public class CultureTimeSpanDto
{
    public TimeSpan Duration { get; set; }
}
