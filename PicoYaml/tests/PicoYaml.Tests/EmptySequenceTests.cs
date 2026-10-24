// TDD (F2): an empty scalar sequence must not swallow the next key.
//
// Root cause: the generator's scalar-list write side emitted only a bare
// `foreach { WriteSequenceItem(...) }` — with no sequence start/end — and
// WritePropertyName leaves `_afterKey` set, so an empty list wrote nothing at
// all after `D:` and the next `WritePropertyName` continued on the same line
// (`D:Count: 5`).
//
// `WriteStartSequence()` / `WriteEndSequence()` are the missing pair: the end
// call flushes ` []` for a sequence that received no items, so an empty list is
// still a value on the key's own line and the following key starts a new line.

// NOTE: PICOSERDE004 is reported with Location.None, so it cannot be silenced by a
// #pragma. The nested fixture below used to be the one intentional warning in this
// project's build; F5 removed that shape from the dropped set, so the warning is gone.
namespace PicoYaml.Tests;

/// <summary>A scalar list member inside a nested object (EmitSerializeInline path).</summary>
public class InnerEmptySeq
{
    public List<int> D { get; set; } = new();
    public int Count { get; set; } = 5;
}

public class OuterEmptySeq
{
    public InnerEmptySeq Inner { get; set; } = new();
    public int Total { get; set; } = 9;
}

/// <summary>Control fixture: a nested object with no collections at all.</summary>
public class PlainInner
{
    public int A { get; set; } = 1;
}

public class PlainNested
{
    public PlainInner Inner { get; set; } = new();
    public int Total { get; set; } = 9;
}

public class EmptySeqInts
{
    public List<int> D { get; set; } = new();
    public int Count { get; set; } = 5;
}

public class EmptySeqStrings
{
    public List<string> D { get; set; } = new();
    public int Count { get; set; } = 5;
}

public class EmptySeqDoubles
{
    public List<double> D { get; set; } = new();
    public int Count { get; set; } = 5;
}

public class NonEmptySeqInts
{
    public List<int> D { get; set; } = new() { 1, 2 };
    public int Count { get; set; } = 5;
}

/// <summary>
/// The F2 fixture whose <c>List&lt;List&lt;int&gt;&gt;</c> member the shared extractor used
/// to drop with PICOSERDE004. F5 wired nested sequences, so the member is written and
/// read back.
/// </summary>
public class NestedSeqInts
{
    public string Name { get; set; } = "";
    public List<List<int>> Grid { get; set; } =
        new()
        {
            new() { 1, 2 },
        };
    public int Count { get; set; } = 5;
}

public class EmptySequenceTests
{
    // ── Writer level: the missing start/end pair ──

    [Test]
    public async Task Writer_EmptySequenceAfterKey_WritesFlowBracketsAndBreaksTheLine()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("D"u8);
        w.WriteStartSequence();
        w.WriteEndSequence();
        w.WritePropertyName("Count"u8);
        w.WriteNumber(5);
        var text = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(text).IsEqualTo("D: []\nCount: 5\n").Because("actual=" + text);
    }

    [Test]
    public async Task Writer_NonEmptySequence_WritesNoBrackets()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("D"u8);
        w.WriteStartSequence();
        w.WriteSequenceItem(1);
        w.WriteSequenceItem(2);
        w.WriteEndSequence();
        w.WritePropertyName("Count"u8);
        w.WriteNumber(5);
        var text = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(text).IsEqualTo("D:\n- 1\n- 2\nCount: 5\n").Because("actual=" + text);
    }

    /// <summary>
    /// A nested empty sequence flushes its own ` []`; the enclosing sequence then
    /// holds one value and must not write a second one.
    /// </summary>
    [Test]
    public async Task Writer_NestedEmptySequences_FlushOnlyOnce()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("D"u8);
        w.WriteStartSequence();
        w.WriteStartSequence();
        w.WriteEndSequence();
        w.WriteEndSequence();
        w.WritePropertyName("Count"u8);
        w.WriteNumber(5);
        var text = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert.That(text).IsEqualTo("D: []\nCount: 5\n").Because("actual=" + text);
    }

    /// <summary>
    /// WriteStartSequence/WriteEndSequence must not disturb the depth counter that
    /// WriteStartSequenceBlock/WriteEndSequenceBlock maintain (and vice versa).
    /// </summary>
    [Test]
    public async Task Writer_SequenceBracketsAndSequenceBlock_DoNotShareCounters()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf);
        w.WritePropertyName("Items"u8);
        w.WriteStartMapping(); // depth 1 (object array wrapper)
        w.WriteStartSequenceBlock(); // depth 2
        w.WritePropertyName("Name"u8);
        w.WriteString("a"u8);
        w.WriteEndSequenceBlock(); // depth 1
        w.WriteEndMapping(); // depth 0
        w.WritePropertyName("Empty"u8);
        w.WriteStartSequence(); // must not consume a block level
        w.WriteEndSequence();
        w.WritePropertyName("Tail"u8);
        w.WriteNumber(1);
        var text = Encoding.UTF8.GetString(buf.WrittenSpan);
        await Assert
            .That(text)
            .IsEqualTo("Items:\n  -\n    Name: a\nEmpty: []\nTail: 1\n")
            .Because("actual=" + text);
    }

    // ── Generator level: empty scalar lists must not glue the next key ──

    [Test]
    public async Task EmptyIntList_EmitsBrackets_AndKeepsTheFollowingKey()
    {
        var text = YamlSerializer.Serialize(new EmptySeqInts());
        await Assert.That(text).DoesNotContain("D:Count").Because("actual=" + text);
        await Assert.That(text).IsEqualTo("D: []\nCount: 5\n").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<EmptySeqInts>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.D).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task EmptyStringList_EmitsBrackets_AndKeepsTheFollowingKey()
    {
        var text = YamlSerializer.Serialize(new EmptySeqStrings());
        await Assert.That(text).IsEqualTo("D: []\nCount: 5\n").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<EmptySeqStrings>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.D).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task EmptyDoubleList_EmitsBrackets_AndKeepsTheFollowingKey()
    {
        var text = YamlSerializer.Serialize(new EmptySeqDoubles());
        await Assert.That(text).IsEqualTo("D: []\nCount: 5\n").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<EmptySeqDoubles>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.D).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// The Indented option only adds indentation to items: an empty list still ends the
    /// key's line, and the result reads back.
    /// </summary>
    [Test]
    public async Task EmptyIntList_IndentedOption_RoundTrips()
    {
        var text = YamlSerializer.Serialize(
            new EmptySeqInts(),
            new YamlOptions { Indented = true }
        );
        await Assert.That(text).DoesNotContain("D:Count").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<EmptySeqInts>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.D).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task EmptyScalarLists_StreamingRoundTrip_KeepsTheFollowingKey()
    {
        var bytes = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(new EmptySeqInts()));
        using var stream = new MemoryStream(bytes);
        var back = await YamlSerializer.DeserializeFromStreamAsync<EmptySeqInts>(stream);
        await Assert.That(back!.D).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// The same defect existed for a nested object's list members (the inline emit
    /// path): the fix wraps both emit paths, so the enclosing mapping's next key
    /// still starts its own line.
    /// </summary>
    [Test]
    public async Task NestedObjectEmptyListMember_KeepsFollowingKeys()
    {
        // Control: a nested object without any collection already ends its block with a
        // blank line before the enclosing key. That blank line predates F2 and is left
        // exactly as it was, so the snapshots below only differ by the list lines.
        var control = YamlSerializer.Serialize(new PlainNested());
        await Assert
            .That(control)
            .IsEqualTo("Inner:\n  A: 1\n\nTotal: 9\n")
            .Because("actual=" + control);

        var text = YamlSerializer.Serialize(new OuterEmptySeq());
        await Assert.That(text).DoesNotContain("D:Count").Because("actual=" + text);
        await Assert
            .That(text)
            .IsEqualTo("Inner:\n  D: []\n  Count: 5\n\nTotal: 9\n")
            .Because("actual=" + text);

        var back = YamlSerializer.Deserialize<OuterEmptySeq>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Inner.D).IsEmpty();
        await Assert.That(back.Inner.Count).IsEqualTo(5);
        await Assert.That(back.Total).IsEqualTo(9);
    }

    /// <summary>Non-empty lists keep the exact shape they had before F2.</summary>
    [Test]
    public async Task NonEmptyIntList_KeepsItsBlockShape()
    {
        var text = YamlSerializer.Serialize(new NonEmptySeqInts());
        await Assert.That(text).IsEqualTo("D:\n- 1\n- 2\nCount: 5\n").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NonEmptySeqInts>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.D).IsEquivalentTo([1, 2]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// The fixture F2 used to prove the drop: <c>List&lt;List&lt;int&gt;&gt;</c> is no longer
    /// dropped (F5). The member is written, round-trips, and its sibling keys still
    /// start their own lines. The diagnostic id itself is asserted by
    /// tests/PicoSerDe.Integration.Tests/YamlNestedListDiagnosticTests.cs, which is the
    /// only project that references PicoYaml.Gen with ReferenceOutputAssembly=true.
    /// </summary>
    [Test]
    public async Task NestedList_IsNoLongerDropped_AndRoundTrips()
    {
        var text = YamlSerializer.Serialize(new NestedSeqInts { Name = "n" });
        await Assert.That(text).Contains("Grid").Because("actual=" + text);
        await Assert
            .That(text)
            .IsEqualTo("Name: n\nGrid:\n-\n  - 1\n  - 2\nCount: 5\n")
            .Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqInts>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Name).IsEqualTo("n");
        await Assert.That(back.Grid.Count).IsEqualTo(1);
        await Assert.That(back.Grid[0]).IsEquivalentTo([1, 2]);
        await Assert.That(back.Count).IsEqualTo(5);
    }
}
