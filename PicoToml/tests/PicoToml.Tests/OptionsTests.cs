namespace PicoToml.Tests;

public class OptionsTests
{
    [Test]
    public async Task TomlOptions_Defaults()
    {
        var opts = new TomlOptions();
        await Assert.That(opts.Indented).IsFalse();
    }
}
