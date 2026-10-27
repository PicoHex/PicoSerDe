namespace PicoSerDe.Integration.Tests;

// ── D-9d parity: the MEMBER-level `[[…]]` element loops (C-1 fix round 1) ────────────────────
//
// C-1 was a ROOT-list bug: the root list treated a foreign header as "the list is over", and
// because the root container has no enclosing dispatcher to come back for the next `[[data]]`,
// `[[data]] a [[Unknown]] [[data]] b` silently lost `b`. The member-level emitters were assumed
// to be fine because their `break` hands control back to the object-level dispatch loop, which
// advances past the header it does not own. That assumption is pinned HERE rather than argued:
// the same document shape — a foreign header WITH a body, between two real elements — must give
// two elements with their real data through every member-level entry point that owns a
// `[[…]]` loop:
//
//   buffered     `Deserialize<T>`                         (buffered emitter's `[[…]]` arm)
//   streaming    `DeserializeFromStreamAsync<T>`          (streaming emitter's `[[…]]` arm)
//   polymorphic  `Deserialize<Base>` with a `$type` line  (polymorphic emitter's `[[…]]` arm)
//
// `tomllib` says the document has two `Items` elements, so 2 (names a,b) is the format's answer,
// not our loop's shape:
//   tomllib.loads('[[Items]]\nName = "a"\n[[Unknown]]\nName = "x"\n[[Items]]\nName = "b"\n')
//     -> {'Items': [{'Name': 'a'}, {'Name': 'b'}], 'Unknown': [{'Name': 'x'}]}

public class ParityItem
{
    public string Name { get; set; } = "";
}

public class ParityDoc
{
    public List<ParityItem> Items { get; set; } = new();
}

public class ParityInner
{
    public List<ParityItem> Items { get; set; } = new();
}

public class ParityOuterDoc
{
    public ParityInner? Inner { get; set; }
}

[PicoSerializable]
[PicoDerivedType(typeof(ParityPolyDerived), "emp")]
public abstract class ParityPolyBase { }

public class ParityPolyDerived : ParityPolyBase
{
    public List<ParityItem> Items { get; set; } = new();
}

public class TomlArrayOfTablesDispatchParityTests
{
    // The C-1 document shape: one header, one body, one header.
    private const string Interleaved =
        "[[Items]]\nName = \"a\"\n[[Unknown]]\nName = \"x\"\n[[Items]]\nName = \"b\"\n";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Names(IEnumerable<ParityItem> items) =>
        string.Join(",", items.Select(i => i.Name));

    [Test]
    public async Task BufferedEmitter_SkipsAForeignHeaderWithBodyWithoutLosingElements()
    {
        var doc = TomlSerializer.Deserialize<ParityDoc>(Bytes(Interleaved));

        await Assert
            .That(Names(doc!.Items))
            .IsEqualTo("a,b")
            .Because("buffered emitter: a foreign header between two real ones costs nothing");
    }

    [Test]
    public async Task StreamingEmitter_SkipsAForeignHeaderWithBodyWithoutLosingElements()
    {
        var doc = await TomlSerializer.DeserializeFromStreamAsync<ParityDoc>(
            new MemoryStream(Bytes(Interleaved))
        );

        await Assert
            .That(Names(doc!.Items))
            .IsEqualTo("a,b")
            .Because("streaming emitter: same contract as the buffered one");
    }

    [Test]
    public async Task PolymorphicEmitter_SkipsAForeignHeaderWithBodyWithoutLosingElements()
    {
        var doc = TomlSerializer.Deserialize<ParityPolyBase>(Bytes("$type = \"emp\"\n" + Interleaved));

        var derived = (ParityPolyDerived)doc!;
        await Assert
            .That(Names(derived.Items))
            .IsEqualTo("a,b")
            .Because("polymorphic emitter: same contract, its own `[[…]]` arm");
    }

    [Test]
    public async Task NestedObjectCollection_IsRefusedRatherThanSilentlyDropped()
    {
        // The temp-list `[[…]]` loop (`EmitDeserializeProp`) sits BEHIND this guard: an object
        // collection inside a nested table has no read path in a flat, section-based reader, so
        // the generator throws instead of handing back an empty list (R2 Task 5 decision — a
        // refused read is a report, an empty collection would be silent loss). Pinned so "that
        // loop is unreachable for this shape" is a tested fact and not just a comment: if the
        // guard ever goes, this test goes red and the loop's D-9d semantics get exercised.
        var thrown = Assert.Throws<NotSupportedException>(() =>
            TomlSerializer.Deserialize<ParityOuterDoc>(
                Bytes("[Inner]\n[[Inner.Items]]\nName = \"a\"\n")
            )
        );

        await Assert
            .That(thrown!.Message)
            .Contains("cannot read an object collection from inside a nested table");
    }
}
