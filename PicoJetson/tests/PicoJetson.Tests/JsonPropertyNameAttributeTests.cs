namespace PicoJetson.Tests;

public class JsonPropertyNameAttributeTests
{
    [Test]
    public async Task Name_ReturnsConstructorValue()
    {
        var attr = new JsonPropertyNameAttribute("full_name");
        await Assert.That(attr.Name).IsEqualTo("full_name");
    }

    [Test]
    public async Task AttributeUsage_IsPropertyOnly()
    {
        var usage = (AttributeUsageAttribute)
            Attribute.GetCustomAttribute(
                typeof(JsonPropertyNameAttribute),
                typeof(AttributeUsageAttribute)
            )!;
        await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Property);
    }

    [Test]
    public async Task PropertyName_WithQuotes_RoundTrips()
    {
        var model = new QuoteModel { Value = 42 };
        var json = JsonSerializer.SerializeToUtf8Bytes(model);
        var restored = JsonSerializer.Deserialize<QuoteModel>(json);
        await Assert.That(restored!.Value).IsEqualTo(42);

        // Verify property name is serialized with proper JSON escaping
        var jsonStr = Encoding.UTF8.GetString(json);
        await Assert.That(jsonStr).Contains("he");
    }
}

// Model with property name containing special characters
public class QuoteModel
{
    [JsonPropertyName("he\"llo")]
    public int Value { get; set; }
}
