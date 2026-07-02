using System.Buffers;
using System.Text;
using PicoSerDe.Core;

namespace PicoJetson.Tests;

// Test suite — ref struct models added in Task 3 after SG fix

// Simple model shared between ref struct compat tests (avoids cross-project dependency)
public class RefStructPerson
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class RefStructSerializerTests
{
    [Test]
    public async Task RegularType_Still_Serializes_Via_Cache()
    {
        // The existing test models should still work —
        // verify the new SerCache<T> path works for regular types.
        var person = new RefStructPerson { Name = "Test", Age = 42 };
        var json = JsonSerializer.Serialize(person);
        await Assert.That(json).Contains("Test");
        await Assert.That(json).Contains("42");
    }

    [Test]
    public async Task Compat_Register_ISerializer_IDeserializer_Still_Works()
    {
        // Hand-written ISerializer + IDeserializer should still register
        // through the compat two-param Register overload.
        JsonSerializer.Register<RefStructPerson>(
            new TestPersonSerializer(),
            new TestPersonDeserializer()
        );
        var person = new RefStructPerson { Name = "Compat", Age = 99 };
        var json = JsonSerializer.Serialize(person);
        await Assert.That(json).Contains("Compat");
        await Assert.That(json).Contains("99");
    }

    // ── SG-generated ref struct tests ──

    [Test]
    public async Task SourceGen_Generates_Serializer_For_RefStruct()
    {
        // When the SG runs, it should detect Vec2 is a ref struct,
        // generate a static serializer, and register it via ModuleInitializer.
        var v = new Vec2 { X = 10, Y = 20 };
        var json = JsonSerializer.Serialize(v);

        await Assert.That(json).Contains("10");
        await Assert.That(json).Contains("20");
        await Assert.That(json).Contains("X");
        await Assert.That(json).Contains("Y");
    }

    [Test]
    public async Task SourceGen_Handles_Nested_RefStruct()
    {
        var o = new Outer3
        {
            Id = 42,
            Inner = new Inner3 { A = 1.0f },
        };
        var json = JsonSerializer.Serialize(o);
        await Assert.That(json).Contains("42");
        await Assert.That(json).Contains("Inner");
        await Assert.That(json).Contains("\"A\":");
    }

    [Test]
    public async Task SourceGen_InnerRefStruct_Direct()
    {
        var inner = new Inner3 { A = 5.0f };
        var json = JsonSerializer.Serialize(inner);
        await Assert.That(json).Contains("\"A\":");
    }
}

// Hand-written serializer/deserializer for compat path testing
file struct TestPersonSerializer : ISerializer<RefStructPerson>
{
    public void Serialize(IBufferWriter<byte> w, RefStructPerson v)
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

file struct TestPersonDeserializer : IDeserializer<RefStructPerson>
{
    public RefStructPerson Deserialize(ReadOnlySpan<byte> d) => new() { Name = "ignored", Age = 0 };
}

// ── Ref struct models for SG tests ──

public ref struct Vec2
{
    public int X,
        Y;
}

public ref struct Inner3
{
    public float A;
}

public ref struct Outer3
{
    public int Id;
    public Inner3 Inner;
}
