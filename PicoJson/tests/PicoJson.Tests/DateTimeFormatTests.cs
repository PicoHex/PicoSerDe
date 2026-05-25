namespace PicoJson.Tests;

public class DateTimeFormatModel
{
    [DateTimeFormat("yyyy-MM-dd")]
    public DateTime Date { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class DateTimeFormatTests
{
    [Test]
    public async Task DateTimeFormatAttribute_SerializesWithCustomFormat()
    {
        var model = new DateTimeFormatModel
        {
            Date = new DateTime(2024, 6, 15),
            CreatedAt = new DateTime(2024, 1, 1)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var json = Encoding.UTF8.GetString(bytes);
        // Date uses [DateTimeFormat("yyyy-MM-dd")] → only date part
        await Assert.That(json).Contains("\"2024-06-15\"");
        // CreatedAt uses default ISO 8601
        await Assert.That(json).Contains("2024-01-01");
    }

    [Test]
    public async Task DateTimeFormatAttribute_RoundTrip()
    {
        var model = new DateTimeFormatModel
        {
            Date = new DateTime(2024, 6, 15),
            CreatedAt = new DateTime(2024, 6, 15)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<DateTimeFormatModel>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateTime(2024, 6, 15));
        await Assert.That(result.CreatedAt).IsEqualTo(new DateTime(2024, 6, 15));
    }
}
