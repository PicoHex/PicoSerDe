namespace PicoToml.Tests;

public class OptionsTests
{
    [Test]
    public async Task TomlOptions_Defaults()
    {
        var opts = new TomlOptions();
        await Assert.That(opts.Indented).IsFalse();
    }
}

public class TomlCtorTests
{
    [Test]
    public async Task TomlImmutable_RoundTrip()
    {
        var person = new TomlImmutablePerson("Alice", 30);
        var toml = TomlSerializer.Serialize(person);
        var result = TomlSerializer.Deserialize<TomlImmutablePerson>(Encoding.UTF8.GetBytes(toml));
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }
}

public class TomlImmutablePerson
{
    public string Name { get; }
    public int Age { get; }

    [TomlConstructor]
    public TomlImmutablePerson(string name, int age) => (Name, Age) = (name, age);
}

public class TomlOptionsSGTests
{
    [Test]
    public async Task TomlOptions_IgnoreNull_Never_WritesAllProperties()
    {
        var dto = new TomlNullableDto { Name = "test", Title = "Mr" };
        TomlOptions.Current = new TomlOptions
        {
            DefaultIgnoreCondition = TomlIgnoreCondition.Never,
        };
        try
        {
            var toml = TomlSerializer.Serialize(dto);
            await Assert.That(toml).Contains("Name");
            await Assert.That(toml).Contains("Title");
            await Assert.That(toml).Contains("Mr");
        }
        finally
        {
            TomlOptions.Current = null;
        }
    }
}

public class TomlNullableDto
{
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
}

public class RequiredTomlTests
{
    [Test]
    public async Task RequiredTomlDto_RoundTrip()
    {
        var dto = new RequiredTomlDto { Name = "test", Value = 42 };
        var toml = TomlSerializer.Serialize(dto);
        var result = TomlSerializer.Deserialize<RequiredTomlDto>(Encoding.UTF8.GetBytes(toml));
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("test");
        await Assert.That(result.Value).IsEqualTo(42);
    }
}

public class RequiredTomlDto
{
    public required string Name { get; set; }
    public int Value { get; set; }
}
