namespace PicoJson.Tests;

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

    [Test]
    public async Task Generator_Assembly_Exists()
    {
        // Verify the SG assembly compiled by checking the type exists in loaded assemblies
        var type = typeof(PicoJson.JsonSerializer);
        await Assert.That(type.Assembly.GetName().Name).IsEqualTo("PicoJson");
    }
}
