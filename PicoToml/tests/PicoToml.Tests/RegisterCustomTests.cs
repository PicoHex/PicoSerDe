namespace PicoToml.Tests;

// RegisterCustom<T> for TOML — the framework gap this lane closes. JSON and MsgPack
// have had it since long (PicoJetson/tests/PicoJetson.Integration.Tests/RegisterCustomTests.cs);
// TOML/YAML/INI had only the plain Register (top level). The contract is the same one:
// RegisterCustom overrides the SG-generated wire form wherever T appears as a NESTED
// value — member, list element, dictionary value — while plain Register stays
// top-level-only.
//
// TOML (like every text format) carries a value as text: the registered ISerializer
// writes the member's TEXT, and TomlWriter quotes/escapes it as the format requires.
// The format never reinterprets the bytes.

// ── Models (dedicated to this file — RegisterCustom state is process-wide) ──

public class TrCustomInner
{
    public int V { get; set; }
}

public class TrCustomOuter
{
    public int Count { get; set; }
    public TrCustomInner Inner { get; set; } = new();
    public List<TrCustomInner> Items { get; set; } = [];
    public Dictionary<string, TrCustomInner> Map { get; set; } = [];
}

// …the never-registered control group (the regression half).
public class TrPlainInner
{
    public int PlainV { get; set; }
}

public class TrPlainOuter
{
    public TrPlainInner Body { get; set; } = new();
}

// ── Custom serializer pair ──

file readonly struct TrCustomSer : ISerializer<TrCustomInner>
{
    public void Serialize(IBufferWriter<byte> writer, TrCustomInner value) =>
        writer.Write(Encoding.UTF8.GetBytes($"RCCUSTOM-{value.V}"));
}

file readonly struct TrCustomDes : IDeserializer<TrCustomInner>
{
    public TrCustomInner Deserialize(ReadOnlySpan<byte> data) => new() { V = 4242 };
}

[NotInParallel("TomlSerializer.RegisterCustom")]
public class RegisterCustomTests
{
    private static void Register() =>
        TomlSerializer.RegisterCustom<TrCustomInner>(new TrCustomSer(), new TrCustomDes());

    [Test]
    public async Task CustomMember_WritesTheCustomText_NotTheNestedTable()
    {
        Register();
        var text = TomlSerializer.Serialize(
            new TrCustomOuter
            {
                Count = 1,
                Inner = new TrCustomInner { V = 7 },
            }
        );
        await Assert.That(text).Contains("RCCUSTOM-7").Because(text);
        // The built-in form for an object member is a nested table `[Inner]` + `V = 7`.
        await Assert.That(text).DoesNotContain("[Inner]").Because(text);
    }

    [Test]
    public async Task CustomListElement_WritesTheCustomTextPerElement()
    {
        Register();
        var text = TomlSerializer.Serialize(
            new TrCustomOuter
            {
                Count = 1,
                Items = [new TrCustomInner { V = 8 }, new TrCustomInner { V = 9 }],
            }
        );
        await Assert.That(text).Contains("RCCUSTOM-8").Because(text);
        await Assert.That(text).Contains("RCCUSTOM-9").Because(text);
        // An object list is an array of tables ([[Items]]) unless the element is custom.
        await Assert.That(text).DoesNotContain("[[Items]]").Because(text);
    }

    [Test]
    public async Task CustomDictValue_WritesTheCustomText()
    {
        Register();
        var text = TomlSerializer.Serialize(
            new TrCustomOuter { Count = 1, Map = { ["k"] = new TrCustomInner { V = 5 } } }
        );
        await Assert.That(text).Contains("RCCUSTOM-5").Because(text);
        await Assert.That(text).DoesNotContain("V = 5").Because(text);
    }

    [Test]
    public async Task Custom_TopLevel_IsTheCustomText()
    {
        Register();
        var text = TomlSerializer.Serialize(new TrCustomInner { V = 3 });
        await Assert.That(text).Contains("RCCUSTOM-3").Because(text);
    }

    /// <summary>
    /// The regression half: a type nobody registered keeps the built-in nested-table shape,
    /// byte for byte what this generator wrote before RegisterCustom existed.
    /// </summary>
    [Test]
    public async Task UnregisteredMember_KeepsTheNestedTable()
    {
        var text = TomlSerializer.Serialize(
            new TrPlainOuter { Body = new TrPlainInner { PlainV = 3 } }
        );
        await Assert.That(text).Contains("[Body]").Because(text);
        await Assert.That(text).Contains("PlainV = 3").Because(text);
    }

    /// <summary>The read side: the IDeserializer handed to RegisterCustom is the one called.</summary>
    [Test]
    public async Task RegisteredDeserializer_IsCalled()
    {
        Register();
        var back = TomlSerializer.Deserialize<TrCustomInner>("RCCUSTOM-3"u8);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V).IsEqualTo(4242);
    }

    [Test]
    public async Task HasCustomSerializer_ReportsRegistration()
    {
        Register();
        await Assert.That(TomlSerializer.HasCustomSerializer<TrCustomInner>()).IsTrue();
        await Assert.That(TomlSerializer.HasCustomSerializer<TrPlainInner>()).IsFalse();
    }
}
