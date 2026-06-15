namespace PicoMsgPack.Tests;

public class OptionsTests
{
    [Test]
    public async Task MsgPackOptions_Defaults()
    {
        var opts = new MsgPackOptions();
        await Assert.That(opts.DefaultIgnoreCondition).IsEqualTo(MsgPackIgnoreCondition.Never);
    }
}
