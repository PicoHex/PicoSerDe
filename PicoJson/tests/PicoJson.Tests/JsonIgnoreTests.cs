using System.Text;

namespace PicoJson.Tests;

public class IgnoreModel
{
    public string Name { get; set; } = "";

    [JsonIgnore]
    public string Secret { get; set; } = "";

    public int Age { get; set; }
}

public class JsonIgnoreTests
{
    // Manual replica of what SG should generate for IgnoreModel
    private readonly struct IgnoreModelJsonSerializer : ISerializer<IgnoreModel>
    {
        public void Serialize(IBufferWriter<byte> writer, IgnoreModel value)
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

    [Test]
    public async Task JsonIgnore_SkipsProperty_Manual()
    {
        var m = new IgnoreModel { Name = "Alice", Secret = "secret123", Age = 30 };
        var s = new IgnoreModelJsonSerializer();
        var bytes = s.SerializeToBytes(m);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).DoesNotContain("Secret");
        await Assert.That(json).DoesNotContain("secret123");
        await Assert.That(json).Contains("Alice");
    }

    [Test]
    public async Task JsonIgnore_SkipsProperty_Generated()
    {
        var m = new IgnoreModel { Name = "Alice", Secret = "secret123", Age = 30 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(m);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).DoesNotContain("Secret");
        await Assert.That(json).DoesNotContain("secret123");
        await Assert.That(json).Contains("Alice");
    }
}
