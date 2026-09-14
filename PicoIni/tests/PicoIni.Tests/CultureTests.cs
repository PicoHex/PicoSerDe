using System.Globalization;

namespace PicoIni.Tests;

/// <summary>
/// Review P1-3: INI numeric output must not depend on the ambient culture
/// (de-DE used to emit "1,5").
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
            var text = IniSerializer.Serialize(new CultureDoubleDto { Value = 1.5 });
            await Assert.That(text).Contains("1.5");
            await Assert.That(text).DoesNotContain("1,5");
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
