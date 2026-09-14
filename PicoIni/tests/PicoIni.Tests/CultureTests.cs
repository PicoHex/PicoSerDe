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

    [Test]
    public async Task Serialize_DoubleList_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var dto = new CultureListDto { Doubles = [1.5, 2.5] };
            var text = IniSerializer.Serialize(dto);
            await Assert.That(text).Contains("1.5,2.5");

            var back = IniSerializer.Deserialize<CultureListDto>(Encoding.UTF8.GetBytes(text));
            await Assert.That(back!.Doubles).IsEquivalentTo([1.5, 2.5]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task Serialize_DateTimeList_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var dto = new CultureListDto
            {
                Times = [new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)],
            };
            var text = IniSerializer.Serialize(dto);
            var back = IniSerializer.Deserialize<CultureListDto>(Encoding.UTF8.GetBytes(text));
            await Assert.That(back!.Times[0]).IsEqualTo(dto.Times[0]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public class CultureListDto
{
    public List<double> Doubles { get; set; } = [];
    public List<DateTime> Times { get; set; } = [];
}

public class CultureDoubleDto
{
    public double Value { get; set; }
}
