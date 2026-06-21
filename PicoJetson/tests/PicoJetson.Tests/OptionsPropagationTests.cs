namespace PicoJetson.Tests;

public class OptionsPropagationTests
{
    private class TestDto
    {
        public string Name { get; set; } = "";
    }

    private readonly struct OptionsAwareSerializer : ISerializer<TestDto>
    {
        public void Serialize(IBufferWriter<byte> writer, TestDto value)
        {
            var indented = JsonOptions.Current?.Indented ?? false;
            var jw = new JsonWriter(writer, indented: indented, maxDepth: 63);
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
    public async Task SerializeToString_WithIndentedTrue_OutputIsIndented()
    {
        // Clean thread-static state
        JsonOptions.Current = null;
        JsonSerializer.Register(new OptionsAwareSerializer(), new OptionsAwareDeserializer());

        var dto = new TestDto { Name = "Test" };

        // BUG: string overload ignores options — JsonOptions.Current is NOT set
        // Serializer sees null → Indented=false → compact output (NO newlines)
        var json = JsonSerializer.Serialize(dto, new JsonOptions { Indented = true });

        // When options are propagated correctly, this outputs indented JSON with \n
        await Assert.That(json).Contains("\n");
    }

    [Test]
    public async Task SerializeToString_CompactAndIndented_Differ()
    {
        JsonOptions.Current = null;
        JsonSerializer.Register(new OptionsAwareSerializer(), new OptionsAwareDeserializer());

        var dto = new TestDto { Name = "Test" };

        var compact = JsonSerializer.Serialize(dto);
        var indented = JsonSerializer.Serialize(dto, new JsonOptions { Indented = true });

        // BUG: both produce identical output because options are never propagated
        await Assert.That(compact).IsNotEqualTo(indented);
    }
}
