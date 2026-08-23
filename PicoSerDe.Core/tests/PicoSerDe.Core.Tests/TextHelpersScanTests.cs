namespace PicoSerDe.Core.Tests;

public class TextHelpersScanTests
{
    [Test]
    public async Task ScanUntilLineEnd_StopsAtNewline()
    {
        var data = "abc\ndef"u8.ToArray();
        var r1 = TextHelpers.ScanUntilLineEnd(data, 0);
        var r2 = TextHelpers.ScanUntilLineEnd(data, 4);
        await Assert.That(r1).IsEqualTo(3);
        await Assert.That(r2).IsEqualTo(7);
    }

    [Test]
    public async Task ScanUntilLineEnd_StopsAtCarriageReturn()
    {
        var data = "abc\rdef"u8.ToArray();
        var r = TextHelpers.ScanUntilLineEnd(data, 0);
        await Assert.That(r).IsEqualTo(3);
    }

    [Test]
    public async Task ScanUntilLineEnd_ReturnsLength_WhenNoLineEnd()
    {
        var data = "abcdef"u8.ToArray();
        var r = TextHelpers.ScanUntilLineEnd(data, 0);
        await Assert.That(r).IsEqualTo(6);
    }

    [Test]
    public async Task ScanUntilLineEnd_HandlesStartAtLineEnd()
    {
        var data = "ab\ncd"u8.ToArray();
        var r = TextHelpers.ScanUntilLineEnd(data, 2);
        await Assert.That(r).IsEqualTo(2);
    }
}
