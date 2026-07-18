// Regression tests: nullable collections must not crash the TOML serializer
// and must respect DefaultIgnoreCondition. TOML has no null literal, so a
// null value can never be written — omission is the only representation.

namespace PicoToml.Tests;

// ── Models ──

public class TIgnNested
{
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class TIgnOuter
{
    public string Name { get; set; } = string.Empty;
    public string[]? NullableArray { get; set; }
    public List<int>? NullableList { get; set; }
    public Dictionary<string, string>? NullableDict { get; set; }
    public TIgnNested? Nested { get; set; }
}

// ── Tests ──

[NotInParallel("TomlOptions.Current")]
public class IgnoreConditionNestedTests
{
    private static string SerializeWith(TomlIgnoreCondition condition, TIgnOuter model)
    {
        TomlOptions.Current = new TomlOptions { DefaultIgnoreCondition = condition };
        try
        {
            return TomlSerializer.Serialize(model);
        }
        finally
        {
            TomlOptions.Current = null;
        }
    }

    // Bug 2: null collections currently hit 'foreach (x.Prop!)' → NRE
    [Test]
    public async Task WhenWritingNull_TopLevelNullableCollections_Omitted()
    {
        var model = new TIgnOuter
        {
            Name = "outer",
            NullableArray = null,
            NullableList = null,
            NullableDict = null,
        };
        var toml = SerializeWith(TomlIgnoreCondition.WhenWritingNull, model);
        await Assert.That(toml).DoesNotContain("NullableArray");
        await Assert.That(toml).DoesNotContain("NullableList");
        await Assert.That(toml).DoesNotContain("NullableDict");
        await Assert.That(toml).Contains("outer");
    }

    // TOML cannot express null: even with Never, a null collection must not
    // crash — omission is the only valid representation.
    [Test]
    public async Task Never_NullCollections_DoesNotThrow()
    {
        var model = new TIgnOuter
        {
            Name = "outer",
            NullableArray = null,
            NullableList = null,
            NullableDict = null,
        };
        var toml = TomlSerializer.Serialize(model);
        await Assert.That(toml).Contains("outer");
    }

    // Behavior lock: nested objects share the top-level emit path — a null
    // property inside a nested table must be omitted under WhenWritingNull.
    [Test]
    public async Task WhenWritingNull_NestedObjectProperty_OmitsNull()
    {
        var model = new TIgnOuter
        {
            Name = "outer",
            Nested = new TIgnNested { Name = "inner", Note = null },
        };
        var toml = SerializeWith(TomlIgnoreCondition.WhenWritingNull, model);
        await Assert.That(toml).DoesNotContain("Note");
        await Assert.That(toml).Contains("inner");
    }

    // Non-null values must still be written when the condition is active
    [Test]
    public async Task WhenWritingNull_NonNullValues_StillWritten()
    {
        var model = new TIgnOuter
        {
            Name = "outer",
            NullableArray = ["a"],
            NullableList = [1, 2],
            NullableDict = new Dictionary<string, string> { ["k"] = "v" },
        };
        var toml = SerializeWith(TomlIgnoreCondition.WhenWritingNull, model);
        await Assert.That(toml).Contains("NullableArray");
        await Assert.That(toml).Contains("NullableList");
        await Assert.That(toml).Contains("NullableDict");
    }
}
