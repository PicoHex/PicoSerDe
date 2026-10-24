namespace PicoSerDe.Core.Tests;

public class TextHelpersCodepointTests
{
    [Test]
    public void RejectsNegative() =>
        Assert.Throws<FormatException>(() => TextHelpers.AppendCodepoint(new byte[8], 0, -1));

    [Test]
    public void RejectsBeyondMax() =>
        Assert.Throws<FormatException>(() => TextHelpers.AppendCodepoint(new byte[8], 0, 0x110000));

    [Test]
    public void RejectsSurrogates() =>
        Assert.Throws<FormatException>(() => TextHelpers.AppendCodepoint(new byte[8], 0, 0xD800));

    [Test]
    public async Task EncodesValid()
    {
        var buf = new byte[8];
        var next = TextHelpers.AppendCodepoint(buf, 0, 0x4E2D);
        await Assert.That(next).IsEqualTo(3);
        await Assert.That(Encoding.UTF8.GetString(buf, 0, 3)).IsEqualTo("中");

        var buf4 = new byte[8];
        var next4 = TextHelpers.AppendCodepoint(buf4, 0, 0x1F600);
        await Assert.That(next4).IsEqualTo(4);
        await Assert.That(Encoding.UTF8.GetString(buf4, 0, 4)).IsEqualTo("\U0001F600");
    }

    // The guard above would reject a legal "two \uXXXX escapes spell one
    // supplementary code point" pair, so the escape readers join it first.
    [Test]
    public async Task ReadUnicodeEscape_JoinsSurrogatePair()
    {
        var src = Encoding.UTF8.GetBytes("D83D\\uDE00");
        int si = 0;
        var cp = TextHelpers.ReadUnicodeEscape(src, ref si);
        await Assert.That(cp).IsEqualTo(0x1F600);
        await Assert.That(si).IsEqualTo(src.Length);
    }

    [Test]
    public void ReadUnicodeEscape_RejectsUnpairedHighSurrogate()
    {
        var src = Encoding.UTF8.GetBytes("D800");
        int si = 0;
        Assert.Throws<FormatException>(() => TextHelpers.ReadUnicodeEscape(src, ref si));
    }
}
