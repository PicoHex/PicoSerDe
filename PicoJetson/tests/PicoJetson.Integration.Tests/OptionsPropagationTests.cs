namespace PicoJetson.Tests;

public class OptionsPropagationTests
{
    private class TestDto
    {
        public string Name { get; set; } = string.Empty;
    }

    private readonly struct OptionsAwareSerializer : ISerializer<TestDto>
    {
        public void Serialize(IBufferWriter<byte> writer, TestDto value)
        {
            // Hand-written serializers do not receive options (explicit contract).
            var jw = new JsonWriter(writer, indented: false, maxDepth: 63);
            jw.WriteStartObject();
            jw.WritePropertyName("Name"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Name));
            jw.WriteEndObject();
        }
    }

    private readonly struct OptionsAwareDeserializer : IDeserializer<TestDto>
    {
        public TestDto Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new JsonReader(data);
            reader.Read();
            var obj = new TestDto();
            while (reader.Read() && reader.TokenType == TokenType.PropertyName)
            {
                var prop = reader.GetStringRaw();
                reader.Read();
                if (prop.SequenceEqual("Name"u8))
                    obj.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
                else
                    reader.TrySkip();
            }
            return obj;
        }
    }

    [Test]
    public async Task HandWrittenSerializer_DoesNotReceiveOptions()
    {
        // New contract: options flow to SG-generated code and reader/writer
        // instances only. Hand-written ISerializer implementations are invoked
        // without options — the serializer sees null and emits compact output.
        JsonSerializer.Register(new OptionsAwareSerializer(), new OptionsAwareDeserializer());

        var dto = new TestDto { Name = "Test" };

        var json = JsonSerializer.Serialize(dto, new JsonOptions { Indented = true });

        await Assert.That(json).DoesNotContain("\n");
    }

    [Test]
    public async Task HandWrittenSerializer_WithAndWithoutOptions_ProducesSameOutput()
    {
        JsonSerializer.Register(new OptionsAwareSerializer(), new OptionsAwareDeserializer());

        var dto = new TestDto { Name = "Test" };

        var compact = JsonSerializer.Serialize(dto);
        var indented = JsonSerializer.Serialize(dto, new JsonOptions { Indented = true });

        // Options are ignored by the hand-written path — output is identical.
        await Assert.That(compact).IsEqualTo(indented);
    }
}
