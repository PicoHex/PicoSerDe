namespace PicoJetson.Tests;

// ── Models ──

/// <summary>Outer model exercising top-level nullable collections (Bug 2).</summary>
public class IgnCondOuter
{
    public string Name { get; set; } = string.Empty;
    public string? TopNote { get; set; }
    public IgnCondNested? Nested { get; set; }
    public List<IgnCondNested> Items { get; set; } = [];
    public string[]? NullableArray { get; set; }
    public List<int>? NullableList { get; set; }
    public Dictionary<string, string>? NullableDict { get; set; }
}

/// <summary>Nested model serialized via SG inner helper (Bug 1).</summary>
public class IgnCondNested
{
    public string Name { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string[]? ToolCalls { get; set; }
    public int? NullableInt { get; set; }
}

/// <summary>Poly base exercising nullable collections in the poly emit path (Bug 2).</summary>
[PicoSerializable]
[PicoDerivedType(typeof(IgnCondDerived), "d1")]
public abstract class IgnCondPolyBase { }

public class IgnCondDerived : IgnCondPolyBase
{
    public string Label { get; set; } = string.Empty;
    public string[]? Tags { get; set; }
    public string? Note { get; set; }
}

// ── Tests ──

/// <summary>
/// Regression tests: DefaultIgnoreCondition must apply to nested types (inner helper
/// emit path) and to explicitly nullable collections in all emit paths.
/// </summary>
public class IgnoreConditionNestedTests
{
    private static readonly JsonOptions WhenWritingNull = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Bug 1: nested object property (inner helper path)
    [Test]
    public async Task WhenWritingNull_NestedObjectProperty_OmitsNull()
    {
        var model = new IgnCondOuter
        {
            Name = "outer",
            Nested = new IgnCondNested { Name = "inner", ToolCallId = null },
        };
        var json = JsonSerializer.Serialize(model, WhenWritingNull);
        await Assert.That(json).DoesNotContain("\"ToolCallId\"");
        await Assert.That(json).Contains("\"inner\"");
    }

    // Bug 1: object element inside a list (inner helper path)
    [Test]
    public async Task WhenWritingNull_ListElementObjectProperty_OmitsNull()
    {
        var model = new IgnCondOuter
        {
            Name = "outer",
            Items = [new IgnCondNested { Name = "item0", ToolCallId = null }],
        };
        var json = JsonSerializer.Serialize(model, WhenWritingNull);
        await Assert.That(json).DoesNotContain("\"ToolCallId\"");
        await Assert.That(json).Contains("\"item0\"");
    }

    // Bug 2: top-level nullable collections
    [Test]
    public async Task WhenWritingNull_TopLevelNullableCollections_Omitted()
    {
        var model = new IgnCondOuter
        {
            Name = "outer",
            NullableArray = null,
            NullableList = null,
            NullableDict = null,
        };
        var json = JsonSerializer.Serialize(model, WhenWritingNull);
        await Assert.That(json).DoesNotContain("\"NullableArray\"");
        await Assert.That(json).DoesNotContain("\"NullableList\"");
        await Assert.That(json).DoesNotContain("\"NullableDict\"");
        await Assert.That(json).Contains("\"outer\"");
    }

    // Bug 1 + Bug 2: nullable collection inside a nested object
    [Test]
    public async Task WhenWritingNull_NestedNullableCollection_Omitted()
    {
        var model = new IgnCondOuter
        {
            Name = "outer",
            Nested = new IgnCondNested { Name = "inner", ToolCalls = null },
        };
        var json = JsonSerializer.Serialize(model, WhenWritingNull);
        await Assert.That(json).DoesNotContain("\"ToolCalls\"");
        await Assert.That(json).Contains("\"inner\"");
    }

    // Bug 2 in poly path: nullable collection on derived type
    [Test]
    public async Task WhenWritingNull_PolyNullableCollection_Omitted()
    {
        IgnCondPolyBase value = new IgnCondDerived
        {
            Label = "poly",
            Tags = null,
            Note = null,
        };
        var json = JsonSerializer.Serialize(value, WhenWritingNull);
        await Assert.That(json).DoesNotContain("\"Tags\"");
        await Assert.That(json).DoesNotContain("\"Note\"");
        await Assert.That(json).Contains("\"poly\"");
    }

    // Non-null values must still be written when the condition is active
    [Test]
    public async Task WhenWritingNull_NonNullValues_StillWritten()
    {
        var model = new IgnCondOuter
        {
            Name = "outer",
            NullableArray = ["a"],
            Nested = new IgnCondNested
            {
                Name = "inner",
                ToolCallId = "call_1",
                ToolCalls = ["t1"],
            },
        };
        var json = JsonSerializer.Serialize(model, WhenWritingNull);
        await Assert.That(json).Contains("\"NullableArray\"");
        await Assert.That(json).Contains("\"ToolCallId\"");
        await Assert.That(json).Contains("\"call_1\"");
        await Assert.That(json).Contains("\"ToolCalls\"");
        await Assert.That(json).Contains("\"t1\"");
    }

    // Without any ignore condition, nulls are written as-is (existing behavior preserved)
    [Test]
    public async Task NoIgnoreCondition_NullsStillWritten()
    {
        var model = new IgnCondOuter
        {
            Name = "outer",
            NullableArray = null,
            Nested = new IgnCondNested
            {
                Name = "inner",
                ToolCallId = null,
                ToolCalls = null,
            },
        };
        var json = JsonSerializer.Serialize(model);
        await Assert.That(json).Contains("\"NullableArray\":null");
        await Assert.That(json).Contains("\"ToolCallId\":null");
        await Assert.That(json).Contains("\"ToolCalls\":null");
    }

    // WhenWritingDefault must also reach nested types (parity with top-level path)
    [Test]
    public async Task WhenWritingDefault_NestedNullProperty_Omitted()
    {
        var model = new IgnCondOuter
        {
            Name = "outer",
            Nested = new IgnCondNested { Name = "inner", NullableInt = null },
        };
        var json = JsonSerializer.Serialize(
            model,
            new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault }
        );
        await Assert.That(json).DoesNotContain("\"NullableInt\"");
        await Assert.That(json).Contains("\"inner\"");
    }
}
