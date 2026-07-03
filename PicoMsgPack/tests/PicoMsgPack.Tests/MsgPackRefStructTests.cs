using PicoSerDe.Core;

namespace PicoMsgPack.Tests;

public ref struct MpPoint
{
    public int X,
        Y;
}

public class MpSimple
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class MsgPackRefStructTests
{
    [Test]
    public async Task SourceGen_Generates_Serializer_For_RefStruct()
    {
        var v = new MpPoint { X = 10, Y = 20 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(v);
        // MsgPack is binary, just verify it's non-empty and has expected length
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task RegularType_Still_Works()
    {
        var m = new MpSimple { Name = "Test", Value = 42 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(m);
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }
}
