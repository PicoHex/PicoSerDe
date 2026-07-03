namespace PicoToml.Tests;

public class SimplePoco
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class ListPoco
{
    public string Name { get; set; } = string.Empty;
    public List<int> Scores { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public class NullablePoco
{
    public string Name { get; set; } = string.Empty;
    public int? Age { get; set; }
    public double? Rating { get; set; }
    public bool? Enabled { get; set; }
}

public class TemporalPoco
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class TemporalExPoco
{
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public TimeSpan Duration { get; set; }
}

public class GuidPoco
{
    public Guid Id { get; set; }
}

public class Address
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
}

public class NestedPoco
{
    public string Name { get; set; } = string.Empty;
    public Address Address { get; set; } = new();
}

public class DictPoco
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, int> Scores { get; set; } = new();
}

public class DictStringLongPoco
{
    public Dictionary<string, long> Counts { get; set; } = new();
}

public class DictStringDoublePoco
{
    public Dictionary<string, double> Ratings { get; set; } = new();
}

public class DictStringBoolPoco
{
    public Dictionary<string, bool> Flags { get; set; } = new();
}

public class DictStringStringPoco
{
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class DecimalPoco
{
    public decimal Price { get; set; }
}

public class ManyScalarTomlPoco
{
    public string Alpha { get; set; } = string.Empty;
    public int Beta { get; set; }
    public bool Gamma { get; set; }
    public long Delta { get; set; }
    public double Epsilon { get; set; }
    public string Zeta { get; set; } = string.Empty;
}

public class ReadOnlyListPoco
{
    public IReadOnlyList<int> Scores { get; set; } = new List<int>();
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

    [Test]
    public async Task RoundTrip_ListPoco()
    {
        var original = new ListPoco
        {
            Name = "Test",
            Scores = new List<int> { 10, 20, 30 },
            Tags = new List<string> { "dev", "runner" },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<ListPoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Scores).IsNotNull();
        await Assert.That(result.Scores.Count).IsEqualTo(3);
        await Assert.That(result.Scores[0]).IsEqualTo(10);
        await Assert.That(result.Scores[1]).IsEqualTo(20);
        await Assert.That(result.Scores[2]).IsEqualTo(30);
        await Assert.That(result.Tags.Count).IsEqualTo(2);
        await Assert.That(result.Tags[0]).IsEqualTo("dev");
        await Assert.That(result.Tags[1]).IsEqualTo("runner");
    }

    [Test]
    public async Task RoundTrip_Nullable_HasValue()
    {
        var original = new NullablePoco
        {
            Name = "N",
            Age = 42,
            Rating = 4.5,
            Enabled = true,
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<NullablePoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("N");
        await Assert.That(result.Age).IsEqualTo(42);
        await Assert.That(result.Rating).IsEqualTo(4.5);
        await Assert.That(result.Enabled).IsTrue();
    }

    [Test]
    public async Task RoundTrip_Nullable_Null()
    {
        var original = new NullablePoco
        {
            Name = "N",
            Age = null,
            Rating = null,
            Enabled = null,
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<NullablePoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("N");
        await Assert.That(result.Age).IsNull();
        await Assert.That(result.Rating).IsNull();
        await Assert.That(result.Enabled).IsNull();
    }

    [Test]
    public async Task RoundTrip_DateTime()
    {
        var dt = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var original = new TemporalPoco { Name = "T", CreatedAt = dt };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<TemporalPoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("T");
        await Assert.That(result.CreatedAt).IsEqualTo(dt);
    }

    [Test]
    public async Task RoundTrip_Guid()
    {
        var g = Guid.NewGuid();
        var original = new GuidPoco { Id = g };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<GuidPoco>(bytes);
        await Assert.That(result!.Id).IsEqualTo(g);
    }

    [Test]
    public async Task RoundTrip_NestedObject()
    {
        var original = new NestedPoco
        {
            Name = "Home",
            Address = new Address { Street = "123 Main", City = "NYC" },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<NestedPoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Home");
        await Assert.That(result.Address).IsNotNull();
        await Assert.That(result.Address.Street).IsEqualTo("123 Main");
        await Assert.That(result.Address.City).IsEqualTo("NYC");
    }

    [Test]
    public async Task RoundTrip_Dictionary()
    {
        var original = new DictPoco
        {
            Name = "D",
            Scores = new Dictionary<string, int> { ["alice"] = 10, ["bob"] = 20 },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<DictPoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("D");
        await Assert.That(result.Scores.Count).IsEqualTo(2);
        await Assert.That(result.Scores["alice"]).IsEqualTo(10);
        await Assert.That(result.Scores["bob"]).IsEqualTo(20);
    }

    [Test]
    public async Task RoundTrip_TemporalEx()
    {
        var original = new TemporalExPoco
        {
            Date = new DateOnly(2024, 6, 15),
            Time = new TimeOnly(12, 30, 0),
            Duration = TimeSpan.FromMinutes(90),
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<TemporalExPoco>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateOnly(2024, 6, 15));
        await Assert.That(result.Time).IsEqualTo(new TimeOnly(12, 30, 0));
        await Assert.That(result.Duration).IsEqualTo(TimeSpan.FromMinutes(90));
    }

    [Test]
    public async Task RoundTrip_ManyScalarPoco_UsesGeneratedDispatch()
    {
        var original = new ManyScalarTomlPoco
        {
            Alpha = "a",
            Beta = 2,
            Gamma = true,
            Delta = 4,
            Epsilon = 5.5,
            Zeta = "z",
        };

        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<ManyScalarTomlPoco>(bytes);

        await Assert.That(result!.Alpha).IsEqualTo("a");
        await Assert.That(result.Beta).IsEqualTo(2);
        await Assert.That(result.Gamma).IsTrue();
        await Assert.That(result.Delta).IsEqualTo(4);
        await Assert.That(result.Epsilon).IsEqualTo(5.5);
        await Assert.That(result.Zeta).IsEqualTo("z");
    }

    [Test]
    public async Task RoundTrip_Decimal()
    {
        var original = new DecimalPoco { Price = 149.99m };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<DecimalPoco>(bytes);
        await Assert.That(result!.Price).IsEqualTo(149.99m);
    }

    [Test]
    public async Task RoundTrip_ReadOnlyList()
    {
        var original = new ReadOnlyListPoco
        {
            Scores = new List<int> { 10, 20, 30 },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<ReadOnlyListPoco>(bytes);
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores.Count).IsEqualTo(3);
        await Assert.That(result.Scores[0]).IsEqualTo(10);
    }

    [Test]
    public async Task RoundTrip_DictStringLong()
    {
        var original = new DictStringLongPoco
        {
            Counts = new Dictionary<string, long> { ["x"] = 100L, ["y"] = 200L },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<DictStringLongPoco>(bytes);
        await Assert.That(result!.Counts.Count).IsEqualTo(2);
        await Assert.That(result.Counts["x"]).IsEqualTo(100L);
        await Assert.That(result.Counts["y"]).IsEqualTo(200L);
    }

    [Test]
    public async Task RoundTrip_DictStringDouble()
    {
        var original = new DictStringDoublePoco
        {
            Ratings = new Dictionary<string, double> { ["a"] = 4.5, ["b"] = 3.2 },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<DictStringDoublePoco>(bytes);
        await Assert.That(result!.Ratings.Count).IsEqualTo(2);
        await Assert.That(result.Ratings["a"]).IsEqualTo(4.5);
        await Assert.That(result.Ratings["b"]).IsEqualTo(3.2);
    }

    [Test]
    public async Task RoundTrip_DictStringBool()
    {
        var original = new DictStringBoolPoco
        {
            Flags = new Dictionary<string, bool> { ["enabled"] = true, ["visible"] = false },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<DictStringBoolPoco>(bytes);
        await Assert.That(result!.Flags.Count).IsEqualTo(2);
        await Assert.That(result.Flags["enabled"]).IsTrue();
        await Assert.That(result.Flags["visible"]).IsFalse();
    }

    [Test]
    public async Task RoundTrip_DictStringString()
    {
        var original = new DictStringStringPoco
        {
            Metadata = new Dictionary<string, string> { ["env"] = "prod", ["ver"] = "1.0" },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<DictStringStringPoco>(bytes);
        await Assert.That(result!.Metadata.Count).IsEqualTo(2);
        await Assert.That(result.Metadata["env"]).IsEqualTo("prod");
        await Assert.That(result.Metadata["ver"]).IsEqualTo("1.0");
    }
}
