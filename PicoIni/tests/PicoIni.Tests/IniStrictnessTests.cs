namespace PicoIni.Tests;

/// <summary>
/// C-18/C-19/C-20 regressions: malformed values must fail loudly, converters
/// must be honoured on the write path, and quote/comment scanning must not
/// swallow or truncate payloads.
/// </summary>
public class IniStrictnessTests
{
    [Test]
    public void InvalidGuid_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<GuidDto>(Encoding.UTF8.GetBytes("Id = not-a-guid"))
        );

    [Test]
    public void InvalidEnum_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<EnumDto>(Encoding.UTF8.GetBytes("Level = Funday"))
        );

    [Test]
    public void UnterminatedQuote_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<TextDto>(Encoding.UTF8.GetBytes("A = \"oops"))
        );

    [Test]
    public async Task HashInsideUrl_IsPreserved() =>
        await Assert
            .That(
                IniSerializer.Deserialize<TextDto>(Encoding.UTF8.GetBytes("A = http://x#frag"))!.A
            )
            .IsEqualTo("http://x#frag");

    [Test]
    public async Task Converter_IsUsedOnWriteAndRead()
    {
        var text = IniSerializer.Serialize(new ConvDto { Values = [1, 2, 3] });
        await Assert.That(text).Contains("1|2|3");
        await Assert
            .That(IniSerializer.Deserialize<ConvDto>(Encoding.UTF8.GetBytes(text))!.Values)
            .IsEquivalentTo([1, 2, 3]);
    }
}

public class GuidDto
{
    public Guid Id { get; set; }
}

public class EnumDto
{
    public Level Level { get; set; }
}

public enum Level
{
    Low = 1,
    High = 2,
}

public class TextDto
{
    public string A { get; set; } = "";
}

public class ConvDto
{
    [IniConverter(typeof(PipeConverter))]
    public int[] Values { get; set; } = [];
}

public sealed class PipeConverter : IIniConverter<int[]>
{
    public void Write(IBufferWriter<byte> writer, int[] value)
    {
        var s = string.Join("|", value);
        var span = writer.GetSpan(s.Length);
        Encoding.UTF8.GetBytes(s, span);
        writer.Advance(s.Length);
    }

    public int[] Read(ReadOnlySpan<byte> value) =>
        Encoding.UTF8.GetString(value).Split('|').Select(int.Parse).ToArray();
}
