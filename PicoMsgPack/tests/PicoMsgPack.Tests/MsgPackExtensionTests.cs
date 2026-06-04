namespace PicoMsgPack.Tests;

using PicoMsgPack;

public class MsgPackExtModel
{
    [MsgPackKey(0)]
    public byte[] RawData { get; set; } = Array.Empty<byte>();

    [MsgPackExtensionTag(42)]
    [MsgPackKey(1)]
    public byte[] ExtData { get; set; } = Array.Empty<byte>();
}

public class MsgPackExtensionTests
{
    [Test]
    public async Task SerializeDeserialize_ExtensionType_Roundtrips()
    {
        var model = new MsgPackExtModel
        {
            RawData = new byte[] { 1, 2, 3 },
            ExtData = new byte[] { 10, 20, 30, 40 },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<MsgPackExtModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.RawData[0]).IsEqualTo((byte)1);
        await Assert.That(result.RawData[1]).IsEqualTo((byte)2);
        await Assert.That(result.RawData[2]).IsEqualTo((byte)3);
        await Assert.That(result.RawData.Length).IsEqualTo(3);
        await Assert.That(result!.ExtData[0]).IsEqualTo((byte)10);
        await Assert.That(result.ExtData[1]).IsEqualTo((byte)20);
        await Assert.That(result.ExtData[2]).IsEqualTo((byte)30);
        await Assert.That(result.ExtData[3]).IsEqualTo((byte)40);
        await Assert.That(result.ExtData.Length).IsEqualTo(4);
    }

    [Test]
    public async Task SerializeDeserialize_ExtensionType_Empty_Roundtrips()
    {
        var model = new MsgPackExtModel
        {
            RawData = Array.Empty<byte>(),
            ExtData = Array.Empty<byte>(),
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<MsgPackExtModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.RawData.Length).IsEqualTo(0);
        await Assert.That(result!.ExtData.Length).IsEqualTo(0);
    }
}
