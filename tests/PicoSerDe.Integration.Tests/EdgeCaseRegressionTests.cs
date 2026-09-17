using System.Text;

namespace PicoSerDe.Integration.Tests;

// ── edge-case regression fixtures ──

public class RvLeaf
{
    public string Name { get; set; } = "";
}

public class RvNullableEdges
{
    public List<Guid?> Guids { get; set; } = new();
    public List<DateTime?> Dates { get; set; } = new();
    public List<bool?> Bools { get; set; } = new();
    public HashSet<string?> Set { get; set; } = new();
    public List<List<int?>> Grid { get; set; } = new();
}

public class RvDeepNested
{
    public string Name { get; set; } = "";
    public List<List<List<RvLeaf>>> Cube { get; set; } = new();
}

[PicoSerializable]
[PicoDerivedType(typeof(RvPolyLeaf), "leaf")]
public class RvPolyBase
{
    public int Id { get; set; }
    public RvPolyBase? Next { get; set; }
    public List<RvPolyBase> Children { get; set; } = new();
}

public class RvPolyLeaf : RvPolyBase
{
    public string Extra { get; set; } = "";
}

[PicoSerializable]
[PicoDerivedType(typeof(RvBaseInst), "ConcBaseName")]
public class RvBaseCollide
{
    public int Id { get; set; }
}

public class RvBaseInst : RvBaseCollide
{
    public string Name { get; set; } = "";
}

/// <summary>
/// Regression tests from the code review probes: nullable scalar/value-type
/// elements in nested collections, 3-level nested lists of objects,
/// polymorphic recursion through object members *and* list elements, and
/// concrete-base instances (in a list and in the streaming path).
/// </summary>
public class EdgeCaseRegressionTests
{
    [Test]
    public async Task Json_NullableScalarAndValueElements_AllKinds()
    {
        var v = new RvNullableEdges
        {
            Guids = new List<Guid?> { Guid.Empty, null },
            Dates = new List<DateTime?> { new DateTime(2020, 1, 1), null },
            Bools = new List<bool?> { true, null },
            Set = new HashSet<string?> { "a", null },
            Grid = new List<List<int?>>
            {
                new() { 1, null },
            },
        };
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvNullableEdges>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Guids.Count).IsEqualTo(2);
        await Assert.That(back.Guids[1]).IsNull();
        await Assert.That(back.Dates[1]).IsNull();
        await Assert.That(back.Bools[1]).IsNull();
        await Assert.That(back.Set.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0][0]).IsEqualTo(1);
        await Assert.That(back.Grid[0][1]).IsNull();
    }

    [Test]
    public async Task MsgPack_NullableScalarAndValueElements_AllKinds()
    {
        var v = new RvNullableEdges
        {
            Guids = new List<Guid?> { Guid.Empty, null },
            Bools = new List<bool?> { true, null },
            Grid = new List<List<int?>>
            {
                new() { 1, null },
            },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(v);
        var back = MsgPackSerializer.Deserialize<RvNullableEdges>(bytes);

        await Assert.That(back!.Guids[1]).IsNull();
        await Assert.That(back.Bools[1]).IsNull();
        await Assert.That(back.Grid[0][1]).IsNull();
    }

    [Test]
    public async Task Json_ThreeLevelNestedListOfObjects()
    {
        var v = new RvDeepNested
        {
            Name = "n",
            Cube = new List<List<List<RvLeaf>>>
            {
                new() { new() { new RvLeaf { Name = "deep" } } },
            },
        };
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvDeepNested>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Cube[0][0][0].Name).IsEqualTo("deep");
    }

    [Test]
    public async Task MsgPack_ThreeLevelNestedListOfObjects()
    {
        var v = new RvDeepNested
        {
            Name = "n",
            Cube = new List<List<List<RvLeaf>>>
            {
                new() { new() { new RvLeaf { Name = "deep" } } },
            },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(v);
        var back = MsgPackSerializer.Deserialize<RvDeepNested>(bytes);

        await Assert.That(back!.Cube[0][0][0].Name).IsEqualTo("deep");
    }

    [Test]
    public async Task Json_PolymorphicRecursionThroughMemberAndListElement()
    {
        RvPolyBase v = new RvPolyLeaf
        {
            Id = 1,
            Extra = "root",
            Next = new RvPolyLeaf { Id = 2, Extra = "next" },
            Children = new List<RvPolyBase>
            {
                new RvPolyLeaf { Id = 3, Extra = "child" },
            },
        };
        var json = JsonSerializer.Serialize(v);
        var back = JsonSerializer.Deserialize<RvPolyBase>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back).IsTypeOf<RvPolyLeaf>();
        await Assert.That(((RvPolyLeaf?)back!.Next)?.Extra).IsEqualTo("next");
        await Assert.That(((RvPolyLeaf)back.Children[0]).Extra).IsEqualTo("child");
    }

    [Test]
    public async Task MsgPack_PolymorphicRecursionThroughMemberAndListElement()
    {
        RvPolyBase v = new RvPolyLeaf
        {
            Id = 1,
            Extra = "root",
            Next = new RvPolyLeaf { Id = 2, Extra = "next" },
            Children = new List<RvPolyBase>
            {
                new RvPolyLeaf { Id = 3, Extra = "child" },
            },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(v);
        var back = MsgPackSerializer.Deserialize<RvPolyBase>(bytes);

        await Assert.That(((RvPolyLeaf?)back!.Next)?.Extra).IsEqualTo("next");
        await Assert.That(((RvPolyLeaf)back.Children[0]).Extra).IsEqualTo("child");
    }

    [Test]
    public async Task BaseInstance_InsideList_DispatchesPerElement()
    {
        var holder = new List<RvBaseCollide>
        {
            new RvBaseCollide { Id = 9 },
            new RvBaseInst { Id = 1, Name = "x" },
        };
        var json = JsonSerializer.Serialize(holder);
        var back = JsonSerializer.Deserialize<List<RvBaseCollide>>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back![0].Id).IsEqualTo(9).Because("json=" + json);
        await Assert.That(back[1]).IsTypeOf<RvBaseInst>();
        await Assert.That(((RvBaseInst)back[1]).Name).IsEqualTo("x");
    }

    [Test]
    public async Task BaseInstance_ChunkedStreaming()
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new RvBaseCollide { Id = 11 }));
        foreach (var chunk in new[] { 1, 5, 64 })
        {
            using var stream = new SkChunkedStream(json, chunk);
            var back = await JsonSerializer.DeserializeFromStreamAsync<RvBaseCollide>(stream);
            await Assert.That(back!.Id).IsEqualTo(11).Because($"chunk={chunk}");
        }
    }
}
