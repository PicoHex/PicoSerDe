namespace PicoYaml.Tests;

[YamlTag("!person")]
public class YamlTaggedPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

[YamlTag("!config")]
public class YamlTaggedConfig
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class YamlTagTests
{
    [Test]
    public async Task Serialize_TaggedType_EmitsTag()
    {
        var person = new YamlTaggedPerson { Name = "Alice", Age = 30 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(person);
        var str = Encoding.UTF8.GetString(bytes);

        await Assert.That(str).Contains("!person");
        await Assert.That(str).Contains("Name: Alice");
        await Assert.That(str).Contains("Age: 30");
        // The tag must terminate its own line; gluing it to the first property
        // (e.g. "!person Name: ...") produces malformed YAML the reader loops on.
        await Assert.That(str).StartsWith("!person\n");
    }

    [Test]
    public async Task Serialize_TaggedType_Roundtrips()
    {
        var person = new YamlTaggedPerson { Name = "Bob", Age = 25 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(person);
        var result = YamlSerializer.Deserialize<YamlTaggedPerson>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Bob");
        await Assert.That(result!.Age).IsEqualTo(25);
    }

    [Test]
    public async Task Serialize_TaggedConfig_EmitsTag()
    {
        var config = new YamlTaggedConfig { Key = "host", Value = "localhost" };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(config);
        var str = Encoding.UTF8.GetString(bytes);

        await Assert.That(str).Contains("!config");
        await Assert.That(str).Contains("Key: host");
        await Assert.That(str).Contains("Value: localhost");
    }
}
