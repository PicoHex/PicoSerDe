// TDD (C-22): a nested object's list members must use the same element reads as
// a top-level member, and must not swallow the members that follow them.
//
// Not covered here (pre-existing, outside C-21/C-22):
//   * List<List<T>> and object-element lists inside a nested object are dropped by
//     shared/Gen/GenInfrastructure.ExtractProperties with PICOSERDE004, so that
//     member shape never reaches the generator.
//   * An empty scalar list writes "D:" with no line break, gluing the next key onto
//     the same line (writer defect, also affects top-level members).
namespace PicoYaml.Tests;

public enum YamlNestedKind
{
    Alpha,
    Beta,
}

public class YamlNestedScalarLists
{
    public List<double> Doubles { get; set; } = new();
    public List<bool> Bools { get; set; } = new();
    public List<decimal> Decimals { get; set; } = new();
    public List<int> Ints { get; set; } = new();
    public List<Guid> Guids { get; set; } = new();
    public List<DateTime> Dates { get; set; } = new();
    public List<TimeSpan> Spans { get; set; } = new();
    public List<YamlNestedKind> Kinds { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    // Every list above is followed by members: they must survive the list reads.
    public int Count { get; set; }
    public string Label { get; set; } = "";
}

public class YamlNestedListHolder
{
    public string Name { get; set; } = "";
    public YamlNestedScalarLists Nested { get; set; } = new();
    public int Total { get; set; }
}

public class YamlNestedListContractTests
{
    private static readonly Guid ProbeGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime ProbeDate = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly TimeSpan ProbeSpan = TimeSpan.FromSeconds(90);

    [Test]
    public async Task NestedScalarListMembers_RoundTrip_AndKeepFollowingMembers()
    {
        var value = new YamlNestedListHolder
        {
            Name = "outer",
            Nested = new YamlNestedScalarLists
            {
                Doubles = { 1.5, 2.5 },
                Bools = { true, false },
                Decimals = { 1.5m },
                Ints = { 7, -3 },
                Guids = { ProbeGuid },
                Dates = { ProbeDate },
                Spans = { ProbeSpan },
                Kinds = { YamlNestedKind.Beta },
                Tags = { "t1" },
                Count = 3,
                Label = "inner",
            },
            Total = 9,
        };

        var text = YamlSerializer.Serialize(value);
        var back = YamlSerializer.Deserialize<YamlNestedListHolder>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back!.Nested.Doubles).IsEquivalentTo([1.5, 2.5]);
        await Assert.That(back.Nested.Bools).IsEquivalentTo([true, false]);
        await Assert.That(back.Nested.Decimals[0]).IsEqualTo(1.5m);
        await Assert.That(back.Nested.Ints).IsEquivalentTo([7, -3]);
        await Assert.That(back.Nested.Guids[0]).IsEqualTo(ProbeGuid);
        await Assert.That(back.Nested.Dates[0]).IsEqualTo(ProbeDate);
        await Assert.That(back.Nested.Spans[0]).IsEqualTo(ProbeSpan);
        await Assert.That(back.Nested.Kinds[0]).IsEqualTo(YamlNestedKind.Beta);
        await Assert.That(back.Nested.Tags).IsEquivalentTo(["t1"]);
        // The members following the lists must not be swallowed by the list read.
        await Assert.That(back.Nested.Count).IsEqualTo(3);
        await Assert.That(back.Nested.Label).IsEqualTo("inner");
        await Assert.That(back.Total).IsEqualTo(9);
    }

    [Test]
    public async Task NestedEmptyListMembers_KeepFollowingMembers()
    {
        var back = YamlSerializer.Deserialize<YamlNestedListHolder>(
            Encoding.UTF8.GetBytes(
                "Nested:\n  Doubles:\n  Bools:\n  Count: 3\n  Label: inner\nTotal: 9"
            )
        );
        await Assert.That(back!.Nested.Doubles).IsEmpty();
        await Assert.That(back.Nested.Bools).IsEmpty();
        await Assert.That(back.Nested.Count).IsEqualTo(3);
        await Assert.That(back.Nested.Label).IsEqualTo("inner");
        await Assert.That(back.Total).IsEqualTo(9);
    }

    /// <summary>
    /// A list that is the last member of a nested object, followed by the enclosing
    /// mapping's next key: the helper must stop at its own ObjectEnd without eating it.
    /// </summary>
    [Test]
    public async Task NestedListAsLastMember_KeepsEnclosingSibling()
    {
        var back = YamlSerializer.Deserialize<YamlNestedListHolder>(
            Encoding.UTF8.GetBytes("Nested:\n  Count: 3\n  Doubles:\n    - 1.5\nTotal: 9")
        );
        await Assert.That(back!.Nested.Doubles).IsEquivalentTo([1.5]);
        await Assert.That(back.Nested.Count).IsEqualTo(3);
        await Assert.That(back.Total).IsEqualTo(9);
    }

    /// <summary>
    /// The nested-object helper must not touch the reader mark the streaming
    /// dispatcher resumes from: a list inside a nested object still resumes.
    /// </summary>
    [Test]
    public async Task NestedListMembers_StreamingOneByteChunks_RoundTrip()
    {
        var value = new YamlNestedListHolder
        {
            Name = "outer",
            // Every list is populated: an empty scalar list writes "D:" without a
            // line break (pre-existing writer defect), which would glue the members
            // that follow onto the same line and mask what this test measures.
            Nested = new YamlNestedScalarLists
            {
                Doubles = { 1.5 },
                Bools = { true },
                Decimals = { 1.5m },
                Ints = { 7 },
                Guids = { ProbeGuid },
                Dates = { ProbeDate },
                Spans = { ProbeSpan },
                Kinds = { YamlNestedKind.Beta },
                Tags = { "t1" },
                Count = 3,
                Label = "inner",
            },
            Total = 9,
        };
        var bytes = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(value));
        using var stream = new OneByteStream(bytes);
        var back = await YamlSerializer.DeserializeFromStreamAsync<YamlNestedListHolder>(stream);
        await Assert.That(back!.Nested.Doubles).IsEquivalentTo([1.5]);
        await Assert.That(back.Nested.Bools).IsEquivalentTo([true]);
        await Assert.That(back.Nested.Ints).IsEquivalentTo([7]);
        await Assert.That(back.Nested.Guids[0]).IsEqualTo(ProbeGuid);
        await Assert.That(back.Nested.Kinds[0]).IsEqualTo(YamlNestedKind.Beta);
        await Assert.That(back.Nested.Count).IsEqualTo(3);
        await Assert.That(back.Nested.Label).IsEqualTo("inner");
        await Assert.That(back.Total).IsEqualTo(9);
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
