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
