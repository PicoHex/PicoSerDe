namespace PicoYaml.Tests;

public class OptionsTests
{
    [Test]
    public async Task YamlOptions_Defaults()
    {
        var opts = new YamlOptions();
        await Assert.That(opts.Indented).IsFalse();
    }
}
