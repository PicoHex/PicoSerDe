using System.Text;
using PicoYaml;

namespace PicoYaml.Tests;

[PicoYamlSerializable]
public partial class FlowIntDto
{
    public string K { get; set; } = "";

    public List<int> G { get; set; } = new();
}

[PicoYamlSerializable]
public partial class FlowStringDto
{
    public string K { get; set; } = "";

    public List<string> G { get; set; } = new();
}

[PicoYamlSerializable]
public partial class FlowSiblingDto
{
    public string K { get; set; } = "";

    public List<int> G { get; set; } = new();

    public int Count { get; set; }
}

/// <summary>
/// Flow sequences (<c>[1, 2, 3]</c>) were never read: the reader had a branch for
/// flow <i>mappings</i> (<c>{...}</c>) but none for flow <i>sequences</i>, so the
/// bracketed text fell through to the scalar path. A member written that way
/// deserialized to an empty list — silent data loss, and worse than the
/// <c>FormatException</c> the multi-line forms threw.
///
/// The reader now tokenizes <c>[...]</c> into the same shape a block sequence
/// produces (<c>ObjectStart</c>, one <c>String</c> per element, <c>ObjectEnd</c>),
/// so the generated deserializer needs no change.
/// </summary>
public class FlowSequenceTests
{
    private static FlowIntDto Ints(string yaml) =>
        YamlSerializer.Deserialize<FlowIntDto>(Encoding.UTF8.GetBytes(yaml))!;

    [Test]
    public async Task SingleLine_ReadsAllElements()
    {
        var d = Ints("K: v\nG: [1, 2, 3]\n");
        await Assert.That(d.K).IsEqualTo("v");
        await Assert.That(d.G.Count).IsEqualTo(3).Because("flow items are real elements");
        await Assert.That(d.G).IsEquivalentTo(new List<int> { 1, 2, 3 });
    }

    [Test]
    public async Task EmptyFlow_IsAnEmptyList()
    {
        var d = Ints("K: v\nG: []\n");
        await Assert.That(d.G).IsNotNull().Because("[] must not read as null");
        await Assert.That(d.G.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TrailingComma_IsTolerated()
    {
        var d = Ints("K: v\nG: [1, 2,]\n");
        await Assert.That(d.G.Count).IsEqualTo(2);
    }

    [Test]
    public async Task NewlinesInsideBrackets_AreAccepted()
    {
        var afterComma = Ints("K: v\nG: [1,\n  2, 3]\n");
        await Assert.That(afterComma.G.Count).IsEqualTo(3).Because("a line break after ','");

        var afterBracket = Ints("K: v\nG: [\n  1, 2\n]\n");
        await Assert.That(afterBracket.G.Count).IsEqualTo(2).Because("a line break after '['");
    }

    [Test]
    public async Task SiblingMemberAfterFlowSequence_Survives()
    {
        var d = YamlSerializer.Deserialize<FlowSiblingDto>(
            Encoding.UTF8.GetBytes("K: v\nG: [1, 2, 3]\nCount: 5\n")
        )!;
        await Assert.That(d.G.Count).IsEqualTo(3);
        await Assert
            .That(d.Count)
            .IsEqualTo(5)
            .Because("the closing ']' must not eat the next key");
    }

    [Test]
    public async Task Strings_QuotesAreStrippedAndCommasInsideQuotesAreNotSplits()
    {
        var d = YamlSerializer.Deserialize<FlowStringDto>(
            Encoding.UTF8.GetBytes("K: v\nG: [a, \"b c\", \"d,e\", 'f,g']\n")
        )!;
        await Assert
            .That(d.G)
            .IsEquivalentTo(new List<string> { "a", "b c", "d,e", "f,g" })
            .Because("quoted commas belong to the element");
    }

    /// <summary>
    /// A <c>null</c> element inside a flow sequence is a null literal, not the string
    /// "null". <c>List&lt;int?&gt;</c> is out of scope: YAML drops that member by design
    /// (README Format-limits), so the shape under test is a string list.
    /// </summary>
    [Test]
    public async Task NullElements_AreNotTheStringNull()
    {
        var d = YamlSerializer.Deserialize<FlowStringDto>(
            Encoding.UTF8.GetBytes("K: v\nG: [a, null, c]\n")
        )!;
        await Assert.That(d.G.Count).IsEqualTo(3);
        await Assert.That(d.G[0]).IsEqualTo("a");
        await Assert.That(d.G[1] is null).IsTrue().Because("a bare null is a null element");
        await Assert.That(d.G[2]).IsEqualTo("c");
    }

    /// <summary>
    /// The block form keeps working exactly as before — flow support must not have
    /// touched F5's nested-sequence reading.
    /// </summary>
    [Test]
    public async Task BlockSequence_StillWorks()
    {
        var d = Ints("K: v\nG:\n  - 1\n  - 2\n");
        await Assert.That(d.G.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Nested flow (<c>[[1, 2], [3]]</c>) is not supported yet. It must fail loudly
    /// rather than silently produce empty inner lists — the whole point of this
    /// change is that flow sequences never lose data quietly.
    /// </summary>
    [Test]
    public async Task NestedFlow_FailsLoudly()
    {
        const string yaml = "K: v\nGrid: [[1, 2], [3]]\n";
        await Assert
            .That(() => YamlSerializer.Deserialize<FlowNestedDto>(Encoding.UTF8.GetBytes(yaml)))
            .Throws<FormatException>();
    }

    [Test]
    public async Task RoundTrip_BlockWrittenOutputStillReads()
    {
        var original = new FlowIntDto
        {
            K = "v",
            G = new List<int> { 4, 5, 6 },
        };
        var text = YamlSerializer.Serialize(original);
        var back = YamlSerializer.Deserialize<FlowIntDto>(Encoding.UTF8.GetBytes(text))!;
        await Assert.That(back.G).IsEquivalentTo(original.G);
    }
}

[PicoYamlSerializable]
public partial class FlowNestedDto
{
    public string K { get; set; } = "";

    public List<List<int>> Grid { get; set; } = new();
}
