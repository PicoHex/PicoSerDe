namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The whole extended-collection FAMILY (D-2), not just the <c>HashSet</c> the matrix
/// corpus measures: <c>HashSet</c>, <c>Queue</c>, <c>Stack</c>, <c>LinkedList</c>,
/// <c>ImmutableArray</c>, <c>Memory</c>, <c>ReadOnlyMemory</c>.
/// <para>
/// One decision point (<see cref="PicoSerDe.Gen.Decision.ExtendedKindResolves"/>) covers all
/// seven kinds, so a table flip promises all seven — which is exactly why they are all
/// exercised here: the cell cannot be true for <c>HashSet</c> and false for <c>Queue</c>.
/// </para>
/// <para>
/// Order matters for five of the seven (everything but the two set-like kinds), so the
/// assertions are ORDER-SENSITIVE where the shape says so: a <c>Stack</c> that comes back
/// reversed is a silent data change, not a valid permutation.
/// </para>
/// </summary>
/// <summary>
/// The seven extended kinds in MEMBER position (spec §3 S7). Declared at namespace level on
/// purpose: the generators are usage-driven, and a nested type is not picked up.
/// </summary>
public class RvKindMemberModel
{
    public HashSet<int> H { get; set; } = [];
    public Queue<int> Q { get; set; } = [];
    public Stack<int> S { get; set; } = [];
    public LinkedList<int> L { get; set; } = [];
    public System.Collections.Immutable.ImmutableArray<int> IA { get; set; } = [];
    public Memory<int> M { get; set; } = Memory<int>.Empty;
    public ReadOnlyMemory<int> RM { get; set; } = ReadOnlyMemory<int>.Empty;
}

/// <summary>The seven extended kinds in ELEMENT position (spec §3 S8).</summary>
public class RvKindElementModel
{
    public List<HashSet<int>> H { get; set; } = [];
    public List<Queue<int>> Q { get; set; } = [];
    public List<Stack<int>> S { get; set; } = [];
    public List<LinkedList<int>> L { get; set; } = [];
    public List<System.Collections.Immutable.ImmutableArray<int>> IA { get; set; } = [];
    public List<Memory<int>> M { get; set; } = [];
    public List<ReadOnlyMemory<int>> RM { get; set; } = [];
}

public class ExtendedKindFamilyTests
{
    private static readonly string[] FourFormats = ["json", "msgpack", "toml", "yaml"];

    /// <summary>
    /// Order-preserving text of a sequence. Used instead of an array comparison on purpose:
    /// the assertion has to be able to say "the Stack came back reversed", and comparing the
    /// array objects says nothing about the items.
    /// </summary>
    private static string Join(System.Collections.IEnumerable items)
    {
        var parts = new List<string>();
        foreach (var item in items)
            parts.Add(item?.ToString() ?? "");
        return string.Join(",", parts);
    }

    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Every_extended_kind_as_a_member_round_trips(string format)
    {
        var model = new RvKindMemberModel
        {
            H = [1, 2],
            Q = new Queue<int>([3, 4]),
            S = new Stack<int>([5, 6]),
            L = new LinkedList<int>([7, 8]),
            IA = [9, 10],
            M = new Memory<int>([11, 12]),
            RM = new ReadOnlyMemory<int>([13, 14]),
        };

        var back = format switch
        {
            "json" => JsonSerializer.Deserialize<RvKindMemberModel>(
                JsonSerializer.SerializeToUtf8Bytes(model)
            )!,
            "msgpack" => MsgPackSerializer.Deserialize<RvKindMemberModel>(
                MsgPackSerializer.SerializeToUtf8Bytes(model)
            )!,
            "toml" => TomlSerializer.Deserialize<RvKindMemberModel>(
                TomlSerializer.SerializeToUtf8Bytes(model)
            )!,
            _ => YamlSerializer.Deserialize<RvKindMemberModel>(
                YamlSerializer.SerializeToUtf8Bytes(model)
            )!,
        };

        await Assert.That(back.H).IsEquivalentTo(new HashSet<int> { 1, 2 });
        await Assert.That(Join(back.Q)).IsEqualTo("3,4").Because($"{format} Queue");
        // The stack's enumeration order IS its top-to-bottom order: reversing it is a
        // lossy round trip, so pin the sequence rather than the multiset.
        await Assert.That(Join(back.S)).IsEqualTo("6,5").Because($"{format} Stack");
        await Assert.That(Join(back.L)).IsEqualTo("7,8").Because($"{format} LinkedList");
        await Assert.That(Join(back.IA)).IsEqualTo("9,10").Because($"{format} ImmutableArray");
        await Assert.That(Join(back.M.ToArray())).IsEqualTo("11,12").Because($"{format} Memory");
        await Assert
            .That(Join(back.RM.ToArray()))
            .IsEqualTo("13,14")
            .Because($"{format} ReadOnlyMemory");
    }

    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Every_extended_kind_as_an_element_round_trips(string format)
    {
        var model = new RvKindElementModel
        {
            H =
            [
                [1, 2],
                [],
            ],
            Q = [new Queue<int>([3])],
            S = [new Stack<int>([4]), new Stack<int>()],
            L = [new LinkedList<int>([5])],
            IA =
            [
                [6],
            ],
            M = [new Memory<int>([7])],
            RM = [new ReadOnlyMemory<int>([8])],
        };

        var back = format switch
        {
            "json" => JsonSerializer.Deserialize<RvKindElementModel>(
                JsonSerializer.SerializeToUtf8Bytes(model)
            )!,
            "msgpack" => MsgPackSerializer.Deserialize<RvKindElementModel>(
                MsgPackSerializer.SerializeToUtf8Bytes(model)
            )!,
            "toml" => TomlSerializer.Deserialize<RvKindElementModel>(
                TomlSerializer.SerializeToUtf8Bytes(model)
            )!,
            _ => YamlSerializer.Deserialize<RvKindElementModel>(
                YamlSerializer.SerializeToUtf8Bytes(model)
            )!,
        };

        // The empty element must survive as an element, not be skipped: that is the
        // difference between "an empty set" and "no set at all".
        await Assert.That(back.H.Count).IsEqualTo(2).Because($"{format} HashSet count");
        await Assert.That(back.H[0]).IsEquivalentTo(new HashSet<int> { 1, 2 });
        await Assert.That(back.H[1].Count).IsEqualTo(0);
        await Assert.That(Join(back.Q[0])).IsEqualTo("3").Because($"{format} Queue");
        await Assert.That(back.S.Count).IsEqualTo(2).Because($"{format} Stack count");
        await Assert.That(Join(back.S[0])).IsEqualTo("4").Because($"{format} Stack");
        await Assert.That(back.S[1].Count).IsEqualTo(0);
        await Assert.That(Join(back.L[0])).IsEqualTo("5").Because($"{format} LinkedList");
        await Assert.That(Join(back.IA[0])).IsEqualTo("6").Because($"{format} ImmutableArray");
        await Assert.That(Join(back.M[0].ToArray())).IsEqualTo("7").Because($"{format} Memory");
        await Assert
            .That(Join(back.RM[0].ToArray()))
            .IsEqualTo("8")
            .Because($"{format} ReadOnlyMemory");
    }
}
