namespace PicoJetson.Tests;

public class AnonTypeSerializationTests
{
    [Test]
    public async Task SimpleProperties()
    {
        var json = JsonSerializer.Serialize(new { Name = "Alice", Age = 30 });
        await Assert.That(json).Contains("\"Name\":\"Alice\"");
        await Assert.That(json).Contains("\"Age\":30");
    }

    [Test]
    public async Task AllPrimitiveKinds()
    {
        var json = JsonSerializer.Serialize(new { S = "h", I = 42, L = 123L, D = 2.5, B = true });
        await Assert.That(json).Contains("\"S\":\"h\"");
        await Assert.That(json).Contains("\"I\":42");
        await Assert.That(json).Contains("\"B\":true");
    }

    [Test]
    public async Task NullProperty()
    {
        var json = JsonSerializer.Serialize(new { Name = (string?)null, Value = 42 });
        await Assert.That(json).Contains("\"Name\":null");
        await Assert.That(json).Contains("\"Value\":42");
    }

    [Test]
    public async Task OptionsPropagation_Indented()
    {
        var json = JsonSerializer.Serialize(new { Name = "test", Value = 42 }, new JsonOptions { Indented = true });
        await Assert.That(json).Contains("\"Name\": \"test\"");
        await Assert.That(json).Contains("\n");
    }

    [Test]
    public async Task OptionsPropagation_Compact()
    {
        var json = JsonSerializer.Serialize(new { Name = "test", Value = 42 });
        await Assert.That(json).DoesNotContain("\n");
    }

    [Test]
    public async Task FieldReordering()
    {
        var json = JsonSerializer.Serialize(new { Text = "t", Count = 1, Ratio = 2.5 });
        await Assert.That(json).Contains("\"Text\":\"t\"");
        await Assert.That(json).Contains("\"Ratio\":2.5");
        await Assert.That(json).Contains("\"Count\":1");
    }
}
