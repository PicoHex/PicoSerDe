namespace PicoIni.Tests;

/// <summary>
/// Fix round 1 regressions found by the INI lane review (C-1, C-2, C-3 and the
/// Important follow-ups): converters must work for every write call site, poly
/// members must fail loudly, and inline-comment scanning must strip in both
/// directions.
/// </summary>
public class IniFixRound1ConverterTests
{
    // ── C-3: converter on a property nested inside an [IniSection] ──

    [Test]
    public async Task SectionConverter_IsUsedOnWriteAndRead()
    {
        var text = IniSerializer.Serialize(
            new SecConvDto { Inner = new SecConvInner { Values = [1, 2, 3] } }
        );
        await Assert.That(text).Contains("1|2|3");
        await Assert
            .That(IniSerializer.Deserialize<SecConvDto>(Encoding.UTF8.GetBytes(text))!.Inner.Values)
            .IsEquivalentTo([1, 2, 3]);
    }

    // ── C-1: two or more converter properties on the same DTO ──
    // Emitting one `var __c` / `var __bw` per converter property used to be a
    // CS0128 duplicate-local compile error as soon as a DTO had two of them.

    [Test]
    public async Task TwoConverterProperties_CompileAndRoundTrip()
    {
        var text = IniSerializer.Serialize(new TwoConvDto { Values = [1, 2, 3], More = [4, 5, 6] });
        await Assert.That(text).Contains("1|2|3");
        await Assert.That(text).Contains("4-5-6");

        var back = IniSerializer.Deserialize<TwoConvDto>(Encoding.UTF8.GetBytes(text))!;
        await Assert.That(back.Values).IsEquivalentTo([1, 2, 3]);
        await Assert.That(back.More).IsEquivalentTo([4, 5, 6]);
    }

    // ── I-5: exception messages must identify the key and the offending text ──

    [Test]
    public async Task InvalidValue_Message_NamesKeyAndExcerpt()
    {
        var ex = Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<GuidDto>(Encoding.UTF8.GetBytes("Id = not-a-guid"))
        );
        await Assert.That(ex.Message).Contains("'Id'");
        await Assert.That(ex.Message).Contains("not-a-guid");
    }

    [Test]
    public async Task InvalidValue_Message_TruncatesLongExcerpt()
    {
        var longJunk = new string('z', 200);
        var ex = Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<GuidDto>(Encoding.UTF8.GetBytes("Id = " + longJunk))
        );
        // Excerpt is capped at 32 chars; the full 200-char payload must not leak in.
        await Assert.That(ex.Message).DoesNotContain(longJunk);
        await Assert.That(ex.Message).Contains("'Id'");
    }
}

public class SecConvDto
{
    public SecConvInner Inner { get; set; } = new();
}

public class SecConvInner
{
    [IniConverter(typeof(PipeConverter))]
    public int[] Values { get; set; } = [];
}

public class TwoConvDto
{
    [IniConverter(typeof(PipeConverter))]
    public int[] Values { get; set; } = [];

    [IniConverter(typeof(DashConverter))]
    public int[] More { get; set; } = [];
}

public sealed class DashConverter : IIniConverter<int[]>
{
    public void Write(IBufferWriter<byte> writer, int[] value)
    {
        var s = string.Join("-", value);
        var span = writer.GetSpan(s.Length);
        Encoding.UTF8.GetBytes(s, span);
        writer.Advance(s.Length);
    }

    public int[] Read(ReadOnlySpan<byte> value) =>
        Encoding.UTF8.GetString(value).Split('-').Select(int.Parse).ToArray();
}
