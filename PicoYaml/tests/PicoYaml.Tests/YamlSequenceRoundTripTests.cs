// End-to-end proof that the generator still round-trips after the wire shape changed:
// a block sequence is now delimited by ArrayStart/ArrayEnd instead of the mapping's
// ObjectStart/ObjectEnd. Four hand-written cases cover the four read/write arms that
// touch a sequence wrapper: a nested sequence, a list of mappings, a mapping inside a
// mapping that holds sequences, and a list of DTOs that carry their own list.
using System.Text;
using PicoYaml;

namespace PicoYaml.Tests;

[PicoYamlSerializable]
public partial class SeqRtGrid
{
    public List<List<int>> Grid { get; set; } = new();

    public int Count { get; set; }
}

[PicoYamlSerializable]
public partial class SeqRtItem
{
    public string Name { get; set; } = "";

    public int Port { get; set; }
}

[PicoYamlSerializable]
public partial class SeqRtObjectList
{
    public List<SeqRtItem> Items { get; set; } = new();

    public int Count { get; set; }
}

[PicoYamlSerializable]
public partial class SeqRtInner
{
    public List<string> Tags { get; set; } = new();

    public int Id { get; set; }
}

[PicoYamlSerializable]
public partial class SeqRtOuter
{
    public SeqRtInner? Inner { get; set; }

    public string Name { get; set; } = "";
}

[PicoYamlSerializable]
public partial class SeqRtDto
{
    public string Name { get; set; } = "";

    public List<int> Values { get; set; } = new();

    public int Count { get; set; }
}

[PicoYamlSerializable]
public partial class SeqRtDtoList
{
    public List<SeqRtDto> Items { get; set; } = new();
}

public class YamlSequenceRoundTripTests
{
    private static string Text(string s) => s.Replace("\r\n", "\n");

    private static T RoundTrip<T>(T value)
        where T : class
    {
        var wire = Text(YamlSerializer.Serialize(value));
        var back = YamlSerializer.Deserialize<T>(Encoding.UTF8.GetBytes(wire));
        if (back is null)
            throw new Exception("round-trip returned null for wire=" + wire);
        return back;
    }

    /// <summary>`List&lt;List&lt;int&gt;&gt;` — the wire is a bare dash per item plus its indented block.</summary>
    [Test]
    public async Task NestedSequence_RoundTrips_AndKeepsTheSiblingKey()
    {
        var source = new SeqRtGrid { Count = 5 };
        source.Grid.Add(new List<int> { 1, 2 });
        source.Grid.Add(new List<int> { 3 });
        var wire = Text(YamlSerializer.Serialize(source));
        var back = RoundTrip(source);
        await Assert.That(back.Grid.Count).IsEqualTo(2).Because("wire=" + wire);
        await Assert.That(back.Grid[0]).IsEquivalentTo([1, 2]);
        await Assert.That(back.Grid[1]).IsEquivalentTo([3]);
        await Assert
            .That(back.Count)
            .IsEqualTo(5)
            .Because("the key after the sequence must survive");
    }

    /// <summary>A list of mappings: the wrapper is an array, each item body is an object.</summary>
    [Test]
    public async Task ObjectList_RoundTrips_AndKeepsTheSiblingKey()
    {
        var source = new SeqRtObjectList { Count = 2 };
        source.Items.Add(new SeqRtItem { Name = "a", Port = 1 });
        source.Items.Add(new SeqRtItem { Name = "b", Port = 2 });
        var wire = Text(YamlSerializer.Serialize(source));
        var back = RoundTrip(source);
        await Assert.That(back.Items.Count).IsEqualTo(2).Because("wire=" + wire);
        await Assert.That(back.Items[0].Name).IsEqualTo("a").Because("wire=" + wire);
        await Assert.That(back.Items[1].Port).IsEqualTo(2).Because("wire=" + wire);
        await Assert.That(back.Count).IsEqualTo(2).Because("wire=" + wire);
    }

    /// <summary>A mapping inside a mapping, holding a sequence of its own.</summary>
    [Test]
    public async Task NestedMappingWithSequenceValue_RoundTrips()
    {
        var source = new SeqRtOuter
        {
            Name = "outer",
            Inner = new SeqRtInner { Id = 7, Tags = { "x", "y" } },
        };
        var wire = Text(YamlSerializer.Serialize(source));
        var back = RoundTrip(source);
        await Assert.That(back.Name).IsEqualTo("outer").Because("wire=" + wire);
        await Assert.That(back.Inner!.Id).IsEqualTo(7).Because("wire=" + wire);
        await Assert
            .That(back.Inner.Tags)
            .IsEquivalentTo(new List<string> { "x", "y" })
            .Because("wire=" + wire);
    }

    /// <summary>A list of DTOs that each carry a list of their own.</summary>
    [Test]
    public async Task ListOfDtosWithInnerLists_RoundTrips()
    {
        var source = new SeqRtDtoList();
        source.Items.Add(
            new SeqRtDto
            {
                Name = "p",
                Count = 3,
                Values = { 1, 2 },
            }
        );
        source.Items.Add(
            new SeqRtDto
            {
                Name = "q",
                Count = 4,
                Values = { 5 },
            }
        );
        var wire = Text(YamlSerializer.Serialize(source));
        var back = RoundTrip(source);
        await Assert.That(back.Items.Count).IsEqualTo(2).Because("wire=" + wire);
        await Assert
            .That(back.Items[0].Values)
            .IsEquivalentTo(new List<int> { 1, 2 })
            .Because("wire=" + wire);
        await Assert.That(back.Items[1].Name).IsEqualTo("q").Because("wire=" + wire);
        await Assert.That(back.Items[1].Count).IsEqualTo(4).Because("wire=" + wire);
    }

    /// <summary>
    /// The generated reader consumed the wrapper by its OLD kind before the fix, so this
    /// pins that the wrapper arm really is the array arm, and that a one-byte-at-a-time
    /// stream (the reader is rebuilt from <c>YamlReaderState</c> per chunk, so the frame
    /// KINDS travel with the state) still keeps the sibling key after a sequence.
    /// </summary>
    [Test]
    public async Task StreamingRoundTrip_OneByteChunks_KeepsTheSiblingKey()
    {
        var source = new SeqRtObjectList { Count = 2 };
        source.Items.Add(new SeqRtItem { Name = "a", Port = 1 });
        var wire = Encoding.UTF8.GetBytes(Text(YamlSerializer.Serialize(source)));
        using var stream = new OneByteStream(wire);
        var back = await YamlSerializer.DeserializeFromStreamAsync<SeqRtObjectList>(stream);
        await Assert
            .That(back!.Items.Count)
            .IsEqualTo(1)
            .Because("wire=" + Text(Encoding.UTF8.GetString(wire)));
        await Assert.That(back.Items[0].Name).IsEqualTo("a");
        await Assert
            .That(back.Count)
            .IsEqualTo(2)
            .Because("the key after the sequence must survive streaming");
    }

    /// <summary>
    /// The same three hand-written wires, read directly: an indented sequence, an absent
    /// sequence followed by a sibling, and a flow sequence.
    /// </summary>
    [Test]
    public async Task HandWrittenWires_StillRead()
    {
        var ints = YamlSerializer.Deserialize<SeqRtObjectList>(
            "Items:\n  - name: a\n    port: 9\nCount: 1\n"u8
        );
        await Assert.That(ints!.Items.Count).IsEqualTo(1).Because("indented sequence");
        await Assert.That(ints.Items[0].Port).IsEqualTo(9);
        await Assert.That(ints.Count).IsEqualTo(1);

        var flat = YamlSerializer.Deserialize<SeqRtObjectList>("Items:\nCount: 1\n"u8);
        await Assert.That(flat!.Items).IsEmpty().Because("absent sequence is an empty list");
        await Assert.That(flat.Count).IsEqualTo(1).Because("and the sibling key is not eaten");

        var flow = YamlSerializer.Deserialize<SeqRtDto>("Name: n\nValues: [1, 2, 3]\nCount: 4\n"u8);
        await Assert.That(flow!.Values).IsEquivalentTo(new List<int> { 1, 2, 3 });
        await Assert.That(flow.Count).IsEqualTo(4);
    }

    /// <summary>A stream that hands out one byte per read, to exercise the chunk loop.</summary>
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0)
                return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
