// The YAML reader used to give a block sequence the SAME container token a mapping gets
// (`ObjectStart`/`ObjectEnd`), so no consumer could tell the two shapes apart from the
// token stream: `A:` and `B:` as two adjacent sequence values flattened to `0:0`, the
// `B` key vanishing from the path (reported by a downstream flattener).
//
// Contract pinned here: a sequence — block or flow — is delimited by
// `ArrayStart`/`ArrayEnd`; a mapping by `ObjectStart`/`ObjectEnd`. Sequence ITEMS stay
// `String` tokens (one per item line, plus the bare-dash flag for a nested block), which
// is what the generated serializer reads either way.
using System.Text;
using PicoYaml;

namespace PicoYaml.Tests;

public class YamlSequenceTokenKindTests
{
    private sealed record Snap(TokenType Kind, int Depth, string Text, bool BareDash);

    /// <summary>Walks a document and returns one snapshot per token.</summary>
    private static List<Snap> Tokens(string yaml)
    {
        var list = new List<Snap>();
        var r = new YamlReader(Encoding.UTF8.GetBytes(yaml));
        while (r.Read())
        {
            var text =
                r.TokenType == TokenType.PropertyName ? Encoding.UTF8.GetString(r.KeySpan)
                : r.ValueSpan.IsEmpty ? ""
                : Encoding.UTF8.GetString(r.ValueSpan);
            list.Add(new Snap(r.TokenType, r.Depth, text, r.IsNestedSequenceItem));
        }
        return list;
    }

    /// <summary>`PropertyName(A) ArrayStart String(1) ...` — the failure message.</summary>
    private static string Dump(List<Snap> tokens) =>
        string.Join(
            " ",
            tokens.Select(t =>
                t.Text.Length == 0 ? t.Kind.ToString() : $"{t.Kind}({t.Text.Replace("\n", "\\n")})"
            )
        );

    /// <summary>Asserts the exact container+item shape of a document.</summary>
    private static void AssertShape(string yaml, string expected)
    {
        var actual = Dump(Tokens(yaml));
        if (actual != expected)
            throw new Exception(
                $"yaml={yaml.Replace("\n", "\\n")}\n  expected: {expected}\n  actual:   {actual}"
            );
    }

    // ── A sequence VALUE is an array, the mapping beside it stays an object ──

    [Test]
    public async Task MappingValueSequence_IsArrayDelimited_AndSiblingMappingIsAnObject()
    {
        AssertShape(
            "A:\n  - 1\n  - 2\nB:\n  x: 1\n",
            "PropertyName(A) ArrayStart String(1) String(2) ArrayEnd "
                + "PropertyName(B) ObjectStart PropertyName(x) ObjectEnd"
        );
        await Task.CompletedTask;
    }

    /// <summary>
    /// The reported downstream symptom, as a token stream: two adjacent sequence values.
    /// Both keys must be visible, and each key must be followed by its OWN array pair —
    /// not by one anonymous object pair the consumer has to guess about.
    /// </summary>
    [Test]
    public async Task TwoAdjacentSequenceValues_KeepBothKeys_EachWithItsOwnArray()
    {
        const string yaml = "A:\n  - 1\n  - 2\nB:\n  - 3\n";
        var tokens = Tokens(yaml);
        var actual = Dump(tokens);
        await Assert
            .That(actual)
            .IsEqualTo(
                "PropertyName(A) ArrayStart String(1) String(2) ArrayEnd "
                    + "PropertyName(B) ArrayStart String(3) ArrayEnd"
            )
            .Because("tokens=" + actual);

        var keys = tokens.Where(t => t.Kind == TokenType.PropertyName).Select(t => t.Text).ToList();
        await Assert.That(keys).IsEquivalentTo(new List<string> { "A", "B" });
    }

    /// <summary>A flat sequence (dashes at the key's own indentation) is delimited too.</summary>
    [Test]
    public async Task FlatSequenceAtKeyIndentation_IsArrayDelimited()
    {
        AssertShape(
            "A:\n- 1\n- 2\nB: 7\n",
            "PropertyName(A) ArrayStart String(1) String(2) ArrayEnd PropertyName(B)"
        );
        await Task.CompletedTask;
    }

    /// <summary>A document whose root is a sequence: one array, no phantom object.</summary>
    [Test]
    public async Task TopLevelBlockSequence_IsArrayDelimited()
    {
        AssertShape("- 1\n- 2\n- 3\n", "ArrayStart String(1) String(2) String(3) ArrayEnd");
        await Task.CompletedTask;
    }

    [Test]
    public async Task TopLevelSequenceOfMappings_IsArrayDelimited_AndItemsStayObjects()
    {
        AssertShape(
            "- name: a\n  v: 1\n- name: b\n  v: 2\n",
            "ArrayStart String(name: a) ObjectStart PropertyName(v) ObjectEnd "
                + "String(name: b) ObjectStart PropertyName(v) ObjectEnd ArrayEnd"
        );
        await Task.CompletedTask;
    }

    /// <summary>
    /// A sequence inside a sequence, in the wire form this writer emits: a bare dash item
    /// whose value is the indented block below it. Two array pairs, nested.
    /// </summary>
    [Test]
    public async Task NestedBlockSequence_OpensAnArrayPerLevel()
    {
        AssertShape(
            "Grid:\n-\n  - 1\n  - 2\n-\n  - 3\nCount: 5\n",
            "PropertyName(Grid) ArrayStart String ArrayStart String(1) String(2) ArrayEnd "
                + "String ArrayStart String(3) ArrayEnd ArrayEnd PropertyName(Count)"
        );
        await Task.CompletedTask;
    }

    /// <summary>A sequence of mappings under a key: the wrapper is an array, each item's body an object.</summary>
    [Test]
    public async Task SequenceOfMappingsUnderKey_WrapperIsAnArray_ItemBodyIsAnObject()
    {
        AssertShape(
            "Items:\n  - name: a\n    port: 1\n",
            "PropertyName(Items) ArrayStart String(name: a) "
                + "ObjectStart PropertyName(port) ObjectEnd ArrayEnd"
        );
        await Task.CompletedTask;
    }

    // ── Flow collections: the same distinction, and recorded as it now stands ──

    [Test]
    public async Task FlowSequence_IsArrayDelimited()
    {
        AssertShape(
            "A: [1, 2, 3]\n",
            "PropertyName(A) ArrayStart String(1) String(2) String(3) ArrayEnd"
        );
        AssertShape("A: []\n", "PropertyName(A) ArrayStart ArrayEnd");
        await Task.CompletedTask;
    }

    [Test]
    public async Task FlowMapping_StaysAnObject()
    {
        AssertShape(
            "A: {x: 1, y: 2}\n",
            "PropertyName(A) ObjectStart PropertyName(x) PropertyName(y) ObjectEnd"
        );
        await Task.CompletedTask;
    }

    // ── Multi-document: each document's sequence is closed before the next opens ──

    [Test]
    public async Task MultiDocument_EachSequenceDocumentIsClosed()
    {
        AssertShape(
            "---\n- 1\n- 2\n---\n- 3\n",
            "ArrayStart String(1) String(2) ArrayEnd ArrayStart String(3) ArrayEnd"
        );
        await Task.CompletedTask;
    }

    /// <summary>
    /// The pairing half: every <c>ArrayStart</c> has its own <c>ArrayEnd</c>, every
    /// <c>ObjectStart</c> its own <c>ObjectEnd</c>, and the walk ends at <c>Depth == 0</c>
    /// — for both reader shapes (span and sequence), since the indentation stack is
    /// maintained twice.
    /// </summary>
    [Test]
    [Arguments("A:\n  - 1\n  - 2\nB:\n  - 3\n")]
    [Arguments("A:\n- 1\n- 2\nB: 7\n")]
    [Arguments("- 1\n- 2\n")]
    [Arguments("- name: a\n  v: 1\n")]
    [Arguments("Items:\n  - name: a\n    tags:\n      - a\n")]
    [Arguments("Grid:\n-\n  - 1\n  - 2\n-\n  - 3\nCount: 5\n")]
    [Arguments("A: [1, 2]\n")]
    [Arguments("Root:\n  A:\n    - 1\n    - 2\n  B:\n    - 3\n")]
    public async Task EveryContainerCloses_AndEndsAtDepthZero(string yaml)
    {
        var span = Pairing(Encoding.UTF8.GetBytes(yaml), useSequence: false);
        var seq = Pairing(Encoding.UTF8.GetBytes(yaml), useSequence: true);
        await Assert
            .That($"span: {span}|seq: {seq}")
            .IsEqualTo("span: |seq: ")
            .Because("yaml=" + yaml);
    }

    /// <summary>
    /// The reported downstream symptom, reproduced end to end: a generic consumer that only
    /// has the token stream builds a path per leaf. With both sequence values opening an
    /// <c>ObjectStart</c> the two paths collided and the `B` key vanished (`0:0`). With the
    /// array/object distinction each leaf keeps its own key.
    /// </summary>
    [Test]
    public async Task GenericConsumer_KeepsBothKeys_WhenTheDistinctionIsOnTheWire()
    {
        var paths = Flatten("A:\n  - 1\n  - 2\nB:\n  - 3\n");
        var actual = string.Join(" ", paths);
        await Assert.That(actual).IsEqualTo("A[0]=1 A[1]=2 B[0]=3").Because("paths=" + actual);
    }

    /// <summary>A minimal format-agnostic flattener: containers carry a name, arrays count.</summary>
    private static List<string> Flatten(string yaml)
    {
        var containers = new List<(string Name, int Next)>();
        var paths = new List<string>();
        string? pending = null;
        var r = new YamlReader(Encoding.UTF8.GetBytes(yaml));
        while (r.Read())
        {
            switch (r.TokenType)
            {
                case TokenType.PropertyName:
                    pending = Encoding.UTF8.GetString(r.KeySpan);
                    if (!r.ValueSpan.IsEmpty)
                    {
                        paths.Add(
                            Join(containers, pending) + "=" + Encoding.UTF8.GetString(r.ValueSpan)
                        );
                        pending = null;
                    }
                    break;
                case TokenType.ArrayStart:
                case TokenType.ObjectStart:
                    containers.Add((pending ?? "$", 0));
                    pending = null;
                    break;
                case TokenType.ArrayEnd:
                case TokenType.ObjectEnd:
                    containers.RemoveAt(containers.Count - 1);
                    break;
                case TokenType.None:
                    break;
                default:
                {
                    var text = Encoding.UTF8.GetString(r.ValueSpan);
                    if (containers.Count > 0 && r.Depth > 0 && containers[^1].Next >= 0)
                    {
                        var top = containers[^1];
                        var indexed = top with { Next = top.Next + 1 };
                        containers[^1] = indexed;
                        paths.Add(
                            Join(containers.GetRange(0, containers.Count - 1), top.Name)
                                + "["
                                + top.Next
                                + "]="
                                + text
                        );
                    }
                    else
                    {
                        paths.Add(Join(containers, pending) + "=" + text);
                        pending = null;
                    }
                    break;
                }
            }
        }
        return paths;
    }

    private static string Join(List<(string Name, int Next)> containers, string? leaf)
    {
        var sb = new StringBuilder();
        foreach (var c in containers)
        {
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(c.Name);
        }
        if (leaf is not null)
        {
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(leaf);
        }
        return sb.ToString();
    }

    // Sync helper: ref structs cannot cross into an async method.
    private static string Pairing(byte[] bytes, bool useSequence)
    {
        var reader = useSequence
            ? new YamlReader(new System.Buffers.ReadOnlySequence<byte>(bytes))
            : new YamlReader(bytes);
        int arrOpen = 0,
            arrClose = 0,
            objOpen = 0,
            objClose = 0,
            negativeAt = -1,
            tokens = 0;
        while (reader.Read())
        {
            if (reader.Depth < 0 && negativeAt < 0)
                negativeAt = tokens;
            switch (reader.TokenType)
            {
                case TokenType.ArrayStart:
                    arrOpen++;
                    break;
                case TokenType.ArrayEnd:
                    arrClose++;
                    break;
                case TokenType.ObjectStart:
                    objOpen++;
                    break;
                case TokenType.ObjectEnd:
                    objClose++;
                    break;
            }
            tokens++;
        }

        var problems = new List<string>();
        if (tokens == 0)
            problems.Add("no tokens");
        if (arrOpen != arrClose)
            problems.Add($"ArrayStart {arrOpen} != ArrayEnd {arrClose}");
        if (objOpen != objClose)
            problems.Add($"ObjectStart {objOpen} != ObjectEnd {objClose}");
        if (reader.Depth != 0)
            problems.Add($"end Depth {reader.Depth} != 0");
        if (negativeAt >= 0)
            problems.Add($"Depth went negative at token {negativeAt}");
        return string.Join("; ", problems);
    }
}
