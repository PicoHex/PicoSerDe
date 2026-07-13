namespace PicoJetson.Tests;

// ── Test model: simple polymorphic hierarchy ──

[PicoSerializable]
[PicoDerivedType(typeof(MessageEntry), "message")]
[PicoDerivedType(typeof(CompactionEntry), "compaction")]
public abstract class SessionEntry { }

public class MessageEntry : SessionEntry
{
    public string Content { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string? OptionalNote { get; set; }
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
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
}

public class SmsEvent : AppEvent
{
    public string Phone { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
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

// ── Test model: polymorphic hierarchy with record derived types ──
// Reproduces: CS7036 (no parameterless ctor) + CS8852 (init-only property assignment)
// when [PicoDerivedType] dispatches to a record type.

[PicoSerializable]
[PicoDerivedType(typeof(RecordMessage), "rec_msg")]
[PicoDerivedType(typeof(RecordCompaction), "rec_comp")]
public abstract record PolyRecordBase;

public record RecordMessage(string Content, int Sequence) : PolyRecordBase;

public record RecordCompaction(int From, int To) : PolyRecordBase;

// ── Test model: abstract record base with record derived types ──

[PicoSerializable]
[PicoDerivedType(typeof(NameUpdated), "name_updated")]
[PicoDerivedType(typeof(AgeUpdated), "age_updated")]
public abstract record DomainEvent(string EventType);

public record NameUpdated(string Name) : DomainEvent("name_updated");

public record AgeUpdated(int Age) : DomainEvent("age_updated");

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

        var ex = Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<SessionEntry>(json)
        );
        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Deserialize_DiscriminatorNotFirst_Throws()
    {
        var json = """{"Content":"hi","$type":"message"}"""u8.ToArray();

        var ex = Assert.Throws<FormatException>(() =>
            JsonSerializer.Deserialize<SessionEntry>(json)
        );
        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Deserialize_WrongDiscriminatorPropertyName_Throws()
    {
        // AppEvent uses "kind" as discriminator field, not "$type"
        var json = """{"$type":"email","To":"a@b.com","Subject":"Test"}"""u8.ToArray();

        var ex = Assert.Throws<FormatException>(() => JsonSerializer.Deserialize<AppEvent>(json));
        await Assert.That(ex.Message).Contains("kind");
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

    // ── DefaultIgnoreCondition ──

    [Test]
    public async Task Serialize_Poly_WhenWritingNull_SkipsNullProperties()
    {
        var entry = new MessageEntry
        {
            Content = "hi",
            Sequence = 42,
            OptionalNote = null,
        };
        SessionEntry session = entry;
        var json = JsonSerializer.Serialize(
            session,
            new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }
        );
        await Assert.That(json).DoesNotContain("OptionalNote");
        await Assert.That(json).Contains("Content");
        await Assert.That(json).Contains("Sequence");
    }

    // ── Streaming ──

    [Test]
    public async Task HasStreamingDelegate_PolyBase_ReturnsTrue()
    {
        var hasDelegate = JsonSerializer.HasStreamingDelegate<SessionEntry>();
        await Assert.That(hasDelegate).IsTrue();
    }

    [Test]
    public async Task HasStreamingDelegate_PolyBase_CustomDiscriminator_ReturnsTrue()
    {
        var hasDelegate = JsonSerializer.HasStreamingDelegate<AppEvent>();
        await Assert.That(hasDelegate).IsTrue();
    }

    [Test]
    public async Task HasStreamingDelegate_ConcreteDerived_ReturnsTrue()
    {
        var hasDelegate = JsonSerializer.HasStreamingDelegate<MessageEntry>();
        await Assert.That(hasDelegate).IsTrue();
    }

    [Test]
    public async Task DeserializeFromStreamAsync_PolyBase_RoundTrips()
    {
        var json = "{\"$type\":\"message\",\"Content\":\"hi\",\"Sequence\":1}"u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<SessionEntry>(stream);

        await Assert.That(result).IsTypeOf<MessageEntry>();
        var msg = (MessageEntry)result;
        await Assert.That(msg.Content).IsEqualTo("hi");
        await Assert.That(msg.Sequence).IsEqualTo(1);
    }

    [Test]
    public async Task DeserializeFromStreamAsync_PolyBase_CustomDiscriminator_RoundTrips()
    {
        var json = "{\"kind\":\"sms\",\"Phone\":\"555-1234\",\"Text\":\"Hi\"}"u8;
        using var stream = new MemoryStream(json.ToArray());
        var result = await JsonSerializer.DeserializeFromStreamAsync<AppEvent>(stream);

        await Assert.That(result).IsTypeOf<SmsEvent>();
        var sms = (SmsEvent)result;
        await Assert.That(sms.Phone).IsEqualTo("555-1234");
        await Assert.That(sms.Text).IsEqualTo("Hi");
    }

    // ── Record derived types (bug regression) ──

    [Test]
    public async Task Deserialize_PolyRecordDerivedType_ReturnsCorrectType()
    {
        // This exercises the code path where [PicoDerivedType] dispatches to a
        // record type. Before the fix, the SG generated new() + property assignment
        // which fails for records (CS7036 + CS8852).
        var json = """{"$type":"rec_msg","Content":"hello","Sequence":42}"""u8;
        var result = JsonSerializer.Deserialize<PolyRecordBase>(json);

        await Assert.That(result).IsTypeOf<RecordMessage>();
        var msg = (RecordMessage)result!;
        await Assert.That(msg.Content).IsEqualTo("hello");
        await Assert.That(msg.Sequence).IsEqualTo(42);
    }

    [Test]
    public async Task Deserialize_PolyRecordDerivedType_AlternateType()
    {
        var json = """{"$type":"rec_comp","From":10,"To":20}"""u8;
        var result = JsonSerializer.Deserialize<PolyRecordBase>(json);

        await Assert.That(result).IsTypeOf<RecordCompaction>();
        var ce = (RecordCompaction)result!;
        await Assert.That(ce.From).IsEqualTo(10);
        await Assert.That(ce.To).IsEqualTo(20);
    }

    [Test]
    public async Task Deserialize_PolyRecordDerivedType_WithAbstractRecordBase()
    {
        // abstract record base + record derived — the most complex scenario.
        var json = """{"$type":"name_updated","Name":"Alice"}"""u8;
        var result = JsonSerializer.Deserialize<DomainEvent>(json);

        await Assert.That(result).IsTypeOf<NameUpdated>();
        var ev = (NameUpdated)result!;
        await Assert.That(ev.Name).IsEqualTo("Alice");
    }

    [Test]
    public async Task Deserialize_PolyRecordDerivedType_WithAbstractRecordBase_IntProperty()
    {
        var json = """{"$type":"age_updated","Age":35}"""u8;
        var result = JsonSerializer.Deserialize<DomainEvent>(json);

        await Assert.That(result).IsTypeOf<AgeUpdated>();
        var ev = (AgeUpdated)result!;
        await Assert.That(ev.Age).IsEqualTo(35);
    }

    [Test]
    public async Task RoundTrip_PolyRecordDerivedType()
    {
        PolyRecordBase original = new RecordMessage("world", 99);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<PolyRecordBase>(bytes);

        await Assert.That(result).IsTypeOf<RecordMessage>();
        var msg = (RecordMessage)result!;
        await Assert.That(msg.Content).IsEqualTo("world");
        await Assert.That(msg.Sequence).IsEqualTo(99);
    }
}
