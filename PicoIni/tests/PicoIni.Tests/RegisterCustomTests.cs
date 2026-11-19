namespace PicoIni.Tests;

// RegisterCustom<T> for INI — the same contract PicoJetson.JsonSerializer has had:
// the registered pair overrides the SG-generated wire form wherever T appears as a
// NESTED value (member, list element, dictionary value), while the plain Register
// overload keeps its top-level-only meaning.
//
// INI (like every text format) carries a value as text: the registered ISerializer
// writes the member's TEXT, and IniWriter writes it as the value. The format never
// reinterprets the bytes.
//
// Two positions and no more, because those are the only ones INI has:
//   * MEMBER — a customized object member is `Key = text` instead of its `[Key]` section.
//   * TOP-LEVEL LIST ELEMENT — a customized element is a value, and INI's form for a list
//     of values is the comma-joined line it already writes for `List<int>` (an object
//     element gets numbered sections, which carry MEMBERS and have no place for a value).
// A `List<T>` / `Dictionary<string,T>` MEMBER of a custom type is not one of them: INI's
// decision table drops those shapes (PICOSERDE004) before any emitter sees them, so there
// is no built-in form for a registration to replace. That is the format matrix's gap, not
// this one.

// ── Models (dedicated to this file — RegisterCustom state is process-wide) ──

public class IrCustomInner
{
    public int V { get; set; }
}

public class IrCustomOuter
{
    public int Count { get; set; }
    public IrCustomInner Inner { get; set; } = new();
}

// …the never-registered control group (the regression half).
public class IrPlainInner
{
    public int PlainV { get; set; }
}

public class IrPlainOuter
{
    public IrPlainInner Body { get; set; } = new();
}

// ── Custom serializer pair ──

file readonly struct IrCustomSer : ISerializer<IrCustomInner>
{
    public void Serialize(IBufferWriter<byte> writer, IrCustomInner value) =>
        writer.Write(Encoding.UTF8.GetBytes($"RCCUSTOM-{value.V}"));
}

file readonly struct IrCustomDes : IDeserializer<IrCustomInner>
{
    public IrCustomInner Deserialize(ReadOnlySpan<byte> data) => new() { V = 4242 };
}

[NotInParallel("IniSerializer.RegisterCustom")]
public class RegisterCustomTests
{
    private static void Register() =>
        IniSerializer.RegisterCustom<IrCustomInner>(new IrCustomSer(), new IrCustomDes());

    [Test]
    public async Task CustomMember_WritesTheCustomText_NotASection()
    {
        Register();
        var text = IniSerializer.Serialize(
            new IrCustomOuter
            {
                Count = 1,
                Inner = new IrCustomInner { V = 7 },
            }
        );
        await Assert.That(text).Contains("RCCUSTOM-7").Because(text);
        // The built-in form for an object member is its own section `[Inner]` + `V = 7`.
        await Assert.That(text).DoesNotContain("[Inner]").Because(text);
    }

    [Test]
    public async Task CustomListElement_WritesTheCustomTextPerElement()
    {
        Register();
        var text = IniSerializer.Serialize(
            new List<IrCustomInner>
            {
                new() { V = 8 },
                new() { V = 9 },
            }
        );
        await Assert.That(text).Contains("RCCUSTOM-8,RCCUSTOM-9").Because(text);
        // The built-in form for an object list is numbered sections.
        await Assert.That(text).DoesNotContain("[0]").Because(text);
    }

    [Test]
    public async Task Custom_TopLevel_IsTheCustomText()
    {
        Register();
        var text = IniSerializer.Serialize(new IrCustomInner { V = 3 });
        await Assert.That(text).Contains("RCCUSTOM-3").Because(text);
    }

    /// <summary>
    /// The regression half: a type nobody registered keeps the built-in section shape.
    /// </summary>
    [Test]
    public async Task UnregisteredMember_KeepsTheSection()
    {
        var text = IniSerializer.Serialize(
            new IrPlainOuter { Body = new IrPlainInner { PlainV = 3 } }
        );
        await Assert.That(text).Contains("[Body]").Because(text);
        await Assert.That(text).Contains("PlainV = 3").Because(text);
    }

    /// <summary>The read side: the IDeserializer handed to RegisterCustom is the one called.</summary>
    [Test]
    public async Task RegisteredDeserializer_IsCalled()
    {
        Register();
        var back = IniSerializer.Deserialize<IrCustomInner>("RCCUSTOM-3"u8);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V).IsEqualTo(4242);
    }

    [Test]
    public async Task HasCustomSerializer_ReportsRegistration()
    {
        Register();
        await Assert.That(IniSerializer.HasCustomSerializer<IrCustomInner>()).IsTrue();
        await Assert.That(IniSerializer.HasCustomSerializer<IrPlainInner>()).IsFalse();
    }
}
