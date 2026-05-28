namespace PicoToml.Tests;

public class TomlReaderSequenceTests
{
    [Test]
    public async Task ReadSequence_SimpleKeyValue_ParsesCorrectly()
    {
        var toml = "Name = \"Alice\"\nAge = 30\n"u8;
        var seq = new ReadOnlySequence<byte>(toml.ToArray());
        var reader = new TomlReader(seq);

        var ok1 = reader.Read();
        var tt1 = reader.TokenType;
        var key1 = Encoding.UTF8.GetString(reader.KeySpan);
        var val1 = Encoding.UTF8.GetString(reader.ValueSpan);
        var ok2 = reader.Read();
        var tt2 = reader.TokenType;
        var key2 = Encoding.UTF8.GetString(reader.KeySpan);
        reader.TryGetInt32(out var age);
        var ok3 = reader.Read();

        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key1).IsEqualTo("Name");
        await Assert.That(val1).IsEqualTo("Alice");
        await Assert.That(ok2).IsTrue();
        await Assert.That(tt2).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key2).IsEqualTo("Age");
        await Assert.That(age).IsEqualTo(30);
        await Assert.That(ok3).IsFalse();
    }

    [Test]
    public async Task ReadSequence_TableHeader_ParsesCorrectly()
    {
        var toml = "[server]\nhost = \"localhost\"\nport = 8080\n"u8;
        var seq = new ReadOnlySequence<byte>(toml.ToArray());
        var reader = new TomlReader(seq);

        var ok1 = reader.Read();
        var tt1 = reader.TokenType;
        var path = Encoding.UTF8.GetString(reader.TablePath);
        var ok2 = reader.Read();
        var tt2 = reader.TokenType;
        var key1 = Encoding.UTF8.GetString(reader.KeySpan);
        var ok3 = reader.Read();
        var key2 = Encoding.UTF8.GetString(reader.KeySpan);

        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(path).IsEqualTo("server");
        await Assert.That(ok2).IsTrue();
        await Assert.That(tt2).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key1).IsEqualTo("host");
        await Assert.That(ok3).IsTrue();
        await Assert.That(key2).IsEqualTo("port");
    }

    [Test]
    public async Task ReadSequence_Array_ParsesCorrectly()
    {
        var toml = "scores = [10, 20, 30]\n"u8;
        var seq = new ReadOnlySequence<byte>(toml.ToArray());
        var reader = new TomlReader(seq);

        var ok1 = reader.Read();
        var tt1 = reader.TokenType;
        var key = Encoding.UTF8.GetString(reader.KeySpan);
        var ok2 = reader.Read();
        var tt2 = reader.TokenType;
        var ok3 = reader.Read();
        reader.TryGetInt32(out var v1);
        var ok4 = reader.Read();
        var ok5 = reader.Read();
        var ok6 = reader.Read();
        var tt6 = reader.TokenType;
        var ok7 = reader.Read();

        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key).IsEqualTo("scores");
        await Assert.That(ok2).IsTrue();
        await Assert.That(tt2).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(v1).IsEqualTo(10);
        await Assert.That(tt6).IsEqualTo(TokenType.ArrayEnd);
        await Assert.That(ok7).IsFalse();
    }
}
