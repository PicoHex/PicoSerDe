namespace PicoYaml.Tests;

[PicoSerializable]
[PicoDerivedType(typeof(YamlMsgEntry), "msg")]
[PicoDerivedType(typeof(YamlCompactionEntry), "compaction")]
public abstract class YamlSessionEntry { }

public class YamlMsgEntry : YamlSessionEntry
{
    public string Content { get; set; } = "";
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
