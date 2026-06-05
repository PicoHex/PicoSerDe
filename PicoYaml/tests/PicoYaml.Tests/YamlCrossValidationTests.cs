namespace PicoYaml.Tests;

public class FlatModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public string String { get; set; } = "";
    public DayOfWeek Enum { get; set; }
}

public class YamlCrossValidationTests
{
    private static FlatModel Model => new() { Bool = true, Int = 42, String = "Hello", Enum = DayOfWeek.Monday };

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = YamlSerializer.SerializeToUtf8Bytes(Model);
        var back = YamlSerializer.Deserialize<FlatModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = YamlSerializer.SerializeToUtf8Bytes(Model);
        var back = YamlSerializer.Deserialize<FlatModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back.Bool).IsTrue();
        await Assert.That(back.Int).IsEqualTo(42);
        await Assert.That(back.String).IsEqualTo("Hello");
        await Assert.That(back.Enum).IsEqualTo(DayOfWeek.Monday);
    }
}
