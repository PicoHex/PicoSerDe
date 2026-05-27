namespace PicoToml.Tests;

public class SimplePoco
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class TomlSerializerTests
{
    [Test]
    public async Task RoundTrip_SimplePoco()
    {
        var original = new SimplePoco { Name = "Alice", Age = 30 };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<SimplePoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }

    [Test]
    public async Task Serialized_ContainsKeys()
    {
        var original = new SimplePoco { Name = "Bob", Age = 25 };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var text = Encoding.UTF8.GetString(bytes);
        await Assert.That(text).Contains("Name");
        await Assert.That(text).Contains("Bob");
        await Assert.That(text).Contains("Age");
        await Assert.That(text).Contains("25");
    }
}
