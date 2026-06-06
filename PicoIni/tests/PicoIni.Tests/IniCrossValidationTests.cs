namespace PicoIni.Tests;

public class IniModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public string String { get; set; } = "";
    public string? NullableProp { get; set; }
    public int? NullableInt { get; set; }
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public DateOnly DateOnly { get; set; }
    public TimeOnly TimeOnly { get; set; }
    public Guid Guid { get; set; }
    public DayOfWeek Enum { get; set; }
    public IniNested? Section { get; set; }
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, string> Dict { get; set; } = [];
}

public class IniNested
{
    public string Name { get; set; } = "";
}

public class IniCrossValidationTests
{
    private static IniModel Model => new()
    {
        Bool = true, Int = 42, Long = 9_876_543_210L,
        Float = 3.14f, Double = 2.71828, Decimal = 123.45m,
        String = "Hello from PicoIni!",
        Enum = DayOfWeek.Wednesday,
        NullableInt = 77,
        DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
        TimeSpan = new TimeSpan(10, 30, 0),
        DateOnly = new DateOnly(2026, 6, 4),
        TimeOnly = new TimeOnly(15, 45, 30),
        Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
        Section = new() { Name = "test" },
        Dict = new() { ["k"] = "v" },
        Tags = ["tag1", "tag2", "tag3"],
    };

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = IniSerializer.SerializeToUtf8Bytes(Model);
        var back = IniSerializer.Deserialize<IniModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = IniSerializer.SerializeToUtf8Bytes(Model);
        var back = IniSerializer.Deserialize<IniModel>(bytes);
        await AssertIniEqual(Model, back!);
    }

    [Test]
    public async Task PicoSerialize_MsConfigRead()
    {
        var bytes = IniSerializer.SerializeToUtf8Bytes(Model);
        var iniText = Encoding.UTF8.GetString(bytes);
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, iniText);
            var config = new ConfigurationBuilder()
                .AddIniFile(tmp)
                .Build();
            await Assert.That(config["Bool"]).IsEqualTo("true");
            await Assert.That(config["Int"]).IsEqualTo("42");
            await Assert.That(config["String"]).IsEqualTo("Hello from PicoIni!");
            await Assert.That(config["Enum"]).IsEqualTo("Wednesday");
            await Assert.That(config["Tags"]).IsEqualTo("tag1,tag2,tag3");
            await Assert.That(config["Section:Name"]).IsEqualTo("test");
            await Assert.That(config["Dict:k"]).IsEqualTo("v");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static async Task AssertIniEqual(IniModel expected, IniModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(Math.Abs(actual.Float - expected.Float) < 0.001f).IsTrue();
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);
        await Assert.That(actual.NullableInt).IsEqualTo(expected.NullableInt);
        await Assert.That(actual.DateTime.ToUniversalTime()).IsEqualTo(expected.DateTime.ToUniversalTime());
        await Assert.That(actual.TimeSpan).IsEqualTo(expected.TimeSpan);
        await Assert.That(actual.DateOnly).IsEqualTo(expected.DateOnly);
        await Assert.That(actual.TimeOnly).IsEqualTo(expected.TimeOnly);
        await Assert.That(actual.Guid).IsEqualTo(expected.Guid);
        await Assert.That(actual.Tags).IsEquivalentTo(expected.Tags);
    }
}
