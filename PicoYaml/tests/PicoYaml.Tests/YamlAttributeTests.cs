namespace PicoYaml.Tests;

public class KeyMappedYaml
{
    [YamlKey("custom_name")]
    public string Name { get; set; } = "";

    [YamlKey("the_age")]
    public int Age { get; set; }
}

public class IgnoredYaml
{
    public string Title { get; set; } = "";

    [YamlIgnore]
    public string Secret { get; set; } = "";

    public int Count { get; set; }
}

public class YamlTagWrapper
{
    public string Value { get; set; } = "";
}

public class YamlTagConverter : IYamlConverter<YamlTagWrapper>
{
    public void Write(IBufferWriter<byte> writer, YamlTagWrapper value)
    {
        var formatted = Encoding.UTF8.GetBytes($"[{value.Value}]");
        writer.Write(formatted);
    }

    public YamlTagWrapper Read(ref YamlReader reader)
    {
        var raw = Encoding.UTF8.GetString(reader.ValueSpan);
        var inner = raw.TrimStart('[').TrimEnd(']');
        return new YamlTagWrapper { Value = inner };
    }
}

public class YamlConverterPoco
{
    public string Name { get; set; } = "";

    [YamlConverter(typeof(YamlTagConverter))]
    public YamlTagWrapper Tag { get; set; } = new();
}

public class YamlAttributeTests
{
    [Test]
    public async Task YamlConverter_RoundTrip_UsesCustomConverter()
    {
        var original = new YamlConverterPoco
        {
            Name = "Test",
            Tag = new YamlTagWrapper { Value = "hello" },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var text = Encoding.UTF8.GetString(bytes);
        await Assert.That(text).Contains("[hello]");

        var result = YamlSerializer.Deserialize<YamlConverterPoco>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Tag.Value).IsEqualTo("hello");
    }

    [Test]
    public async Task YamlKey_Serialize_UsesCustomKeyName()
    {
        var obj = new KeyMappedYaml { Name = "Alice", Age = 30 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("custom_name");
        await Assert.That(text).Contains("the_age");
        await Assert.That(text).DoesNotContain("Name:");
        await Assert.That(text).DoesNotContain("Age:");
    }

    [Test]
    public async Task YamlKey_RoundTrip_WorksWithCustomKeys()
    {
        var original = new KeyMappedYaml { Name = "Bob", Age = 25 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<KeyMappedYaml>(bytes);

        await Assert.That(result!.Name).IsEqualTo("Bob");
        await Assert.That(result.Age).IsEqualTo(25);
    }

    [Test]
    public async Task YamlIgnore_Serialize_ExcludesIgnoredProperty()
    {
        var obj = new IgnoredYaml
        {
            Title = "Hello",
            Secret = "hidden",
            Count = 42,
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("Title");
        await Assert.That(text).Contains("Count");
        await Assert.That(text).DoesNotContain("Secret");
    }

    [Test]
    public async Task YamlIgnore_RoundTrip_ExcludesIgnoredProperty()
    {
        var original = new IgnoredYaml
        {
            Title = "Hello",
            Secret = "hidden",
            Count = 42,
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<IgnoredYaml>(bytes);

        await Assert.That(result!.Title).IsEqualTo("Hello");
        await Assert.That(result.Count).IsEqualTo(42);
        await Assert.That(result.Secret).IsEqualTo("");
    }
}

[YamlCamelCase]
public class YamlCamelPoco
{
    public string FirstName { get; set; } = "";
    public int UserAge { get; set; }
}

public class YamlCamelCaseTests
{
    [Test]
    public async Task YamlCamelCase_Serialize_UsesCamelCaseKeys()
    {
        var obj = new YamlCamelPoco { FirstName = "Alice", UserAge = 30 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("firstName");
        await Assert.That(text).Contains("userAge");
        await Assert.That(text).DoesNotContain("FirstName");
        await Assert.That(text).DoesNotContain("UserAge");
    }

    [Test]
    public async Task YamlCamelCase_RoundTrip_Works()
    {
        var original = new YamlCamelPoco { FirstName = "Bob", UserAge = 25 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlCamelPoco>(bytes);

        await Assert.That(result!.FirstName).IsEqualTo("Bob");
        await Assert.That(result.UserAge).IsEqualTo(25);
    }
}

public class YamlDateTimePoco
{
    [YamlDateTimeFormat("yyyy-MM-dd")]
    public DateTime Date { get; set; }
}

public class YamlDateTimeFormatTests
{
    [Test]
    public async Task YamlDateTimeFormat_RoundTrip_UsesCustomFormat()
    {
        var original = new YamlDateTimePoco { Date = new DateTime(2024, 6, 15) };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("2024-06-15");
        await Assert.That(text).DoesNotContain("2024-06-15T");

        var result = YamlSerializer.Deserialize<YamlDateTimePoco>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateTime(2024, 6, 15));
    }
}

[YamlCamelCase]
public class YamlNestedCamelAddr
{
    public string StreetName { get; set; } = "";
    public int ZipCode { get; set; }
}

[YamlCamelCase]
public class YamlNestedCamelParent
{
    public string FullName { get; set; } = "";
    public YamlNestedCamelAddr Address { get; set; } = new();
}

public class YamlNestedConverterAddr
{
    [YamlConverter(typeof(YamlTagConverter))]
    public YamlTagWrapper Tag { get; set; } = new();
}

public class YamlNestedConverterParent
{
    public string Name { get; set; } = "";
    public YamlNestedConverterAddr Address { get; set; } = new();
}

public class YamlNestedAdvancedTests
{
    [Test]
    public async Task YamlCamelCase_NestedObject_UsesCamelCaseKeys()
    {
        var obj = new YamlNestedCamelParent
        {
            FullName = "Alice",
            Address = new YamlNestedCamelAddr { StreetName = "Main St", ZipCode = 12345 },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("streetName");
        await Assert.That(text).Contains("zipCode");
        await Assert.That(text).DoesNotContain("StreetName");
        await Assert.That(text).DoesNotContain("ZipCode");
    }

    [Test]
    public async Task YamlCamelCase_NestedObject_RoundTrip_Works()
    {
        var original = new YamlNestedCamelParent
        {
            FullName = "Bob",
            Address = new YamlNestedCamelAddr { StreetName = "Main St", ZipCode = 12345 },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<YamlNestedCamelParent>(bytes);

        await Assert.That(result!.FullName).IsEqualTo("Bob");
        await Assert.That(result.Address.StreetName).IsEqualTo("Main St");
        await Assert.That(result.Address.ZipCode).IsEqualTo(12345);
    }

    [Test]
    public async Task YamlConverter_NestedObject_UsesConverter()
    {
        var obj = new YamlNestedConverterParent
        {
            Name = "Test",
            Address = new YamlNestedConverterAddr { Tag = new YamlTagWrapper { Value = "hello" } },
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(obj);
        var text = Encoding.UTF8.GetString(bytes);

        await Assert.That(text).Contains("[hello]");

        var result = YamlSerializer.Deserialize<YamlNestedConverterParent>(bytes);
        await Assert.That(result!.Name).IsEqualTo("Test");
        await Assert.That(result.Address.Tag.Value).IsEqualTo("hello");
    }
}
