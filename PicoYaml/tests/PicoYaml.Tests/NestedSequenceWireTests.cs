// F5 review I-1 / I-2: the wire form of a nested sequence must not depend on this
// writer's trailing space, and a dash that carries only a comment or an alias must not
// be mistaken for the "the block below is my value" marker.
//
// Wire shape pinned here (a bare dash, no trailing space — what every other YAML
// implementation writes, and what YamlDotNet accepts and emits):
//
//     Grid:
//     -
//       - 1
//       - 2
//     -
//       - 3
//     Count: 5
namespace PicoYaml.Tests;

public class NestedSequenceWireTests
{
    private const string HandWrittenBareDash = "Grid:\n-\n  - 1\n  - 2\n-\n  - 3\nCount: 5\n";

    private static string Text(string yaml) => yaml.Replace("\r\n", "\n");

    // ── I-1: bare dash is readable, and the writer no longer adds a trailing space ──

    /// <summary>
    /// A hand-written constant (not a Serialize product): the item dash stands alone on
    /// its line, so the reader has to accept "-" followed by a line break.
    /// </summary>
    [Test]
    public async Task HandWrittenBareDash_ReadsTheNestedGrid()
    {
        var back = YamlSerializer.Deserialize<NestedSeqIntHolder>(
            Encoding.UTF8.GetBytes(HandWrittenBareDash)
        );
        await Assert.That(back!.Grid.Count).IsEqualTo(2).Because("wire=" + HandWrittenBareDash);
        await Assert.That(back.Grid[0]).IsEquivalentTo([1, 2]);
        await Assert.That(back.Grid[1]).IsEquivalentTo([3]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// The writer must produce the same trailing-space-free shape; a line ending in a
    /// space is the tell that the wire form is only readable by its own reader.
    /// </summary>
    [Test]
    public async Task Writer_WritesBareDashesWithoutTrailingSpace()
    {
        var text = Text(YamlSerializer.Serialize(new NestedSeqIntHolder()));
        await Assert.That(text).IsEqualTo(HandWrittenBareDash).Because("actual=" + text);
        foreach (var line in text.Split('\n'))
            await Assert.That(line.EndsWith(' ')).IsFalse().Because("line=[" + line + "]");
    }

    /// <summary>Cross-implementation check of the emitted (bare dash) document.</summary>
    [Test]
    public async Task BareDashOutput_IsReadBackByYamlDotNet()
    {
        var text = Text(YamlSerializer.Serialize(new NestedSeqIntHolder()));
        var yaml = new DeserializerBuilder().Build().Deserialize<NestedSeqIntHolder>(text);
        await Assert.That(yaml.Grid.Count).IsEqualTo(2).Because("yaml=" + text);
        await Assert.That(yaml.Grid[0]).IsEquivalentTo([1, 2]);
        await Assert.That(yaml.Grid[1]).IsEquivalentTo([3]);
        await Assert.That(yaml.Count).IsEqualTo(5);
    }

    // ── I-2: "- # comment" and "- *alias" are null items, never nested blocks ──

    /// <summary>
    /// A dash that carries only a comment has no inline value, so the item's value is
    /// whatever block follows (here: none) — it must read as an empty inner sequence
    /// instead of throwing "Expected a nested sequence".
    /// </summary>
    [Test]
    public async Task CommentOnlyDashItem_ReadsAsEmptyInnerSequence()
    {
        var doc = "Grid:\n- # note\n-\n  - 1\nCount: 5\n";
        var back = YamlSerializer.Deserialize<NestedSeqIntHolder>(Encoding.UTF8.GetBytes(doc));
        await Assert.That(back!.Grid.Count).IsEqualTo(2).Because("wire=" + doc);
        await Assert.That(back.Grid[0]).IsEmpty();
        await Assert.That(back.Grid[1]).IsEquivalentTo([1]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// The reader must not report the alias item as a nested-sequence item: the alias
    /// carries no inline scalar, so the item keeps the "empty value" semantics.
    /// </summary>
    [Test]
    public async Task AliasDashItem_IsNotANestedSequenceItem()
    {
        // An anchor on a mapping, then an alias as a sequence item.
        var tokens = Tokens("A: &m\n  X: 1\nGrid:\n- *m\n-\n  - 1\n"u8);
        var grid = tokens.FindIndex(t => t.Kind == TokenType.PropertyName && t.Value == "Grid");
        await Assert.That(grid).IsGreaterThan(-1).Because("tokens=" + string.Join(",", tokens));

        var aliasItem = tokens[grid + 1];
        await Assert.That(aliasItem.Kind).IsEqualTo(TokenType.String).Because("item=" + aliasItem);
        await Assert.That(aliasItem.Value).IsEqualTo("").Because("item=" + aliasItem);
        await Assert.That(aliasItem.IsNested).IsFalse().Because("item=" + aliasItem);
        await Assert.That(aliasItem.IsNull).IsTrue().Because("empty value == null item");
    }

    /// <summary>
    /// End-to-end: an alias item inside a nested sequence is a null item, not a
    /// structural error — reading it must not throw.
    /// </summary>
    [Test]
    public async Task AliasDashItem_NestedGridDoesNotThrow()
    {
        var doc = "Child: &c\n  N: 3\nGrid:\n- *c\n-\n  - 1\nCount: 5\n";
        var back = YamlSerializer.Deserialize<NestedSeqAliasHolder>(Encoding.UTF8.GetBytes(doc));
        await Assert.That(back!.Child.N).IsEqualTo(3).Because("wire=" + doc);
        await Assert.That(back.Grid.Count).IsEqualTo(2).Because("wire=" + doc);
        await Assert.That(back.Grid[0]).IsEmpty();
        await Assert.That(back.Grid[1]).IsEquivalentTo([1]);
    }

    /// <summary>Token snapshot of a document, for the flag assertions above.</summary>
    private static List<TokenSnapshot> Tokens(ReadOnlySpan<byte> yaml)
    {
        var r = new YamlReader(yaml);
        var tokens = new List<TokenSnapshot>();
        while (r.Read())
            tokens.Add(
                new(
                    r.TokenType,
                    r.TokenType == TokenType.String
                        ? Encoding.UTF8.GetString(r.ValueSpan)
                        : Encoding.UTF8.GetString(r.KeySpan),
                    r.IsNestedSequenceItem,
                    r.IsNullLiteral
                )
            );
        return tokens;
    }

    private sealed record TokenSnapshot(TokenType Kind, string Value, bool IsNested, bool IsNull);
}

/// <summary>A holder whose first member is an anchor target and whose second is a grid.</summary>
public class NestedSeqAliasHolder
{
    public NestedSeqAliasChild Child { get; set; } = new();
    public List<List<int>> Grid { get; set; } = new();
}

public class NestedSeqAliasChild
{
    public int N { get; set; }
}
