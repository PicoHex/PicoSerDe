namespace PicoToml.Tests;

public class KeyMappedPoco
{
    [TomlKey("custom_name")]
    public string Name { get; set; } = "";

    [TomlKey("the_age")]
    public int Age { get; set; }
}

public class IgnoredPoco
{
    public string Title { get; set; } = "";

    [TomlIgnore]
    public string Secret { get; set; } = "";

    public int Count { get; set; }
}

public class TagWrapper
{
    public string Value { get; set; } = "";
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
    public string Name { get; set; } = "";

    [TomlConverter(typeof(TagWrapperConverter))]
    public TagWrapper Tag { get; set; } = new();
}

public class TomlAttributeTests
{
    [Test]
    public async Task TomlConverter_RoundTrip_UsesCustomConverter()
    {
        var original = new ConverterPoco
        {
            Name = "Test",
            Tag = new TagWrapper { Value = "hello" }
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
    public async Task TomlIgnore_Serialize_ExcludesIgnoredProperty()
    {
        var obj = new IgnoredPoco
        {
            Title = "Hello",
            Secret = "hidden",
            Count = 42
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
            Count = 42
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
