using System.Buffers;

namespace PicoJson.Tests;

public class JsonSerializerTests
{
    public class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    // Manual implementations until Source Generator is built
    private readonly struct PersonJsonSerializer : ISerializer<Person>
    {
        public void Serialize(IBufferWriter<byte> writer, Person value)
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Name"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Name));
            jw.WritePropertyName("Age"u8);
            jw.WriteNumber(value.Age);
            jw.WriteEndObject();
        }
    }

    private readonly struct PersonJsonDeserializer : IDeserializer<Person>
    {
        public Person Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            var obj = new Person();
            reader.Read();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var prop = reader.GetStringRaw();
                reader.Read();
                if (prop.SequenceEqual("Name"u8))
                    obj.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
                else if (prop.SequenceEqual("Age"u8))
                {
                    reader.TryGetInt32(out var age);
                    obj.Age = age;
                }
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task Manual_RoundTrip_Works()
    {
        var person = new Person { Name = "Alice", Age = 30 };
        var serializer = new PersonJsonSerializer();
        var deserializer = new PersonJsonDeserializer();
        var buf = new ArrayBufferWriter<byte>(256);
        serializer.Serialize(buf, person);
        var result = deserializer.Deserialize(buf.WrittenSpan);
        await Assert.That(result.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }

    [Test]
    public async Task SerializeToBytes_WithManualImpl_ProducesValidJson()
    {
        var person = new Person { Name = "Bob", Age = 25 };
        var serializer = new PersonJsonSerializer();
        var bytes = serializer.SerializeToBytes(person);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("\"Bob\"");
    }

    [Test]
    public async Task JsonSerializerClass_Exists()
    {
        var t = typeof(JsonSerializer);
        await Assert.That(t.IsAbstract).IsTrue();
        await Assert.That(t.IsSealed).IsTrue();
    }

    [Test]
    public async Task StaticApi_WithManualRegistration_RoundTrips()
    {
        // Simulate what the Source Generator produces
        JsonSerializer._serializers[typeof(Person)] = new PersonJsonSerializer();
        JsonSerializer._deserializers[typeof(Person)] = new PersonJsonDeserializer();

        var person = new Person { Name = "Charlie", Age = 35 };
        var buf = new ArrayBufferWriter<byte>(256);

        JsonSerializer.Serialize(buf, person);
        var result = JsonSerializer.Deserialize<Person>(buf.WrittenSpan);

        await Assert.That(result!.Name).IsEqualTo("Charlie");
        await Assert.That(result.Age).IsEqualTo(35);
    }

    [Test]
    public async Task StaticApi_SerializeToUtf8Bytes_ReturnsValidJson()
    {
        JsonSerializer._serializers[typeof(Person)] = new PersonJsonSerializer();
        JsonSerializer._deserializers[typeof(Person)] = new PersonJsonDeserializer();

        var person = new Person { Name = "Dave", Age = 40 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(person);
        var json = Encoding.UTF8.GetString(bytes);

        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("\"Dave\"");
        await Assert.That(json).Contains("\"Age\"");
        await Assert.That(json).Contains("40");
    }

    [Test]
    public async Task StaticApi_SerializeToString_ReturnsJsonString()
    {
        JsonSerializer._serializers[typeof(Person)] = new PersonJsonSerializer();
        JsonSerializer._deserializers[typeof(Person)] = new PersonJsonDeserializer();

        var person = new Person { Name = "Eve", Age = 28 };
        var json = JsonSerializer.Serialize(person);

        await Assert.That(json).Contains("\"Name\"");
        await Assert.That(json).Contains("\"Eve\"");
        await Assert.That(json).Contains("28");
    }

    private struct UnregisteredType { }

    [Test]
    public async Task StaticApi_NoRegistration_ThrowsInvalidOperation()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JsonSerializer.SerializeToUtf8Bytes(new UnregisteredType())
        );
        await Assert.That(ex.Message).Contains("UnregisteredType");
    }
}
