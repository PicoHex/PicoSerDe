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
            Tag = new YamlTagWrapper { Value = "hello" }
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
            Count = 42
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
            Count = 42
        };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(original);
        var result = YamlSerializer.Deserialize<IgnoredYaml>(bytes);

        await Assert.That(result!.Title).IsEqualTo("Hello");
        await Assert.That(result.Count).IsEqualTo(42);
        await Assert.That(result.Secret).IsEqualTo("");
    }
}
