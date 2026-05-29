namespace PicoMsgPack.Tests;

public class MsgPackAttributeTests
{
    [Test]
    public async Task MsgPackKeyAttribute_HasCorrectValue()
    {
        var attr = new MsgPackKeyAttribute(5);
        await Assert.That(attr.Key).IsEqualTo(5);
    }

    [Test]
    public async Task MsgPackIgnoreAttribute_Exists()
    {
        var attr = new MsgPackIgnoreAttribute();
        await Assert.That(attr).IsNotNull();
    }

    [Test]
    public async Task MsgPackConverterAttribute_HoldsType()
    {
        var attr = new MsgPackConverterAttribute(typeof(TestConverter));
        await Assert.That(attr.ConverterType).IsEqualTo(typeof(TestConverter));
    }
}

public class TestConverter : IMsgPackConverter<string>
{
    public void Write(IBufferWriter<byte> writer, string value) { }
    public string Read(ref MsgPackReader reader) => "";
}
