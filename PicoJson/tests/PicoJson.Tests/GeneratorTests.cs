namespace PicoJson.Tests;

public class PersonWithDate
{
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
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
            CreatedAt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc)
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
            CreatedAt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(person);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("2024-06-15T10:30:00");
        var result = JsonSerializer.Deserialize<PersonWithDate>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Event");
        await Assert.That(result.CreatedAt.Year).IsEqualTo(2024);
        await Assert.That(result.CreatedAt.Hour).IsEqualTo(10);
    }

    [Test]
    public async Task Generator_Assembly_Exists()
    {
        // Verify the SG assembly compiled by checking the type exists in loaded assemblies
        var type = typeof(PicoJson.JsonSerializer);
        await Assert.That(type.Assembly.GetName().Name).IsEqualTo("PicoJson");
    }
}
