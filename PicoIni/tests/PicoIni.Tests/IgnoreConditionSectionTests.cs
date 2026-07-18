// Regression tests: DefaultIgnoreCondition must apply to properties inside
// nested sections (object properties emitted as [Section] blocks).

namespace PicoIni.Tests;

// ── Models ──

public class IIgnSection
{
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class IIgnOuter
{
    public string Title { get; set; } = string.Empty;
    public string? TopNote { get; set; }
    public IIgnSection Config { get; set; } = new();
}

// ── Tests ──

[NotInParallel("IniOptions.Current")]
public class IgnoreConditionSectionTests
{
    private static string SerializeWhenWritingNull(IIgnOuter model)
    {
        IniOptions.Current = new IniOptions
        {
            DefaultIgnoreCondition = IniIgnoreCondition.WhenWritingNull,
        };
        try
        {
            return IniSerializer.Serialize(model);
        }
        finally
        {
            IniOptions.Current = null;
        }
    }

    // Bug 1 variant: property inside a nested [Section] must honor WhenWritingNull
    [Test]
    public async Task WhenWritingNull_SectionProperty_OmitsNull()
    {
        var model = new IIgnOuter
        {
            Title = "outer",
            TopNote = null,
            Config = new IIgnSection { Name = "inner", Note = null },
        };
        var ini = SerializeWhenWritingNull(model);
        // Top-level path already honors the condition
        await Assert.That(ini).DoesNotContain("TopNote");
        // Section path must do the same
        await Assert.That(ini).DoesNotContain("Note");
        await Assert.That(ini).Contains("inner");
    }

    // Non-null section values must still be written when the condition is active
    [Test]
    public async Task WhenWritingNull_SectionNonNullValues_StillWritten()
    {
        var model = new IIgnOuter
        {
            Title = "outer",
            Config = new IIgnSection { Name = "inner", Note = "n1" },
        };
        var ini = SerializeWhenWritingNull(model);
        await Assert.That(ini).Contains("Note");
        await Assert.That(ini).Contains("n1");
    }

    // Never (default): current behavior is preserved — nulls do not throw
    [Test]
    public async Task Never_SectionNullProperty_DoesNotThrow()
    {
        var model = new IIgnOuter
        {
            Title = "outer",
            Config = new IIgnSection { Name = "inner", Note = null },
        };
        var ini = IniSerializer.Serialize(model);
        await Assert.That(ini).Contains("inner");
    }
}
