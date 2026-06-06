namespace PicoToml.Tests;

public class TomlModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public string String { get; set; } = "";
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public DateOnly DateOnly { get; set; }
    public TimeOnly TimeOnly { get; set; }
    public Guid Guid { get; set; }
    public DayOfWeek Enum { get; set; }
    public int? NullableInt { get; set; }
}

public class TomlCrossValidationTests
{
    private static TomlModel Model => new()
    {
        Bool = true, Int = 42, Long = 9_876_543_210L,
        Float = 3.14f, Double = 2.71828, Decimal = 123.45m,
        String = "Hello, TOML!",
        DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
        TimeSpan = new TimeSpan(10, 30, 0),
        DateOnly = new DateOnly(2026, 6, 4),
        TimeOnly = new TimeOnly(15, 45, 30),
        Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
        Enum = DayOfWeek.Wednesday,
        NullableInt = 77,
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
        await Assert.That(back.Bool).IsEqualTo(Model.Bool);
        await Assert.That(back.Int).IsEqualTo(Model.Int);
        await Assert.That(back.Long).IsEqualTo(Model.Long);
        await Assert.That(Math.Abs(back.Float - Model.Float) < 0.001f).IsTrue();
        await Assert.That(back.Double).IsEqualTo(Model.Double);
        await Assert.That(back.String).IsEqualTo(Model.String);
        await Assert.That(back.Enum).IsEqualTo(Model.Enum);
        await Assert.That(back.NullableInt).IsEqualTo(Model.NullableInt);
        await Assert.That(back.DateTime.ToUniversalTime()).IsEqualTo(Model.DateTime.ToUniversalTime());
        await Assert.That(back.TimeSpan).IsEqualTo(Model.TimeSpan);
    }
}
