namespace PicoIni.Tests;

public class OptionsTests
{
    [Test]
    public async Task IniOptions_Defaults()
    {
        var opts = new IniOptions();
        await Assert.That(opts.Indented).IsFalse();
    }

    [Test]
    public async Task IniOptions_ThreadStatic()
    {
        IniOptions.Current = new IniOptions { Indented = true };
        await Assert.That(IniOptions.Current?.Indented).IsTrue();
        IniOptions.Current = null;
    }
}

public class ConstructorTests
{
    [Test]
    public async Task ImmutablePerson_RoundTrip()
    {
        var person = new ImmutablePerson("Alice", 30);
        var ini = IniSerializer.Serialize(person);
        var result = IniSerializer.Deserialize<ImmutablePerson>(Encoding.UTF8.GetBytes(ini));
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result.Age).IsEqualTo(30);
    }
}

public class ImmutablePerson
{
    public string Name { get; }
    public int Age { get; }

    [IniConstructor]
    public ImmutablePerson(string name, int age) => (Name, Age) = (name, age);
}

public class OptionsSGTests
{
    [Test]
    public async Task IniOptions_IgnoreNull_OmitsNullProperty()
    {
        var dto = new NullableIniDto { Name = "test" }; // Title is null
        IniOptions.Current = new IniOptions
        {
            DefaultIgnoreCondition = IniIgnoreCondition.WhenWritingNull,
        };
        try
        {
            var ini = IniSerializer.Serialize(dto);
            await Assert.That(ini).Contains("Name");
            await Assert.That(ini).DoesNotContain("Title");
        }
        finally
        {
            IniOptions.Current = null;
        }
    }
}

public class NullableIniDto
{
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
}

public class RequiredIniTests
{
    [Test]
    public async Task RequiredIniDto_RoundTrip()
    {
        var dto = new RequiredIniDto { Name = "test", Value = 42 };
        var ini = IniSerializer.Serialize(dto);
        var result = IniSerializer.Deserialize<RequiredIniDto>(Encoding.UTF8.GetBytes(ini));
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("test");
        await Assert.That(result.Value).IsEqualTo(42);
    }
}

public class RequiredIniDto
{
    public required string Name { get; set; }
    public int Value { get; set; }
}
