namespace PicoYaml.Tests;

public class YamlReaderEdgeTests
{
    [Test]
    public async Task QuotedString_Value()
    {
        var yaml = "message: \"hello world\"\n"u8;
        var reader = new YamlReader(yaml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(val).IsEqualTo("hello world");
    }

    [Test]
    public async Task BoolValue_IsParsed()
    {
        var yaml = "enabled: true\n"u8;
        var reader = new YamlReader(yaml);
        reader.Read();
        var isTrue = reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'t';
        await Assert.That(isTrue).IsTrue();
    }

    [Test]
    public async Task NullValue_ProducesTilde()
    {
        var yaml = "missing: ~\n"u8;
        var reader = new YamlReader(yaml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(val).IsEqualTo("~");
    }

    [Test]
    public async Task FloatValue_IsParsed()
    {
        var yaml = "pi: 3.14\n"u8;
        var reader = new YamlReader(yaml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(val).Contains("3.14");
    }

    [Test]
    public async Task NegativeInt_IsParsed()
    {
        var yaml = "offset: -5\n"u8;
        var reader = new YamlReader(yaml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(val).Contains("-5");
    }

    [Test]
    public async Task EmptyDocument_ReturnsFalse()
    {
        var yaml = ""u8;
        var reader = new YamlReader(yaml);
        var ok = reader.Read();
        await Assert.That(ok).IsFalse();
    }
}
