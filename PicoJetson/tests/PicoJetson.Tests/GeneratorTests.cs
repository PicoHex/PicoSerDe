namespace PicoJetson.Tests;

public class PersonWithDate
{
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class PrimitiveModel
{
    public Guid Id { get; set; }
    public decimal Price { get; set; }
    public DayOfWeek Day { get; set; }
}

public class NullableModel
{
    public int? OptionalAge { get; set; }
    public string? Nickname { get; set; }
}

public class ListModel
{
    public List<string> Tags { get; set; } = new();
    public int[] Scores { get; set; } = [];
}

public class DictionaryModel
{
    public Dictionary<string, int> Counts { get; set; } = new();
}

public class TemporalModel
{
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public TimeSpan Duration { get; set; }
}

public class NestedAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
}

public class NestedCustomer
{
    public string Name { get; set; } = "";
    public NestedAddress? Address { get; set; }
}

public class NestedOrder
{
    public int Id { get; set; }
    public NestedCustomer? Customer { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class CaseModel
{
    public string Title { get; set; } = "";
    public double Price { get; set; }
}

// Shared nested type — used by multiple parent types (tests M×N dedup)
public class SharedAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
}

public class CompanyA
{
    public string Name { get; set; } = "";
    public SharedAddress? Headquarters { get; set; }
}

public class CompanyB
{
    public string Name { get; set; } = "";
    public SharedAddress? Branch { get; set; }
}

// Cross-namespace collision test (see CollisionNs1.cs / CollisionNs2.cs)
public class CollisionModel
{
    public string Name { get; set; } = "";
    public CollisionNs1.CollisionAddress? Home { get; set; }
    public CollisionNs2.CollisionAddress? Work { get; set; }
}

public class GeneratorTests
{
    public class Product
    {
        public string Title { get; set; } = "";
        public double Price { get; set; }
    }

    // Manual replica of what SG generates for Product
    private readonly struct ProductJsonSerializer : ISerializer<Product>
    {
        public void Serialize(IBufferWriter<byte> writer, Product value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Title"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Title));
            jw.WritePropertyName("Price"u8);
            jw.WriteNumber(value.Price);
            jw.WriteEndObject();
        }
    }

    private readonly struct ProductJsonDeserializer : IDeserializer<Product>
    {
        public Product Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new Product();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("Title"u8))
                    obj.Title = Encoding.UTF8.GetString(reader.GetStringRaw());
                else if (p.SequenceEqual("Price"u8))
                {
                    reader.TryGetFloat64(out var v);
                    obj.Price = v;
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task GeneratedSerializer_RoundTrip()
    {
        var p = new Product { Title = "Widget", Price = 9.99 };
        var s = new ProductJsonSerializer();
        var d = new ProductJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, p);
        var r = d.Deserialize(buf.WrittenSpan);
        await Assert.That(r.Title).IsEqualTo("Widget");
        await Assert.That(r.Price).IsEqualTo(9.99);
    }

    // DateTime model classes
    public class DateTimeModel
    {
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    // Manual replicas of what SG should generate for DateTimeModel
    private readonly struct DateTimeModelJsonSerializer : ISerializer<DateTimeModel>
    {
        public void Serialize(IBufferWriter<byte> writer, DateTimeModel value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Name"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Name));
            jw.WritePropertyName("CreatedAt"u8);
            var isoStr = value.CreatedAt.ToString("O");
            jw.WriteString(Encoding.UTF8.GetBytes(isoStr));
            jw.WriteEndObject();
        }
    }

    private readonly struct DateTimeModelJsonDeserializer : IDeserializer<DateTimeModel>
    {
        public DateTimeModel Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new DateTimeModel();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("Name"u8))
                    obj.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
                else if (p.SequenceEqual("CreatedAt"u8))
                {
                    var raw = reader.GetStringRaw();
                    var str = Encoding.UTF8.GetString(raw);
                    DateTime.TryParse(
                        str,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var dt
                    );
                    obj.CreatedAt = dt;
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task DateTime_RoundTrip_ISO8601()
    {
        var model = new DateTimeModel
        {
            Name = "Event",
            CreatedAt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
        };
        var s = new DateTimeModelJsonSerializer();
        var d = new DateTimeModelJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, model);
        var json = Encoding.UTF8.GetString(buf.WrittenSpan);
        // Verify ISO 8601 format
        await Assert.That(json).Contains("2024-06-15T10:30:00");
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Name).IsEqualTo("Event");
        await Assert.That(result.CreatedAt.Year).IsEqualTo(2024);
        await Assert.That(result.CreatedAt.Hour).IsEqualTo(10);
    }

    [Test]
    public async Task GeneratedSerializer_DateTime_RoundTrip()
    {
        var person = new PersonWithDate
        {
            Name = "Event",
            CreatedAt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(person);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("2024-06-15T10:30:00");
        var result = JsonSerializer.Deserialize<PersonWithDate>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Event");
        await Assert.That(result.CreatedAt.Year).IsEqualTo(2024);
        await Assert.That(result.CreatedAt.Hour).IsEqualTo(10);
    }

    // ---------- PrimitiveModel manual structs (golden reference) ----------

    private readonly struct PrimitiveModelJsonSerializer : ISerializer<PrimitiveModel>
    {
        public void Serialize(IBufferWriter<byte> writer, PrimitiveModel value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Id"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Id.ToString()));
            jw.WritePropertyName("Price"u8);
            jw.WriteString(
                Encoding.UTF8.GetBytes(
                    value.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
            );
            jw.WritePropertyName("Day"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Day.ToString()));
            jw.WriteEndObject();
        }
    }

    private readonly struct PrimitiveModelJsonDeserializer : IDeserializer<PrimitiveModel>
    {
        public PrimitiveModel Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new PrimitiveModel();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("Id"u8))
                {
                    var raw = reader.GetStringRaw();
                    Guid.TryParse(raw, out var g);
                    obj.Id = g;
                }
                else if (p.SequenceEqual("Price"u8))
                {
                    var raw = reader.GetStringRaw();
                    decimal.TryParse(
                        raw,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var d
                    );
                    obj.Price = d;
                }
                else if (p.SequenceEqual("Day"u8))
                {
                    var raw = reader.GetStringRaw();
                    Enum.TryParse<DayOfWeek>(Encoding.UTF8.GetString(raw), out var day);
                    obj.Day = day;
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task GuidDecimalEnum_RoundTrip_Manual()
    {
        var model = new PrimitiveModel
        {
            Id = Guid.NewGuid(),
            Price = 99.99m,
            Day = DayOfWeek.Friday,
        };
        var s = new PrimitiveModelJsonSerializer();
        var d = new PrimitiveModelJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, model);
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Id).IsEqualTo(model.Id);
        await Assert.That(result.Price).IsEqualTo(99.99m);
        await Assert.That(result.Day).IsEqualTo(DayOfWeek.Friday);
    }

    [Test]
    public async Task GuidDecimalEnum_RoundTrip_Generated()
    {
        var model = new PrimitiveModel
        {
            Id = Guid.NewGuid(),
            Price = 99.99m,
            Day = DayOfWeek.Friday,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<PrimitiveModel>(bytes);
        await Assert.That(result!.Id).IsEqualTo(model.Id);
        await Assert.That(result.Price).IsEqualTo(99.99m);
        await Assert.That(result.Day).IsEqualTo(DayOfWeek.Friday);
    }

    // ---------- NullableModel manual structs ----------

    private readonly struct NullableModelJsonSerializer : ISerializer<NullableModel>
    {
        public void Serialize(IBufferWriter<byte> writer, NullableModel value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("OptionalAge"u8);
            if (value.OptionalAge.HasValue)
                jw.WriteNumber(value.OptionalAge.Value);
            else
                jw.WriteNull();
            jw.WritePropertyName("Nickname"u8);
            if (value.Nickname != null)
                jw.WriteString(Encoding.UTF8.GetBytes(value.Nickname));
            else
                jw.WriteNull();
            jw.WriteEndObject();
        }
    }

    private readonly struct NullableModelJsonDeserializer : IDeserializer<NullableModel>
    {
        public NullableModel Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new NullableModel();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("OptionalAge"u8))
                {
                    if (reader.TokenType == TokenType.Null)
                        obj.OptionalAge = null;
                    else
                    {
                        reader.TryGetInt32(out var v);
                        obj.OptionalAge = v;
                    }
                }
                else if (p.SequenceEqual("Nickname"u8))
                {
                    if (reader.TokenType == TokenType.Null)
                        obj.Nickname = null;
                    else
                        obj.Nickname = Encoding.UTF8.GetString(reader.GetStringRaw());
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task Nullable_RoundTrip_WithValues_Manual()
    {
        var model = new NullableModel { OptionalAge = 42, Nickname = "Test" };
        var s = new NullableModelJsonSerializer();
        var d = new NullableModelJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, model);
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.OptionalAge).IsEqualTo(42);
        await Assert.That(result.Nickname).IsEqualTo("Test");
    }

    [Test]
    public async Task Nullable_RoundTrip_WithNulls_Manual()
    {
        var model = new NullableModel { OptionalAge = null, Nickname = null };
        var s = new NullableModelJsonSerializer();
        var d = new NullableModelJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, model);
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.OptionalAge).IsNull();
        await Assert.That(result.Nickname).IsNull();
    }

    [Test]
    public async Task Nullable_RoundTrip_Generated()
    {
        var model = new NullableModel { OptionalAge = 42, Nickname = "Test" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NullableModel>(bytes);
        await Assert.That(result!.OptionalAge).IsEqualTo(42);
        await Assert.That(result.Nickname).IsEqualTo("Test");
    }

    [Test]
    public async Task Nullable_StringNull_RoundTrip()
    {
        // string? = null should serialize as JSON null and deserialize back to null
        var model = new NullableModel { OptionalAge = null, Nickname = null };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NullableModel>(bytes);
        await Assert.That(result!.OptionalAge).IsNull();
        await Assert.That(result.Nickname).IsNull();
    }

    // ---------- ListModel manual structs ----------

    private readonly struct ListModelJsonSerializer : ISerializer<ListModel>
    {
        public void Serialize(IBufferWriter<byte> writer, ListModel value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Tags"u8);
            jw.WriteStartArray();
            foreach (var tag in value.Tags)
                jw.WriteString(Encoding.UTF8.GetBytes(tag));
            jw.WriteEndArray();
            jw.WritePropertyName("Scores"u8);
            jw.WriteStartArray();
            foreach (var score in value.Scores)
                jw.WriteNumber(score);
            jw.WriteEndArray();
            jw.WriteEndObject();
        }
    }

    private readonly struct ListModelJsonDeserializer : IDeserializer<ListModel>
    {
        public ListModel Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new ListModel();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("Tags"u8))
                {
                    var tags = new List<string>();
                    if (reader.TokenType == TokenType.ArrayStart)
                    {
                        while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                            tags.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
                    }
                    obj.Tags = tags;
                }
                else if (p.SequenceEqual("Scores"u8))
                {
                    var scores = new List<int>();
                    if (reader.TokenType == TokenType.ArrayStart)
                    {
                        while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                        {
                            reader.TryGetInt32(out var v);
                            scores.Add(v);
                        }
                    }
                    obj.Scores = scores.ToArray();
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task ListModel_RoundTrip_Manual()
    {
        var model = new ListModel { Tags = ["dev", "runner"], Scores = [10, 20, 30] };
        var s = new ListModelJsonSerializer();
        var d = new ListModelJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, model);
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Tags).IsEquivalentTo(["dev", "runner"]);
        await Assert.That(result.Scores).IsEquivalentTo([10, 20, 30]);
    }

    [Test]
    public async Task ListModel_RoundTrip_Generated()
    {
        var model = new ListModel { Tags = ["dev", "runner"], Scores = [10, 20, 30] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<ListModel>(bytes);
        await Assert.That(result!.Tags).IsEquivalentTo(["dev", "runner"]);
        await Assert.That(result.Scores).IsEquivalentTo([10, 20, 30]);
    }

    // ---------- DictionaryModel manual structs ----------

    private readonly struct DictionaryModelJsonSerializer : ISerializer<DictionaryModel>
    {
        public void Serialize(IBufferWriter<byte> writer, DictionaryModel value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Counts"u8);
            jw.WriteStartObject();
            foreach (var kvp in value.Counts)
            {
                jw.WritePropertyName(Encoding.UTF8.GetBytes(kvp.Key));
                jw.WriteNumber(kvp.Value);
            }
            jw.WriteEndObject();
            jw.WriteEndObject();
        }
    }

    private readonly struct DictionaryModelJsonDeserializer : IDeserializer<DictionaryModel>
    {
        public DictionaryModel Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new DictionaryModel();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("Counts"u8))
                {
                    var dict = new Dictionary<string, int>();
                    if (reader.TokenType == TokenType.ObjectStart)
                    {
                        while (reader.Read() && reader.TokenType == TokenType.PropertyName)
                        {
                            var key = Encoding.UTF8.GetString(reader.GetStringRaw());
                            reader.Read();
                            reader.TryGetInt32(out var v);
                            dict[key] = v;
                        }
                    }
                    obj.Counts = dict;
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task Dictionary_RoundTrip_Manual()
    {
        var model = new DictionaryModel
        {
            Counts = new() { ["a"] = 1, ["b"] = 2 },
        };
        var s = new DictionaryModelJsonSerializer();
        var d = new DictionaryModelJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, model);
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Counts["a"]).IsEqualTo(1);
        await Assert.That(result.Counts["b"]).IsEqualTo(2);
    }

    [Test]
    public async Task Dictionary_RoundTrip_Generated()
    {
        var model = new DictionaryModel
        {
            Counts = new() { ["a"] = 1, ["b"] = 2 },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<DictionaryModel>(bytes);
        await Assert.That(result!.Counts["a"]).IsEqualTo(1);
        await Assert.That(result.Counts["b"]).IsEqualTo(2);
    }

    // ---------- TemporalModel manual structs ----------

    private readonly struct TemporalModelJsonSerializer : ISerializer<TemporalModel>
    {
        public void Serialize(IBufferWriter<byte> writer, TemporalModel value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Date"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Date.ToString("O")));
            jw.WritePropertyName("Time"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Time.ToString("O")));
            jw.WritePropertyName("Duration"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Duration.ToString()));
            jw.WriteEndObject();
        }
    }

    private readonly struct TemporalModelJsonDeserializer : IDeserializer<TemporalModel>
    {
        public TemporalModel Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new TemporalModel();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("Date"u8))
                {
                    var raw = reader.GetStringRaw();
                    var s = Encoding.UTF8.GetString(raw);
                    DateOnly.TryParse(s, out var d);
                    obj.Date = d;
                }
                else if (p.SequenceEqual("Time"u8))
                {
                    var raw = reader.GetStringRaw();
                    var s = Encoding.UTF8.GetString(raw);
                    TimeOnly.TryParse(s, out var t);
                    obj.Time = t;
                }
                else if (p.SequenceEqual("Duration"u8))
                {
                    var raw = reader.GetStringRaw();
                    var s = Encoding.UTF8.GetString(raw);
                    TimeSpan.TryParse(s, out var ts);
                    obj.Duration = ts;
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task Temporal_RoundTrip_Manual()
    {
        var model = new TemporalModel
        {
            Date = new DateOnly(2024, 6, 15),
            Time = new TimeOnly(10, 30, 0),
            Duration = TimeSpan.FromHours(1.5),
        };
        var s = new TemporalModelJsonSerializer();
        var d = new TemporalModelJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        s.Serialize(buf, model);
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Date).IsEqualTo(new DateOnly(2024, 6, 15));
        await Assert.That(result.Time).IsEqualTo(new TimeOnly(10, 30, 0));
        await Assert.That(result.Duration).IsEqualTo(TimeSpan.FromHours(1.5));
    }

    [Test]
    public async Task Temporal_RoundTrip_Generated()
    {
        var model = new TemporalModel
        {
            Date = new DateOnly(2024, 6, 15),
            Time = new TimeOnly(10, 30, 0),
            Duration = TimeSpan.FromHours(1.5),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<TemporalModel>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateOnly(2024, 6, 15));
        await Assert.That(result.Time).IsEqualTo(new TimeOnly(10, 30, 0));
        await Assert.That(result.Duration).IsEqualTo(TimeSpan.FromHours(1.5));
    }

    [Test]
    public async Task Generator_Assembly_Exists()
    {
        // Verify the SG assembly compiled by checking the type exists in loaded assemblies
        var type = typeof(PicoJetson.JsonSerializer);
        await Assert.That(type.Assembly.GetName().Name).IsEqualTo("PicoJetson");
    }

    // === Nested Types Test ===

    // Manual structs simulating what SG should generate for NestedOrder
    private readonly struct NestedOrderJsonSerializer : ISerializer<NestedOrder>
    {
        public void Serialize(IBufferWriter<byte> writer, NestedOrder value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Id"u8);
            jw.WriteNumber(value.Id);
            jw.WritePropertyName("Customer"u8);
            if (value.Customer == null)
            {
                jw.WriteNull();
            }
            else
            {
                jw.WriteStartObject();
                jw.WritePropertyName("Name"u8);
                jw.WriteString(Encoding.UTF8.GetBytes(value.Customer.Name));
                jw.WritePropertyName("Address"u8);
                if (value.Customer.Address == null)
                {
                    jw.WriteNull();
                }
                else
                {
                    jw.WriteStartObject();
                    jw.WritePropertyName("Street"u8);
                    jw.WriteString(Encoding.UTF8.GetBytes(value.Customer.Address.Street));
                    jw.WritePropertyName("City"u8);
                    jw.WriteString(Encoding.UTF8.GetBytes(value.Customer.Address.City));
                    jw.WriteEndObject();
                }
                jw.WriteEndObject();
            }
            jw.WritePropertyName("Tags"u8);
            jw.WriteStartArray();
            foreach (var t in value.Tags)
                jw.WriteString(Encoding.UTF8.GetBytes(t));
            jw.WriteEndArray();
            jw.WriteEndObject();
        }
    }

    private readonly struct NestedOrderJsonDeserializer : IDeserializer<NestedOrder>
    {
        public NestedOrder Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new NestedOrder();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var p = reader.GetStringRaw();
                reader.Read();
                if (p.SequenceEqual("Id"u8))
                {
                    reader.TryGetInt32(out var v);
                    obj.Id = v;
                }
                else if (p.SequenceEqual("Customer"u8))
                {
                    if (reader.TokenType == TokenType.Null)
                    {
                        obj.Customer = null;
                    }
                    else
                    {
                        var cust = new NestedCustomer();
                        while (reader.Read() && reader.TokenType == TokenType.PropertyName)
                        {
                            var cp = reader.GetStringRaw();
                            reader.Read();
                            if (cp.SequenceEqual("Name"u8))
                                cust.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
                            else if (cp.SequenceEqual("Address"u8))
                            {
                                if (reader.TokenType == TokenType.Null)
                                {
                                    cust.Address = null;
                                }
                                else
                                {
                                    var addr = new NestedAddress();
                                    while (
                                        reader.Read() && reader.TokenType == TokenType.PropertyName
                                    )
                                    {
                                        var ap = reader.GetStringRaw();
                                        reader.Read();
                                        if (ap.SequenceEqual("Street"u8))
                                            addr.Street = Encoding.UTF8.GetString(
                                                reader.GetStringRaw()
                                            );
                                        else if (ap.SequenceEqual("City"u8))
                                            addr.City = Encoding.UTF8.GetString(
                                                reader.GetStringRaw()
                                            );
                                        else
                                            reader.TrySkip();
                                    }
                                    cust.Address = addr;
                                }
                            }
                            else
                                reader.TrySkip();
                        }
                        obj.Customer = cust;
                    }
                }
                else if (p.SequenceEqual("Tags"u8))
                {
                    if (reader.TokenType == TokenType.ArrayStart)
                    {
                        var tags = new List<string>();
                        while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                            tags.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
                        obj.Tags = tags;
                    }
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task NestedObject_RoundTrip_ThreeLevels_Manual()
    {
        var order = new NestedOrder
        {
            Id = 1,
            Customer = new NestedCustomer
            {
                Name = "Alice",
                Address = new NestedAddress { Street = "123 Main", City = "SF" },
            },
            Tags = ["vip"],
        };
        var s = new NestedOrderJsonSerializer();
        var d = new NestedOrderJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(512);
        s.Serialize(buf, order);
        var json = Encoding.UTF8.GetString(buf.WrittenSpan);
        // Verify nested structure exists in JSON
        await Assert.That(json).Contains("\"Customer\"");
        await Assert.That(json).Contains("\"Address\"");
        await Assert.That(json).Contains("\"Street\"");
        var result = d.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Id).IsEqualTo(1);
        await Assert.That(result.Customer?.Name).IsEqualTo("Alice");
        await Assert.That(result.Customer?.Address?.Street).IsEqualTo("123 Main");
        await Assert.That(result.Customer?.Address?.City).IsEqualTo("SF");
        await Assert.That(result.Tags[0]).IsEqualTo("vip");
    }

    // SG integration test — MUST FAIL before SG is updated
    [Test]
    public async Task NestedObject_RoundTrip_Generated()
    {
        var order = new NestedOrder
        {
            Id = 1,
            Customer = new NestedCustomer
            {
                Name = "Alice",
                Address = new NestedAddress { Street = "123 Main", City = "SF" },
            },
            Tags = ["vip"],
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(order);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"Customer\"");
        await Assert.That(json).Contains("\"Address\"");
        var result = JsonSerializer.Deserialize<NestedOrder>(bytes);
        await Assert.That(result!.Id).IsEqualTo(1);
        await Assert.That(result.Customer?.Name).IsEqualTo("Alice");
        await Assert.That(result.Customer?.Address?.Street).IsEqualTo("123 Main");
        await Assert.That(result.Tags[0]).IsEqualTo("vip");
    }

    // === P1-5: Case-Insensitive Deserialization ===

    [Test]
    public async Task CaseInsensitive_Deserialization_Generated()
    {
        var json = "{\"title\":\"Widget\",\"PRICE\":30.5}"u8;
        var result = JsonSerializer.Deserialize<CaseModel>(json);
        await Assert.That(result!.Title).IsEqualTo("Widget");
        await Assert.That(result.Price).IsEqualTo(30.5);
    }

    // === Shared nested type (M×N dedup) ===

    [Test]
    public async Task SharedNestedType_CompanyA_RoundTrip()
    {
        var company = new CompanyA
        {
            Name = "Acme",
            Headquarters = new SharedAddress { Street = "1 Main St", City = "NYC" },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(company);
        var result = JsonSerializer.Deserialize<CompanyA>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Acme");
        await Assert.That(result.Headquarters!.Street).IsEqualTo("1 Main St");
        await Assert.That(result.Headquarters.City).IsEqualTo("NYC");
    }

    [Test]
    public async Task SharedNestedType_CompanyB_RoundTrip()
    {
        var company = new CompanyB
        {
            Name = "Globex",
            Branch = new SharedAddress { Street = "2 Oak Ave", City = "LA" },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(company);
        var result = JsonSerializer.Deserialize<CompanyB>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Globex");
        await Assert.That(result.Branch!.Street).IsEqualTo("2 Oak Ave");
        await Assert.That(result.Branch.City).IsEqualTo("LA");
    }

    [Test]
    public async Task CrossNamespace_CollisionAddress_RoundTrip()
    {
        var model = new CollisionModel
        {
            Name = "test",
            Home = new CollisionNs1.CollisionAddress { Line = "123 Main" },
            Work = new CollisionNs2.CollisionAddress { Code = "90210" },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<CollisionModel>(bytes);
        await Assert.That(result!.Name).IsEqualTo("test");
        await Assert.That(result.Home!.Line).IsEqualTo("123 Main");
        await Assert.That(result.Work!.Code).IsEqualTo("90210");
    }
}
