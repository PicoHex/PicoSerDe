namespace PicoToml.Tests;

public class TomlModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public double Double { get; set; }
    public string String { get; set; } = "";
    public DateTime DateTime { get; set; }
    public DayOfWeek Enum { get; set; }
}

public class TomlCrossValidationTests
{
    private static TomlModel Model => new()
    {
        Bool = true,
        Int = 42,
        Long = 9_876_543_210L,
        Double = 2.71828,
        String = "Hello, TOML! 测试",
        DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
        Enum = DayOfWeek.Wednesday,
    };

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = TomlSerializer.SerializeToUtf8Bytes(Model);
        var back = TomlSerializer.Deserialize<TomlModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = TomlSerializer.SerializeToUtf8Bytes(Model);
        var back = TomlSerializer.Deserialize<TomlModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back.Bool).IsTrue();
        await Assert.That(back.Int).IsEqualTo(42);
        await Assert.That(back.Long).IsEqualTo(9_876_543_210L);
        await Assert.That(back.String).IsEqualTo("Hello, TOML! 测试");
        await Assert.That(back.DateTime.ToUniversalTime()).IsEqualTo(new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc));
        await Assert.That(back.Enum).IsEqualTo(DayOfWeek.Wednesday);
    }
}
