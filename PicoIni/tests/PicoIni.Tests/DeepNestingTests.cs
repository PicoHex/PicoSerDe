namespace PicoIni.Tests;

/// <summary>
/// Deep object nesting (3+ levels) is not representable with flat INI sections.
/// It must fail loudly instead of writing "TypeName" text and losing data
/// (review follow-up).
/// </summary>
public class DeepNestingTests
{
    [Test]
    public async Task ThreeLevelNesting_Serialize_ThrowsNotSupported()
    {
        var dto = new IniDeepL1 { L2 = new IniDeepL2 { L3 = new IniDeepL3 { X = 42 } } };

        await Assert.That(() => IniSerializer.Serialize(dto)).Throws<NotSupportedException>();
    }
}

internal sealed class IniDeepL1
{
    public IniDeepL2 L2 { get; set; } = new();
}

internal sealed class IniDeepL2
{
    public IniDeepL3 L3 { get; set; } = new();
}

internal sealed class IniDeepL3
{
    public int X { get; set; }
}
