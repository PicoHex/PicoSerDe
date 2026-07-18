// Regression tests: the poly serialization path must honor
// DefaultIgnoreCondition like all other YAML emit paths.

namespace PicoYaml.Tests;

// ── Models ──

[PicoSerializable]
[PicoDerivedType(typeof(YIgnPolyMsg), "m")]
public abstract class YIgnPolyBase { }

public class YIgnPolyMsg : YIgnPolyBase
{
    public string Content { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int? Rank { get; set; }
}

// ── Tests ──

[NotInParallel("YamlOptions.Current")]
public class IgnoreConditionPolyTests
{
    private static string SerializeWhenWritingNull(YIgnPolyBase value)
    {
        YamlOptions.Current = new YamlOptions
        {
            DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull,
        };
        try
        {
            return YamlSerializer.Serialize(value);
        }
        finally
        {
            YamlOptions.Current = null;
        }
    }

    [Test]
    public async Task WhenWritingNull_PolyNullProperties_Omitted()
    {
        YIgnPolyBase value = new YIgnPolyMsg
        {
            Content = "hello",
            Note = null,
            Rank = null,
        };
        var yaml = SerializeWhenWritingNull(value);
        await Assert.That(yaml).DoesNotContain("Note");
        await Assert.That(yaml).DoesNotContain("Rank");
        await Assert.That(yaml).Contains("hello");
    }

    [Test]
    public async Task WhenWritingNull_PolyNonNullProperties_StillWritten()
    {
        YIgnPolyBase value = new YIgnPolyMsg
        {
            Content = "hello",
            Note = "n1",
            Rank = 3,
        };
        var yaml = SerializeWhenWritingNull(value);
        await Assert.That(yaml).Contains("Note");
        await Assert.That(yaml).Contains("n1");
        await Assert.That(yaml).Contains("Rank");
        await Assert.That(yaml).Contains("3");
    }

    // Never (default): null properties must not crash the serializer
    [Test]
    public async Task Never_PolyNullProperties_DoesNotThrow()
    {
        YIgnPolyBase value = new YIgnPolyMsg
        {
            Content = "hello",
            Note = null,
            Rank = null,
        };
        var yaml = YamlSerializer.Serialize(value);
        await Assert.That(yaml).Contains("hello");
    }
}
