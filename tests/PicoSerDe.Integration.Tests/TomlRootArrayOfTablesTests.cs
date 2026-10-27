namespace PicoSerDe.Integration.Tests;

// ── D-9d, the fourth `[[…]]` dispatch site (spec §4 D-9d, R2 debt sweep) ─────────────────
//
// D-9d says a foreign array-of-tables header (`[[Unknown]]`) must NOT append an element to a
// list it does not name — the document never had that value. R2 Task 6 fixed the three
// MEMBER-level emitters (buffered / streaming / polymorphic) by re-checking the header name
// before the element loop continues. The ROOT-list deserializer (`Deserialize<List<T>>`) is a
// fourth element loop and kept the old behaviour: every `ArrayStart` token was taken as
// "another element of this list", whatever the header said.
//
// The rule the tests below pin is TOML's own: a `[[name]]` header starts a new element of the
// array of tables NAMED `name`, so an element belongs to the array its header names, not to the
// array that happened to be read first. The independent judge is `tomllib` (TOML v1.0.0's own
// implementation), not our loop's shape:
//
//   tomllib.loads('[[Items]]\nName = "a"\n[[Unknown]]\n')      → {'Items': [{'Name': 'a'}], 'Unknown': [{}]}
//   tomllib.loads('[[data]]\na = 1\n[[Unknown]]\n[[data]]\nb = 2\n')
//                                           → {'data': [{'a': 1}, {'b': 2}], 'Unknown': [{}]}
//
// The first line is D-9d's count (1 — and 0 would be wrong too, the real element must survive);
// the second is C-1 (fix round 1): a foreign header in the MIDDLE costs nothing, because the
// root list has no enclosing dispatcher to come back for the next `[[data]]`.
//
// WHICH array does a root `List<T>` own? (I-1, fix round 1) The name the WRITER emits, `data`
// (`TomlSerializerGenerator` writes `tw.WriteArrayTable("data"u8)`), compared same-level — the
// header's LAST dotted segment, exactly like the member-level arms (`TextHelpers.EqLastSegment`),
// so a hand-written `[[Parent.data]]` still reads. Strict, not "whatever header came first":
// otherwise `[[Unknown]] a [[data]] b` makes `Unknown` the list's name and drops `b` — the same
// silent loss in another shape — and `[[Unknown]]` alone would open a one-element list where the
// member-level equivalent reads 0. The price, recorded in spec §4 D-9d: a document whose array
// is named anything but `data` reads as 0 elements.

[PicoSerializable]
public class RootArrayItem
{
    public string Name { get; set; } = "";
}

// The reviewer's literal C-1 document uses two different keys, one per element.
public class RootNumberedItem
{
    public int A { get; set; }
    public int B { get; set; }
}

public class TomlRootArrayOfTablesTests
{
    private static List<RootArrayItem> Read(string input) =>
        TomlSerializer.Deserialize<List<RootArrayItem>>(Encoding.UTF8.GetBytes(input))!;

    /// <summary>
    /// The D-9d shape at the root: a foreign header after the real one used to append an EMPTY
    /// element (measured: <c>count=2, names=a+&lt;empty&gt;</c>), so the caller received a value
    /// the document never contained.
    /// </summary>
    [Test]
    [Arguments("[[data]]\nName = \"a\"\n[[Unknown]]\n", 1)]
    // A foreign header that carries its own keys is still not an element of `data`.
    [Arguments("[[data]]\nName = \"a\"\n[[Unknown]]\nName = \"x\"\n", 1)]
    // A foreign header FIRST does not make the list "the Unknown list" (I-1: the list owns the
    // name the writer emits, `data` — not whichever header came first), and it costs nothing
    // either: the header is skipped and the scan goes on, so the real element after it survives.
    [Arguments("[[Unknown]]\nName = \"x\"\n[[data]]\nName = \"a\"\n", 1)]
    // ... and a document whose array is named something ELSE reads as 0 elements, exactly like
    // the member-level emitter (`TomlUnknownTableHangTests`: `[[Unknown]]` ⇒ 0). Strict on both
    // sides of the fix; flips if the root list ever gains a name from the model.
    [Arguments("[[Unknown]]\nName = \"x\"\n", 0)]
    [Arguments("[[items]]\nName = \"a\"\n", 0)]
    public async Task RootList_DoesNotOpenAnElementForAForeignHeader(
        string input,
        int expectedCount
    )
    {
        var list = Read(input);

        await Assert
            .That(list.Count)
            .IsEqualTo(expectedCount)
            .Because(
                "a header that does not name `data` (the array this list owns, I-1) contributes "
                    + "no element (D-9d); input: "
                    + input.Replace("\n", "\\n")
            );
    }

    /// <summary>
    /// <b>C-1 (fix round 1) — a foreign header BETWEEN two real ones must not END the scan.</b>
    /// The root list has no enclosing dispatcher to come back for the next <c>[[data]]</c>: unlike
    /// a member arm (whose <c>break</c> hands control back to the object-level dispatch loop, so
    /// the same document counts 2 — <c>TomlUnknownTableHangTests</c>), <c>Deserialize</c> here
    /// <i>is</i> the outermost loop, so "stop at a foreign header" means "silently drop every
    /// element after it". Round 0 got the direction wrong the other way (a phantom element, but
    /// <c>b</c> survived); the first attempt at D-9d at the root turned that into
    /// <b>count = 1, `b` gone, no exception, no diagnostic</b> — the exact failure shape I-1/I-7
    /// forbid. The correct reading of a foreign header is "this list does not own it ⇒ skip the
    /// header AND its body", never "the list is over".
    /// </summary>
    [Test]
    [Arguments("[[data]]\nName = \"a\"\n[[Unknown]]\n[[data]]\nName = \"b\"\n", "a,b")]
    [Arguments("[[data]]\nName = \"a\"\n[[Unknown]]\nName = \"x\"\n[[data]]\nName = \"b\"\n", "a,b")]
    // Two foreign headers in a row, and a foreign body that carries an inline collection: the
    // skip consumes the BODY, not just the header line (`Name = "x"` must never be dispatched
    // into the next element).
    [Arguments(
        "[[data]]\nName = \"a\"\n[[Unknown]]\nName = \"x\"\n[[Other]]\nTag = \"t\"\n[[data]]\nName = \"b\"\n",
        "a,b"
    )]
    [Arguments(
        "[[data]]\nName = \"a\"\n[[Unknown]]\nName = \"x\"\nNums = [1, 2]\n[[data]]\nName = \"b\"\n",
        "a,b"
    )]
    // A plain table (single brackets) is foreign too, and a real element after it must live.
    [Arguments("[Other]\nTag = \"t\"\n[[data]]\nName = \"a\"\n", "a")]
    public async Task RootList_SkipsAForeignHeaderInsteadOfEndingTheScan(
        string input,
        string expectedNames
    )
    {
        var list = Read(input);

        await Assert
            .That(string.Join(",", list.Select(i => i.Name)))
            .IsEqualTo(expectedNames)
            .Because(
                "a header this list does not own is skipped WITH its body, then the scan "
                    + "continues: dropping the elements after it would be silent data loss "
                    + "(C-1); input: "
                    + input.Replace("\n", "\\n")
            );
    }

    /// <summary>
    /// The other half of D-9d: refusing the phantom element must not cost the REAL one. A count
    /// alone cannot tell those apart (0 is "nothing survived", 1-with-empty-name is still the
    /// phantom), so the surviving element's data is asserted.
    /// </summary>
    [Test]
    [Arguments("[[data]]\nName = \"a\"\n[[Unknown]]\n", "a")]
    [Arguments("[[data]]\nName = \"a\"\n[[Unknown]]\nName = \"x\"\n", "a")]
    [Arguments("[[data]]\nName = \"a\"\n[[data]]\nName = \"b\"\n", "a,b")]
    // C-1's content half: the survivor after a foreign header must be `b`, the real element —
    // not an element holding the foreign table's `x`.
    [Arguments("[[data]]\nName = \"a\"\n[[Unknown]]\nName = \"x\"\n[[data]]\nName = \"b\"\n", "a,b")]
    // A dotted header names the member by its LAST segment (same rule as the member-level arms),
    // so the writer's own name behind a parent path still reads.
    [Arguments("[[Parent.data]]\nName = \"a\"\n", "a")]
    public async Task RootList_KeepsTheRealElementsData(string input, string expectedNames)
    {
        var list = Read(input);

        await Assert
            .That(string.Join(",", list.Select(i => i.Name)))
            .IsEqualTo(expectedNames)
            .Because("the surviving elements must be the ones the document really has");
    }

    /// <summary>
    /// The C-1 counter-example VERBATIM, from the review: <c>[[data]] a = 1 / [[Unknown]] /
    /// [[data]] b = 2</c>. The fixed root list must return BOTH elements, each holding its own
    /// number, and `Unknown` must contribute none — <c>tomllib</c> says
    /// <c>{'data': [{'a': 1}, {'b': 2}], 'Unknown': [{}]}</c>. Before the fix this returned one
    /// element holding <c>a = 1</c>: `b` was gone with no exception and no diagnostic.
    /// </summary>
    [Test]
    public async Task RootList_KeepsBothElementsOfTheReviewersC1Document()
    {
        var list = TomlSerializer.Deserialize<List<RootNumberedItem>>(
            Encoding.UTF8.GetBytes("[[data]]\na = 1\n[[Unknown]]\n[[data]]\nb = 2\n")
        )!;

        await Assert.That(list.Count).IsEqualTo(2).Because("the document has two `data` elements");
        await Assert.That(list[0].A).IsEqualTo(1);
        await Assert.That(list[0].B).IsEqualTo(0);
        await Assert.That(list[1].B).IsEqualTo(2);
        await Assert.That(list[1].A).IsEqualTo(0);
    }
}
