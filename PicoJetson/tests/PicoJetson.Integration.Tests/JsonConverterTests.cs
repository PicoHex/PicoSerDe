namespace PicoJetson.Tests;

public class CustomDateConverter : IJsonConverter<DateTime>
{
    public void Write(IBufferWriter<byte> writer, DateTime value)
    {
        var jw = new JsonWriter(writer);
        jw.WriteString(Encoding.UTF8.GetBytes(value.ToString("yyyy-MM-dd")));
    }

    public DateTime Read(ref JsonReader reader)
    {
        var str = Encoding.UTF8.GetString(reader.GetStringRaw());
        return DateTime.Parse(str);
    }
}

public class ConverterModel
{
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(CustomDateConverter))]
    public DateTime Date { get; set; }
}

public class JsonConverterTests
{
    // Manual replica of what SG should generate for ConverterModel
    private readonly struct ConverterModelJsonSerializer : ISerializer<ConverterModel>
    {
        public void Serialize(IBufferWriter<byte> writer, ConverterModel value)
        {
            var converter = new CustomDateConverter();
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("Name"u8);
            jw.WriteString(Encoding.UTF8.GetBytes(value.Name));
            jw.WritePropertyName("Date"u8);
            converter.Write(writer, value.Date);
            // Mirrors the SG emit: the converter only gets the raw buffer, so the
            // value has to be declared written on the outer writer.
            jw.NotifyRawValueWritten();
            jw.WriteEndObject();
        }
    }

    [Test]
    public async Task JsonConverter_CustomDateFormat_Manual()
    {
        var model = new ConverterModel { Name = "Event", Date = new DateTime(2024, 6, 15) };
        var s = new ConverterModelJsonSerializer();
        var bytes = s.SerializeToBytes(model);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"2024-06-15\"");
    }

    [Test]
    public async Task JsonConverter_CustomDateFormat_Generated()
    {
        var model = new ConverterModel { Name = "Event", Date = new DateTime(2024, 6, 15) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"2024-06-15\"");
    }
}
