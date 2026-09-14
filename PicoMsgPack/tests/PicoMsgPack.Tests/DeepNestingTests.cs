namespace PicoMsgPack.Tests;

/// <summary>
/// Deep object nesting (3+ levels) must compile and round-trip. The nested
/// object branch used to collide local names and assign to the wrong receiver.
/// </summary>
public class DeepNestingTests
{
    [Test]
    public async Task ThreeLevelNesting_RoundTrips()
    {
        var dto = new MpDeepL1 { L2 = new MpDeepL2 { L3 = new MpDeepL3 { X = 42 } } };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(dto);
        var back = MsgPackSerializer.Deserialize<MpDeepL1>(bytes);
        await Assert.That(back!.L2.L3.X).IsEqualTo(42);
    }
}

internal sealed class MpDeepL1
{
    public MpDeepL2 L2 { get; set; } = new();
}

internal sealed class MpDeepL2
{
    public MpDeepL3 L3 { get; set; } = new();
}

internal sealed class MpDeepL3
{
    public int X { get; set; }
}
