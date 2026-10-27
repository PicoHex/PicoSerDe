// TDD (yaml-empty-flow): an empty FLOW MAPPING `{}` is legal YAML, but the reader
// treated the `{` as the first byte of a block mapping KEY, scanned the line for a
// `:` that a flow mapping does not need, and threw
// "FormatException: Invalid YAML mapping line: expected ':'."
//
// `[]` already has a flow branch (see EmptySequenceTests); this pins the mapping
// half of the symmetry. The fix routes a line-leading `{` into the existing flow
// mapping reader, so `{}` produces an empty mapping (the POCO keeps its defaults)
// instead of an exception. The negative tests at the bottom keep the fix honest:
// malformed flow must still fail loudly rather than be swallowed.
namespace PicoYaml.Tests;

/// <summary>POCO with non-default initializers, so "empty mapping" is observable.</summary>
public class FlowDoc
{
    public string Name { get; set; } = "untouched";
    public int Count { get; set; } = 7;
}

/// <summary>A nested object member that may itself be an empty flow mapping.</summary>
public class FlowDocHolder
{
    public FlowDoc V { get; set; } = new();
    public int Tail { get; set; } = 3;
}

public class EmptyFlowMappingTests
{
    // ── Top-level (document root) ──

    [Test]
    public async Task RootEmptyFlowMapping_ReadsAsDefaultObject()
    {
        var back = YamlSerializer.Deserialize<FlowDoc>(Encoding.UTF8.GetBytes("{}"));
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("untouched");
        await Assert.That(back.Count).IsEqualTo(7);
    }

    [Test]
    public async Task RootEmptyFlowMapping_WithSpaceAndTrailingNewline_ReadsAsDefaultObject()
    {
        foreach (var text in new[] { "{ }", "{}\n", " {}\n" })
        {
            var back = YamlSerializer.Deserialize<FlowDoc>(Encoding.UTF8.GetBytes(text));
            await Assert.That(back).IsNotNull().Because("input=" + text);
            await Assert.That(back!.Name).IsEqualTo("untouched").Because("input=" + text);
            await Assert.That(back.Count).IsEqualTo(7).Because("input=" + text);
        }
    }

    [Test]
    public async Task EmptyFlowMappingDictionaryMember_ReadsAsEmptyDictionary()
    {
        // A top-level Dictionary<K,V> has no generated serializer (pre-existing generator
        // limitation), so the dictionary rides as a member - the shape a config file uses.
        var back = YamlSerializer.Deserialize<DictHolder>(
            Encoding.UTF8.GetBytes("M: {}\nTail: 3\n")
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.M).IsNotNull();
        await Assert.That(back.M.Count).IsEqualTo(0);
        await Assert.That(back.Tail).IsEqualTo(3);
    }

    /// <summary>
    /// Not a `{}` special case: the same line-leading branch feeds the existing flow
    /// mapping reader, so a populated document root reads its pairs too.
    /// </summary>
    [Test]
    public async Task RootNonEmptyFlowMapping_ReadsEveryPair()
    {
        var back = YamlSerializer.Deserialize<FlowDoc>(
            Encoding.UTF8.GetBytes("{Name: x, Count: 2}")
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("x");
        await Assert.That(back.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RootNonEmptyFlowSequence_ReadsEveryItem()
    {
        var back = YamlSerializer.Deserialize<List<int>>(Encoding.UTF8.GetBytes("[1, 2]"));
        await Assert.That(back).IsNotNull();
        await Assert.That(back!).IsEquivalentTo([1, 2]);
    }

    [Test]
    public async Task EmptyFlowMapping_AfterDocumentStartMarker_ReadsAsDefaultObject()
    {
        var back = YamlSerializer.Deserialize<FlowDoc>(Encoding.UTF8.GetBytes("---\n{}\n"));
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("untouched");
        await Assert.That(back.Count).IsEqualTo(7);
    }

    /// <summary>The flow collection may also sit on its own indented line under a key.</summary>
    [Test]
    public async Task EmptyFlowMappingOnItsOwnIndentedLine_ReadsAsDefaultObject()
    {
        var back = YamlSerializer.Deserialize<FlowDocHolder>(
            Encoding.UTF8.GetBytes("V:\n  {}\nTail: 3\n")
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V).IsNotNull();
        await Assert.That(back.V.Name).IsEqualTo("untouched");
        await Assert.That(back.Tail).IsEqualTo(3);
    }

    // ── Member value ──

    [Test]
    public async Task EmptyFlowMappingMemberValue_ReadsAsDefaultObject_AndKeepsFollowingKeys()
    {
        var back = YamlSerializer.Deserialize<FlowDocHolder>(
            Encoding.UTF8.GetBytes("V: {}\nTail: 3\n")
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V).IsNotNull();
        await Assert.That(back.V.Name).IsEqualTo("untouched");
        await Assert.That(back.V.Count).IsEqualTo(7);
        await Assert.That(back.Tail).IsEqualTo(3);
    }

    // ── Symmetry with the empty flow sequence `[]` ──

    [Test]
    public async Task RootEmptyFlowSequence_ReadsAsEmptyList()
    {
        var back = YamlSerializer.Deserialize<List<int>>(Encoding.UTF8.GetBytes("[]"));
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyFlowSequenceMemberValue_ReadsAsEmptyList()
    {
        var back = YamlSerializer.Deserialize<EmptySeqInts>(
            Encoding.UTF8.GetBytes("D: []\nCount: 5\n")
        );
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.D).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// Both flow-collection spellings live on the same key line: the empty mapping and
    /// the empty sequence must not be confused with each other or with a scalar.
    /// </summary>
    [Test]
    public async Task EmptyFlowMappingAndEmptyFlowSequence_OnTheSameKeyLine_StayDistinct()
    {
        var doc = YamlSerializer.Deserialize<BothEmpty>(
            Encoding.UTF8.GetBytes("Map: {}\nSeq: []\nTail: 4\n")
        );
        await Assert.That(doc).IsNotNull();
        await Assert.That(doc!.Map).IsNotNull();
        await Assert.That(doc.Map.Name).IsEqualTo("untouched");
        await Assert.That(doc.Seq).IsEmpty();
        await Assert.That(doc.Tail).IsEqualTo(4);
    }

    // ── Streaming path: the same reader, resumed per chunk ──

    [Test]
    public async Task RootEmptyFlowMapping_StreamingPath_ReadsAsDefaultObject()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        var back = await YamlSerializer.DeserializeFromStreamAsync<FlowDoc>(stream);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Name).IsEqualTo("untouched");
        await Assert.That(back.Count).IsEqualTo(7);
    }

    // ── Negative guards: the fix must not swallow malformed flow ──

    [Test]
    public async Task UnterminatedFlowMapping_StillThrows()
    {
        await Assert
            .That(() => YamlSerializer.Deserialize<FlowDoc>(Encoding.UTF8.GetBytes("{Name: x")))
            .Throws<FormatException>();
    }

    [Test]
    public async Task FlowMappingMissingColon_StillThrows()
    {
        await Assert
            .That(() => YamlSerializer.Deserialize<FlowDoc>(Encoding.UTF8.GetBytes("{Name x}")))
            .Throws<FormatException>();
    }
}

/// <summary>A POCO whose members are the two empty flow collections.</summary>
public class BothEmpty
{
    public FlowDoc Map { get; set; } = new();
    public List<int> Seq { get; set; } = new();
    public int Tail { get; set; } = 4;
}

/// <summary>A dictionary member that may be an empty flow mapping.</summary>
public class DictHolder
{
    public Dictionary<string, int> M { get; set; } = new();
    public int Tail { get; set; } = 3;
}
