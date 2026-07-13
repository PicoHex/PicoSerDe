namespace PicoToml.Tests;

[PicoSerializable]
[PicoDerivedType(typeof(TomlMsgEntry), "msg")]
[PicoDerivedType(typeof(TomlCompactionEntry), "compaction")]
public abstract class TomlSessionEntry { }

public class TomlMsgEntry : TomlSessionEntry
{
    public string Content { get; set; } = string.Empty;
    public int Sequence { get; set; }
}

public class TomlCompactionEntry : TomlSessionEntry
{
    public int From { get; set; }
    public int To { get; set; }
}

public class TomlPolymorphicTests
{
    [Test]
    public async Task RoundTrip_Polymorphic_ReturnsCorrectDerivedType()
    {
        TomlSessionEntry entry = new TomlMsgEntry { Content = "hello", Sequence = 1 };
        var toml = TomlSerializer.Serialize(entry);
        // Debug: print actual TOML output
        Console.WriteLine("TOML output:");
        Console.WriteLine(toml);
        var result = TomlSerializer.Deserialize<TomlSessionEntry>(Encoding.UTF8.GetBytes(toml));
        await Assert.That(result).IsTypeOf<TomlMsgEntry>();
        var msg = (TomlMsgEntry)result!;
        await Assert.That(msg.Content).IsEqualTo("hello");
        await Assert.That(msg.Sequence).IsEqualTo(1);
    }

    [Test]
    public async Task RoundTrip_AltType_Works()
    {
        TomlSessionEntry entry = new TomlCompactionEntry { From = 10, To = 20 };
        var toml = TomlSerializer.Serialize(entry);
        var result = TomlSerializer.Deserialize<TomlSessionEntry>(Encoding.UTF8.GetBytes(toml));
        await Assert.That(result).IsTypeOf<TomlCompactionEntry>();
    }
}

// ── Record-based polymorphic hierarchy ──

[PicoSerializable]
[PicoDerivedType(typeof(TomlRecMsg), "rec_msg")]
[PicoDerivedType(typeof(TomlRecComp), "rec_comp")]
public abstract record TomlPolyRecordBase;

public record TomlRecMsg(string Content, int Sequence) : TomlPolyRecordBase;

public record TomlRecComp(int From, int To) : TomlPolyRecordBase;

public class TomlRecordPolymorphicTests
{
    [Test]
    public async Task Deserialize_PolyRecord_ReturnsCorrectType()
    {
        var toml = Encoding.UTF8.GetBytes(
            "$type = \"rec_msg\"\nContent = \"hello\"\nSequence = 42\n"
        );
        var result = TomlSerializer.Deserialize<TomlPolyRecordBase>(toml);

        await Assert.That(result).IsTypeOf<TomlRecMsg>();
        var msg = (TomlRecMsg)result!;
        await Assert.That(msg.Content).IsEqualTo("hello");
        await Assert.That(msg.Sequence).IsEqualTo(42);
    }

    [Test]
    public async Task Deserialize_PolyRecord_AlternateType()
    {
        var toml = Encoding.UTF8.GetBytes("$type = \"rec_comp\"\nFrom = 10\nTo = 20\n");
        var result = TomlSerializer.Deserialize<TomlPolyRecordBase>(toml);

        await Assert.That(result).IsTypeOf<TomlRecComp>();
        var c = (TomlRecComp)result!;
        await Assert.That(c.From).IsEqualTo(10);
        await Assert.That(c.To).IsEqualTo(20);
    }
}
