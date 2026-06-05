namespace PicoToml.Tests;

public class TomlModel
{
    public bool Bool { get; set; }
    public string String { get; set; } = "";
}

public class TomlCrossValidationTests
{
    private static TomlModel Model => new() { Bool = true, String = "Hello" };

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
        await Assert.That(back.String).IsEqualTo("Hello");
    }
}
