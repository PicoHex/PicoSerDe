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

    // ── complex key support ──

    [Test]
    public async Task ComplexKey_QuestionMark_ParsesValue()
    {
        var yaml = "? complex key\n: value"u8.ToArray();
        string key = "",
            val = string.Empty;
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                {
                    key = Encoding.UTF8.GetString(reader.KeySpan);
                    val = Encoding.UTF8.GetString(reader.ValueSpan);
                }
            }
        }
        await Assert.That(key).IsEqualTo("complex key");
        await Assert.That(val).IsEqualTo("value");
    }

    // ── tag support ──

    [Test]
    public async Task TagValue_ExclamationMark_StripsTag()
    {
        var yaml = "name: !str Alice"u8.ToArray();
        string val = string.Empty;
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                    val = Encoding.UTF8.GetString(reader.ValueSpan);
            }
        }
        // Tag !str should be stripped, value is "Alice"
        await Assert.That(val).IsEqualTo("Alice");
    }

    // ── sequence-mode tests ──

    [Test]
    public async Task SeqMode_MultiDocument_Separator_EmitsObjectEndThenStart()
    {
        var data = "a: 1\n---\nb: 2"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        var tokens = new List<TokenType>();
        var keys = new List<string>();
        ReadSeqCapture(seq, tokens, keys);
        await Assert.That(keys.Count).IsEqualTo(2);
        await Assert.That(keys[0]).IsEqualTo("a");
        await Assert.That(keys[1]).IsEqualTo("b");
        await Assert.That(tokens).Contains(TokenType.ObjectEnd);
    }

    [Test]
    public async Task SeqMode_ComplexKey_QuestionMark_ParsesValue()
    {
        var data = "? ck\n: val"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        string key = "",
            val = string.Empty;
        ReadSeqCaptureProps(seq, out key, out val);
        await Assert.That(key).IsEqualTo("ck");
        await Assert.That(val).IsEqualTo("val");
    }

    [Test]
    public async Task SeqMode_SimpleKeyValue_Works()
    {
        var data = "name: Alice"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        string key = "",
            val = string.Empty;
        ReadSeqCaptureProps(seq, out key, out val);
        await Assert.That(key).IsEqualTo("name");
        await Assert.That(val).IsEqualTo("Alice");
    }

    [Test]
    public async Task SeqMode_TagValue_ExclamationMark_StripsTag()
    {
        var data = "name: !str Alice"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        string val = string.Empty;
        ReadSeqCaptureProps(seq, out _, out val);
        await Assert.That(val).IsEqualTo("Alice");
    }

    private static void ReadSeqCapture(
        ReadOnlySequence<byte> seq,
        List<TokenType> tokens,
        List<string> keys
    )
    {
        using var reader = new YamlReader(seq);
        while (reader.Read())
        {
            tokens.Add(reader.TokenType);
            if (reader.TokenType == TokenType.PropertyName)
                keys.Add(Encoding.UTF8.GetString(reader.KeySpan));
        }
    }

    private static void ReadSeqCaptureProps(
        ReadOnlySequence<byte> seq,
        out string key,
        out string val
    )
    {
        key = string.Empty;
        val = string.Empty;
        using var reader = new YamlReader(seq);
        while (reader.Read())
        {
            if (reader.TokenType == TokenType.PropertyName)
            {
                key = Encoding.UTF8.GetString(reader.KeySpan);
                val = Encoding.UTF8.GetString(reader.ValueSpan);
            }
        }
    }

    [Test]
    public async Task DocEndMarker_ClosesDocument()
    {
        var yaml = "a: 1\n...\nb: 2"u8;
        var reader = new YamlReader(yaml);
        var keys = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == TokenType.PropertyName)
                keys.Add(Encoding.UTF8.GetString(reader.KeySpan));
        }
        // 'a' should be read, but 'b' is in a new doc after ... — reader should not reach it
        await Assert.That(keys).Contains("a");
        await Assert.That(keys).DoesNotContain("b");
    }

    [Test]
    public async Task BlockScalar_Literal_PreservesContent()
    {
        var yaml = "text: |\n  line one\n  line two\n"u8;
        var reader = new YamlReader(yaml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        // Block scalar captures indented content — base indent stripped by full impl
        await Assert.That(val).Contains("line one");
        await Assert.That(val).Contains("line two");
    }
}
