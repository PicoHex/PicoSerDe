namespace PicoToml.Tests;

public class TomlReaderEdgeTests
{
    [Test]
    public async Task MultilineBasicString()
    {
        var toml = "desc = \"\"\"\nline one\nline two\"\"\"\n"u8;
        var reader = new TomlReader(toml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(val).Contains("line one");
        await Assert.That(val).Contains("line two");
    }

    [Test]
    public async Task LiteralString_SingleLine()
    {
        var toml = "path = 'C:\\Users\\test'\n"u8;
        var reader = new TomlReader(toml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(val).Contains("C:");
    }

    [Test]
    public async Task DateParsing()
    {
        var toml = "created = 2024-01-15\n"u8;
        var reader = new TomlReader(toml);
        reader.Read();
        var val = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(val).Contains("2024-01-15");
    }

    [Test]
    public async Task ArrayOfTables_WithMultipleItems()
    {
        var toml = "[[fruits]]\nname = \"apple\"\n\n[[fruits]]\nname = \"banana\"\n"u8;
        var reader = new TomlReader(toml);
        var names = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == TokenType.PropertyName)
            {
                var key = Encoding.UTF8.GetString(reader.KeySpan);
                if (key == "name")
                    names.Add(Encoding.UTF8.GetString(reader.ValueSpan));
            }
        }
        await Assert.That(names).Contains("apple");
        await Assert.That(names).Contains("banana");
        await Assert.That(names.Count).IsEqualTo(2);
    }
}
