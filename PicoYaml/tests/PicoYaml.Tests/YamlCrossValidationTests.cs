namespace PicoYaml.Tests;

public class YamlModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public string String { get; set; } = "";
    public DayOfWeek Enum { get; set; }
    public List<int> Ints { get; set; } = [];
    public YamlSub? Nested { get; set; }
}

public class YamlSub
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class YamlCrossValidationTests
{
    private static YamlModel Model => new()
    {
        Bool = true, Int = 42, Long = 9_876_543_210L,
        Float = 3.14f, Double = 2.71828,
        String = "Hello YAML!", Enum = DayOfWeek.Monday,
        Ints = [10, 20],
        Nested = new() { Name = "sub", Value = 99 },
    };

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = YamlSerializer.SerializeToUtf8Bytes(Model);
        var back = YamlSerializer.Deserialize<YamlModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = YamlSerializer.SerializeToUtf8Bytes(Model);
        var back = YamlSerializer.Deserialize<YamlModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back.Bool).IsTrue();
        await Assert.That(back.Int).IsEqualTo(42);
        await Assert.That(back.Long).IsEqualTo(9_876_543_210L);
        await Assert.That(Math.Abs(back.Float - 3.14f) < 0.001f).IsTrue();
        await Assert.That(back.String).IsEqualTo("Hello YAML!");
        await Assert.That(back.Enum).IsEqualTo(DayOfWeek.Monday);
    }
}
