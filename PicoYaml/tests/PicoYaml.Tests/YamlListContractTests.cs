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

    /// <summary>
    /// The mark/rewind probe must never clobber the reader mark the streaming
    /// dispatcher resumes from — a one-byte chunk stream still yields every item.
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
