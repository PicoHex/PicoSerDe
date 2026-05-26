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
        var attrs = typeof(SectionAttributedClass).GetCustomAttributes(typeof(IniSectionAttribute), false);
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
        var attrs = typeof(CommentAttributedClass).GetCustomAttributes(typeof(IniCommentAttribute), false);
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
