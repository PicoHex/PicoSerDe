namespace PicoIni.Tests;

public class IniModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public double Double { get; set; }
    public string String { get; set; } = "";
    public DayOfWeek Enum { get; set; }
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
    public async Task ManualIni_PicoDeserialize()
    {
        var iniText = "Bool = True\nInt = 42\nString = Hello\n";
        var bytes = Encoding.UTF8.GetBytes(iniText);
        var model = IniSerializer.Deserialize<IniModel>(bytes);
        await Assert.That(model).IsNotNull();
        await Assert.That(model.Bool).IsTrue();
        await Assert.That(model.Int).IsEqualTo(42);
        await Assert.That(model.String).IsEqualTo("Hello");
    }

    private static async Task AssertIniEqual(IniModel expected, IniModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);
    }
}
