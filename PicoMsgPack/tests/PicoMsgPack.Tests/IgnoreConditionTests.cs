// Regression tests: MsgPackIgnoreCondition.WhenWritingNull must skip null
// members entirely (dynamic map count) instead of writing nil, and the
// resulting payload must stay readable (map header count consistency).

namespace PicoMsgPack.Tests;

// ── Models ──

public class MIgnNested
{
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class MIgnOuter
{
    public string Name { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int? Count { get; set; }
    public MIgnNested? Nested { get; set; }
    public List<string> Tags { get; set; } = [];
}

[PicoSerializable]
[PicoDerivedType(typeof(MIgnPolyA), "a")]
[PicoDerivedType(typeof(MIgnPolyB), "b")]
public abstract class MIgnPolyBase { }

public class MIgnPolyA : MIgnPolyBase
{
    public string Content { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class MIgnPolyB : MIgnPolyBase
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
}

// ── Tests ──

[NotInParallel("MsgPackOptions.Current")]
public class IgnoreConditionTests
{
    private static byte[] SerializeWhenWritingNull(MIgnOuter model)
    {
        MsgPackOptions.Current = new MsgPackOptions
        {
            DefaultIgnoreCondition = MsgPackIgnoreCondition.WhenWritingNull,
        };
        try
        {
            return MsgPackSerializer.SerializeToUtf8Bytes(model);
        }
        finally
        {
            MsgPackOptions.Current = null;
        }
    }

    // Core: null members are skipped (payload shrinks) and the map header
    // count stays consistent (round-trip succeeds).
    [Test]
    public async Task WhenWritingNull_SkipsNullMembers_AndRoundTrips()
    {
        var model = new MIgnOuter
        {
            Name = "outer",
            Note = null,
            Count = null,
            Nested = null,
            Tags = ["t1"],
        };
        var full = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var skipped = SerializeWhenWritingNull(model);

        await Assert.That(skipped.Length).IsLessThan(full.Length);

        var back = MsgPackSerializer.Deserialize<MIgnOuter>(skipped);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("outer");
        await Assert.That(back.Note).IsNull();
        await Assert.That(back.Count).IsNull();
        await Assert.That(back.Nested).IsNull();
        await Assert.That(back.Tags.Count).IsEqualTo(1);
    }

    // Nested object members (inline emit path) must also be skipped
    [Test]
    public async Task WhenWritingNull_NestedObjectNullMember_SkippedAndRoundTrips()
    {
        var model = new MIgnOuter
        {
            Name = "outer",
            Note = "keep",
            Count = 7,
            Nested = new MIgnNested { Name = "inner", Note = null },
        };
        var full = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var skipped = SerializeWhenWritingNull(model);

        await Assert.That(skipped.Length).IsLessThan(full.Length);

        var back = MsgPackSerializer.Deserialize<MIgnOuter>(skipped);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Note).IsEqualTo("keep");
        await Assert.That(back.Count).IsEqualTo(7);
        await Assert.That(back.Nested).IsNotNull();
        await Assert.That(back.Nested!.Name).IsEqualTo("inner");
        await Assert.That(back.Nested.Note).IsNull();
    }

    // Never (default): nulls are written as nil and round-trip unchanged
    [Test]
    public async Task Never_NullMembers_RoundTripUnchanged()
    {
        var model = new MIgnOuter
        {
            Name = "outer",
            Note = null,
            Count = null,
            Nested = new MIgnNested { Name = "inner", Note = null },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var back = MsgPackSerializer.Deserialize<MIgnOuter>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("outer");
        await Assert.That(back.Note).IsNull();
        await Assert.That(back.Nested).IsNotNull();
        await Assert.That(back.Nested!.Note).IsNull();
    }

    // ── Poly path ──

    // Wire format compliance: the poly map header must count only the pairs
    // actually written for the runtime type (not all derived types' members).
    // Verified with the official MessagePack reader, which trusts the header.
    [Test]
    public async Task Poly_MapHeaderCount_MatchesWrittenPairs()
    {
        MIgnPolyBase value = new MIgnPolyA { Content = "c", Note = "n" };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(value);
        var map = MessagePackSerializer.Deserialize<Dictionary<string, object?>>(bytes);
        // $type + Content + Note
        await Assert.That(map.Count).IsEqualTo(3);
    }

    // Never (default): a null poly member must not crash the serializer
    [Test]
    public async Task Never_PolyNullMember_DoesNotThrow()
    {
        MIgnPolyBase value = new MIgnPolyA { Content = "c", Note = null };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(value);
        var back = MsgPackSerializer.Deserialize<MIgnPolyBase>(bytes);
        await Assert.That(back).IsNotNull();
        var a = (MIgnPolyA)back!;
        await Assert.That(a.Content).IsEqualTo("c");
        await Assert.That(a.Note).IsNull();
    }

    // WhenWritingNull: null poly members are skipped and the payload stays readable
    [Test]
    public async Task WhenWritingNull_PolyNullMember_SkippedAndRoundTrips()
    {
        MIgnPolyBase value = new MIgnPolyA { Content = "c", Note = null };
        var full = MsgPackSerializer.SerializeToUtf8Bytes(value);
        MsgPackOptions.Current = new MsgPackOptions
        {
            DefaultIgnoreCondition = MsgPackIgnoreCondition.WhenWritingNull,
        };
        byte[] skipped;
        try
        {
            skipped = MsgPackSerializer.SerializeToUtf8Bytes(value);
        }
        finally
        {
            MsgPackOptions.Current = null;
        }
        await Assert.That(skipped.Length).IsLessThan(full.Length);
        var back = MsgPackSerializer.Deserialize<MIgnPolyBase>(skipped);
        await Assert.That(back).IsNotNull();
        var a = (MIgnPolyA)back!;
        await Assert.That(a.Content).IsEqualTo("c");
        await Assert.That(a.Note).IsNull();
    }
}
