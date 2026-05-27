namespace PicoYaml.Tests;

public class YamlSerModel
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class YamlSerializerTests
{
    [Test]
    public async Task RoundTrip_SimpleModel()
    {
        var original = new YamlSerModel { Name = "Alice", Age = 30 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlSerModel>(bytes);

        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }

    [Test]
    public async Task Serialized_ContainsYamlFormat()
    {
        var original = new YamlSerModel { Name = "Bob", Age = 25 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("Name");
        await Assert.That(text).Contains("Bob");
        await Assert.That(text).Contains("Age");
        await Assert.That(text).Contains("25");
    }
}
