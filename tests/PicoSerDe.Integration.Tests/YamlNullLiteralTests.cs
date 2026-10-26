namespace PicoSerDe.Integration.Tests;

// ── the four nullable shapes, with a NULLABLE member so "missing" is representable ────
//
// The member is declared nullable on purpose: the three states the spec (§7, "null 三态")
// requires to be distinct are (1) a null INSIDE the collection, (2) the member MISSING from
// the document, (3) the collection EMPTY. With a non-nullable member and an initializer,
// (2) and (3) both deserialize to an empty collection and the test could not tell them
// apart — the fixture would then be unable to catch the very bug it is here for.
//
// Every call below is NON-generic on purpose: the generators are usage-driven (the
// interceptor sits at the `YamlSerializer.Serialize/Deserialize<T>` call site), so a value
// passed through a `Text<T>(T value)` helper would have no call site and therefore no
// generated code at all.

/// <summary>S5 — <c>List&lt;int?&gt;</c>, a nullable value leaf.</summary>
public sealed class RvYamlNullS5
{
    public int N { get; set; }
    public List<int?>? V { get; set; }
}

/// <summary>S6 — <c>List&lt;List&lt;int?&gt;&gt;</c>, a nullable value leaf one level down.</summary>
public sealed class RvYamlNullS6
{
    public int N { get; set; }
    public List<List<int?>>? V { get; set; }
}

/// <summary>S12 — <c>List&lt;string?&gt;</c>, a null ELEMENT in a string list.</summary>
public sealed class RvYamlNullS12
{
    public int N { get; set; }
    public List<string?>? V { get; set; }
}

/// <summary>S13 — <c>Dictionary&lt;string,int?&gt;</c>, a null VALUE in a dictionary.</summary>
public sealed class RvYamlNullS13
{
    public int N { get; set; }
    public Dictionary<string, int?>? V { get; set; }
}

/// <summary>
/// R2 Task 4 — YAML's null literal, both directions.
/// <para>
/// The root cause was one-sided: <see cref="YamlReader"/> has read null literals all along
/// (<c>IsNullLiteral</c>: <c>null</c>, <c>~</c>, or a missing value; a QUOTED scalar is
/// never null), while <c>YamlWriter</c> had no way to write one. So the generator either
/// dropped the member (S5/S6/S13, the extractor's nullable-leaf gate) or dropped the
/// element (S12, <c>if (__item != null)</c>) — "write null, read it back as missing".
/// </para>
/// <para>
/// Spec D1 decides the wire: <c>- null</c>, not a bare dash (a bare dash already means
/// "nested sequence item" in this writer) and not <c>~</c> (same literal per YAML 1.1 tag
/// resolution, but the one an eyeball reads wrong).
/// </para>
/// <para>
/// The four tests below are NOT "it can be read". Each one asserts the three states of
/// §7's invariant are pairwise DIFFERENT — that is what a write side which drops nulls or
/// collapses them into empty cannot pass, and it is the only assertion that makes the
/// "null / missing / empty" distinction part of the contract instead of a comment.
/// </para>
/// </summary>
public class YamlNullLiteralTests
{
    private static RvYamlNullS5 ReadS5(string yaml) =>
        YamlSerializer.Deserialize<RvYamlNullS5>(Encoding.UTF8.GetBytes(yaml))!;

    private static RvYamlNullS6 ReadS6(string yaml) =>
        YamlSerializer.Deserialize<RvYamlNullS6>(Encoding.UTF8.GetBytes(yaml))!;

    private static RvYamlNullS12 ReadS12(string yaml) =>
        YamlSerializer.Deserialize<RvYamlNullS12>(Encoding.UTF8.GetBytes(yaml))!;

    private static RvYamlNullS13 ReadS13(string yaml) =>
        YamlSerializer.Deserialize<RvYamlNullS13>(Encoding.UTF8.GetBytes(yaml))!;

    /// <summary>
    /// One shape's three states: the writer must produce three DIFFERENT documents (one
    /// carrying the null literal, one carrying no member, one carrying an empty
    /// collection), and the reader must bring them back as three DIFFERENT values.
    /// </summary>
    private static async Task AssertThreeStates(
        string shape,
        string withNullWire,
        string missingWire,
        string emptyWire,
        string nullState,
        string missingState,
        string emptyState,
        string expectedNullLiteral
    )
    {
        await Assert
            .That(withNullWire)
            .Contains(expectedNullLiteral)
            .Because(
                shape
                    + ": the null state must be written as a YAML null literal — "
                    + withNullWire
            );
        await Assert
            .That(withNullWire)
            .DoesNotContain("\"" + expectedNullLiteral + "\"")
            .Because(
                shape
                    + ": a QUOTED null is the four-character STRING (the reader refuses it as"
                    + " a null literal), i.e. the write side would still be lossy — "
                    + withNullWire
            );
        await Assert
            .That(missingWire)
            .DoesNotContain("V")
            .Because(
                shape + ": the missing state must not emit the member at all — " + missingWire
            );
        await Assert
            .That(emptyWire)
            .Contains("V:")
            .Because(shape + ": the empty state must still emit the member — " + emptyWire);

        var wires = new[] { withNullWire, missingWire, emptyWire };
        await Assert
            .That(wires.Distinct().Count())
            .IsEqualTo(3)
            .Because(
                shape
                    + ": one document per state — got ["
                    + withNullWire
                    + "] ["
                    + missingWire
                    + "] ["
                    + emptyWire
                    + "]"
            );

        var states = new[] { nullState, missingState, emptyState };
        await Assert
            .That(states.Distinct().Count())
            .IsEqualTo(3)
            .Because(
                shape
                    + ": null / missing / empty collapsed into each other — got ["
                    + nullState
                    + "] ["
                    + missingState
                    + "] ["
                    + emptyState
                    + "]"
            );
    }

    [Test]
    public async Task S5_nullable_value_leaf_three_states()
    {
        var withNull = new RvYamlNullS5 { N = 5, V = [1, null, 3] };
        var missing = new RvYamlNullS5 { N = 5 };
        var empty = new RvYamlNullS5 { N = 5, V = [] };

        var withNullWire = YamlSerializer.Serialize(withNull);
        await AssertThreeStates(
            "S5 List<int?>",
            withNullWire: withNullWire,
            missingWire: YamlSerializer.Serialize(missing),
            emptyWire: YamlSerializer.Serialize(empty),
            nullState: SigS5(ReadS5("N: 5\nV:\n- 1\n- null\n- 3\n")),
            missingState: SigS5(ReadS5("N: 5\n")),
            emptyState: SigS5(ReadS5("N: 5\nV: []\n")),
            expectedNullLiteral: "- null"
        );

        // Value level: the null keeps its POSITION (index 1). That is the difference
        // between "null element" and "element dropped", which a count-only assertion hides.
        var back = ReadS5(withNullWire);
        await Assert.That(back.N).IsEqualTo(5);
        await Assert.That(back.V!.Count).IsEqualTo(3).Because("actual=" + withNullWire);
        await Assert.That(back.V[0]).IsEqualTo(1);
        await Assert.That(back.V[1]).IsNull();
        await Assert.That(back.V[2]).IsEqualTo(3);
    }

    [Test]
    public async Task S6_deep_nullable_value_leaf_three_states()
    {
        var withNull = new RvYamlNullS6 { N = 5, V = [[null, 7], [null]] };
        var missing = new RvYamlNullS6 { N = 5 };
        var empty = new RvYamlNullS6 { N = 5, V = [] };

        var withNullWire = YamlSerializer.Serialize(withNull);
        await AssertThreeStates(
            "S6 List<List<int?>>",
            withNullWire: withNullWire,
            missingWire: YamlSerializer.Serialize(missing),
            emptyWire: YamlSerializer.Serialize(empty),
            nullState: SigS6(ReadS6("N: 5\nV:\n-\n  - null\n  - 7\n-\n  - null\n")),
            missingState: SigS6(ReadS6("N: 5\n")),
            emptyState: SigS6(ReadS6("N: 5\nV: []\n")),
            expectedNullLiteral: "- null"
        );

        var back = ReadS6(withNullWire);
        await Assert.That(back.V!.Count).IsEqualTo(2).Because("actual=" + withNullWire);
        await Assert.That(back.V[0].Count).IsEqualTo(2);
        await Assert.That(back.V[0][0]).IsNull();
        await Assert.That(back.V[0][1]).IsEqualTo(7);
        await Assert.That(back.V[1][0]).IsNull();
    }

    [Test]
    public async Task S12_nullable_reference_leaf_three_states()
    {
        var withNull = new RvYamlNullS12 { N = 5, V = ["a", null, ""] };
        var missing = new RvYamlNullS12 { N = 5 };
        var empty = new RvYamlNullS12 { N = 5, V = [] };

        var withNullWire = YamlSerializer.Serialize(withNull);
        await AssertThreeStates(
            "S12 List<string?>",
            withNullWire: withNullWire,
            missingWire: YamlSerializer.Serialize(missing),
            emptyWire: YamlSerializer.Serialize(empty),
            nullState: SigS12(ReadS12("N: 5\nV:\n- a\n- null\n- \"\"\n")),
            missingState: SigS12(ReadS12("N: 5\n")),
            emptyState: SigS12(ReadS12("N: 5\nV: []\n")),
            expectedNullLiteral: "- null"
        );

        // null, "" and the four characters "null" are three different elements: the writer
        // must not turn the null into the string, and must not turn it into an empty string.
        var text = YamlSerializer.Serialize(
            new RvYamlNullS12
            {
                N = 5,
                V = ["a", null, "", "null"],
            }
        );
        var back = ReadS12(text);
        await Assert.That(back.V!.Count).IsEqualTo(4).Because("actual=" + text);
        await Assert.That(back.V[0]).IsEqualTo("a");
        await Assert.That(back.V[1]).IsNull();
        await Assert.That(back.V[2]).IsEqualTo("");
        await Assert.That(back.V[3]).IsEqualTo("null").Because("actual=" + text);
    }

    [Test]
    public async Task S13_nullable_dict_value_three_states()
    {
        var withNull = new RvYamlNullS13 { N = 5, V = new() { ["n"] = null } };
        var missing = new RvYamlNullS13 { N = 5 };
        var empty = new RvYamlNullS13 { N = 5, V = new() };

        var withNullWire = YamlSerializer.Serialize(withNull);
        await AssertThreeStates(
            "S13 Dictionary<string,int?>",
            withNullWire: withNullWire,
            missingWire: YamlSerializer.Serialize(missing),
            emptyWire: YamlSerializer.Serialize(empty),
            nullState: SigS13(ReadS13("N: 5\nV:\n  n: null\n")),
            missingState: SigS13(ReadS13("N: 5\n")),
            emptyState: SigS13(ReadS13("N: 5\nV:\n")),
            expectedNullLiteral: "n: null"
        );

        // A key whose value is null is not a missing key, which is not an empty dictionary.
        var back = ReadS13(withNullWire);
        await Assert.That(back.V!.ContainsKey("n")).IsTrue().Because("actual=" + withNullWire);
        await Assert.That(back.V["n"]).IsNull();

        var backEmpty = ReadS13(YamlSerializer.Serialize(empty));
        await Assert.That(backEmpty.V!.Count).IsEqualTo(0);
        await Assert.That(backEmpty.V.ContainsKey("n")).IsFalse();

        var backMissing = ReadS13(YamlSerializer.Serialize(missing));
        await Assert.That(backMissing.V).IsNull();
    }

    /// <summary>
    /// The reader half of the same contract, pinned separately so a future "helpful"
    /// writer change cannot hide behind it: every null spelling YAML offers reads as null,
    /// while the QUOTED <c>"null"</c> stays a four-character string.
    /// </summary>
    [Test]
    public async Task Reader_keeps_null_missing_and_empty_apart()
    {
        foreach (var wire in new[] { "N: 5\nV:\n- a\n- null\n", "N: 5\nV:\n- a\n- ~\n" })
        {
            var back = ReadS12(wire);
            await Assert.That(back.V!.Count).IsEqualTo(2).Because("wire=" + wire);
            await Assert.That(back.V[1]).IsNull().Because("wire=" + wire);
        }

        var quoted = ReadS12("N: 5\nV:\n- a\n- \"null\"\n");
        await Assert.That(quoted.V![1]).IsEqualTo("null");
    }

    /// <summary>
    /// The resumable (streaming) reader takes a different path through the same emit
    /// helpers, so the null literal has to survive being fed one byte at a time — a null
    /// element that only works whole-document would be half a fix. The dictionary arm is
    /// the one with the extra alignment (an entry loop over PropertyName tokens), so both
    /// shapes are exercised here.
    /// </summary>
    [Test]
    public async Task Streaming_reads_a_null_element_and_a_null_dict_value()
    {
        var listBytes = Encoding.UTF8.GetBytes(
            YamlSerializer.Serialize(new RvYamlNullS5 { N = 5, V = [1, null, 3] })
        );
        using (var listStream = new OneByteStream(listBytes))
        {
            var back = await YamlSerializer.DeserializeFromStreamAsync<RvYamlNullS5>(listStream);
            await Assert.That(back!.N).IsEqualTo(5);
            await Assert.That(back.V!.Count).IsEqualTo(3);
            await Assert.That(back.V[0]).IsEqualTo(1);
            await Assert.That(back.V[1]).IsNull();
            await Assert.That(back.V[2]).IsEqualTo(3);
        }

        var dictBytes = Encoding.UTF8.GetBytes(
            YamlSerializer.Serialize(
                new RvYamlNullS13
                {
                    N = 5,
                    V = new() { ["a"] = 7, ["n"] = null },
                }
            )
        );
        using (var dictStream = new OneByteStream(dictBytes))
        {
            var back = await YamlSerializer.DeserializeFromStreamAsync<RvYamlNullS13>(dictStream);
            await Assert.That(back!.V!.Count).IsEqualTo(2);
            await Assert.That(back.V["a"]).IsEqualTo(7);
            await Assert.That(back.V.ContainsKey("n")).IsTrue();
            await Assert.That(back.V["n"]).IsNull();
        }
    }

    /// <summary>One byte at a time: the reader must resume inside every token.</summary>
    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0)
                return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
    }

    // ── state signatures: what "different" means for these four shapes ────────────────

    private static string SigS5(RvYamlNullS5 m) =>
        m.V is null
            ? "V=missing"
            : "V=[" + string.Join(",", m.V.Select(x => x?.ToString() ?? "null")) + "]";

    private static string SigS6(RvYamlNullS6 m) =>
        m.V is null
            ? "V=missing"
            : "V=["
                + string.Join(
                    ";",
                    m.V.Select(row =>
                        "[" + string.Join(",", row.Select(x => x?.ToString() ?? "null")) + "]"
                    )
                )
                + "]";

    private static string SigS12(RvYamlNullS12 m) =>
        m.V is null
            ? "V=missing"
            : "V=["
                + string.Join(",", m.V.Select(x => x is null ? "null" : "\"" + x + "\""))
                + "]";

    private static string SigS13(RvYamlNullS13 m) =>
        m.V is null
            ? "V=missing"
            : "V={"
                + string.Join(
                    ",",
                    m.V.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => kv.Key + ":" + (kv.Value?.ToString() ?? "null"))
                )
                + "}";
}
