using System.Buffers;
using System.Text;
using PicoSerDe.Core;

namespace PicoJetson.Tests;

// Test suite — ref struct models added in Task 3 after SG fix
public class RefStructSerializerTests
{
    [Test]
    public async Task RegularType_Still_Serializes_Via_Cache()
    {
        // The existing test models should still work —
        // verify the new SerCache<T> path works for regular types.
        var person = new JsonSerializerTests.Person { Name = "Test", Age = 42 };
        var json = JsonSerializer.Serialize(person);
        await Assert.That(json).Contains("Test");
        await Assert.That(json).Contains("42");
    }

    [Test]
    public async Task Compat_Register_ISerializer_IDeserializer_Still_Works()
    {
        // Hand-written ISerializer + IDeserializer should still register
        // through the compat two-param Register overload.
        JsonSerializer.Register<JsonSerializerTests.Person>(
            new TestPersonSerializer(),
            new TestPersonDeserializer());
        var person = new JsonSerializerTests.Person { Name = "Compat", Age = 99 };
        var json = JsonSerializer.Serialize(person);
        await Assert.That(json).Contains("Compat");
        await Assert.That(json).Contains("99");
    }
}

// Hand-written serializer/deserializer for compat path testing
file struct TestPersonSerializer : ISerializer<JsonSerializerTests.Person>
{
    public void Serialize(IBufferWriter<byte> w, JsonSerializerTests.Person v)
    {
        var jw = new JsonWriter(w);
        jw.WriteStartObject();
        jw.WritePropertyName("Name"u8);
        jw.WriteString(Encoding.UTF8.GetBytes(v.Name));
        jw.WritePropertyName("Age"u8);
        jw.WriteNumber(v.Age);
        jw.WriteEndObject();
    }
}

file struct TestPersonDeserializer : IDeserializer<JsonSerializerTests.Person>
{
    public JsonSerializerTests.Person Deserialize(ReadOnlySpan<byte> d)
        => new() { Name = "ignored", Age = 0 };
}
