// Regression tests for MsgPackSerializer.RegisterCustom<T>: user serializers
// must override SG-generated serialization for nested occurrences of T.

namespace PicoMsgPack.Tests;

// ── Models ──

public class McInner
{
    public int V { get; set; }
}

public class McOuter
{
    public string Name { get; set; } = string.Empty;
    public McInner Inner { get; set; } = new();
    public List<McInner> Items { get; set; } = [];
}

// ── Custom serializer pair ──

file readonly struct McInnerCustomSer : ISerializer<McInner>
{
    public void Serialize(IBufferWriter<byte> writer, McInner value)
    {
        var mw = new MsgPackWriter(writer);
        mw.WriteString(Encoding.UTF8.GetBytes($"custom-{value.V}"));
        // A custom serializer owns its writer: after writing a bare value (no enclosing
        // map/array to close) it must commit the tail itself, or the bytes stay in the
        // writer's span and the caller's buffer never sees them. Reading `Buffer` is the
        // public way to do it -- exactly what generated serializers emit, and the only
        // exit available to an external assembly (this sample deliberately uses the public
        // surface, not the friend-only FlushToBuffer()).
        _ = mw.Buffer;
    }
}

file readonly struct McInnerCustomDes : IDeserializer<McInner>
{
    public McInner Deserialize(ReadOnlySpan<byte> data) => new() { V = 42 };
}

// ── Tests ──

[NotInParallel("MsgPackSerializer.RegisterCustom")]
public class RegisterCustomTests
{
    [Test]
    public async Task RegisterCustom_OverridesNestedOccurrences()
    {
        MsgPackSerializer.RegisterCustom<McInner>(new McInnerCustomSer(), new McInnerCustomDes());
        var outer = new McOuter
        {
            Name = "n",
            Inner = new McInner { V = 7 },
            Items = [new McInner { V = 8 }],
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(outer);
        // Official reader: keys Name=0, Inner=1, Items=2. With the custom
        // serializer the nested values become strings instead of maps.
        var map = MessagePackSerializer.Deserialize<Dictionary<int, object?>>(bytes);
        await Assert.That(map[1]).IsEqualTo("custom-7");
        var items = (object?[])map[2]!;
        await Assert.That(items[0]).IsEqualTo("custom-8");
    }

    [Test]
    public async Task RegisterCustom_TopLevel_AlsoCustom()
    {
        MsgPackSerializer.RegisterCustom<McInner>(new McInnerCustomSer(), new McInnerCustomDes());
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new McInner { V = 5 });
        var value = MessagePackSerializer.Deserialize<string>(bytes);
        await Assert.That(value).IsEqualTo("custom-5");
    }
}
