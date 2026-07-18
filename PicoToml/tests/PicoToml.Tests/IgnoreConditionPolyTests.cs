// Regression tests: the poly serialization path must omit null values —
// TOML has no null literal, so omission is the only valid representation.

namespace PicoToml.Tests;

// ── Models ──

[PicoSerializable]
[PicoDerivedType(typeof(TIgnPolyMsg), "m")]
public abstract class TIgnPolyBase { }

public class TIgnPolyMsg : TIgnPolyBase
{
    public string Content { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int? Rank { get; set; }
}

// ── Tests ──

[NotInParallel("TomlOptions.Current")]
public class IgnoreConditionPolyTests
{
    [Test]
    public async Task WhenWritingNull_PolyNullProperties_Omitted()
    {
        TIgnPolyBase value = new TIgnPolyMsg
        {
            Content = "hello",
            Note = null,
            Rank = null,
        };
        TomlOptions.Current = new TomlOptions
        {
            DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var toml = TomlSerializer.Serialize(value);
            await Assert.That(toml).DoesNotContain("Note");
            await Assert.That(toml).DoesNotContain("Rank");
            await Assert.That(toml).Contains("hello");
        }
        finally
        {
            TomlOptions.Current = null;
        }
    }

    // TOML cannot express null: null poly properties are omitted under any
    // condition and must not crash.
    [Test]
    public async Task Never_PolyNullProperties_OmittedAndDoesNotThrow()
    {
        TIgnPolyBase value = new TIgnPolyMsg
        {
            Content = "hello",
            Note = null,
            Rank = null,
        };
        var toml = TomlSerializer.Serialize(value);
        await Assert.That(toml).DoesNotContain("Note");
        await Assert.That(toml).DoesNotContain("Rank");
        await Assert.That(toml).Contains("hello");
    }

    [Test]
    public async Task PolyNonNullProperties_StillWritten()
    {
        TIgnPolyBase value = new TIgnPolyMsg
        {
            Content = "hello",
            Note = "n1",
            Rank = 3,
        };
        var toml = TomlSerializer.Serialize(value);
        await Assert.That(toml).Contains("Note");
        await Assert.That(toml).Contains("n1");
        await Assert.That(toml).Contains("Rank");
    }
}
