namespace PicoIni.Tests;

// ── C-2: polymorphic members must fail loudly too ──
//
// The polymorphic pipeline emits its own member readers and never goes through
// EmitRead, so C-18's strictness has to be mirrored there or `$type`-correct
// payloads keep poisoning Guid/DateTime/enum/TimeSpan members with silent
// defaults.

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[PicoDerivedType(typeof(PolyStrictEntry), "entry")]
[PicoDerivedType(typeof(PolyStrictCtorEntry), "ctor")]
public abstract class PolyStrictBase { }

public class PolyStrictEntry : PolyStrictBase
{
    public Guid Id { get; set; }
    public DateTime When { get; set; }
    public IniTestColor Color { get; set; }
    public TimeSpan Span { get; set; }
}

/// <summary>
/// Constructor-shaped derived type. It exercises the polymorphic ctor read path
/// and doubles as the <c>[IniConstructor]</c> + enum coverage review M-4 asked
/// for (other kinds there are Task 14's lane).
/// </summary>
public class PolyStrictCtorEntry : PolyStrictBase
{
    public IniTestColor Color { get; }
    public int Code { get; }

    [IniConstructor]
    public PolyStrictCtorEntry(IniTestColor color, int code) => (Color, Code) = (color, code);
}

public class PolyStrictTests
{
    [Test]
    public void PolyGuid_InvalidValue_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<PolyStrictBase>(
                Encoding.UTF8.GetBytes("$type = entry\nId = not-a-guid\n")
            )
        );

    [Test]
    public void PolyDateTime_InvalidValue_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<PolyStrictBase>(
                Encoding.UTF8.GetBytes("$type = entry\nWhen = not-a-date\n")
            )
        );

    [Test]
    public void PolyEnum_InvalidValue_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<PolyStrictBase>(
                Encoding.UTF8.GetBytes("$type = entry\nColor = Purple\n")
            )
        );

    [Test]
    public void PolyTimeSpan_InvalidValue_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<PolyStrictBase>(
                Encoding.UTF8.GetBytes("$type = entry\nSpan = not-a-span\n")
            )
        );

    [Test]
    public void PolyCtorEnum_InvalidValue_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<PolyStrictBase>(
                Encoding.UTF8.GetBytes("$type = ctor\nColor = Purple\n")
            )
        );

    [Test]
    public void PolyCtorInt_InvalidValue_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<PolyStrictBase>(
                Encoding.UTF8.GetBytes("$type = ctor\nCode = not-an-int\n")
            )
        );

    [Test]
    public async Task PolyValidValues_StillRoundTrip()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var when = new DateTime(2026, 10, 23, 1, 2, 3, DateTimeKind.Utc);
        var original = new PolyStrictEntry
        {
            Id = id,
            When = when,
            Color = IniTestColor.Green,
            Span = TimeSpan.FromMinutes(90),
        };
        var text = IniSerializer.Serialize<PolyStrictBase>(original);
        var back = IniSerializer.Deserialize<PolyStrictBase>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back).IsTypeOf<PolyStrictEntry>();
        var entry = (PolyStrictEntry)back!;
        await Assert.That(entry.Id).IsEqualTo(id);
        await Assert.That(entry.When).IsEqualTo(when);
        await Assert.That(entry.Color).IsEqualTo(IniTestColor.Green);
        await Assert.That(entry.Span).IsEqualTo(TimeSpan.FromMinutes(90));
    }

    [Test]
    public async Task PolyCtorValidValues_StillRoundTrip()
    {
        var original = new PolyStrictCtorEntry(IniTestColor.Blue, 7);
        var text = IniSerializer.Serialize<PolyStrictBase>(original);
        var back = IniSerializer.Deserialize<PolyStrictBase>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back).IsTypeOf<PolyStrictCtorEntry>();
        var entry = (PolyStrictCtorEntry)back!;
        await Assert.That(entry.Color).IsEqualTo(IniTestColor.Blue);
        await Assert.That(entry.Code).IsEqualTo(7);
    }
}
