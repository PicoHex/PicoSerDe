namespace PicoYaml.Tests;

public class YamlReaderSequenceTests
{
    [Test]
    public async Task ReadSequence_SimpleKeyValue_ParsesCorrectly()
    {
        var yaml = "Name: Alice\nAge: 30\n"u8;
        var seq = new ReadOnlySequence<byte>(yaml.ToArray());
        var reader = new YamlReader(seq);

        var ok1 = reader.Read();
        var tt1 = reader.TokenType;
        var key1 = Encoding.UTF8.GetString(reader.KeySpan);
        var val1 = Encoding.UTF8.GetString(reader.ValueSpan);

        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key1).IsEqualTo("Name");
        await Assert.That(val1).IsEqualTo("Alice");
    }

    [Test]
    public async Task ReadSequence_WithNumbers_ParsesCorrectly()
    {
        var yaml = "port: 8080\nhost: localhost\n"u8;
        var seq = new ReadOnlySequence<byte>(yaml.ToArray());
        var reader = new YamlReader(seq);

        var ok1 = reader.Read();
        var tt1 = reader.TokenType;
        var key1 = Encoding.UTF8.GetString(reader.KeySpan);
        reader.TryGetInt32(out var port);

        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key1).IsEqualTo("port");
        await Assert.That(port).IsEqualTo(8080);
    }
}
