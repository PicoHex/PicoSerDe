namespace PicoIni.Tests;

public class IniTokenTypeTests
{
    [Test]
    public async Task IniTokenType_HasExpectedValues()
    {
        var values = Enum.GetValues<IniTokenType>();
        await Assert.That(values).Count().IsEqualTo(6);
    }

    [Test]
    public async Task IniTokenType_NoneIsZero()
    {
        await Assert.That((int)IniTokenType.None).IsEqualTo(0);
    }

    [Test]
    public async Task IniTokenType_HasRequiredMembers()
    {
        await Assert.That(IniTokenType.SectionStart).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Key).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Value).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Comment).IsNotEqualTo(IniTokenType.None);
        await Assert.That(IniTokenType.Blank).IsNotEqualTo(IniTokenType.None);
    }
}
