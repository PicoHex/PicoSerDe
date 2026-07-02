namespace PicoJetson.Tests;

/// <summary>
/// Immutable class with [JsonConstructor] — properties have no setter,
/// SG must use constructor to create the object.
/// </summary>
public class ImmutablePerson
{
    public string Name { get; }
    public int Age { get; }

    [JsonConstructor]
    public ImmutablePerson(string name, int age)
    {
        Name = name;
        Age = age;
    }
}

/// <summary>
/// Immutable class with constructor parameter order different from property declaration.
/// </summary>
public class ImmutableBook
{
    public string Title { get; }
    public string Author { get; }
    public int Pages { get; }

    [JsonConstructor]
    public ImmutableBook(int pages, string author, string title)
    {
        Title = title;
        Author = author;
        Pages = pages;
    }
}

/// <summary>
/// Immutable class with getter-only properties — SG must use [JsonConstructor].
/// Properties have no setter, so SG must detect constructor and emit temp vars.
/// </summary>
public class ImmutablePoint
{
    public int X { get; }
    public int Y { get; }

    [JsonConstructor]
    public ImmutablePoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}

public class JsonConstructorTests
{
    [Test]
    public async Task Deserialize_ImmutableClass_ReturnsPopulatedObject()
    {
        var json = """{"name":"Alice","age":30}"""u8;
        var result = JsonSerializer.Deserialize<ImmutablePerson>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result!.Age).IsEqualTo(30);
    }

    [Test]
    public async Task Deserialize_ImmutableClass_DifferentOrder_Works()
    {
        var json = """{"pages":200,"author":"Bob","title":"My Book"}"""u8;
        var result = JsonSerializer.Deserialize<ImmutableBook>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Title).IsEqualTo("My Book");
        await Assert.That(result!.Author).IsEqualTo("Bob");
        await Assert.That(result!.Pages).IsEqualTo(200);
    }

    [Test]
    public async Task Deserialize_ImmutablePoint_ReturnsPopulatedObject()
    {
        var json = """{"X":10,"Y":20}"""u8;
        var result = JsonSerializer.Deserialize<ImmutablePoint>(json);

        await Assert.That(result!.X).IsEqualTo(10);
        await Assert.That(result!.Y).IsEqualTo(20);
    }

    [Test]
    public async Task Deserialize_ImmutableClass_MissingOptionalProperty_DefaultValue()
    {
        var json = """{"name":"Charlie"}"""u8;
        var result = JsonSerializer.Deserialize<ImmutablePerson>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Charlie");
        await Assert.That(result!.Age).IsEqualTo(0); // default
    }

    [Test]
    public async Task Serialize_ImmutableClass_RoundTrips()
    {
        var person = new ImmutablePerson("Diana", 28);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(person);
        var result = JsonSerializer.Deserialize<ImmutablePerson>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Diana");
        await Assert.That(result!.Age).IsEqualTo(28);
    }

    [Test]
    public async Task StreamingDelegate_ImmutableType_IsRegistered()
    {
        // Force SG generation for ImmutablePerson
        _ = JsonSerializer.SerializeToUtf8Bytes(new ImmutablePerson("x", 1));
        var hasDelegate = JsonSerializer.HasStreamingDelegate<ImmutablePerson>();
        await Assert.That(hasDelegate).IsTrue();
    }

    [Test]
    public async Task DeserializeFromStreamAsync_ImmutablePerson_Works()
    {
        var bytes = """{"name":"Alice","age":30}"""u8.ToArray();
        using var stream = new MemoryStream(bytes);

        var result = await JsonSerializer.DeserializeFromStreamAsync<ImmutablePerson>(stream);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result!.Age).IsEqualTo(30);
    }
}
