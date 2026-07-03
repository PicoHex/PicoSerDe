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

public class YamlAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
}

public class YamlNestedModel
{
    public string Name { get; set; } = "";
    public YamlAddress Address { get; set; } = new();
}

public class YamlDictModel
{
    public string Name { get; set; } = "";
    public Dictionary<string, int> Scores { get; set; } = new();
}

public class YamlNestedWithList
{
    public string Street { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

public class YamlOuterWithNestedList
{
    public string Name { get; set; } = "";
    public YamlNestedWithList Child { get; set; } = new();
}

public class YamlNestedWithDict
{
    public string Label { get; set; } = "";
    public Dictionary<string, int> Counts { get; set; } = new();
}

public class YamlOuterWithNestedDict
{
    public string Name { get; set; } = "";
    public YamlNestedWithDict Child { get; set; } = new();
}

public class TemporalExYaml
{
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public TimeSpan Duration { get; set; }
}

public class YamlDecimalModel
{
    public decimal Price { get; set; }
}

public class YamlReadOnlyListModel
{
    public IReadOnlyList<int> Scores { get; set; } = new List<int>();
}

// ── CS8604 repro: nullable string array with null elements ──

public class YamlNullableStringArrayModel
{
    public string Name { get; set; } = "";
    public string?[] Items { get; set; } = [];
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
            Tags = new List<string> { "dev", "runner" },
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
            Enabled = true,
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
            Enabled = null,
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

    [Test]
    public async Task RoundTrip_NestedObject()
    {
        var original = new YamlNestedModel
        {
            Name = "Home",
            Address = new YamlAddress { Street = "123 Main", City = "NYC" },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlNestedModel>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Home");
        await Assert.That(result.Address).IsNotNull();
        await Assert.That(result.Address.Street).IsEqualTo("123 Main");
        await Assert.That(result.Address.City).IsEqualTo("NYC");
    }

    [Test]
    public async Task RoundTrip_TemporalEx()
    {
        var original = new TemporalExYaml
        {
            Date = new DateOnly(2024, 6, 15),
            Time = new TimeOnly(12, 30, 0),
            Duration = TimeSpan.FromMinutes(90),
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<TemporalExYaml>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateOnly(2024, 6, 15));
        await Assert.That(result.Time).IsEqualTo(new TimeOnly(12, 30, 0));
        await Assert.That(result.Duration).IsEqualTo(TimeSpan.FromMinutes(90));
    }

    [Test]
    public async Task RoundTrip_Decimal()
    {
        var original = new YamlDecimalModel { Price = 149.99m };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlDecimalModel>(bytes);
        await Assert.That(result!.Price).IsEqualTo(149.99m);
    }

    [Test]
    public async Task RoundTrip_ReadOnlyList()
    {
        var original = new YamlReadOnlyListModel
        {
            Scores = new List<int> { 10, 20, 30 },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlReadOnlyListModel>(bytes);
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores.Count).IsEqualTo(3);
        await Assert.That(result.Scores[0]).IsEqualTo(10);
    }

    [Test]
    public async Task RoundTrip_Dict()
    {
        var original = new YamlDictModel
        {
            Name = "D",
            Scores = new Dictionary<string, int> { ["alice"] = 10, ["bob"] = 20 },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlDictModel>(bytes);
        await Assert.That(result!.Name).IsEqualTo("D");
        await Assert.That(result.Scores.Count).IsEqualTo(2);
        await Assert.That(result.Scores["alice"]).IsEqualTo(10);
        await Assert.That(result.Scores["bob"]).IsEqualTo(20);
    }

    [Test]
    public async Task Serialize_ToBufferWriter_ProducesValidOutput()
    {
        var model = new YamlSerModel { Name = "Test", Age = 42 };
        var bw = new ArrayBufferWriter<byte>();
        YamlSerializer.Serialize(bw, model);
        var text = Encoding.UTF8.GetString(bw.WrittenSpan);

        await Assert.That(text).Contains("Name");
        await Assert.That(text).Contains("Test");
    }

    [Test]
    public async Task RoundTrip_NestedObjectWithList()
    {
        var original = new YamlOuterWithNestedList
        {
            Name = "Parent",
            Child = new YamlNestedWithList
            {
                Street = "Main St",
                Tags = new List<string> { "a", "b", "c" },
            },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlOuterWithNestedList>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Parent");
        await Assert.That(result.Child.Street).IsEqualTo("Main St");
        await Assert.That(result.Child.Tags).IsNotNull();
        await Assert.That(result.Child.Tags.Count).IsEqualTo(3);
        await Assert.That(result.Child.Tags[0]).IsEqualTo("a");
        await Assert.That(result.Child.Tags[1]).IsEqualTo("b");
        await Assert.That(result.Child.Tags[2]).IsEqualTo("c");
    }

    [Test]
    public async Task RoundTrip_NestedObjectWithDict()
    {
        var original = new YamlOuterWithNestedDict
        {
            Name = "Parent",
            Child = new YamlNestedWithDict
            {
                Label = "Stats",
                Counts = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 },
            },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlOuterWithNestedDict>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Parent");
        await Assert.That(result.Child.Label).IsEqualTo("Stats");
        await Assert.That(result.Child.Counts).IsNotNull();
        await Assert.That(result.Child.Counts.Count).IsEqualTo(2);
        await Assert.That(result.Child.Counts["x"]).IsEqualTo(1);
        await Assert.That(result.Child.Counts["y"]).IsEqualTo(2);
    }

    // ── CS8604: nullable string array serialization must not throw ──

    [Test]
    public async Task NullableStringArray_Serialize_HandlesNullElements()
    {
        var model = new YamlNullableStringArrayModel
        {
            Name = "test",
            Items = ["hello", null, "world"],
        };

        // Must not throw ArgumentNullException from Encoding.UTF8.GetBytes(null)
        var yaml = YamlSerializer.Serialize(model);
        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml).Contains("hello");
        await Assert.That(yaml).Contains("world");
    }

    [Test]
    public async Task NullableStringArray_RoundTrip_SkipsNulls()
    {
        // YAML reader/writer don't natively support null in sequences.
        // Null elements are skipped during serialization.
        var model = new YamlNullableStringArrayModel { Name = "test", Items = ["a", null, "b"] };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(model);
        var result = YamlSerializer.Deserialize<YamlNullableStringArrayModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("test");
        await Assert.That(result.Items).IsNotNull();
        await Assert.That(result.Items.Length).IsEqualTo(2);
        await Assert.That(result.Items[0]).IsEqualTo("a");
        await Assert.That(result.Items[1]).IsEqualTo("b");
    }
}
