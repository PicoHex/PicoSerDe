namespace PicoYaml.Tests;

public class YamlSerModel
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class YamlListModel
{
    public string Name { get; set; } = "";
    public List<int> Scores { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public class YamlNullableModel
{
    public string Name { get; set; } = "";
    public int? Age { get; set; }
    public double? Rating { get; set; }
    public bool? Enabled { get; set; }
}

public class YamlTemporalModel
{
    public DateTime CreatedAt { get; set; }
}

public class YamlGuidModel
{
    public Guid Id { get; set; }
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

    [Test]
    public async Task RoundTrip_ListModel()
    {
        var original = new YamlListModel
        {
            Name = "Test",
            Scores = new List<int> { 10, 20, 30 },
            Tags = new List<string> { "dev", "runner" }
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlListModel>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Scores).IsNotNull();
        await Assert.That(result.Scores.Count).IsEqualTo(3);
        await Assert.That(result.Tags.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RoundTrip_Nullable()
    {
        var original = new YamlNullableModel
        {
            Name = "N",
            Age = 42,
            Rating = 4.5,
            Enabled = true
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlNullableModel>(bytes);
        await Assert.That(result!.Name).IsEqualTo("N");
        await Assert.That(result.Age).IsEqualTo(42);
        await Assert.That(result.Rating).IsEqualTo(4.5);
        await Assert.That(result.Enabled).IsTrue();
    }

    [Test]
    public async Task RoundTrip_Nullable_Nulls()
    {
        var original = new YamlNullableModel
        {
            Name = "N",
            Age = null,
            Rating = null,
            Enabled = null
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlNullableModel>(bytes);
        await Assert.That(result!.Name).IsEqualTo("N");
        await Assert.That(result.Age).IsNull();
        await Assert.That(result.Rating).IsNull();
        await Assert.That(result.Enabled).IsNull();
    }

    [Test]
    public async Task RoundTrip_DateTime()
    {
        var dt = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var original = new YamlTemporalModel { CreatedAt = dt };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlTemporalModel>(bytes);
        await Assert.That(result!.CreatedAt).IsEqualTo(dt);
    }

    [Test]
    public async Task RoundTrip_Guid()
    {
        var g = Guid.NewGuid();
        var original = new YamlGuidModel { Id = g };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlGuidModel>(bytes);
        await Assert.That(result!.Id).IsEqualTo(g);
    }
}
