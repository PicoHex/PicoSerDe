namespace PicoYaml.Tests;

[PicoSerializable]
[PicoDerivedType(typeof(YamlMsgEntry), "msg")]
[PicoDerivedType(typeof(YamlCompactionEntry), "compaction")]
public abstract class YamlSessionEntry { }

public class YamlMsgEntry : YamlSessionEntry
{
    public string Content { get; set; } = string.Empty;
    public int Sequence { get; set; }
}

public class YamlCompactionEntry : YamlSessionEntry
{
    public int From { get; set; }
    public int To { get; set; }
}

public class YamlPolymorphicTests
{
    [Test]
    public async Task RoundTrip_Polymorphic_ReturnsCorrectDerivedType()
    {
        YamlSessionEntry entry = new YamlMsgEntry { Content = "hello", Sequence = 1 };
        var yaml = YamlSerializer.Serialize(entry);
        var result = YamlSerializer.Deserialize<YamlSessionEntry>(Encoding.UTF8.GetBytes(yaml));
        await Assert.That(result).IsTypeOf<YamlMsgEntry>();
    }

    [Test]
    public async Task RoundTrip_AltType_Works()
    {
        YamlSessionEntry entry = new YamlCompactionEntry { From = 10, To = 20 };
        var yaml = YamlSerializer.Serialize(entry);
        var result = YamlSerializer.Deserialize<YamlSessionEntry>(Encoding.UTF8.GetBytes(yaml));
        await Assert.That(result).IsTypeOf<YamlCompactionEntry>();
    }
}

// ── Record-based polymorphic hierarchy ──

[PicoSerializable]
[PicoDerivedType(typeof(YamlRecMsg), "rec_msg")]
[PicoDerivedType(typeof(YamlRecComp), "rec_comp")]
public abstract record YamlPolyRecordBase;

public record YamlRecMsg(string Content, int Sequence) : YamlPolyRecordBase;

public record YamlRecComp(int From, int To) : YamlPolyRecordBase;

public class YamlRecordPolymorphicTests
{
    [Test]
    public async Task Deserialize_PolyRecord_ReturnsCorrectType()
    {
        YamlPolyRecordBase entry = new YamlRecMsg("hello", 42);
        var yaml = YamlSerializer.SerializeToUtf8Bytes(entry);
        var result = YamlSerializer.Deserialize<YamlPolyRecordBase>(yaml);

        await Assert.That(result).IsTypeOf<YamlRecMsg>();
        var msg = (YamlRecMsg)result!;
        await Assert.That(msg.Content).IsEqualTo("hello");
        await Assert.That(msg.Sequence).IsEqualTo(42);
    }

    [Test]
    public async Task Deserialize_PolyRecord_AlternateType()
    {
        YamlPolyRecordBase entry = new YamlRecComp(10, 20);
        var yaml = YamlSerializer.SerializeToUtf8Bytes(entry);
        var result = YamlSerializer.Deserialize<YamlPolyRecordBase>(yaml);

        await Assert.That(result).IsTypeOf<YamlRecComp>();
        var c = (YamlRecComp)result!;
        await Assert.That(c.From).IsEqualTo(10);
        await Assert.That(c.To).IsEqualTo(20);
    }
}
