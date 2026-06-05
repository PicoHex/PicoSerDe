namespace PicoMsgPack.Tests;

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
        await AssertMpEqual(Model, back!);
    }

    private static async Task AssertMpEqual(MpModel expected, MpModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.String).IsEqualTo(expected.String);
    }
}
