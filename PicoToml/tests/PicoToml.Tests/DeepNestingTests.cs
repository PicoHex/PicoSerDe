namespace PicoToml.Tests;

/// <summary>
/// Deep object nesting (3+ levels) is not representable with the current flat
/// table emitter. It must fail loudly instead of silently losing the nested
/// values (review follow-up).
/// </summary>
public class DeepNestingTests
{
    [Test]
    public async Task ThreeLevelNesting_Deserialize_ThrowsNotSupported()
    {
        var dto = new TomlDeepL1 { L2 = new TomlDeepL2 { L3 = new TomlDeepL3 { X = 42 } } };
        var text = TomlSerializer.Serialize(dto);

        await Assert
            .That(() => TomlSerializer.Deserialize<TomlDeepL1>(Encoding.UTF8.GetBytes(text)))
            .Throws<NotSupportedException>();
    }
}

internal sealed class TomlDeepL1
{
    public TomlDeepL2 L2 { get; set; } = new();
}

internal sealed class TomlDeepL2
{
    public TomlDeepL3 L3 { get; set; } = new();
}

internal sealed class TomlDeepL3
{
    public int X { get; set; }
}
