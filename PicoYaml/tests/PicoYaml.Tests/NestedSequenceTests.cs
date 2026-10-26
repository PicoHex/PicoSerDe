// TDD (F5): a nested sequence must survive a YAML round-trip.
//
// Before F5 the shared extractor dropped every nested-sequence member for
// YAML (`List<List<T>>`, `List<T[]>`) and every nested member that contained an
// object sequence — both reported through `PICOSERDE004` — while the writer's
// `WriteStartSequence`/`WriteEndSequence` pair (F2) made a nested-sequence write
// path possible at all.
//
// Wire shape this file pins (a block sequence item whose value is the following
// indented sequence — valid YAML, verifiable with YamlDotNet). The item dash stands
// alone on its line: the writer emits no trailing space (review I-1), and the reader
// accepts a bare "-" followed by a line break:
//
//     Grid:
//     -
//       - 1
//       - 2
//     -
//       - 3
//     Count: 5
//
// An empty inner sequence is written as the `[]` flow marker on its own item
// line (`- []`), an empty outer sequence as `Grid: []`.
namespace PicoYaml.Tests;

public class NestedSeqRow
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class NestedSeqIntHolder
{
    public List<List<int>> Grid { get; set; } =
        new()
        {
            new() { 1, 2 },
            new() { 3 },
        };
    public int Count { get; set; } = 5;
}

public class NestedSeqStringHolder
{
    public List<List<string>> Grid { get; set; } =
        new()
        {
            new() { "a", "b" },
            new() { "c" },
        };
    public int Count { get; set; } = 5;
}

public class NestedSeqArrayHolder
{
    public List<int[]> Grid { get; set; } = new() { new[] { 1, 2 }, new[] { 3 } };
    public int Count { get; set; } = 5;
}

public class NestedSeqDtoHolder
{
    public List<List<NestedSeqRow>> Grid { get; set; } =
        new()
        {
            new()
            {
                new() { Name = "a", Value = 1 },
                new() { Name = "b", Value = 2 },
            },
            new()
            {
                new() { Name = "c", Value = 3 },
            },
        };
    public int Count { get; set; } = 5;
}

public class NestedSeqEmptyHolder
{
    public List<List<int>> Grid { get; set; } = new();
    public int Count { get; set; } = 5;
}

public class NestedSeqEmptyInnerHolder
{
    public List<List<int>> Grid { get; set; } =
        new()
        {
            new(),
            new() { 1 },
        };
    public int Count { get; set; } = 5;
}

/// <summary>Nested sequence inside a nested object: the member the F5 drop branch removed.</summary>
public class NestedSeqInner
{
    public List<NestedSeqRow> Rows { get; set; } = new();
    public int N { get; set; } = 1;
}

public class NestedSeqOuter
{
    public NestedSeqInner Child { get; set; } = new();
    public int Total { get; set; } = 9;
}

/// <summary>A nested sequence and an object sequence as members of the same nested object.</summary>
public class NestedSeqGridInner
{
    public List<List<int>> Grid { get; set; } =
        new()
        {
            new() { 1 },
            new() { 2, 3 },
        };
    public int N { get; set; } = 1;
}

public class NestedSeqGridOuter
{
    public NestedSeqGridInner Child { get; set; } = new();
    public int Total { get; set; } = 9;
}

public class NestedSeqDeepHolder
{
    public List<List<List<int>>> Grid { get; set; } =
        new()
        {
            new()
            {
                new() { 1, 2 },
                new() { 3 },
            },
        };
    public int Count { get; set; } = 5;
}

/// <summary>A nested sequence as the LAST member: nothing follows to re-dispatch.</summary>
public class NestedSeqLastHolder
{
    public string Name { get; set; } = "n";
    public List<List<int>> Grid { get; set; } =
        new()
        {
            new() { 1 },
            new() { 2, 3 },
        };
}

public class NestedSeqNullableHolder
{
    public List<List<int>>? Grid { get; set; }
    public int Count { get; set; } = 5;
}

/// <summary>
/// I-3 (F5 review): a nullable *reference* element stays supported — it is the nullable
/// *value* element (<c>int?</c>, <c>MyEnum?</c>) that YAML cannot carry, because the
/// scalar writers have no null representation.
/// </summary>
public class NestedSeqNullableStringHolder
{
    public List<List<string?>> Grid { get; set; } =
        new()
        {
            new() { "a", null, "b" },
            new() { null },
        };
    public int Count { get; set; } = 5;
}

public class NestedSeqTests
{
    private static string RoundTripText(string text) => text.Replace("\r\n", "\n");

    // ── List<List<int>> ──

    [Test]
    public async Task NestedIntGrid_WritesBlockSequenceAndRoundTrips()
    {
        var value = new NestedSeqIntHolder();
        var text = RoundTripText(YamlSerializer.Serialize(value));
        await Assert.That(text).Contains("Grid:").Because("actual=" + text);
        await Assert
            .That(text)
            .IsEqualTo("Grid:\n-\n  - 1\n  - 2\n-\n  - 3\nCount: 5\n")
            .Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqIntHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0]).IsEquivalentTo([1, 2]);
        await Assert.That(back.Grid[1]).IsEquivalentTo([3]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task NestedIntGrid_YamlDotNetReadsTheSameValues()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqIntHolder()));
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder()
            .Build()
            .Deserialize<NestedSeqIntHolder>(text);
        await Assert.That(yaml.Grid.Count).IsEqualTo(2).Because("yaml=" + text);
        await Assert.That(yaml.Grid[0]).IsEquivalentTo([1, 2]);
        await Assert.That(yaml.Grid[1]).IsEquivalentTo([3]);
        await Assert.That(yaml.Count).IsEqualTo(5);
    }

    // ── List<List<string>> / List<int[]> ──

    [Test]
    public async Task NestedStringGrid_RoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqStringHolder()));
        await Assert.That(text).Contains("- a").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqStringHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0]).IsEquivalentTo(["a", "b"]);
        await Assert.That(back.Grid[1]).IsEquivalentTo(["c"]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task NestedArrayGrid_RoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqArrayHolder()));
        await Assert.That(text).Contains("- 1").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqArrayHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0]).IsEquivalentTo([1, 2]);
        await Assert.That(back.Grid[1]).IsEquivalentTo([3]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>A string element that looks like the markers must not be mistaken for one.</summary>
    [Test]
    public async Task NestedStringGrid_WithMarkerLikeElements_RoundTrips()
    {
        var value = new NestedSeqStringHolder
        {
            Grid = new()
            {
                new() { "[]", "", "- 1" },
                new() { "x" },
            },
        };
        var text = RoundTripText(YamlSerializer.Serialize(value));
        var back = YamlSerializer.Deserialize<NestedSeqStringHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid.Count).IsEqualTo(2).Because("actual=" + text);
        await Assert.That(back.Grid[0]).IsEquivalentTo(["[]", "", "- 1"]).Because("actual=" + text);
        await Assert.That(back.Grid[1]).IsEquivalentTo(["x"]).Because("actual=" + text);
    }

    // ── List<List<Dto>>: objects at the innermost level ──

    [Test]
    public async Task NestedDtoGrid_RoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqDtoHolder()));
        await Assert.That(text).Contains("Name: a").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqDtoHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0].Count).IsEqualTo(2);
        await Assert.That(back.Grid[0][0].Name).IsEqualTo("a");
        await Assert.That(back.Grid[0][1].Name).IsEqualTo("b");
        await Assert.That(back.Grid[1][0].Value).IsEqualTo(3);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    // ── Empty shapes ──

    [Test]
    public async Task EmptyNestedGrid_WritesFlowEmptySequence_AndRoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqEmptyHolder()));
        await Assert.That(text).IsEqualTo("Grid: []\nCount: 5\n").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqEmptyHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task EmptyInnerSequence_WritesItsOwnItem_AndRoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqEmptyInnerHolder()));
        await Assert
            .That(text)
            .IsEqualTo("Grid:\n- []\n-\n  - 1\nCount: 5\n")
            .Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqEmptyInnerHolder>(
            Encoding.UTF8.GetBytes(text)
        );
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0]).IsEmpty();
        await Assert.That(back.Grid[1]).IsEquivalentTo([1]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// Minor-1 (F5 review): every inner sequence is empty, so the outer sequence has
    /// items but no nested block at all — two "- []" lines and no wrapper.
    /// </summary>
    [Test]
    public async Task AllInnerSequencesEmpty_WritesTwoFlowMarkers_AndRoundTrips()
    {
        var value = new NestedSeqEmptyInnerHolder
        {
            Grid = new() { new(), new() },
        };
        var text = RoundTripText(YamlSerializer.Serialize(value));
        await Assert
            .That(text)
            .IsEqualTo("Grid:\n- []\n- []\nCount: 5\n")
            .Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqEmptyInnerHolder>(
            Encoding.UTF8.GetBytes(text)
        );
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0]).IsEmpty();
        await Assert.That(back.Grid[1]).IsEmpty();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// <c>List&lt;List&lt;string?&gt;&gt;</c> is representable, and a null reference element is
    /// written as the YAML null literal (R2 Task 4) instead of being skipped: the nested
    /// chain keeps <c>[a, null, b]</c> as three elements, and <c>[null]</c> as ONE element
    /// rather than an empty row.
    /// </summary>
    [Test]
    public async Task NestedNullableStringGrid_RoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqNullableStringHolder()));
        await Assert.That(text).Contains("Grid:").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqNullableStringHolder>(
            Encoding.UTF8.GetBytes(text)
        );
        await Assert.That(back!.Grid.Count).IsEqualTo(2).Because("actual=" + text);
        await Assert.That(back.Grid[0]!.Count).IsEqualTo(3).Because("actual=" + text);
        await Assert.That(back.Grid[0]![0]).IsEqualTo("a");
        await Assert.That(back.Grid[0]![1]).IsNull();
        await Assert.That(back.Grid[0]![2]).IsEqualTo("b");
        await Assert.That(back.Grid[1]!.Count).IsEqualTo(1).Because("actual=" + text);
        await Assert.That(back.Grid[1]![0]).IsNull();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    // ── Nested objects containing lists (the removed YAML-only drop) ──

    [Test]
    public async Task NestedObjectWithObjectSequence_IsNoLongerDropped()
    {
        var value = new NestedSeqOuter
        {
            Child = new NestedSeqInner
            {
                Rows = new()
                {
                    new() { Name = "a", Value = 1 },
                    new() { Name = "b", Value = 2 },
                },
                N = 7,
            },
        };
        var text = RoundTripText(YamlSerializer.Serialize(value));
        await Assert.That(text).Contains("Rows").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqOuter>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Child.Rows.Count).IsEqualTo(2).Because("actual=" + text);
        await Assert.That(back.Child.Rows[0].Name).IsEqualTo("a");
        await Assert.That(back.Child.Rows[1].Value).IsEqualTo(2);
        await Assert.That(back.Child.N).IsEqualTo(7);
        await Assert.That(back.Total).IsEqualTo(9);
    }

    [Test]
    public async Task NestedSequenceInsideNestedObject_RoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqGridOuter()));
        await Assert.That(text).Contains("Grid").Because("actual=" + text);

        var back = YamlSerializer.Deserialize<NestedSeqGridOuter>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Child.Grid.Count).IsEqualTo(2).Because("actual=" + text);
        await Assert.That(back.Child.Grid[0]).IsEquivalentTo([1]);
        await Assert.That(back.Child.Grid[1]).IsEquivalentTo([2, 3]);
        await Assert.That(back.Child.N).IsEqualTo(1);
        await Assert.That(back.Total).IsEqualTo(9);
    }

    // ── Options and streaming ──

    /// <summary>
    /// The fixture defaults already hold a grid, so a dropped member would leave those
    /// untouched and mask the defect: every streaming/option case uses its own values.
    /// </summary>
    [Test]
    public async Task NestedIntGrid_IndentedOption_RoundTrips()
    {
        var value = new NestedSeqIntHolder
        {
            Grid = new()
            {
                new() { 7 },
                new() { 8, 9 },
            },
        };
        var text = RoundTripText(
            YamlSerializer.Serialize(value, new YamlOptions { Indented = true })
        );
        var back = YamlSerializer.Deserialize<NestedSeqIntHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid.Count).IsEqualTo(2).Because("actual=" + text);
        await Assert.That(back.Grid[0]).IsEquivalentTo([7]);
        await Assert.That(back.Grid[1]).IsEquivalentTo([8, 9]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task NestedIntGrid_StreamingRoundTrip()
    {
        var value = new NestedSeqIntHolder
        {
            Grid = new()
            {
                new() { 7 },
                new() { 8, 9 },
            },
        };
        var bytes = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(value));
        using var stream = new OneByteStream(bytes);
        var back = await YamlSerializer.DeserializeFromStreamAsync<NestedSeqIntHolder>(stream);
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0]).IsEquivalentTo([7]);
        await Assert.That(back.Grid[1]).IsEquivalentTo([8, 9]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task NestedDtoGrid_StreamingRoundTrip()
    {
        var value = new NestedSeqDtoHolder
        {
            Grid = new()
            {
                new()
                {
                    new() { Name = "x", Value = 42 },
                },
            },
        };
        var bytes = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(value));
        using var stream = new OneByteStream(bytes);
        var back = await YamlSerializer.DeserializeFromStreamAsync<NestedSeqDtoHolder>(stream);
        await Assert.That(back!.Grid.Count).IsEqualTo(1);
        await Assert.That(back.Grid[0][0].Name).IsEqualTo("x");
        await Assert.That(back.Grid[0][0].Value).IsEqualTo(42);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task ThreeLevelNestedGrid_RoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqDeepHolder()));
        var back = YamlSerializer.Deserialize<NestedSeqDeepHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Grid.Count).IsEqualTo(1).Because("actual=" + text);
        await Assert.That(back.Grid[0].Count).IsEqualTo(2);
        await Assert.That(back.Grid[0][0]).IsEquivalentTo([1, 2]);
        await Assert.That(back.Grid[0][1]).IsEquivalentTo([3]);
        await Assert.That(back.Count).IsEqualTo(5);
    }

    [Test]
    public async Task NestedGridAsLastMember_RoundTrips()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqLastHolder()));
        var back = YamlSerializer.Deserialize<NestedSeqLastHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Name).IsEqualTo("n").Because("actual=" + text);
        await Assert.That(back.Grid.Count).IsEqualTo(2).Because("actual=" + text);
        await Assert.That(back.Grid[0]).IsEquivalentTo([1]);
        await Assert.That(back.Grid[1]).IsEquivalentTo([2, 3]);
    }

    /// <summary>A null nested sequence is omitted (YAML has no null literal), not written.</summary>
    [Test]
    public async Task NullNestedGrid_IsOmitted()
    {
        var text = RoundTripText(YamlSerializer.Serialize(new NestedSeqNullableHolder()));
        await Assert.That(text).IsEqualTo("Count: 5\n").Because("actual=" + text);
        var back = YamlSerializer.Deserialize<NestedSeqNullableHolder>(
            Encoding.UTF8.GetBytes(text)
        );
        await Assert.That(back!.Grid).IsNull();
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
