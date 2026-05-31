namespace PicoYaml.Tests;

public class YamlReaderFastPathTests
{
    [Test]
    public async Task TryReadInt32ArrayFast_Basic()
    {
        var yaml = "[1, 42, -7, 0]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        Span<int> dest = new int[4];
        var n = reader.TryReadInt32ArrayFast(dest);
        await Assert.That(n).IsEqualTo(4);
        await Assert.That(dest[0]).IsEqualTo(1);
        await Assert.That(dest[1]).IsEqualTo(42);
        await Assert.That(dest[2]).IsEqualTo(-7);
        await Assert.That(dest[3]).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadInt64ArrayFast_Basic()
    {
        var yaml = "[1, -5, 9223372036854775807]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        Span<long> dest = new long[3];
        var n = reader.TryReadInt64ArrayFast(dest);
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(dest[0]).IsEqualTo(1);
        await Assert.That(dest[1]).IsEqualTo(-5);
        await Assert.That(dest[2]).IsEqualTo(9223372036854775807);
    }

    [Test]
    public async Task TryReadBoolArrayFast_Basic()
    {
        var yaml = "[true, false, true]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        Span<bool> dest = new bool[3];
        var n = reader.TryReadBoolArrayFast(dest);
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(dest[0]).IsTrue();
        await Assert.That(dest[1]).IsFalse();
        await Assert.That(dest[2]).IsTrue();
    }

    [Test]
    public async Task TryReadInt32ArrayFast_Empty()
    {
        var yaml = "[]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        Span<int> dest = new int[1];
        var n = reader.TryReadInt32ArrayFast(dest);
        await Assert.That(n).IsEqualTo(0);
    }
}
