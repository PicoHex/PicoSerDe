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

public class IgnoreConditionPolyTests
{
    // Bug: poly dispatch property loop had no ignore-condition guard
    [Test]
    public async Task WhenWritingNull_PolyNullProperty_Omitted()
    {
        IIgnPolyBase value = new IIgnPolyMsg { Content = "hello", Note = null };

        var ini = IniSerializer.Serialize(value);
        await Assert.That(ini).DoesNotContain("Note");
        await Assert.That(ini).Contains("hello");
    }

    // Non-null values must still be written when the condition is active
    [Test]
    public async Task WhenWritingNull_PolyNonNullProperty_StillWritten()
    {
        IIgnPolyBase value = new IIgnPolyMsg { Content = "hello", Note = "n1" };

        var ini = IniSerializer.Serialize(value);
        await Assert.That(ini).Contains("Note");
        await Assert.That(ini).Contains("n1");
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
