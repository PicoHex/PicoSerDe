namespace PicoMsgPack.Tests;

// Test models with integer keys
public class PersonMsgPack
{
    [MsgPackKey(0)]
    public string Name { get; set; } = "";

    [MsgPackKey(1)]
    public int Age { get; set; }
}

public class BookMsgPack
{
    [MsgPackKey(0)]
    public string Title { get; set; } = "";

    [MsgPackKey(1)]
    public int Pages { get; set; }

    [MsgPackKey(2)]
    public List<string> Tags { get; set; } = new();
}

// Nested object models
public class Address
{
    [MsgPackKey(0)]
    public string Street { get; set; } = "";

    [MsgPackKey(1)]
    public string City { get; set; } = "";
}

public class PersonWithAddress
{
    [MsgPackKey(0)]
    public string Name { get; set; } = "";

    [MsgPackKey(1)]
    public Address? Home { get; set; }
}

public class NullableModel
{
    [MsgPackKey(0)]
    public int? OptionalAge { get; set; }

    [MsgPackKey(1)]
    public string? Nickname { get; set; }
}

public class TemporalModel
{
    [MsgPackKey(0)]
    public DateTime CreatedAt { get; set; }

    [MsgPackKey(1)]
    public TimeSpan Duration { get; set; }
}

public class DateOnlyTimeOnlyModel
{
    [MsgPackKey(0)]
    public DateOnly Date { get; set; }

    [MsgPackKey(1)]
    public TimeOnly Time { get; set; }

    [MsgPackKey(2)]
    public TimeSpan Span { get; set; }
}

public class EnumModel
{
    [MsgPackKey(0)]
    public DayOfWeek Day { get; set; }

    [MsgPackKey(1)]
    public Guid Id { get; set; }
}

public class DictModel
{
    [MsgPackKey(0)]
    public Dictionary<string, int> Counts { get; set; } = new();
}

public class IntArrayModel
{
    [MsgPackKey(0)]
    public int[] Scores { get; set; } = [];
}

public class BytesModel
{
    [MsgPackKey(0)]
    public byte[] Data { get; set; } = [];
}

public class MsgPackSerializerGeneratorTests
{
    [Test]
    public async Task Generated_Person_RoundTrip()
    {
        var person = new PersonMsgPack { Name = "Alice", Age = 30 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
        var result = MsgPackSerializer.Deserialize<PersonMsgPack>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }

    [Test]
    public async Task Generated_Book_RoundTrip()
    {
        var book = new BookMsgPack
        {
            Title = "Dune",
            Pages = 412,
            Tags = ["sci-fi", "classic"],
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(book);
        var result = MsgPackSerializer.Deserialize<BookMsgPack>(bytes);
        await Assert.That(result!.Title).IsEqualTo("Dune");
        await Assert.That(result.Pages).IsEqualTo(412);
        await Assert.That(result.Tags).HasCount(2);
    }

    [Test]
    public async Task Generated_Person_ProducesValidMsgPack()
    {
        var person = new PersonMsgPack { Name = "Bob", Age = 25 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);

        // Verify structure manually
        TokenType t1;
        int k0,
            k1,
            age;
        string v1;
        using (var reader = new MsgPackReader(bytes))
        {
            reader.Read();
            t1 = reader.TokenType;
            reader.Read();
            reader.TryGetInt32(out k0);
            reader.Read();
            v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            reader.TryGetInt32(out k1);
            reader.Read();
            reader.TryGetInt32(out age);
        }
        await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(k0).IsEqualTo(0);
        await Assert.That(v1).IsEqualTo("Bob");
        await Assert.That(k1).IsEqualTo(1);
        await Assert.That(age).IsEqualTo(25);
    }

    // ── Nested object ──

    // ── Direct nested type test ──

    [Test]
    public async Task Generated_Address_Direct_RoundTrip()
    {
        var addr = new Address { Street = "1 Main", City = "NYC" };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(addr);
        var result = MsgPackSerializer.Deserialize<Address>(bytes);
        await Assert.That(result!.Street).IsEqualTo("1 Main");
        await Assert.That(result!.City).IsEqualTo("NYC");
    }

    [Test]
    public async Task Generated_Nested_RoundTrip()
    {
        var person = new PersonWithAddress
        {
            Name = "Alice",
            Home = new Address { Street = "1 Main", City = "NYC" },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
        var result = MsgPackSerializer.Deserialize<PersonWithAddress>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Home!.Street).IsEqualTo("1 Main");
        await Assert.That(result.Home.City).IsEqualTo("NYC");
    }

    [Test]
    public async Task Generated_NestedNull_RoundTrip()
    {
        var person = new PersonWithAddress { Name = "Bob", Home = null };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(person);
        var result = MsgPackSerializer.Deserialize<PersonWithAddress>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Bob");
        await Assert.That(result.Home).IsNull();
    }

    // ── Nullable ──

    [Test]
    public async Task Generated_Nullable_RoundTrip()
    {
        var model = new NullableModel { OptionalAge = 42, Nickname = "Test" };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<NullableModel>(bytes);
        await Assert.That(result!.OptionalAge).IsEqualTo(42);
        await Assert.That(result.Nickname).IsEqualTo("Test");
    }

    [Test]
    public async Task Generated_NullableNullValues_RoundTrip()
    {
        var model = new NullableModel { OptionalAge = null, Nickname = null };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<NullableModel>(bytes);
        await Assert.That(result!.OptionalAge).IsNull();
        await Assert.That(result.Nickname).IsNull();
    }

    // ── DateTime / TimeSpan ──

    [Test]
    public async Task Generated_DateTime_RoundTrip()
    {
        var model = new TemporalModel
        {
            CreatedAt = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromSeconds(90),
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<TemporalModel>(bytes);
        await Assert.That(result!.CreatedAt).IsEqualTo(model.CreatedAt);
        await Assert.That(result.Duration).IsEqualTo(model.Duration);
    }

    // ── Enum / Guid ──

    [Test]
    public async Task Generated_DateOnlyTimeOnly_RoundTrip()
    {
        var model = new DateOnlyTimeOnlyModel
        {
            Date = new DateOnly(2024, 6, 15),
            Time = new TimeOnly(12, 30, 0),
            Span = TimeSpan.FromMinutes(90),
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<DateOnlyTimeOnlyModel>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateOnly(2024, 6, 15));
        await Assert.That(result.Time).IsEqualTo(new TimeOnly(12, 30, 0));
        await Assert.That(result.Span).IsEqualTo(TimeSpan.FromMinutes(90));
    }

    [Test]
    public async Task Generated_EnumGuid_RoundTrip()
    {
        var model = new EnumModel { Day = DayOfWeek.Friday, Id = Guid.NewGuid() };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<EnumModel>(bytes);
        await Assert.That(result!.Day).IsEqualTo(DayOfWeek.Friday);
        await Assert.That(result.Id).IsEqualTo(model.Id);
    }

    // ── Dictionary ──

    [Test]
    public async Task Generated_Dict_RoundTrip()
    {
        var model = new DictModel();
        model.Counts["a"] = 1;
        model.Counts["b"] = 2;
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<DictModel>(bytes);
        await Assert.That(result!.Counts["a"]).IsEqualTo(1);
        await Assert.That(result.Counts["b"]).IsEqualTo(2);
    }

    // ── int[] ──

    [Test]
    public async Task Generated_IntArray_RoundTrip()
    {
        var model = new IntArrayModel { Scores = [10, 20, 30] };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<IntArrayModel>(bytes);
        await Assert.That(result!.Scores).IsEquivalentTo(new[] { 10, 20, 30 });
    }

    [Test]
    public async Task Generated_Bytes_RoundTrip()
    {
        var model = new BytesModel { Data = new byte[] { 1, 2, 3, 4 } };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<BytesModel>(bytes);
        await Assert.That(result!.Data).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    }

    [Test]
    public async Task StreamingDelegate_NotRegistered_Yet()
    {
        // SG streaming for MsgPack is a future enhancement.
        // The test documents the current state: delegate is not auto-registered.
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new PersonMsgPack { Name = "x", Age = 1 });
        var hasDelegate = MsgPackSerializer.HasStreamingDelegate<PersonMsgPack>();
        await Assert.That(hasDelegate).IsFalse();
    }

    [Test]
    public async Task DeserializeFromStreamAsync_Person_Works()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new PersonMsgPack { Name = "Alice", Age = 30 });
        using var stream = new MemoryStream(bytes);

        var result = await MsgPackSerializer.DeserializeFromStreamAsync<PersonMsgPack>(stream);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }
}
