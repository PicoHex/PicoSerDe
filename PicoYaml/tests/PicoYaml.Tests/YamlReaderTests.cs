namespace PicoYaml.Tests;

public class YamlReaderTests
{
    [Test]
    public async Task SimpleMapping_KeyValue()
    {
        var r = new YamlReader("name: Alice\n"u8);
        r.Read(); var k = Encoding.UTF8.GetString(r.KeySpan); var v = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(k).IsEqualTo("name"); await Assert.That(v).IsEqualTo("Alice");
    }
    [Test]
    public async Task IntValue_TryGetInt32()
    {
        var r = new YamlReader("port: 8080\n"u8);
        r.Read(); var ok = r.TryGetInt32(out var p);
        await Assert.That(ok).IsTrue(); await Assert.That(p).IsEqualTo(8080);
    }
    [Test]
    public async Task Comment_IsSkipped()
    {
        var r = new YamlReader("# comment\nkey: value\n"u8);
        r.Read(); var k = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(k).IsEqualTo("key");
    }
    [Test]
    public async Task NestedMapping()
    {
        var r = new YamlReader("server:\n  host: localhost\n  port: 8080\n"u8);
        r.Read(); var t1 = r.TokenType; var k1 = Encoding.UTF8.GetString(r.KeySpan);
        r.Read(); var t2 = r.TokenType;
        r.Read(); var t3 = r.TokenType; var k2 = Encoding.UTF8.GetString(r.KeySpan); var v2 = Encoding.UTF8.GetString(r.ValueSpan);
        r.Read(); var t4 = r.TokenType; var k3 = Encoding.UTF8.GetString(r.KeySpan); var ok = r.TryGetInt32(out var port);
        r.Read(); var t5 = r.TokenType;
        await Assert.That(t1).IsEqualTo(TokenType.PropertyName); await Assert.That(k1).IsEqualTo("server");
        await Assert.That(t2).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(t3).IsEqualTo(TokenType.PropertyName); await Assert.That(k2).IsEqualTo("host");
        await Assert.That(v2).IsEqualTo("localhost");
        await Assert.That(t4).IsEqualTo(TokenType.PropertyName); await Assert.That(k3).IsEqualTo("port");
        await Assert.That(ok).IsTrue(); await Assert.That(port).IsEqualTo(8080);
        await Assert.That(t5).IsEqualTo(TokenType.ObjectEnd);
    }
    [Test]
    public async Task Sequence_Items()
    {
        var r = new YamlReader("tags:\n  - dev\n  - runner\n"u8);
        r.Read(); var k1 = Encoding.UTF8.GetString(r.KeySpan);
        r.Read(); var t2 = r.TokenType;
        r.Read(); var v1 = Encoding.UTF8.GetString(r.ValueSpan);
        r.Read(); var v2 = Encoding.UTF8.GetString(r.ValueSpan);
        r.Read(); var tEnd = r.TokenType;
        await Assert.That(k1).IsEqualTo("tags"); await Assert.That(t2).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(v1).IsEqualTo("dev"); await Assert.That(v2).IsEqualTo("runner");
        await Assert.That(tEnd).IsEqualTo(TokenType.ObjectEnd);
    }
}
