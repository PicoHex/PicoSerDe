namespace PicoSerDe.Core.Tests;

public class SharedAttributeTests
{
    [Test]
    public async Task PicoDateTimeFormat_ExposesFormat()
    {
        var a = new PicoDateTimeFormatAttribute("yyyy-MM-dd");
        await Assert.That(a.Format).IsEqualTo("yyyy-MM-dd");
    }

    [Test]
    public async Task PicoConverter_ExposesConverterType()
    {
        var a = new PicoConverterAttribute(typeof(string));
        await Assert.That(a.ConverterType).IsEqualTo(typeof(string));
    }

    [Test]
    public async Task PicoIgnore_IsSubclassable()
    {
        var a = new TestIgnoreAttribute { Condition = PicoIgnoreCondition.WhenWritingNull };
        await Assert.That(a.Condition).IsEqualTo(PicoIgnoreCondition.WhenWritingNull);
    }

    [Test]
    public async Task Subclass_InheritsAttributeUsage()
    {
        var usage = Attribute.GetCustomAttribute(typeof(TestIgnoreAttribute), typeof(AttributeUsageAttribute));
        await Assert.That(usage).IsNotNull();
        var u = (AttributeUsageAttribute)usage!;
        await Assert.That(u.ValidOn).IsEqualTo(AttributeTargets.Property);
    }

    public sealed class TestIgnoreAttribute : PicoIgnoreAttribute { }
}
