namespace PicoIni.Tests;

// Test models
[IniSection("CustomName")]
public class SectionAttributedClass { }

public class SectionAttributedProps
{
    [IniSection("ServerConfig")]
    public ServerSettings Server { get; set; } = new();
}

public class ServerSettings
{
    public string Host { get; set; } = "";
}

public class KeyAttributedModel
{
    [IniKey("host_name")]
    public string Host { get; set; } = "";

    [IniKey("port_number")]
    public int Port { get; set; }
}

public class IgnoreAttributedModel
{
    public string Visible { get; set; } = "";

    [IniIgnore]
    public string Hidden { get; set; } = "";
}

[IniComment("Top-level config")]
public class CommentAttributedClass
{
    [IniComment("Server hostname")]
    public string Host { get; set; } = "";
}

public class ConverterAttributedModel
{
    [IniConverter(typeof(TestConverter))]
    public string Value { get; set; } = "";
}

public class TestConverter : IIniConverter<string>
{
    public void Write(IBufferWriter<byte> writer, string value) { }

    public string Read(ReadOnlySpan<byte> value) => "";
}

public class AttributeTests
{
    [Test]
    public async Task IniSectionAttribute_HasName()
    {
        var attr = new IniSectionAttribute("TestName");
        await Assert.That(attr.Name).IsEqualTo("TestName");
    }

    [Test]
    public async Task IniSectionAttribute_CanBeAppliedToClass()
    {
        var attrs = typeof(SectionAttributedClass).GetCustomAttributes(
            typeof(IniSectionAttribute),
            false
        );
        await Assert.That(attrs).Count().IsEqualTo(1);
        await Assert.That(((IniSectionAttribute)attrs[0]).Name).IsEqualTo("CustomName");
    }

    [Test]
    public async Task IniSectionAttribute_CanBeAppliedToProperty()
    {
        var prop = typeof(SectionAttributedProps).GetProperty("Server")!;
        var attr = prop.GetCustomAttributes(typeof(IniSectionAttribute), false);
        await Assert.That(attr).Count().IsEqualTo(1);
        await Assert.That(((IniSectionAttribute)attr[0]).Name).IsEqualTo("ServerConfig");
    }

    [Test]
    public async Task IniKeyAttribute_HasName()
    {
        var attr = new IniKeyAttribute("my_key");
        await Assert.That(attr.Name).IsEqualTo("my_key");
    }

    [Test]
    public async Task IniKeyAttribute_CanBeAppliedToProperty()
    {
        var prop = typeof(KeyAttributedModel).GetProperty("Host")!;
        var attr = prop.GetCustomAttributes(typeof(IniKeyAttribute), false);
        await Assert.That(attr).Count().IsEqualTo(1);
        await Assert.That(((IniKeyAttribute)attr[0]).Name).IsEqualTo("host_name");
    }

    [Test]
    public async Task IniIgnoreAttribute_CanBeAppliedToProperty()
    {
        var prop = typeof(IgnoreAttributedModel).GetProperty("Hidden")!;
        var hasIgnore = prop.IsDefined(typeof(IniIgnoreAttribute), false);
        await Assert.That(hasIgnore).IsTrue();

        var propVisible = typeof(IgnoreAttributedModel).GetProperty("Visible")!;
        await Assert.That(propVisible.IsDefined(typeof(IniIgnoreAttribute), false)).IsFalse();
    }

    [Test]
    public async Task IniCommentAttribute_HasText()
    {
        var attr = new IniCommentAttribute("a comment");
        await Assert.That(attr.Text).IsEqualTo("a comment");
    }

    [Test]
    public async Task IniCommentAttribute_CanBeAppliedToClass()
    {
        var attrs = typeof(CommentAttributedClass).GetCustomAttributes(
            typeof(IniCommentAttribute),
            false
        );
        await Assert.That(attrs).Count().IsEqualTo(1);
        await Assert.That(((IniCommentAttribute)attrs[0]).Text).IsEqualTo("Top-level config");
    }

    [Test]
    public async Task IniCommentAttribute_CanBeAppliedToProperty()
    {
        var prop = typeof(CommentAttributedClass).GetProperty("Host")!;
        var attr = prop.GetCustomAttributes(typeof(IniCommentAttribute), false);
        await Assert.That(attr).Count().IsEqualTo(1);
        await Assert.That(((IniCommentAttribute)attr[0]).Text).IsEqualTo("Server hostname");
    }

    [Test]
    public async Task IniConverterAttribute_HasConverterType()
    {
        var attr = new IniConverterAttribute(typeof(TestConverter));
        await Assert.That(attr.ConverterType).IsEqualTo(typeof(TestConverter));
    }

    [Test]
    public async Task IIniConverter_Interface_Exists()
    {
        var t = typeof(IIniConverter<string>);
        await Assert.That(t.IsInterface).IsTrue();
    }
}

[IniCamelCase]
public class CamelCaseIniPoco
{
    public string FullName { get; set; } = "";
    public int UserAge { get; set; }
}

public class IniCamelCaseTests
{
    [Test]
    public async Task IniCamelCase_Serialize_UsesCamelCaseKeys()
    {
        var obj = new CamelCaseIniPoco { FullName = "Alice", UserAge = 30 };
        var ini = IniSerializer.Serialize(obj);

        await Assert.That(ini).Contains("fullName");
        await Assert.That(ini).Contains("userAge");
        await Assert.That(ini).DoesNotContain("FullName");
        await Assert.That(ini).DoesNotContain("UserAge");
    }

    [Test]
    public async Task IniCamelCase_RoundTrip_Works()
    {
        var original = new CamelCaseIniPoco { FullName = "Bob", UserAge = 25 };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<CamelCaseIniPoco>(bytes);

        await Assert.That(result!.FullName).IsEqualTo("Bob");
        await Assert.That(result.UserAge).IsEqualTo(25);
    }
}

[IniCamelCase]
public class CamelCaseNested
{
    public string StreetName { get; set; } = "";
    public int ZipCode { get; set; }
}

[IniCamelCase]
public class CamelCaseParent
{
    public string FullName { get; set; } = "";
    public CamelCaseNested Address { get; set; } = new();
}

public class IniNestedCamelCaseTests
{
    [Test]
    public async Task IniCamelCase_NestedObject_UsesCamelCaseKeys()
    {
        var obj = new CamelCaseParent
        {
            FullName = "Alice",
            Address = new CamelCaseNested { StreetName = "Main St", ZipCode = 12345 }
        };
        var ini = IniSerializer.Serialize(obj);

        await Assert.That(ini).Contains("streetName");
        await Assert.That(ini).Contains("zipCode");
        await Assert.That(ini).DoesNotContain("StreetName");
        await Assert.That(ini).DoesNotContain("ZipCode");
    }

    [Test]
    public async Task IniCamelCase_NestedObject_RoundTrip_Works()
    {
        var original = new CamelCaseParent
        {
            FullName = "Bob",
            Address = new CamelCaseNested { StreetName = "Main St", ZipCode = 12345 }
        };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<CamelCaseParent>(bytes);

        await Assert.That(result!.FullName).IsEqualTo("Bob");
        await Assert.That(result.Address.StreetName).IsEqualTo("Main St");
        await Assert.That(result.Address.ZipCode).IsEqualTo(12345);
    }
}

public class IniDateTimeFormatPoco
{
    [IniDateTimeFormat("yyyy-MM-dd")]
    public DateTime Date { get; set; }
}

public class IniDateTimeFormatTests
{
    [Test]
    public async Task IniDateTimeFormat_RoundTrip_UsesCustomFormat()
    {
        var original = new IniDateTimeFormatPoco { Date = new DateTime(2024, 6, 15) };
        var ini = IniSerializer.Serialize(original);
        await Assert.That(ini).Contains("2024-06-15");
        await Assert.That(ini).DoesNotContain("2024-06-15T");

        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<IniDateTimeFormatPoco>(bytes);
        await Assert.That(result!.Date).IsEqualTo(new DateTime(2024, 6, 15));
    }
}

// ── Bug: INI list serialization uses comma-join/split, corrupting comma-containing elements ──

public class IniListPoco
{
    public List<string> Tags { get; set; } = new();
}

public class IniStringListPoco
{
    public string[] Items { get; set; } = Array.Empty<string>();
}

public class IniListRoundTripTests
{
    [Test]
    public async Task ListString_RoundTrip_PreservesCommaContainingElements()
    {
        var original = new IniListPoco
        {
            Tags = new List<string> { "hello, world", "foo", "bar,baz" }
        };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<IniListPoco>(bytes);

        await Assert.That(result!.Tags).Count().IsEqualTo(3);
        await Assert.That(result.Tags[0]).IsEqualTo("hello, world");
        await Assert.That(result.Tags[1]).IsEqualTo("foo");
        await Assert.That(result.Tags[2]).IsEqualTo("bar,baz");
    }

    [Test]
    public async Task ArrayString_RoundTrip_PreservesCommaContainingElements()
    {
        var original = new IniStringListPoco { Items = new[] { "a,b", "c", "d,e,f" } };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<IniStringListPoco>(bytes);

        await Assert.That(result!.Items).Count().IsEqualTo(3);
        await Assert.That(result.Items[0]).IsEqualTo("a,b");
        await Assert.That(result.Items[1]).IsEqualTo("c");
        await Assert.That(result.Items[2]).IsEqualTo("d,e,f");
    }

    [Test]
    public async Task ListString_RoundTrip_SimpleNoCommas_StillWorks()
    {
        var original = new IniListPoco
        {
            Tags = new List<string> { "alpha", "beta", "gamma" }
        };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<IniListPoco>(bytes);

        await Assert.That(result!.Tags).Count().IsEqualTo(3);
        await Assert.That(result.Tags[0]).IsEqualTo("alpha");
        await Assert.That(result.Tags[1]).IsEqualTo("beta");
        await Assert.That(result.Tags[2]).IsEqualTo("gamma");
    }

    [Test]
    public async Task ListInt32_RoundTrip_WorksWithCommaFreeValues()
    {
        var original = new IniListPoco
        {
            Tags = new List<string> { "1", "2", "3" }
        };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<IniListPoco>(bytes);

        await Assert.That(result!.Tags).Count().IsEqualTo(3);
        await Assert.That(result.Tags).Contains("1");
        await Assert.That(result.Tags).Contains("2");
        await Assert.That(result.Tags).Contains("3");
    }
}

// ── Dict support ──

public class IniDictPoco
{
    public string Name { get; set; } = "";
    public Dictionary<string, int> Scores { get; set; } = new();
}

public class IniDictRoundTripTests
{
    [Test]
    public async Task DictStringInt_RoundTrip_Works()
    {
        var original = new IniDictPoco
        {
            Name = "D",
            Scores = new Dictionary<string, int> { ["alice"] = 10, ["bob"] = 20 }
        };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<IniDictPoco>(bytes);

        await Assert.That(result!.Name).IsEqualTo("D");
        await Assert.That(result.Scores).IsNotNull();
        await Assert.That(result.Scores.Count).IsEqualTo(2);
        await Assert.That(result.Scores["alice"]).IsEqualTo(10);
        await Assert.That(result.Scores["bob"]).IsEqualTo(20);
    }

    [Test]
    public async Task DictStringInt_Serialize_ContainsRepeatedKeys()
    {
        var original = new IniDictPoco
        {
            Name = "D",
            Scores = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 }
        };
        var ini = IniSerializer.Serialize(original);
        await Assert.That(ini).Contains("Scores");
        await Assert.That(ini).Contains("1");
        await Assert.That(ini).Contains("2");
    }
}

// ── Comment serialization ──

[IniComment("Top-level config file")]
public class IniCommentPoco
{
    [IniComment("The server hostname")]
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public class IniCommentEmitTests
{
    [Test]
    public async Task Serialize_EmitsCommentBeforeProperty()
    {
        var original = new IniCommentPoco { Host = "localhost", Port = 8080 };
        var ini = IniSerializer.Serialize(original);
        await Assert.That(ini).Contains("The server hostname");
    }

    [Test]
    public async Task Serialize_EmitsClassLevelComment()
    {
        var original = new IniCommentPoco { Host = "localhost", Port = 8080 };
        var ini = IniSerializer.Serialize(original);
        await Assert.That(ini).Contains("Top-level config file");
    }
}
