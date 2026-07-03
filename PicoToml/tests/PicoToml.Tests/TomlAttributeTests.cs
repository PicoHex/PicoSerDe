namespace PicoToml.Tests;

public class KeyMappedPoco
{
    [TomlKey("custom_name")]
    public string Name { get; set; } = string.Empty;

    [TomlKey("the_age")]
    public int Age { get; set; }
}

public class IgnoredPoco
{
    public string Title { get; set; } = string.Empty;

    [TomlIgnore]
    public string Secret { get; set; } = string.Empty;

    public int Count { get; set; }
}

public class TagWrapper
{
    public string Value { get; set; } = string.Empty;
}

public class TagWrapperConverter : ITomlConverter<TagWrapper>
{
    public void Write(IBufferWriter<byte> writer, TagWrapper value)
    {
        var formatted = Encoding.UTF8.GetBytes($"[{value.Value}]");
        writer.Write(formatted);
    }

    public TagWrapper Read(ref TomlReader reader)
    {
        var raw = Encoding.UTF8.GetString(reader.ValueSpan);
        var inner = raw.TrimStart('[').TrimEnd(']');
        return new TagWrapper { Value = inner };
    }
}

public class ConverterPoco
{
    public string Name { get; set; } = string.Empty;

    [TomlConverter(typeof(TagWrapperConverter))]
    public TagWrapper Tag { get; set; } = new();
}

[TomlCamelCase]
public class CamelCasePoco
{
    public string FirstName { get; set; } = string.Empty;
    public int UserAge { get; set; }
}

public class DateTimeFormatPoco
{
    [TomlDateTimeFormat("yyyy-MM-dd")]
    public DateTime Date { get; set; }
}

public class TomlAttributeTests
{
    [Test]
    public async Task TomlConverter_RoundTrip_UsesCustomConverter()
    {
        var original = new ConverterPoco
        {
            Name = "Test",
            Tag = new TagWrapper { Value = "hello" },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var text = Encoding.UTF8.GetString(bytes);
        // Converter should produce key-value format: Tag = "[hello]"
        await Assert.That(text).Contains("Tag = \"[hello]\"");

        var result = TomlSerializer.Deserialize<ConverterPoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Tag.Value).IsEqualTo("hello");
    }

    [Test]
    public async Task TomlCamelCase_Serialize_UsesCamelCaseKeys()
    {
        var obj = new CamelCasePoco { FirstName = "Alice", UserAge = 30 };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("firstName");
        await Assert.That(text).Contains("userAge");
        await Assert.That(text).DoesNotContain("FirstName");
        await Assert.That(text).DoesNotContain("UserAge");
    }

    [Test]
    public async Task TomlCamelCase_RoundTrip_Works()
    {
        var original = new CamelCasePoco { FirstName = "Bob", UserAge = 25 };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<CamelCasePoco>(bytes);

        await Assert.That(result!.FirstName).IsEqualTo("Bob");
        await Assert.That(result.UserAge).IsEqualTo(25);
    }

    [Test]
    public async Task TomlDateTimeFormat_RoundTrip_UsesCustomFormat()
    {
        var original = new DateTimeFormatPoco { Date = new DateTime(2024, 6, 15) };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("2024-06-15");
        await Assert.That(text).DoesNotContain("2024-06-15T"); // Not ISO format

        var result = TomlSerializer.Deserialize<DateTimeFormatPoco>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateTime(2024, 6, 15));
    }

    [Test]
    public async Task TomlIgnore_Serialize_ExcludesIgnoredProperty()
    {
        var obj = new IgnoredPoco
        {
            Title = "Hello",
            Secret = "hidden",
            Count = 42,
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("Title");
        await Assert.That(text).Contains("Count");
        await Assert.That(text).DoesNotContain("Secret");
    }

    [Test]
    public async Task TomlIgnore_RoundTrip_ExcludesIgnoredProperty()
    {
        var original = new IgnoredPoco
        {
            Title = "Hello",
            Secret = "hidden",
            Count = 42,
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<IgnoredPoco>(bytes);

        await Assert.That(result!.Title).IsEqualTo("Hello");
        await Assert.That(result.Count).IsEqualTo(42);
        await Assert.That(result.Secret).IsEqualTo("");
    }

    [Test]
    public async Task TomlKey_Serialize_UsesCustomKeyName()
    {
        var obj = new KeyMappedPoco { Name = "Alice", Age = 30 };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("custom_name");
        await Assert.That(text).Contains("the_age");
        await Assert.That(text).DoesNotContain("\"Name\"");
        await Assert.That(text).DoesNotContain("\"Age\"");
    }

    [Test]
    public async Task TomlKey_RoundTrip_WorksWithCustomKeys()
    {
        var original = new KeyMappedPoco { Name = "Bob", Age = 25 };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<KeyMappedPoco>(bytes);

        await Assert.That(result!.Name).IsEqualTo("Bob");
        await Assert.That(result.Age).IsEqualTo(25);
    }
}

public class NestedKeyAddress
{
    [TomlKey("street_address")]
    public string Street { get; set; } = string.Empty;

    [TomlKey("city_name")]
    public string City { get; set; } = string.Empty;
}

public class NestedKeyParent
{
    public string Name { get; set; } = string.Empty;
    public NestedKeyAddress Address { get; set; } = new();
}

public class NestedConverterAddr
{
    [TomlConverter(typeof(TagWrapperConverter))]
    public TagWrapper Tag { get; set; } = new();
}

public class NestedConverterParent
{
    public string Name { get; set; } = string.Empty;
    public NestedConverterAddr Address { get; set; } = new();
}

public class TomlNestedKeyTests
{
    [Test]
    public async Task TomlKey_NestedObject_UsesCustomKeyNames()
    {
        var obj = new NestedKeyParent
        {
            Name = "Home",
            Address = new NestedKeyAddress { Street = "123 Main", City = "NYC" },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("street_address");
        await Assert.That(text).Contains("city_name");
        await Assert.That(text).DoesNotContain("Street");
        await Assert.That(text).DoesNotContain("City");
    }

    [Test]
    public async Task TomlKey_NestedObject_RoundTrip_Works()
    {
        var original = new NestedKeyParent
        {
            Name = "Home",
            Address = new NestedKeyAddress { Street = "123 Main", City = "NYC" },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<NestedKeyParent>(bytes);

        await Assert.That(result!.Name).IsEqualTo("Home");
        await Assert.That(result.Address).IsNotNull();
        await Assert.That(result.Address.Street).IsEqualTo("123 Main");
        await Assert.That(result.Address.City).IsEqualTo("NYC");
    }
}

[TomlCamelCase]
public class CamelCaseNestedAddr
{
    public string StreetName { get; set; } = string.Empty;
    public int ZipCode { get; set; }
}

[TomlCamelCase]
public class CamelCaseNestedParent
{
    public string FullName { get; set; } = string.Empty;
    public CamelCaseNestedAddr Address { get; set; } = new();
}

public class TomlNestedCamelCaseTests
{
    [Test]
    public async Task TomlCamelCase_NestedObject_UsesCamelCaseKeys()
    {
        var obj = new CamelCaseNestedParent
        {
            FullName = "Alice",
            Address = new CamelCaseNestedAddr { StreetName = "Main St", ZipCode = 12345 },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("streetName");
        await Assert.That(text).Contains("zipCode");
        await Assert.That(text).DoesNotContain("StreetName");
        await Assert.That(text).DoesNotContain("ZipCode");
    }

    [Test]
    public async Task TomlCamelCase_NestedObject_RoundTrip_Works()
    {
        var original = new CamelCaseNestedParent
        {
            FullName = "Bob",
            Address = new CamelCaseNestedAddr { StreetName = "Main St", ZipCode = 12345 },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(original);
        var result = TomlSerializer.Deserialize<CamelCaseNestedParent>(bytes);

        await Assert.That(result!.FullName).IsEqualTo("Bob");
        await Assert.That(result.Address.StreetName).IsEqualTo("Main St");
        await Assert.That(result.Address.ZipCode).IsEqualTo(12345);
    }
}

public class TomlNestedConverterTests
{
    [Test]
    public async Task TomlConverter_NestedObject_UsesConverter()
    {
        var obj = new NestedConverterParent
        {
            Name = "Test",
            Address = new NestedConverterAddr { Tag = new TagWrapper { Value = "hello" } },
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        // Converter should produce [hello] format
        await Assert.That(text).Contains("[hello]");
        await Assert.That(text).DoesNotContain("[Tag]");

        var result = TomlSerializer.Deserialize<NestedConverterParent>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Address.Tag.Value).IsEqualTo("hello");
    }
}
