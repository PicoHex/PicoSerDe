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
}
