namespace PicoIni.Tests;

/// <summary>
/// C-31 / I-4 regressions: a null dictionary has no section, and the blank-line
/// separator between sections must be driven by sections actually written — not
/// by dictionary properties visited, which opened the file with a blank line
/// when the first dictionary was null.
/// </summary>
public class IniNullableDictTests
{
    [Test]
    public async Task NullDictionary_IsOmitted() =>
        await Assert.That(IniSerializer.Serialize(new IniNullableDictHolder()).Trim()).IsEmpty();

    [Test]
    public async Task NonNullDictionary_StillWrites() =>
        await Assert
            .That(IniSerializer.Serialize(new IniNullableDictHolder { Map = new() { ["a"] = 1 } }))
            .Contains("[Map]");

    // ── I-4: first dict null, second dict present ──

    [Test]
    public async Task LeadingNullDictionary_DoesNotStartWithBlankLine()
    {
        var text = IniSerializer.Serialize(
            new IniTwoDicts
            {
                A = null,
                B = new() { ["x"] = 1 },
            }
        );
        await Assert.That(text.StartsWith("\r\n")).IsFalse();
        await Assert.That(text.StartsWith("\n")).IsFalse();
        await Assert.That(text).Contains("[B]");
        await Assert.That(text).DoesNotContain("[A]");
    }

    [Test]
    public async Task SingleNullDictionaryAmongTwo_StillSeparatesSections()
    {
        var text = IniSerializer.Serialize(
            new IniTwoDicts
            {
                A = new() { ["x"] = 1 },
                B = new() { ["y"] = 2 },
            }
        );
        await Assert.That(text).Contains("[A]");
        await Assert.That(text).Contains("[B]");
        // Exactly one blank line between the two sections, none before the first.
        await Assert.That(text).Contains("x = 1\r\n\r\n[B]");
        await Assert.That(text.StartsWith("[A]")).IsTrue();
    }
}

public class IniNullableDictHolder
{
    public Dictionary<string, int>? Map { get; set; }
}

public class IniTwoDicts
{
    public Dictionary<string, int>? A { get; set; }
    public Dictionary<string, int>? B { get; set; }
}
