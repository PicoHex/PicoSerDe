namespace PicoYaml.Tests;

public class YamlWriterEdgeTests
{
    [Test]
    public async Task WriteBoolean()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("enabled"u8);
        w.WriteBoolean(true);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).IsEqualTo("enabled: true\n");
    }

    [Test]
    public async Task WriteInt64()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("big"u8);
        w.WriteInt64(9_999_999_999L);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("9" + new string('9', 9));
    }

    [Test]
    public async Task WriteDouble()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("pi"u8);
        w.WriteDouble(3.14);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("3.14");
    }

    [Test]
    public async Task QuotedString_WhenNeeded()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("comment"u8);
        w.WriteString("# not a comment"u8);
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("\"# not a comment\"");
    }

    [Test]
    public async Task Sequence_WithItems()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("tags"u8);
        w.WriteStartMapping();
        w.WritePropertyName("items"u8);
        w.WriteSequenceItem("dev"u8);
        w.WriteSequenceItem("runner"u8);
        w.WriteEndMapping();
        var result = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(result).Contains("items:");
        await Assert.That(result).Contains("- dev");
        await Assert.That(result).Contains("- runner");
    }

    [Test]
    public async Task BytesWritten_TracksOutput()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        var initial = w.BytesWritten;
        w.WritePropertyName("x"u8);
        w.WriteNumber(1);
        var after = w.BytesWritten;
        await Assert.That(initial).IsEqualTo(0);
        await Assert.That(after).IsGreaterThan(0);
    }
}
