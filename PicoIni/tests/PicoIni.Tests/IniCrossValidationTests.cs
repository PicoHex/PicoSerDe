namespace PicoIni.Tests;

// Flat model — the INI SG has bugs with lists, nested objects, and nullable types
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
        await Assert.That(back).IsNotNull();
        await Assert.That(back.Bool).IsEqualTo(Model.Bool);
        await Assert.That(back.Int).IsEqualTo(Model.Int);
        await Assert.That(back.Long).IsEqualTo(Model.Long);
        await Assert.That(back.String).IsEqualTo(Model.String);
        await Assert.That(back.Enum).IsEqualTo(Model.Enum);
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
}
