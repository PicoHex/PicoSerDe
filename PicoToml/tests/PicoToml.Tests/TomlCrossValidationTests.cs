using Tomlyn;
using Tomlyn.Model;

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
    public string? NullableString { get; set; }
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public DateOnly DateOnly { get; set; }
    public TimeOnly TimeOnly { get; set; }
    public Guid Guid { get; set; }
    public DayOfWeek Enum { get; set; }
    public int? NullableInt { get; set; }
    public List<int> IntList { get; set; } = [];
    public List<string> StringList { get; set; } = [];
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
        IntList = [10, 20, 30],
        StringList = ["foo", "bar"],
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
        await AssertTomlEqual(Model, back!);
    }

    [Test]
    public async Task PicoSerialize_TomlynDeserialize()
    {
        var picoBytes = TomlSerializer.SerializeToUtf8Bytes(Model);
        var tomlText = Encoding.UTF8.GetString(picoBytes);
        var table = Toml.ToModel(tomlText);
        await Assert.That((bool)table["Bool"]).IsTrue();
        await Assert.That((long)table["Int"]).IsEqualTo(42);
        await Assert.That((string)table["String"]).IsEqualTo(Model.String);
    }

    [Test]
    public async Task TomlynSerialize_PicoDeserialize()
    {
        var tomlText = Toml.FromModel(new TomlTable
        {
            ["Bool"] = true,
            ["Int"] = 42L,
            ["Long"] = 9_876_543_210L,
            ["Double"] = 2.71828,
            ["String"] = "Hello from Tomlyn!",
            ["DateTime"] = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
        });
        var bytes = Encoding.UTF8.GetBytes(tomlText);
        var model = TomlSerializer.Deserialize<TomlModel>(bytes);
        await Assert.That(model).IsNotNull();
        await Assert.That(model.Bool).IsTrue();
        await Assert.That(model.Int).IsEqualTo(42);
        await Assert.That(model.String).IsEqualTo("Hello from Tomlyn!");
    }

    private static async Task AssertTomlEqual(TomlModel expected, TomlModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(Math.Abs(actual.Float - expected.Float) < 0.001f).IsTrue();
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);
        await Assert.That(actual.NullableInt).IsEqualTo(expected.NullableInt);
        await Assert.That(actual.DateTime.ToUniversalTime()).IsEqualTo(expected.DateTime.ToUniversalTime());
        await Assert.That(actual.TimeSpan).IsEqualTo(expected.TimeSpan);
        await Assert.That(actual.DateOnly).IsEqualTo(expected.DateOnly);
        await Assert.That(actual.TimeOnly).IsEqualTo(expected.TimeOnly);
        await Assert.That(actual.Guid).IsEqualTo(expected.Guid);
        await Assert.That(actual.IntList).IsEquivalentTo(expected.IntList);
        await Assert.That(actual.StringList).IsEquivalentTo(expected.StringList);
    }
}
