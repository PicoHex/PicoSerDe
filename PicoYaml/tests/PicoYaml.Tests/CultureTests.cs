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
}

public class CultureDoubleDto
{
    public double Value { get; set; }
}
