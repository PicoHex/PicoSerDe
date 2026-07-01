namespace PicoIni.Tests;

// ── Test model: simple polymorphic hierarchy ──

[PicoSerializable]
[PicoDerivedType(typeof(IniMsgEntry), "msg")]
[PicoDerivedType(typeof(IniCompactionEntry), "compaction")]
public abstract class IniSessionEntry { }

public class IniMsgEntry : IniSessionEntry
{
    public string Content { get; set; } = "";
    public int Sequence { get; set; }
}

public class IniCompactionEntry : IniSessionEntry
{
    public int From { get; set; }
    public int To { get; set; }
}

// ── Test model: custom discriminator key ──

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "event_kind")]
[PicoDerivedType(typeof(IniEmailEvent), "email")]
[PicoDerivedType(typeof(IniSmsEvent), "sms")]
public abstract class IniAppEvent { }

public class IniEmailEvent : IniAppEvent
{
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
}

public class IniSmsEvent : IniAppEvent
{
    public string Phone { get; set; } = "";
    public string Text { get; set; } = "";
}

public class IniPolymorphicTests
{
    // ── Serialization ──

    [Test]
    public async Task Serialize_Polymorphic_WritesDiscriminator()
    {
        IniSessionEntry entry = new IniMsgEntry { Content = "hello", Sequence = 1 };
        var ini = IniSerializer.Serialize(entry);

        await Assert.That(ini).Contains("$type");
        await Assert.That(ini).Contains("msg");
        await Assert.That(ini).Contains("Content");
        await Assert.That(ini).Contains("hello");
    }

    [Test]
    public async Task Serialize_ConcreteType_DoesNotWriteDiscriminator()
    {
        var msg = new IniMsgEntry { Content = "hi", Sequence = 2 };
        var ini = IniSerializer.Serialize(msg);

        await Assert.That(ini).DoesNotContain("$type");
    }

    [Test]
    public async Task Serialize_CustomDiscriminatorKey_WritesCorrectly()
    {
        IniAppEvent ev = new IniEmailEvent { To = "a@b.com", Subject = "Test" };
        var ini = IniSerializer.Serialize(ev);

        await Assert.That(ini).Contains("event_kind");
        await Assert.That(ini).Contains("email");
    }

    // ── Deserialization ──

    [Test]
    public async Task Deserialize_Polymorphic_ReturnsCorrectDerivedType()
    {
        var ini = "$type=msg\nContent=world\nSequence=42\n"u8.ToArray();
        var result = IniSerializer.Deserialize<IniSessionEntry>(ini);

        await Assert.That(result).IsTypeOf<IniMsgEntry>();
        var msg = (IniMsgEntry)result!;
        await Assert.That(msg.Content).IsEqualTo("world");
        await Assert.That(msg.Sequence).IsEqualTo(42);
    }

    [Test]
    public async Task Deserialize_Polymorphic_AlternateDerivedType()
    {
        var ini = "$type=compaction\nFrom=10\nTo=20\n"u8.ToArray();
        var result = IniSerializer.Deserialize<IniSessionEntry>(ini);

        await Assert.That(result).IsTypeOf<IniCompactionEntry>();
        var ce = (IniCompactionEntry)result!;
        await Assert.That(ce.From).IsEqualTo(10);
        await Assert.That(ce.To).IsEqualTo(20);
    }

    [Test]
    public async Task Deserialize_UnknownDiscriminator_Throws()
    {
        var ini = "$type=unknown\nx=1\n"u8.ToArray();

        var ex = Assert.Throws<FormatException>(
            () => IniSerializer.Deserialize<IniSessionEntry>(ini)
        );
        await Assert.That(ex).IsNotNull();
    }

    // ── Round-trip ──

    [Test]
    public async Task RoundTrip_Polymorphic_Identity()
    {
        IniSessionEntry original = new IniCompactionEntry { From = 5, To = 15 };
        var ini = IniSerializer.Serialize(original);
        var result = IniSerializer.Deserialize<IniSessionEntry>(Encoding.UTF8.GetBytes(ini));

        await Assert.That(result).IsTypeOf<IniCompactionEntry>();
        var ce = (IniCompactionEntry)result!;
        await Assert.That(ce.From).IsEqualTo(5);
        await Assert.That(ce.To).IsEqualTo(15);
    }
}
