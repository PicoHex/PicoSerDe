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
