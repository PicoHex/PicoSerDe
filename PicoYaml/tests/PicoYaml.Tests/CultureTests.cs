namespace PicoYaml.Tests;

/// <summary>
/// Review P1-3/P1-4: YAML numeric I/O must not depend on the ambient culture
/// (de-DE used to write "1,5" and read "1.5" back as 15).
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
            var text = YamlSerializer.Serialize(new CultureDoubleDto { Value = 1.5 });
            await Assert.That(text).Contains("1.5");
            await Assert.That(text).DoesNotContain("1,5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task Deserialize_Double_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var dto = YamlSerializer.Deserialize<CultureDoubleDto>("Value: 1.5\n"u8);
            await Assert.That(dto!.Value).IsEqualTo(1.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // C-14: the decimal/TimeSpan write side must be culture-invariant. Under de-DE
    // 1.5m used to be written as "1,5" (read back as 15) and TimeSpan.ToString()
    // produced culture-specific separators.
    [Test]
    [NotInParallel]
    public async Task Decimal_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = YamlSerializer.Serialize(new YamlDecimalDto { Price = 1.5m });
            await Assert.That(text).Contains("1.5");
            await Assert.That(text).DoesNotContain("1,5");
            await Assert
                .That(
                    YamlSerializer.Deserialize<YamlDecimalDto>(Encoding.UTF8.GetBytes(text))!.Price
                )
                .IsEqualTo(1.5m);
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
            var text = YamlSerializer.Serialize(
                new YamlTimeSpanDto { Duration = new TimeSpan(1, 2, 3) }
            );
            await Assert.That(text).Contains("01:02:03");
            await Assert
                .That(
                    YamlSerializer
                        .Deserialize<YamlTimeSpanDto>(Encoding.UTF8.GetBytes(text))!
                        .Duration
                )
                .IsEqualTo(new TimeSpan(1, 2, 3));
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

public class YamlDecimalDto
{
    public decimal Price { get; set; }
}

public class YamlTimeSpanDto
{
    public TimeSpan Duration { get; set; }
}
