namespace PicoYaml.Tests;

public class YamlReaderTests
{
    [Test]
    public async Task SimpleMapping_KeyValue()
    {
        var r = new YamlReader("name: Alice\n"u8);
        r.Read();
        var k = Encoding.UTF8.GetString(r.KeySpan);
        var v = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(k).IsEqualTo("name");
        await Assert.That(v).IsEqualTo("Alice");
    }

    [Test]
    public async Task IntValue_TryGetInt32()
    {
        var r = new YamlReader("port: 8080\n"u8);
        r.Read();
        var ok = r.TryGetInt32(out var p);
        await Assert.That(ok).IsTrue();
        await Assert.That(p).IsEqualTo(8080);
    }

    [Test]
    public async Task Comment_IsSkipped()
    {
        var r = new YamlReader("# comment\nkey: value\n"u8);
        r.Read();
        var k = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(k).IsEqualTo("key");
    }

    [Test]
    public async Task NestedMapping()
    {
        var r = new YamlReader("server:\n  host: localhost\n  port: 8080\n"u8);
        r.Read();
        var t1 = r.TokenType;
        var k1 = Encoding.UTF8.GetString(r.KeySpan);
        r.Read();
        var t2 = r.TokenType;
        r.Read();
        var t3 = r.TokenType;
        var k2 = Encoding.UTF8.GetString(r.KeySpan);
        var v2 = Encoding.UTF8.GetString(r.ValueSpan);
        r.Read();
        var t4 = r.TokenType;
        var k3 = Encoding.UTF8.GetString(r.KeySpan);
        var ok = r.TryGetInt32(out var port);
        r.Read();
        var t5 = r.TokenType;
        await Assert.That(t1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(k1).IsEqualTo("server");
        await Assert.That(t2).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(t3).IsEqualTo(TokenType.PropertyName);
        await Assert.That(k2).IsEqualTo("host");
        await Assert.That(v2).IsEqualTo("localhost");
        await Assert.That(t4).IsEqualTo(TokenType.PropertyName);
        await Assert.That(k3).IsEqualTo("port");
        await Assert.That(ok).IsTrue();
        await Assert.That(port).IsEqualTo(8080);
        await Assert.That(t5).IsEqualTo(TokenType.ObjectEnd);
    }

    [Test]
    public async Task Sequence_Items()
    {
        var r = new YamlReader("tags:\n  - dev\n  - runner\n"u8);
        r.Read();
        var k1 = Encoding.UTF8.GetString(r.KeySpan);
        r.Read();
        var t2 = r.TokenType;
        r.Read();
        var v1 = Encoding.UTF8.GetString(r.ValueSpan);
        r.Read();
        var v2 = Encoding.UTF8.GetString(r.ValueSpan);
        r.Read();
        var tEnd = r.TokenType;
        await Assert.That(k1).IsEqualTo("tags");
        await Assert.That(t2).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(v1).IsEqualTo("dev");
        await Assert.That(v2).IsEqualTo("runner");
        await Assert.That(tEnd).IsEqualTo(TokenType.ObjectEnd);
    }

    [Test]
    public async Task FloatValue_TryGetFloat64()
    {
        var r = new YamlReader("pi: 3.14\n"u8);
        r.Read();
        var ok = r.TryGetFloat64(out var pi);
        await Assert.That(ok).IsTrue();
        await Assert.That(pi).IsGreaterThan(3.13);
    }

    [Test]
    public async Task BoolValue_TryGetBool()
    {
        var r = new YamlReader("enabled: true\n"u8);
        r.Read();
        var ok = r.TryGetBool(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task Int64Value_TryGetInt64()
    {
        var r = new YamlReader("big: 9999999999\n"u8);
        r.Read();
        var ok = r.TryGetInt64(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(9999999999L);
    }

    [Test]
    public async Task FlowMapping_ParsesInline()
    {
        var r = new YamlReader("point: {x: 1, y: 2}\n"u8);
        var tokens = new List<(TokenType Type, string? Key, string? Value)>();
        while (r.Read())
        {
            var v = r.ValueSpan.Length > 0 ? Encoding.UTF8.GetString(r.ValueSpan) : null;
            var k = r.KeySpan.Length > 0 ? Encoding.UTF8.GetString(r.KeySpan) : null;
            tokens.Add((r.TokenType, k, v));
        }
        await Assert.That(tokens.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(tokens[0].Type).IsEqualTo(TokenType.PropertyName);
        await Assert.That(tokens[0].Key).IsEqualTo("point");
        await Assert.That(tokens[1].Type).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(tokens[2].Type).IsEqualTo(TokenType.PropertyName);
        await Assert.That(tokens[2].Key).IsEqualTo("x");
    }

    [Test]
    public async Task TryGetBool_TrueLiteral_ReturnsTrue()
    {
        var r = new YamlReader("enabled: true\n"u8);
        r.Read();
        var ok = r.TryGetBool(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task TryGetBool_FalseLiteral_ReturnsFalse()
    {
        var r = new YamlReader("enabled: false\n"u8);
        r.Read();
        var ok = r.TryGetBool(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsFalse();
    }

    [Test]
    public async Task TryGetBool_TreeLiteral_ReturnsFalse()
    {
        // "tree" starts with 't' but is NOT "true" — TryGetBool must reject it
        var r = new YamlReader("species: tree\n"u8);
        r.Read();
        var ok = r.TryGetBool(out var v);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryGetBool_FrogLiteral_ReturnsFalse()
    {
        // "frog" starts with 'f' but is NOT "false" — TryGetBool must reject it
        var r = new YamlReader("animal: frog\n"u8);
        r.Read();
        var ok = r.TryGetBool(out var v);
        await Assert.That(ok).IsFalse();
    }
}
