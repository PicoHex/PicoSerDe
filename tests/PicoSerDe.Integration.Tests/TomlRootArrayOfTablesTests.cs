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
// The rule the tests below pin is TOML's own (TOML v1.0.0, "Array of Tables": a `[[name]]`
// header "will start a new element of the array of tables named `name`"; an element belongs to
// the array its header names, not to the array that happened to be read first). A root
// `List<T>` therefore owns the ONE array whose header it started with: `[[data]]` + `Name = "a"`
// is one element, `[[Unknown]]` contributes none, and the real element must survive.

[PicoSerializable]
public class RootArrayItem
{
    public string Name { get; set; } = "";
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
    // ... and a foreign header FIRST does not make the list "the Unknown list": the first
    // header it reads is the one that names the array, so `[[Unknown]]` opens the list and
    // `data` is the foreign one here.
    [Arguments("[[Unknown]]\nName = \"x\"\n[[data]]\nName = \"a\"\n", 1)]
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
                "a header that does not name the array this list started with contributes no "
                    + "element (D-9d); input: "
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
    public async Task RootList_KeepsTheRealElementsData(string input, string expectedNames)
    {
        var list = Read(input);

        await Assert
            .That(string.Join(",", list.Select(i => i.Name)))
            .IsEqualTo(expectedNames)
            .Because("the surviving elements must be the ones the document really has");
    }
}
