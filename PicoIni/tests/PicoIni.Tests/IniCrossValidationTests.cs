namespace PicoIni.Tests;

public class IniModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public double Double { get; set; }
    public string String { get; set; } = "";
    public DayOfWeek Enum { get; set; }
    public string? NullableProp { get; set; }
    public int? NullableInt { get; set; }
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
        Bool = true,
        Int = 42,
        Long = 9_876_543_210L,
        Double = 2.71828,
        String = "Hello from PicoIni!",
        Enum = DayOfWeek.Wednesday,
        NullableInt = 77,
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
    public async Task PicoSerialize_ListOutput()
    {
        var bytes = IniSerializer.SerializeToUtf8Bytes(Model);
        var iniText = Encoding.UTF8.GetString(bytes);
        await Assert.That(iniText).Contains("Tags = tag1,tag2,tag3");
    }

    private static async Task AssertIniEqual(IniModel expected, IniModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);
        await Assert.That(actual.NullableInt).IsEqualTo(expected.NullableInt);
        await Assert.That(actual.Tags).IsEquivalentTo(expected.Tags);
    }
}
