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

    // ── multi-document support ──

    [Test]
    public async Task MultiDocument_Separator_EmitsObjectEndThenStart()
    {
        var yaml = "a: 1\n---\nb: 2"u8.ToArray();
        var tokens = new List<TokenType>();
        var keys = new List<string>();
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                tokens.Add(reader.TokenType);
                if (reader.TokenType == TokenType.PropertyName)
                    keys.Add(Encoding.UTF8.GetString(reader.KeySpan));
            }
        }
        // Doc 1: ObjectStart, a:1 PropertyName, ObjectEnd
        // Doc 2: ObjectStart, b:2 PropertyName, ObjectEnd
        await Assert.That(keys.Count).IsEqualTo(2);
        await Assert.That(keys[0]).IsEqualTo("a");
        await Assert.That(keys[1]).IsEqualTo("b");
        await Assert.That(tokens).Contains(TokenType.ObjectEnd);
    }
}
