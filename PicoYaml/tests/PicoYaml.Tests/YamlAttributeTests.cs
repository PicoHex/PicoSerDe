namespace PicoYaml.Tests;

public class YamlOptionsSGTests
{
    [Test]
    public async Task YamlOptions_WhenWritingNull_OmitsNull()
    {
        var dto = new YamlNullableDto { Name = "test" }; // Title is null
        YamlOptions.Current = new YamlOptions
        {
            DefaultIgnoreCondition = YamlIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var yaml = YamlSerializer.Serialize(dto);
            await Assert.That(yaml).Contains("Name:");
            await Assert.That(yaml).DoesNotContain("Title:");
        }
        finally
        {
            YamlOptions.Current = null;
        }
    }
}

public class YamlNullableDto
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
}

public class RequiredYamlTests
{
    [Test]
    public async Task RequiredYamlDto_RoundTrip()
    {
        var dto = new RequiredYamlDto { Name = "test", Value = 42 };
        var yaml = YamlSerializer.Serialize(dto);
        var result = YamlSerializer.Deserialize<RequiredYamlDto>(Encoding.UTF8.GetBytes(yaml));
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("test");
        await Assert.That(result.Value).IsEqualTo(42);
    }
}

public class RequiredYamlDto
{
    public required string Name { get; set; }
    public int Value { get; set; }
}
