namespace PicoYaml.Tests;

public class YamlReaderFastPathTests
{
    [Test]
    public async Task TryReadInt32ArrayFast_Basic()
    {
        var yaml = "[1, 42, -7, 0]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        var list = new List<int>();
        var buf = new int[4];
        var n = reader.TryReadInt32ArrayFast(buf);
        for (int i = 0; i < n; i++) list.Add(buf[i]);
        await Assert.That(list).HasCount(4);
        await Assert.That(list[0]).IsEqualTo(1);
        await Assert.That(list[1]).IsEqualTo(42);
        await Assert.That(list[2]).IsEqualTo(-7);
        await Assert.That(list[3]).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadInt64ArrayFast_Basic()
    {
        var yaml = "[1, -5, 9223372036854775807]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        var list = new List<long>();
        var buf = new long[3];
        var n = reader.TryReadInt64ArrayFast(buf);
        for (int i = 0; i < n; i++) list.Add(buf[i]);
        await Assert.That(list).HasCount(3);
        await Assert.That(list[0]).IsEqualTo(1);
        await Assert.That(list[1]).IsEqualTo(-5);
        await Assert.That(list[2]).IsEqualTo(9223372036854775807);
    }

    [Test]
    public async Task TryReadBoolArrayFast_Basic()
    {
        var yaml = "[true, false, true]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        var list = new List<bool>();
        var buf = new bool[3];
        var n = reader.TryReadBoolArrayFast(buf);
        for (int i = 0; i < n; i++) list.Add(buf[i]);
        await Assert.That(list).HasCount(3);
        await Assert.That(list[0]).IsTrue();
        await Assert.That(list[1]).IsFalse();
        await Assert.That(list[2]).IsTrue();
    }

    [Test]
    public async Task TryReadInt32ArrayFast_Empty()
    {
        var yaml = "[]"u8.ToArray();
        var reader = new YamlReader(yaml);
        reader.Read();
        var buf = new int[1];
        var n = reader.TryReadInt32ArrayFast(buf);
        await Assert.That(n).IsEqualTo(0);
    }
}
