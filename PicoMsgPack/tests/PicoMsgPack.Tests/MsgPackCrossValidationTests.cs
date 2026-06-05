namespace PicoMsgPack.Tests;

// Minimal model — the MsgPack SG has CS0305 List<T> issues with generics
public class MpModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public string String { get; set; } = "";
}

public class MsgPackCrossValidationTests
{
    private static MpModel Model => new() { Bool = true, Int = 42, String = "Hello" };

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(Model);
        var back = MsgPackSerializer.Deserialize<MpModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(Model);
        var back = MsgPackSerializer.Deserialize<MpModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back.Bool).IsTrue();
        await Assert.That(back.Int).IsEqualTo(42);
        await Assert.That(back.String).IsEqualTo("Hello");
    }
}
