using System.Text;

namespace PicoSerDe.Integration.Tests;

public class TreeNode
{
    public int Value { get; set; }
    public TreeNode? Child { get; set; }
}

public class MutualA
{
    public string Name { get; set; } = "";
    public MutualB? B { get; set; }
}

public class MutualB
{
    public int N { get; set; }
    public MutualA? A { get; set; }
}

public class DictNode
{
    public string Name { get; set; } = "";
    public Dictionary<string, DictNode> Children { get; set; } = new();
}

[PicoSerializable]
[PicoDerivedType(typeof(PolyRecLeaf), "leaf")]
public abstract class PolyRecAbstract
{
    public int Value { get; set; }
    public PolyRecAbstract? Next { get; set; }
}

public class PolyRecLeaf : PolyRecAbstract
{
    public string Extra { get; set; } = "";
}

[PicoSerializable]
[PicoDerivedType(typeof(ConcRecLeaf), "leaf")]
public class ConcRecBase
{
    public int Value { get; set; }
    public ConcRecBase? Next { get; set; }
}

public class ConcRecLeaf : ConcRecBase
{
    public string Extra { get; set; } = "";
}

public class ElemNode
{
    public string Name { get; set; } = "";
    public List<ElemNode> Items { get; set; } = new();
    public Dictionary<string, ElemNode> Map { get; set; } = new();
}

public class ListNode
{
    public string Name { get; set; } = "";
    public List<ListNode> Items { get; set; } = new();
}

/// <summary>
/// Recursive DTO support: self-referencing and mutually-referencing types must
/// compile and round-trip. A data-level object graph cycle must fail loudly
/// (depth limit), never stack-overflow.
/// </summary>
public class RecursiveTypeTests
{
    [Test]
    public async Task Json_SelfRecursive_RoundTripsToDepth3()
    {
        var root = new TreeNode
        {
            Value = 1,
            Child = new TreeNode
            {
                Value = 2,
                Child = new TreeNode { Value = 3 },
            },
        };
        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<TreeNode>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Value).IsEqualTo(1);
        await Assert.That(back.Child!.Value).IsEqualTo(2);
        await Assert.That(back.Child.Child!.Value).IsEqualTo(3);
        await Assert.That(back.Child.Child.Child).IsNull();
    }

    [Test]
    public async Task Json_MutualRecursion_RoundTrips()
    {
        var a = new MutualA
        {
            Name = "a",
            B = new MutualB
            {
                N = 7,
                A = new MutualA { Name = "deep" },
            },
        };
        var json = JsonSerializer.Serialize(a);
        var back = JsonSerializer.Deserialize<MutualA>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Name).IsEqualTo("a");
        await Assert.That(back.B!.N).IsEqualTo(7);
        await Assert.That(back.B.A!.Name).IsEqualTo("deep");
    }

    [Test]
    public async Task AllFormats_ShallowRecursive_Serialize()
    {
        var node = new TreeNode { Value = 5 };

        await Assert.That(JsonSerializer.Serialize(node)).Contains("\"Value\":5");
        await Assert.That(TomlSerializer.Serialize(node)).Contains("Value");
        await Assert.That(YamlSerializer.Serialize(node)).Contains("Value");
        await Assert.That(IniSerializer.Serialize(node)).Contains("Value");
        await Assert.That(MsgPackSerializer.SerializeToUtf8Bytes(node).Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Json_RecursiveDictValue_RoundTrips()
    {
        var root = new DictNode
        {
            Name = "root",
            Children = new Dictionary<string, DictNode>
            {
                ["a"] = new DictNode
                {
                    Name = "a",
                    Children = new Dictionary<string, DictNode>
                    {
                        ["b"] = new DictNode { Name = "b" },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<DictNode>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Children["a"].Name).IsEqualTo("a");
        await Assert.That(back.Children["a"].Children["b"].Name).IsEqualTo("b");
    }

    [Test]
    public async Task MsgPack_RecursiveDictValue_RoundTrips()
    {
        var root = new DictNode
        {
            Name = "root",
            Children = new Dictionary<string, DictNode> { ["a"] = new DictNode { Name = "a" } },
        };

        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(root);
        var back = MsgPackSerializer.Deserialize<DictNode>(bytes);

        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Children["a"].Name).IsEqualTo("a");
    }

    [Test]
    public async Task Json_RecursiveListElement_RoundTrips()
    {
        var root = new ListNode
        {
            Name = "root",
            Items = new List<ListNode> { new ListNode { Name = "child" } },
        };

        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<ListNode>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.Items[0].Name).IsEqualTo("child");
    }

    [Test]
    public async Task Json_ListAndDictElement_RecursiveRef_RoundTrips()
    {
        var root = new ElemNode
        {
            Name = "r",
            Items = new List<ElemNode> { new ElemNode { Name = "i" } },
            Map = new Dictionary<string, ElemNode> { ["m"] = new ElemNode { Name = "mm" } },
        };

        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<ElemNode>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back!.Items[0]!.Name).IsEqualTo("i");
        await Assert.That(back.Map["m"]!.Name).IsEqualTo("mm");
    }

    [Test]
    public async Task Json_AbstractPolyRecursive_NestedDerivedTypeSurvives()
    {
        PolyRecAbstract root = new PolyRecLeaf
        {
            Value = 1,
            Extra = "root",
            Next = new PolyRecLeaf { Value = 2, Extra = "leaf" },
        };

        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<PolyRecAbstract>(Encoding.UTF8.GetBytes(json));

        await Assert.That(back).IsTypeOf<PolyRecLeaf>();
        await Assert.That(back!.Next).IsTypeOf<PolyRecLeaf>();
        await Assert.That(((PolyRecLeaf)back.Next!).Extra).IsEqualTo("leaf");
    }

    [Test]
    public async Task MsgPack_AbstractPolyRecursive_NestedDerivedTypeSurvives()
    {
        PolyRecAbstract root = new PolyRecLeaf
        {
            Value = 1,
            Extra = "root",
            Next = new PolyRecLeaf { Value = 2, Extra = "leaf" },
        };

        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(root);
        var back = MsgPackSerializer.Deserialize<PolyRecAbstract>(bytes);

        await Assert.That(back).IsTypeOf<PolyRecLeaf>();
        await Assert.That(((PolyRecLeaf)back!.Next!).Extra).IsEqualTo("leaf");
    }

    [Test]
    public async Task Json_ConcretePolyRecursive_NestedDerivedTypeSurvives()
    {
        ConcRecBase root = new ConcRecLeaf
        {
            Value = 1,
            Extra = "root",
            Next = new ConcRecLeaf { Value = 2, Extra = "leaf" },
        };

        var json = JsonSerializer.Serialize(root);
        var back = JsonSerializer.Deserialize<ConcRecBase>(Encoding.UTF8.GetBytes(json));

        await Assert.That(((ConcRecLeaf)back!.Next!).Extra).IsEqualTo("leaf");
    }

    [Test]
    public async Task Json_Streaming_AbstractPolyRecursive_NestedDerivedTypeSurvives()
    {
        PolyRecAbstract root = new PolyRecLeaf
        {
            Value = 1,
            Extra = "root",
            Next = new PolyRecLeaf { Value = 2, Extra = "leaf" },
        };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(root));

        foreach (var chunk in new[] { 1, 7, 64 })
        {
            using var stream = new SkChunkedStream(json, chunk);
            var back = await JsonSerializer.DeserializeFromStreamAsync<PolyRecAbstract>(stream);
            await Assert
                .That(((PolyRecLeaf)back!.Next!).Extra)
                .IsEqualTo("leaf")
                .Because($"chunk={chunk}");
        }
    }

    [Test]
    public async Task MsgPack_SelfRecursiveObjectMember_RoundTrips()
    {
        var root = new TreeNode
        {
            Value = 1,
            Child = new TreeNode { Value = 2 },
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(root);
        var back = MsgPackSerializer.Deserialize<TreeNode>(bytes);

        await Assert.That(back!.Value).IsEqualTo(1);
        await Assert.That(back.Child!.Value).IsEqualTo(2);
    }

    [Test]
    public async Task Json_CyclicObjectGraph_FailsLoudly()
    {
        var node = new TreeNode { Value = 1 };
        node.Child = node; // data-level cycle (not just a type-level one)

        Exception? error = null;
        try
        {
            JsonSerializer.Serialize(node);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await Assert.That(error).IsNotNull();
    }
}
