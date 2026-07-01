namespace PicoMsgPack.Tests;

[PicoSerializable]
[PicoDerivedType(typeof(MpMsgEntry), "msg")]
[PicoDerivedType(typeof(MpCompactionEntry), "compaction")]
public abstract class MpSessionEntry { }

public class MpMsgEntry : MpSessionEntry
{
    public string Content { get; set; } = "";
    public int Sequence { get; set; }
}

public class MpCompactionEntry : MpSessionEntry
{
    public int From { get; set; }
    public int To { get; set; }
}

public class MpPolymorphicTests
{
    [Test]
    public async Task RoundTrip_Polymorphic_ReturnsCorrectDerivedType()
    {
        MpSessionEntry entry = new MpMsgEntry { Content = "hello", Sequence = 1 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(entry);
        var result = MsgPackSerializer.Deserialize<MpSessionEntry>(bytes);
        await Assert.That(result).IsTypeOf<MpMsgEntry>();
        var msg = (MpMsgEntry)result!;
        await Assert.That(msg.Content).IsEqualTo("hello");
        await Assert.That(msg.Sequence).IsEqualTo(1);
    }

    [Test]
    public async Task RoundTrip_AltType_Works()
    {
        MpSessionEntry entry = new MpCompactionEntry { From = 10, To = 20 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(entry);
        var result = MsgPackSerializer.Deserialize<MpSessionEntry>(bytes);
        await Assert.That(result).IsTypeOf<MpCompactionEntry>();
        var ce = (MpCompactionEntry)result!;
        await Assert.That(ce.From).IsEqualTo(10);
    }

    [Test]
    public async Task Serialize_ConcreteType_DoesNotWriteDiscriminator()
    {
        var msg = new MpMsgEntry { Content = "hi", Sequence = 1 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(msg);
        var result = MsgPackSerializer.Deserialize<MpMsgEntry>(bytes);
        await Assert.That(result!.Content).IsEqualTo("hi");
    }
}
