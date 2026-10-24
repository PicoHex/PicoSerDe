// TDD: empty object lists must not swallow sibling keys; scalar element lists round-trip.
namespace PicoYaml.Tests;

public class YamlListContractItem
{
    public string Name { get; set; } = "";
}

public class YamlListContractHolder
{
    public List<YamlListContractItem> Items { get; set; } = new();
    public int Count { get; set; }
}

public class YamlScalarListHolder
{
    public List<string> Tags { get; set; } = new();
    public int Count { get; set; }
}

public class YamlListContractScalars
{
    public List<double> Doubles { get; set; } = new();
    public List<bool> Bools { get; set; } = new();
    public List<decimal> Decimals { get; set; } = new();
    public List<Guid> Guids { get; set; } = new();
    public List<DateTime> Dates { get; set; } = new();
}

public class YamlListContractTests
{
    [Test]
    public async Task EmptyObjectList_DoesNotSwallowNextProperty()
    {
        var v = YamlSerializer.Deserialize<YamlListContractHolder>(
            Encoding.UTF8.GetBytes("Items:\nCount: 7")
        );
        await Assert.That(v!.Items).IsEmpty();
        await Assert.That(v.Count).IsEqualTo(7);
    }

    [Test]
    public async Task ScalarElementLists_RoundTrip()
    {
        var value = new YamlListContractScalars
        {
            Doubles = { 1.5, 2.5 },
            Bools = { true, false },
            Decimals = { 1.5m },
            Guids = { Guid.Parse("11111111-2222-3333-4444-555555555555") },
            Dates = { new DateTime(2024, 1, 2) },
        };
        var back = YamlSerializer.Deserialize<YamlListContractScalars>(
            Encoding.UTF8.GetBytes(YamlSerializer.Serialize(value))
        );
        await Assert.That(back!.Doubles).IsEquivalentTo([1.5, 2.5]);
        await Assert.That(back.Bools).IsEquivalentTo([true, false]);
        await Assert.That(back.Decimals[0]).IsEqualTo(1.5m);
    }

    // C-21 (streaming): the streaming dispatcher resumes from the reader mark, so the
    // list branch cannot use Mark()/RewindToMark() (RestoreMark clears NeedsMoreData).
    // It must probe without consuming: an empty `Items:` must leave the sibling
    // `Count` property name for the dispatcher to see.
    [Test]
    public async Task StreamingEmptyObjectList_SingleChunk_KeepsSibling()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Items:\nCount: 7"));
        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlListContractHolder>(stream);
        await Assert.That(back!.Items).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(7);
    }

    [Test]
    public async Task StreamingEmptyObjectList_OneByteChunks_KeepsSibling()
    {
        using var stream = new OneByteStream(Encoding.UTF8.GetBytes("Items:\nCount: 7"));
        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlListContractHolder>(stream);
        await Assert.That(back!.Items).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(7);
    }

    /// <summary>
    /// The wrapper branch must also keep the sibling: the list is non-empty, so the
    /// closing wrapper ObjectEnd — not the sibling key — is what follows the items.
    /// </summary>
    [Test]
    public async Task StreamingObjectList_SiblingAfterList_OneByteChunks()
    {
        var bytes = Encoding.UTF8.GetBytes(
            YamlSerializer.Serialize(
                new YamlListContractHolder
                {
                    Items = { new YamlListContractItem { Name = "a" } },
                    Count = 5,
                }
            )
        );
        using var stream = new OneByteStream(bytes);
        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlListContractHolder>(stream);
        await Assert.That(back!.Items.Count).IsEqualTo(1);
        await Assert.That(back.Items[0].Name).IsEqualTo("a");
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task StreamingScalarList_SiblingAfterList_BothFeedings()
    {
        const string yaml = "Tags:\n  - a\n  - b\nCount: 5";

        using var single = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
        var viaSingle = await YamlSerializer.DeserializeFromStreamAsync<YamlScalarListHolder>(
            single
        );
        await Assert.That(viaSingle!.Tags).IsEquivalentTo(["a", "b"]);
        await Assert.That(viaSingle.Count).IsEqualTo(5);

        using var chunked = new OneByteStream(Encoding.UTF8.GetBytes(yaml));
        var viaChunks = await YamlSerializer.DeserializeFromStreamAsync<YamlScalarListHolder>(
            chunked
        );
        await Assert.That(viaChunks!.Tags).IsEquivalentTo(["a", "b"]);
        await Assert.That(viaChunks.Count).IsEqualTo(5);
    }

    /// <summary>
    /// A one-byte chunk stream still yields every item: the list branch must not
    /// clobber the reader mark the streaming dispatcher resumes from.
    /// </summary>
    [Test]
    public async Task StreamingChunked_ObjectList_KeepsAllItems()
    {
        var model = new YamlListContractHolder
        {
            Items = { new YamlListContractItem { Name = "a" } },
            Count = 5,
        };
        var bytes = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(model));
        using var stream = new OneByteStream(bytes);
        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlListContractHolder>(stream);
        await Assert.That(back!.Items.Count).IsEqualTo(1);
        await Assert.That(back.Items[0].Name).IsEqualTo("a");
        await Assert.That(back.Count).IsEqualTo(5);
    }

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
