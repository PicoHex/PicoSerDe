namespace PicoIni.Tests;

public class OptionsTests
{
    [Test]
    public async Task IniOptions_Defaults()
    {
        var opts = new IniOptions();
        await Assert.That(opts.Indented).IsFalse();
    }

    [Test]
    public async Task IniOptions_ThreadStatic()
    {
        IniOptions.Current = new IniOptions { Indented = true };
        await Assert.That(IniOptions.Current?.Indented).IsTrue();
        IniOptions.Current = null;
    }
}
