namespace PicoJson.Tests;

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
}
