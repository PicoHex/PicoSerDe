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

public class YamlCtorTests
{
    [Test]
    public async Task YamlImmutable_RoundTrip()
    {
        var dto = new YamlImmutableDto("hello", 7);
        var yaml = YamlSerializer.Serialize(dto);
        var result = YamlSerializer.Deserialize<YamlImmutableDto>(Encoding.UTF8.GetBytes(yaml));
        await Assert.That(result!.Label).IsEqualTo("hello");
        await Assert.That(result.Count).IsEqualTo(7);
    }
}

public class YamlImmutableDto
{
    public string Label { get; }
    public int Count { get; }

    [YamlConstructor]
    public YamlImmutableDto(string label, int count) => (Label, Count) = (label, count);
}

public class YamlCtorAllTypes
{
    [Test]
    public async Task YamlCtor_Decimal_RoundTrip()
    {
        var dto = new YamlCtorDecimal(99.99m, "test");
        var yaml = YamlSerializer.Serialize(dto);
        var result = YamlSerializer.Deserialize<YamlCtorDecimal>(Encoding.UTF8.GetBytes(yaml));
        await Assert.That(result!.Price).IsEqualTo(99.99m);
        await Assert.That(result.Name).IsEqualTo("test");
    }
}

public class YamlCtorDecimal
{
    public decimal Price { get; }
    public string Name { get; }

    [YamlConstructor]
    public YamlCtorDecimal(decimal price, string name) => (Price, Name) = (price, name);
}
