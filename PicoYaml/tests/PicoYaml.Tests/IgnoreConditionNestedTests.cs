// Regression tests: DefaultIgnoreCondition must apply to nested types
// (YamlInner emit path) and to explicitly nullable collections.

namespace PicoYaml.Tests;

// ── Models ──

public class YIgnNested
{
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string[]? Tags { get; set; }
}

public class YIgnOuter
{
    public string Name { get; set; } = string.Empty;
    public YIgnNested? Nested { get; set; }
    public List<YIgnNested> Items { get; set; } = [];
    public string[]? NullableArray { get; set; }
    public List<int>? NullableList { get; set; }
    public Dictionary<string, string>? NullableDict { get; set; }
}

// ── Tests ──

[NotInParallel("YamlOptions.Current")]
public class IgnoreConditionNestedTests
{
    private static string SerializeWhenWritingNull(YIgnOuter model)
    {
        YamlOptions.Current = new YamlOptions
        {
            DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull,
        };
        try
        {
            return YamlSerializer.Serialize(model);
        }
        finally
        {
            YamlOptions.Current = null;
        }
    }

    // Bug 1: nested object property (YamlInner.Serialize path)
    [Test]
    public async Task WhenWritingNull_NestedObjectProperty_OmitsNull()
    {
        var model = new YIgnOuter
        {
            Name = "outer",
            Nested = new YIgnNested { Name = "inner", Note = null },
        };
        var yaml = SerializeWhenWritingNull(model);
        await Assert.That(yaml).DoesNotContain("Note:");
        await Assert.That(yaml).Contains("inner");
    }

    // Bug 1: object element inside a list (YamlInner.SerializeBlock path)
    [Test]
    public async Task WhenWritingNull_ListElementObjectProperty_OmitsNull()
    {
        var model = new YIgnOuter
        {
            Name = "outer",
            Items = [new YIgnNested { Name = "item0", Note = null }],
        };
        var yaml = SerializeWhenWritingNull(model);
        await Assert.That(yaml).DoesNotContain("Note:");
        await Assert.That(yaml).Contains("item0");
    }

    // Bug 2: top-level nullable collections must be omitted entirely
    [Test]
    public async Task WhenWritingNull_TopLevelNullableCollections_Omitted()
    {
        var model = new YIgnOuter
        {
            Name = "outer",
            NullableArray = null,
            NullableList = null,
            NullableDict = null,
        };
        var yaml = SerializeWhenWritingNull(model);
        await Assert.That(yaml).DoesNotContain("NullableArray");
        await Assert.That(yaml).DoesNotContain("NullableList");
        await Assert.That(yaml).DoesNotContain("NullableDict");
        await Assert.That(yaml).Contains("outer");
    }

    // Bug 1 + Bug 2: nullable collection inside a nested object
    [Test]
    public async Task WhenWritingNull_NestedNullableCollection_Omitted()
    {
        var model = new YIgnOuter
        {
            Name = "outer",
            Nested = new YIgnNested { Name = "inner", Tags = null },
        };
        var yaml = SerializeWhenWritingNull(model);
        await Assert.That(yaml).DoesNotContain("Tags:");
        await Assert.That(yaml).Contains("inner");
    }

    // Non-null values must still be written when the condition is active
    [Test]
    public async Task WhenWritingNull_NonNullValues_StillWritten()
    {
        var model = new YIgnOuter
        {
            Name = "outer",
            NullableArray = ["a"],
            Nested = new YIgnNested
            {
                Name = "inner",
                Note = "n1",
                Tags = ["t1"],
            },
        };
        var yaml = SerializeWhenWritingNull(model);
        await Assert.That(yaml).Contains("NullableArray");
        await Assert.That(yaml).Contains("Note:");
        await Assert.That(yaml).Contains("n1");
        await Assert.That(yaml).Contains("Tags:");
        await Assert.That(yaml).Contains("t1");
    }

    // Never (default): serialization must not throw and non-null content is written
    [Test]
    public async Task Never_NullValues_DoesNotThrow()
    {
        var model = new YIgnOuter
        {
            Name = "outer",
            Nested = new YIgnNested
            {
                Name = "inner",
                Note = null,
                Tags = null,
            },
            NullableArray = null,
            NullableList = null,
            NullableDict = null,
        };
        var yaml = YamlSerializer.Serialize(model);
        await Assert.That(yaml).Contains("outer");
        await Assert.That(yaml).Contains("inner");
    }
}
