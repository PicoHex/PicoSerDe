namespace PicoSerDe.Abs.Tests;

public class SimdWhitespaceTests
{
    [Test]
    public async Task SkipWhitespace_AllSpaces_SkipsAll()
    {
        var data = "        hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        var nextChar = (char)data[pos];
        await Assert.That(pos).IsEqualTo(8);
        await Assert.That(nextChar).IsEqualTo('h');
    }

    [Test]
    public async Task SkipWhitespace_NoWhitespace_ReturnsSamePosition()
    {
        var data = "hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(0);
    }

    [Test]
    public async Task SkipWhitespace_MixedTypes_SkipsAll()
    {
        var data = " \t\n\r  hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        var nextChar = (char)data[pos];
        await Assert.That(pos).IsEqualTo(6);
        await Assert.That(nextChar).IsEqualTo('h');
    }

    [Test]
    public async Task SkipWhitespace_EmptyInput_ReturnsZero()
    {
        var data = Array.Empty<byte>();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(0);
    }

    [Test]
    public async Task SkipWhitespace_AllWhitespace_ReturnsLength()
    {
        var data = "   \t\n\r  "u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(data.Length);
    }

    [Test]
    public async Task SkipWhitespace_OffsetStart_SkipsFromOffset()
    {
        var data = "xxx   hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 3);
        var nextChar = (char)data[pos];
        await Assert.That(pos).IsEqualTo(6);
        await Assert.That(nextChar).IsEqualTo('h');
    }
}
