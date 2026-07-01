namespace PicoJetson.Tests;

// ── Test model: simple polymorphic hierarchy ──

[PicoSerializable]
[PicoDerivedType(typeof(MessageEntry), "message")]
[PicoDerivedType(typeof(CompactionEntry), "compaction")]
public abstract class SessionEntry { }

public class MessageEntry : SessionEntry
{
    public string Content { get; set; } = "";
    public int Sequence { get; set; }
}

public class CompactionEntry : SessionEntry
{
    public int From { get; set; }
    public int To { get; set; }
}

// ── Test model: custom discriminator field name ──

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[PicoDerivedType(typeof(EmailEvent), "email")]
[PicoDerivedType(typeof(SmsEvent), "sms")]
public abstract class AppEvent { }

public class EmailEvent : AppEvent
{
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
}

public class SmsEvent : AppEvent
{
    public string Phone { get; set; } = "";
    public string Text { get; set; } = "";
}

// ── Test model: derived type with [JsonConstructor] ──

[PicoSerializable]
[PicoDerivedType(typeof(ImmutableMessage), "immutable_msg")]
public abstract class PolyEntry { }

public class ImmutableMessage : PolyEntry
{
    public string Content { get; }
    public int Id { get; }

    [JsonConstructor]
    public ImmutableMessage(string content, int id)
    {
        Content = content;
        Id = id;
    }
}

public class PolymorphicTests
{
    // ── Serialization ──

    [Test]
    public async Task Serialize_Polymorphic_WritesDiscriminatorFirst()
    {
        SessionEntry entry = new MessageEntry { Content = "hello", Sequence = 1 };
        var json = JsonSerializer.Serialize(entry);

        await Assert.That(json).Contains("\"$type\"");
        await Assert.That(json).Contains("\"message\"");
        await Assert.That(json).Contains("\"hello\"");
        var typeIdx = json.IndexOf("$type", StringComparison.Ordinal);
        var contentIdx = json.IndexOf("Content", StringComparison.Ordinal);
        await Assert.That(typeIdx).IsLessThan(contentIdx);
    }

    [Test]
    public async Task Serialize_ConcreteType_DoesNotWriteDiscriminator()
    {
        var msg = new MessageEntry { Content = "hi", Sequence = 2 };
        var json = JsonSerializer.Serialize(msg);

        await Assert.That(json).DoesNotContain("$type");
    }

    [Test]
    public async Task Serialize_CustomDiscriminatorName_WritesCorrectly()
    {
        AppEvent ev = new EmailEvent { To = "user@test.com", Subject = "Hello" };
        var json = JsonSerializer.Serialize(ev);

        await Assert.That(json).Contains("\"kind\"");
        await Assert.That(json).Contains("\"email\"");
    }

    // ── Deserialization ──

    [Test]
    public async Task Deserialize_Polymorphic_ReturnsCorrectDerivedType()
    {
        var json = """{"$type":"message","Content":"world","Sequence":42}"""u8;
        var result = JsonSerializer.Deserialize<SessionEntry>(json);

        await Assert.That(result).IsTypeOf<MessageEntry>();
        var msg = (MessageEntry)result!;
        await Assert.That(msg.Content).IsEqualTo("world");
        await Assert.That(msg.Sequence).IsEqualTo(42);
    }

    [Test]
    public async Task Deserialize_Polymorphic_AlternateDerivedType()
    {
        var json = """{"$type":"compaction","From":10,"To":20}"""u8;
        var result = JsonSerializer.Deserialize<SessionEntry>(json);

        await Assert.That(result).IsTypeOf<CompactionEntry>();
        var ce = (CompactionEntry)result!;
        await Assert.That(ce.From).IsEqualTo(10);
        await Assert.That(ce.To).IsEqualTo(20);
    }

    [Test]
    public async Task Deserialize_CustomDiscriminatorName_Works()
    {
        var json = """{"kind":"sms","Phone":"555-1234","Text":"Hi"}"""u8;
        var result = JsonSerializer.Deserialize<AppEvent>(json);

        await Assert.That(result).IsTypeOf<SmsEvent>();
        var sms = (SmsEvent)result!;
        await Assert.That(sms.Phone).IsEqualTo("555-1234");
        await Assert.That(sms.Text).IsEqualTo("Hi");
    }

    [Test]
    public async Task Deserialize_UnknownDiscriminator_Throws()
    {
        var json = """{"$type":"unknown_type","x":1}"""u8.ToArray();

        var ex = Assert.Throws<FormatException>(
            () => JsonSerializer.Deserialize<SessionEntry>(json)
        );
        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Deserialize_DiscriminatorNotFirst_Throws()
    {
        var json = """{"Content":"hi","$type":"message"}"""u8.ToArray();

        var ex = Assert.Throws<FormatException>(
            () => JsonSerializer.Deserialize<SessionEntry>(json)
        );
        await Assert.That(ex).IsNotNull();
    }

    // ── [JsonConstructor] derived type ──

    [Test]
    public async Task Deserialize_PolyWithJsonConstructor_Works()
    {
        var json = """{"$type":"immutable_msg","Content":"hello","Id":99}"""u8;
        var result = JsonSerializer.Deserialize<PolyEntry>(json);

        await Assert.That(result).IsTypeOf<ImmutableMessage>();
        var im = (ImmutableMessage)result!;
        await Assert.That(im.Content).IsEqualTo("hello");
        await Assert.That(im.Id).IsEqualTo(99);
    }

    // ── Round-trip ──

    [Test]
    public async Task RoundTrip_Polymorphic_Identity()
    {
        SessionEntry original = new CompactionEntry { From = 5, To = 15 };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<SessionEntry>(json);

        await Assert.That(result).IsTypeOf<CompactionEntry>();
        var ce = (CompactionEntry)result!;
        await Assert.That(ce.From).IsEqualTo(5);
        await Assert.That(ce.To).IsEqualTo(15);
    }

    [Test]
    public async Task RoundTrip_EmptyStrings_Works()
    {
        var json = """{"$type":"message","Content":"","Sequence":0}"""u8;
        var result = JsonSerializer.Deserialize<SessionEntry>(json);
        var json2 = JsonSerializer.Serialize(result);

        await Assert.That(json2).Contains("\"$type\"");
        await Assert.That(json2).Contains("\"message\"");
    }
}
