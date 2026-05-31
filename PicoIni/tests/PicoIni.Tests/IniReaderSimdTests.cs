namespace PicoIni.Tests;

public class IniReaderSimdTests
{
    [Test]
    public async Task WhitespaceHeavy_ScalarParsing()
    {
        var ini = """


            key1 = value1


            key2 = value2


            """;
        var bytes = Encoding.UTF8.GetBytes(ini);

        string k1, v1, k2, v2;
        using (var reader = new IniReader(bytes))
        {
            reader.Read();
            k1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.ReadValue();
            v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            k2 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.ReadValue();
            v2 = Encoding.UTF8.GetString(reader.GetStringRaw());
        }

        await Assert.That(k1).IsEqualTo("key1");
        await Assert.That(v1).IsEqualTo("value1");
        await Assert.That(k2).IsEqualTo("key2");
        await Assert.That(v2).IsEqualTo("value2");
    }

    [Test]
    public async Task WhitespaceHeavy_WithSections()
    {
        var ini = """

            [section1]

            a = 1

            b = 2

            [section2]

            x = hello

            """;
        var bytes = Encoding.UTF8.GetBytes(ini);

        var sections = new List<string>();
        using (var reader = new IniReader(bytes))
        {
            while (reader.Read())
            {
                if (reader.TokenType == TokenType.ObjectStart)
                    sections.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
            }
        }

        await Assert.That(sections).HasCount(2);
        await Assert.That(sections[0]).IsEqualTo("section1");
        await Assert.That(sections[1]).IsEqualTo("section2");
    }

    [Test]
    public async Task WhitespaceHeavy_QuotedValue()
    {
        var ini = """
            
            key = "hello world"
            """;
        var bytes = Encoding.UTF8.GetBytes(ini);

        string key, value;
        using (var reader = new IniReader(bytes))
        {
            reader.Read();
            key = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.ReadValue();
            value = Encoding.UTF8.GetString(reader.GetStringRaw());
        }

        await Assert.That(key).IsEqualTo("key");
        await Assert.That(value).IsEqualTo("hello world");
    }
}
