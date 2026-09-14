namespace PicoYaml.Tests;

/// <summary>
/// Review P3-3: the flow-sequence fast-path readers are currently only
/// reachable through their raw position API (flow sequences are not tokenized
/// in the span read path). They must still bail on integer overflow instead of
/// silently wrapping.
/// </summary>
public class YamlReaderFastPathTests
{
    [Test]
    public async Task TryReadInt32ArrayFast_Basic()
    {
        var reader = new YamlReader("[1, 42, -7, 0]"u8);
        reader.SetRawPos(1); // past '['
        var buf = new int[4];
        var n = reader.TryReadInt32ArrayFast(buf);
        await Assert.That(n).IsEqualTo(4);
        await Assert.That(buf[0]).IsEqualTo(1);
        await Assert.That(buf[1]).IsEqualTo(42);
        await Assert.That(buf[2]).IsEqualTo(-7);
        await Assert.That(buf[3]).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadInt64ArrayFast_Basic()
    {
        var reader = new YamlReader("[1, -5, 9223372036854775807]"u8);
        reader.SetRawPos(1);
        var buf = new long[3];
        var n = reader.TryReadInt64ArrayFast(buf);
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(buf[1]).IsEqualTo(-5);
        await Assert.That(buf[2]).IsEqualTo(9223372036854775807);
    }

    [Test]
    public async Task TryReadInt32ArrayFast_Overflow_BailsOut()
    {
        var reader = new YamlReader("[99999999999]"u8);
        reader.SetRawPos(1);
        var buf = new int[1];
        var n = reader.TryReadInt32ArrayFast(buf);
        await Assert.That(n).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadInt64ArrayFast_Overflow_BailsOut()
    {
        var reader = new YamlReader("[99999999999999999999]"u8);
        reader.SetRawPos(1);
        var buf = new long[1];
        var n = reader.TryReadInt64ArrayFast(buf);
        await Assert.That(n).IsEqualTo(0);
    }
}
