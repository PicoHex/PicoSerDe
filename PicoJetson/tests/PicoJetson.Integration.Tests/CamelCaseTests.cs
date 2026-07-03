namespace PicoJetson.Tests;

[JsonCamelCase]
public class CamelModel
{
    public string FullName { get; set; } = string.Empty;
    public int UserAge { get; set; }
}

public class CamelCaseTests
{
    [Test]
    public async Task JsonCamelCase_SerializesWithCamelCase()
    {
        var model = new CamelModel { FullName = "Alice", UserAge = 30 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"fullName\"");
        await Assert.That(json).Contains("\"userAge\"");
        await Assert.That(json).DoesNotContain("FullName");
    }

    [Test]
    public async Task JsonCamelCase_RoundTrip()
    {
        var model = new CamelModel { FullName = "Bob", UserAge = 25 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<CamelModel>(bytes);
        await Assert.That(result!.FullName).IsEqualTo("Bob");
        await Assert.That(result.UserAge).IsEqualTo(25);
    }
}
