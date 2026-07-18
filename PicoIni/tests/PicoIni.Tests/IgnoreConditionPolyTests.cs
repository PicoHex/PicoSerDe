// Regression tests: the poly serialization path must honor
// DefaultIgnoreCondition the same way the regular emit paths do.

namespace PicoIni.Tests;

// ── Models ──

[PicoSerializable]
[PicoDerivedType(typeof(IIgnPolyMsg), "m")]
public abstract class IIgnPolyBase { }

public class IIgnPolyMsg : IIgnPolyBase
{
    public string Content { get; set; } = string.Empty;
    public string? Note { get; set; }
}

// ── Tests ──

[NotInParallel("IniOptions.Current")]
public class IgnoreConditionPolyTests
{
    // Bug: poly dispatch property loop had no ignore-condition guard
    [Test]
    public async Task WhenWritingNull_PolyNullProperty_Omitted()
    {
        IIgnPolyBase value = new IIgnPolyMsg { Content = "hello", Note = null };
        IniOptions.Current = new IniOptions
        {
            DefaultIgnoreCondition = IniIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var ini = IniSerializer.Serialize(value);
            await Assert.That(ini).DoesNotContain("Note");
            await Assert.That(ini).Contains("hello");
        }
        finally
        {
            IniOptions.Current = null;
        }
    }

    // Non-null values must still be written when the condition is active
    [Test]
    public async Task WhenWritingNull_PolyNonNullProperty_StillWritten()
    {
        IIgnPolyBase value = new IIgnPolyMsg { Content = "hello", Note = "n1" };
        IniOptions.Current = new IniOptions
        {
            DefaultIgnoreCondition = IniIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var ini = IniSerializer.Serialize(value);
            await Assert.That(ini).Contains("Note");
            await Assert.That(ini).Contains("n1");
        }
        finally
        {
            IniOptions.Current = null;
        }
    }

    // Never (default): INI has no null literal — null values are omitted
    // regardless of the condition, and serialization must not throw.
    [Test]
    public async Task Never_PolyNullProperty_Omitted()
    {
        IIgnPolyBase value = new IIgnPolyMsg { Content = "hello", Note = null };
        var ini = IniSerializer.Serialize(value);
        await Assert.That(ini).DoesNotContain("Note");
        await Assert.That(ini).Contains("hello");
    }
}
