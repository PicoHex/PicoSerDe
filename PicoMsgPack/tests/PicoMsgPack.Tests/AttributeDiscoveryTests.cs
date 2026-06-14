namespace PicoMsgPack.Tests;

[PicoMsgPackSerializable]
public class MsgPackAttrDto
{
    public string Label { get; set; } = "";
    public int Value { get; set; }
}

public class AttributeDiscoveryTests
{
    [Test]
    public async Task MsgPackAttrDto_RoundTrip()
    {
        var dto = new MsgPackAttrDto { Label = "attr-test", Value = 99 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(dto);
        var result = MsgPackSerializer.Deserialize<MsgPackAttrDto>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Label).IsEqualTo("attr-test");
        await Assert.That(result.Value).IsEqualTo(99);
    }
}
