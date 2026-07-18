// Regression tests for RegisterCustom<T>: user serializers must override
// SG-generated serialization wherever T appears as a NESTED value
// (object property, nullable property, list element) — closing the
// "registration does not override SG-generated serializers for nested
// types" limitation. The plain Register overload keeps its existing
// top-level-only semantics.

namespace PicoJetson.Tests;

// ── Models (dedicated to this file — RegisterCustom state is process-wide) ──

public class RcInner
{
    public int V { get; set; }
}

public class RcOuter
{
    public string Name { get; set; } = string.Empty;
    public RcInner Inner { get; set; } = new();
    public RcInner? MaybeInner { get; set; }
    public List<RcInner> Items { get; set; } = [];
}

public class Rc2Inner
{
    public int V { get; set; }
}

public class Rc2Outer
{
    public Rc2Inner Inner { get; set; } = new();
}

// ── Custom serializer pair ──

file readonly struct RcInnerCustomSer : ISerializer<RcInner>
{
    public void Serialize(IBufferWriter<byte> writer, RcInner value)
    {
        var jw = new JsonWriter(writer);
        jw.WriteString(Encoding.UTF8.GetBytes($"custom-{value.V}"));
    }
}

file readonly struct RcInnerCustomDes : IDeserializer<RcInner>
{
    public RcInner Deserialize(ReadOnlySpan<byte> data) => new() { V = 42 };
}

file readonly struct Rc2InnerCustomSer : ISerializer<Rc2Inner>
{
    public void Serialize(IBufferWriter<byte> writer, Rc2Inner value)
    {
        var jw = new JsonWriter(writer);
        jw.WriteString("plain-custom"u8);
    }
}

file readonly struct Rc2InnerCustomDes : IDeserializer<Rc2Inner>
{
    public Rc2Inner Deserialize(ReadOnlySpan<byte> data) => new();
}

// ── Tests ──

[NotInParallel("JsonSerializer.RegisterCustom")]
public class RegisterCustomTests
{
    [Test]
    public async Task RegisterCustom_OverridesNestedOccurrences()
    {
        JsonSerializer.RegisterCustom<RcInner>(new RcInnerCustomSer(), new RcInnerCustomDes());
        var outer = new RcOuter
        {
            Name = "n",
            Inner = new RcInner { V = 7 },
            MaybeInner = new RcInner { V = 9 },
            Items = [new RcInner { V = 8 }],
        };
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(outer));
        await Assert.That(json).Contains("\"custom-7\""); // object property
        await Assert.That(json).Contains("\"custom-9\""); // nullable object property
        await Assert.That(json).Contains("\"custom-8\""); // list element
        await Assert.That(json).DoesNotContain("\"V\":");
    }

    [Test]
    public async Task RegisterCustom_TopLevel_AlsoCustom()
    {
        JsonSerializer.RegisterCustom<RcInner>(new RcInnerCustomSer(), new RcInnerCustomDes());
        var json = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(new RcInner { V = 5 })
        );
        await Assert.That(json).IsEqualTo("\"custom-5\"");
    }

    // Existing semantics locked: the plain Register overload affects the
    // top level only — nested occurrences still use the SG serializer.
    [Test]
    public async Task Register_Plain_DoesNotOverrideNested()
    {
        // Trigger SG registration for both types first
        _ = JsonSerializer.SerializeToUtf8Bytes(new Rc2Outer { Inner = new Rc2Inner { V = 3 } });

        JsonSerializer.Register<Rc2Inner>(new Rc2InnerCustomSer(), new Rc2InnerCustomDes());

        var top = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(new Rc2Inner { V = 3 })
        );
        await Assert.That(top).IsEqualTo("\"plain-custom\"");

        var nested = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(new Rc2Outer { Inner = new Rc2Inner { V = 3 } })
        );
        await Assert.That(nested).Contains("\"V\":3");
        await Assert.That(nested).DoesNotContain("plain-custom");
    }
}
