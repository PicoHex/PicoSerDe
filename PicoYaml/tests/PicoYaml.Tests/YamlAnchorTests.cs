namespace PicoYaml.Tests;

public class YamlAnchorTests
{
    [Test]
    public async Task SimpleAnchor_DefineAndReference_ResolvesValue()
    {
        var yaml = "name: &label Alice\ncopy: *label"u8.ToArray();
        string k1, v1, k2, v2;
        using (var reader = new YamlReader(yaml))
        {
            reader.Read();
            k1 = Encoding.UTF8.GetString(reader.KeySpan);
            v1 = Encoding.UTF8.GetString(reader.ValueSpan);
            reader.Read();
            k2 = Encoding.UTF8.GetString(reader.KeySpan);
            v2 = Encoding.UTF8.GetString(reader.ValueSpan);
        }
        await Assert.That(k1).IsEqualTo("name");
        await Assert.That(v1).IsEqualTo("Alice");
        await Assert.That(k2).IsEqualTo("copy");
        await Assert.That(v2).IsEqualTo("Alice"); // alias resolves to Alice
    }

    [Test]
    public async Task UnresolvedAlias_ThrowsFormatException()
    {
        var yaml = "key: *undefined"u8.ToArray();
        using (var reader = new YamlReader(yaml))
        {
            try
            {
                reader.Read();
                throw new Exception("Expected FormatException");
            }
            catch (FormatException) { }
        }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task DuplicateAnchor_ThrowsFormatException()
    {
        var yaml = "a: &dup 1\nb: &dup 2"u8.ToArray();
        using (var reader = new YamlReader(yaml))
        {
            try
            {
                while (reader.Read()) { }
                throw new Exception("Expected FormatException");
            }
            catch (FormatException) { }
        }
        await Assert.That(true).IsTrue();
    }

    // ── Merge key (<<:) ──

    [Test]
    public async Task MappingAnchor_StoresPairs()
    {
        // Verify &def on a mapping stores key-value pairs
        var yaml = "defaults: &def\n  host: localhost\n  port: 8080"u8.ToArray();
        var keys = new List<string>();
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.PropertyName)
                    keys.Add(Encoding.UTF8.GetString(reader.KeySpan));
            }
        }
        await Assert.That(keys).Contains("defaults");
        await Assert.That(keys).Contains("host");
        await Assert.That(keys).Contains("port");
    }

    [Test]
    public async Task MergeKey_MergesAnchorMapping()
    {
        // server section merges def into itself:
        //   server:
        //     <<: *def      (brings host=localhost, port=8080)
        //     name: main     (explicit override)
        var yaml = "defaults: &def\n  host: localhost\n  port: 8080\nserver:\n  <<: *def\n  name: main"u8.ToArray();
        bool inServer = false;
        var serverKeys = new List<string>();
        var serverVals = new List<string>();
        using (var reader = new YamlReader(yaml))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.ObjectStart) continue;
                if (reader.TokenType == TokenType.ObjectEnd) { inServer = false; continue; }
                if (reader.TokenType == TokenType.PropertyName)
                {
                    var key = Encoding.UTF8.GetString(reader.KeySpan);
                    if (key == "server") { inServer = true; continue; }
                    if (inServer)
                    {
                        serverKeys.Add(key);
                        serverVals.Add(Encoding.UTF8.GetString(reader.ValueSpan));
                    }
                }
            }
        }
        await Assert.That(serverKeys).Contains("host");
        await Assert.That(serverKeys).Contains("port");
        await Assert.That(serverKeys).Contains("name");
    }
}
