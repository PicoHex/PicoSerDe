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

    [Test]
    public async Task Converter_RoundTrip_UsesCustomConverter()
    {
        var model = new ConverterModel
        {
            Name = "Test",
            Tag = "hello",
            Score = 42
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);

        // Verify converter was used during serialization: should contain "CVT:" and "NUM:"
        var asString = System.Text.Encoding.UTF8.GetString(bytes);
        await Assert.That(asString).Contains("CVT:hello");
        await Assert.That(asString).Contains("NUM:42");

        var result = MsgPackSerializer.Deserialize<ConverterModel>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Tag).IsEqualTo("hello");
        await Assert.That(result.Score).IsEqualTo(42);
    }
}

public class TestConverter : IMsgPackConverter<string>
{
    public void Write(IBufferWriter<byte> writer, string value)
    {
        // Prefix with marker to make it unambiguous
        var bytes = System.Text.Encoding.UTF8.GetBytes($"CVT:{value}");
        writer.Write(bytes);
    }

    public string Read(ref MsgPackReader reader)
    {
        var raw = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());
        if (raw.StartsWith("CVT:"))
            return raw.Substring(4);
        throw new FormatException($"Expected CVT: prefix, got: {raw}");
    }
}

// IntToString converter — serializes int as string, deserializes back to int
public class IntToStringConverter : IMsgPackConverter<int>
{
    public void Write(IBufferWriter<byte> writer, int value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"NUM:{value}");
        writer.Write(bytes);
    }

    public int Read(ref MsgPackReader reader)
    {
        var raw = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());
        if (raw.StartsWith("NUM:"))
            return int.Parse(raw.Substring(4));
        throw new FormatException($"Expected NUM: prefix, got: {raw}");
    }
}

public class ConverterModel
{
    [MsgPackKey(0)]
    public string Name { get; set; } = "";

    [MsgPackKey(1)]
    [MsgPackConverter(typeof(TestConverter))]
    public string Tag { get; set; } = "";

    [MsgPackKey(2)]
    [MsgPackConverter(typeof(IntToStringConverter))]
    public int Score { get; set; }
}
