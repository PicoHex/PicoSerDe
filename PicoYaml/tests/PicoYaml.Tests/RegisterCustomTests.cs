namespace PicoYaml.Tests;

// RegisterCustom<T> for YAML — the same contract PicoJetson.JsonSerializer has had:
// the registered pair overrides the SG-generated wire form wherever T appears as a
// NESTED value (member, sequence element, dictionary value), while the plain Register
// overload keeps its top-level-only meaning.
//
// YAML (like every text format) carries a value as text: the registered ISerializer
// writes the member's TEXT, and YamlWriter quotes/escapes it as the format requires.
// The format never reinterprets the bytes.

// ── Models (dedicated to this file — RegisterCustom state is process-wide) ──

public class YrCustomInner
{
    public int V { get; set; }
}

public class YrCustomOuter
{
    public int Count { get; set; }
    public YrCustomInner Inner { get; set; } = new();
    public List<YrCustomInner> Items { get; set; } = [];
    public Dictionary<string, YrCustomInner> Map { get; set; } = [];

    /// <summary>
    /// A NULLABLE dictionary value: the null literal still wins, the non-null one is customized.
    /// </summary>
    public Dictionary<string, YrCustomInner?> NullableMap { get; set; } = [];
}

// …the never-registered control group (the regression half).
public class YrPlainInner
{
    public int PlainV { get; set; }
}

public class YrPlainOuter
{
    public YrPlainInner Body { get; set; } = new();
}

// ── Custom serializer pair ──

file readonly struct YrCustomSer : ISerializer<YrCustomInner>
{
    public void Serialize(IBufferWriter<byte> writer, YrCustomInner value) =>
        writer.Write(Encoding.UTF8.GetBytes($"RCCUSTOM-{value.V}"));
}

file readonly struct YrCustomDes : IDeserializer<YrCustomInner>
{
    public YrCustomInner Deserialize(ReadOnlySpan<byte> data) => new() { V = 4242 };
}

[NotInParallel("YamlSerializer.RegisterCustom")]
public class RegisterCustomTests
{
    private static void Register() =>
        YamlSerializer.RegisterCustom<YrCustomInner>(new YrCustomSer(), new YrCustomDes());

    [Test]
    public async Task CustomMember_WritesTheCustomText_NotTheNestedMapping()
    {
        Register();
        var text = YamlSerializer.Serialize(
            new YrCustomOuter
            {
                Count = 1,
                Inner = new YrCustomInner { V = 7 },
            }
        );
        await Assert.That(text).Contains("RCCUSTOM-7").Because(text);
        await Assert.That(text).DoesNotContain("V: 7").Because(text);
    }

    [Test]
    public async Task CustomSequenceElement_WritesTheCustomTextPerElement()
    {
        Register();
        var text = YamlSerializer.Serialize(
            new YrCustomOuter
            {
                Count = 1,
                Items = [new YrCustomInner { V = 8 }, new YrCustomInner { V = 9 }],
            }
        );
        await Assert.That(text).Contains("- RCCUSTOM-8").Because(text);
        await Assert.That(text).Contains("- RCCUSTOM-9").Because(text);
        await Assert.That(text).DoesNotContain("V: 8").Because(text);
    }

    [Test]
    public async Task CustomDictValue_WritesTheCustomText()
    {
        Register();
        var text = YamlSerializer.Serialize(
            new YrCustomOuter { Count = 1, Map = { ["k"] = new YrCustomInner { V = 5 } } }
        );
        await Assert.That(text).Contains("RCCUSTOM-5").Because(text);
        await Assert.That(text).DoesNotContain("V: 5").Because(text);
    }

    [Test]
    public async Task CustomNullableDictValue_CustomizesOnlyTheNonNullValue()
    {
        Register();
        var text = YamlSerializer.Serialize(
            new YrCustomOuter
            {
                Count = 1,
                NullableMap =
                {
                    ["a"] = new YrCustomInner { V = 5 },
                    ["b"] = null,
                },
            }
        );
        await Assert.That(text).Contains("RCCUSTOM-5").Because(text);
        // The null entry is the null literal — a value the document never had is not a
        // registration's business.
        await Assert.That(text).Contains("b: null").Because(text);
    }

    [Test]
    public async Task Custom_TopLevel_IsTheCustomText()
    {
        Register();
        var text = YamlSerializer.Serialize(new YrCustomInner { V = 3 });
        await Assert.That(text).Contains("RCCUSTOM-3").Because(text);
    }

    /// <summary>
    /// The regression half: a type nobody registered keeps the built-in nested-mapping shape.
    /// </summary>
    [Test]
    public async Task UnregisteredMember_KeepsTheNestedMapping()
    {
        var text = YamlSerializer.Serialize(
            new YrPlainOuter { Body = new YrPlainInner { PlainV = 3 } }
        );
        await Assert.That(text).Contains("Body:").Because(text);
        await Assert.That(text).Contains("PlainV: 3").Because(text);
    }

    /// <summary>The read side: the IDeserializer handed to RegisterCustom is the one called.</summary>
    [Test]
    public async Task RegisteredDeserializer_IsCalled()
    {
        Register();
        var back = YamlSerializer.Deserialize<YrCustomInner>("RCCUSTOM-3"u8);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V).IsEqualTo(4242);
    }

    [Test]
    public async Task HasCustomSerializer_ReportsRegistration()
    {
        Register();
        await Assert.That(YamlSerializer.HasCustomSerializer<YrCustomInner>()).IsTrue();
        await Assert.That(YamlSerializer.HasCustomSerializer<YrPlainInner>()).IsFalse();
    }
}
