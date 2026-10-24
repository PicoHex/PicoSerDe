using PicoToml;

namespace PicoToml.Tests;

// ── Fixtures: nested primitive arrays (F6) ────────────────────────────────
// TOML can express an inline array of inline arrays (`G = [[1, 2], [3]]`), so
// these members used to be dropped by the shared PICOSERDE004 diagnostic for
// no good reason.

public class TomlNestedIntGridDoc
{
    public string K { get; set; } = string.Empty;
    public List<List<int>> Grid { get; set; } = new();
}

public class TomlNestedIntArrayDoc
{
    public string K { get; set; } = string.Empty;
    public List<int[]> Chunks { get; set; } = new();
}

public class TomlNestedJaggedArrayDoc
{
    public string K { get; set; } = string.Empty;
    public int[][] Rows { get; set; } = [];
}

public class TomlNestedMixedLeafDoc
{
    public string K { get; set; } = string.Empty;
    public List<List<long>> Longs { get; set; } = new();
    public List<List<bool>> Flags { get; set; } = new();
    public List<List<ushort>> Ports { get; set; } = new();
    public List<List<double>> Ratios { get; set; } = new();
}

public class TomlNestedStringGridDoc
{
    public string K { get; set; } = string.Empty;
    public List<List<string>> Rows { get; set; } = new();
}

public class TomlNestedTripleDoc
{
    public string K { get; set; } = string.Empty;
    public List<List<List<int>>> Cube { get; set; } = new();
}

/// <summary>Regression fixture: object element lists keep the <c>[[Rows]]</c> array-table form.</summary>
public class TomlNestedArrayRow
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
}

public class TomlNestedArrayTableDoc
{
    public string K { get; set; } = string.Empty;
    public List<TomlNestedArrayRow> Rows { get; set; } = new();
}

/// <summary>
/// F6: nested primitive arrays in TOML. The wire form is an inline array of
/// inline arrays (TOML-native); every shape below must round-trip through the
/// span reader and the streaming reader, and the member must survive next to
/// its siblings.
/// </summary>
public class TomlNestedArrayTests
{
    [Test]
    public async Task ListOfListInt_WritesInlineArrayOfInlineArrays_AndRoundTrips()
    {
        var doc = new TomlNestedIntGridDoc
        {
            K = "k",
            Grid = new List<List<int>>
            {
                new() { 1, 2 },
                new() { 3 },
            },
        };

        var toml = TomlSerializer.Serialize(doc);

        await Assert.That(toml).Contains("Grid = [[1, 2], [3]]");
        await Assert.That(toml).Contains("K = \"k\"").Because("sibling must survive");

        var back = TomlSerializer.Deserialize<TomlNestedIntGridDoc>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.K).IsEqualTo("k");
        await Assert.That(back.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0]).IsEquivalentTo(new List<int> { 1, 2 });
        await Assert.That(back.Grid[1]).IsEquivalentTo(new List<int> { 3 });
    }

    [Test]
    public async Task ListOfIntArray_RoundTrips()
    {
        var doc = new TomlNestedIntArrayDoc
        {
            K = "k",
            Chunks = new List<int[]> { new[] { 1, 2, 3 }, new[] { 4 } },
        };

        var toml = TomlSerializer.Serialize(doc);

        await Assert.That(toml).Contains("Chunks = [[1, 2, 3], [4]]");

        var back = TomlSerializer.Deserialize<TomlNestedIntArrayDoc>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Chunks.Count).IsEqualTo(2);
        await Assert.That(back.Chunks[0]).IsEquivalentTo(new[] { 1, 2, 3 });
        await Assert.That(back.Chunks[1]).IsEquivalentTo(new[] { 4 });
    }

    /// <summary>A jagged array member (<c>int[][]</c>) is the same nested shape.</summary>
    [Test]
    public async Task JaggedArrayMember_RoundTrips()
    {
        var doc = new TomlNestedJaggedArrayDoc { K = "k", Rows = [new[] { 1, 2 }, new[] { 3 }] };

        var toml = TomlSerializer.Serialize(doc);

        await Assert.That(toml).Contains("Rows = [[1, 2], [3]]");

        var back = TomlSerializer.Deserialize<TomlNestedJaggedArrayDoc>(
            Encoding.UTF8.GetBytes(toml)
        );

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Rows.Length).IsEqualTo(2);
        await Assert.That(back.Rows[0]).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(back.Rows[1]).IsEquivalentTo(new[] { 3 });
    }

    /// <summary>
    /// The representable leaf kinds are the ones the TOML list writer and reader
    /// both handle; this pins a few of them (64-bit, bool, 16-bit unsigned, double).
    /// </summary>
    [Test]
    public async Task ListOfList_OtherScalarLeafKinds_RoundTrip()
    {
        var doc = new TomlNestedMixedLeafDoc
        {
            K = "k",
            Longs = new List<List<long>>
            {
                new() { long.MaxValue, -1L },
            },
            Flags = new List<List<bool>>
            {
                new() { true, false },
            },
            Ports = new List<List<ushort>> { new() { ushort.MaxValue } },
            Ratios = new List<List<double>>
            {
                new() { 0.5, -1.25 },
            },
        };

        var toml = TomlSerializer.Serialize(doc);
        var back = TomlSerializer.Deserialize<TomlNestedMixedLeafDoc>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Longs[0][0]).IsEqualTo(long.MaxValue);
        await Assert.That(back.Longs[0][1]).IsEqualTo(-1L);
        await Assert.That(back.Flags[0][0]).IsTrue();
        await Assert.That(back.Flags[0][1]).IsFalse();
        await Assert.That(back.Ports[0][0]).IsEqualTo(ushort.MaxValue);
        await Assert.That(back.Ratios[0][0]).IsEqualTo(0.5);
        await Assert.That(back.Ratios[0][1]).IsEqualTo(-1.25);
    }

    [Test]
    public async Task ListOfListString_EscapesElements_AndRoundTrips()
    {
        var doc = new TomlNestedStringGridDoc
        {
            K = "k",
            Rows = new List<List<string>>
            {
                new() { "a,b", "一\n二" },
                new() { "plain" },
            },
        };

        var toml = TomlSerializer.Serialize(doc);

        // A comma inside a string must not leak into the array structure, and
        // the newline must be escaped (a raw newline would end the value).
        await Assert.That(toml).Contains("\"a,b\"");
        await Assert.That(toml).Contains("\"一\\n二\"");
        await Assert.That(toml).DoesNotContain("一\n二");

        var back = TomlSerializer.Deserialize<TomlNestedStringGridDoc>(
            Encoding.UTF8.GetBytes(toml)
        );

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Rows.Count).IsEqualTo(2);
        await Assert.That(back.Rows[0][0]).IsEqualTo("a,b");
        await Assert.That(back.Rows[0][1]).IsEqualTo("一\n二");
        await Assert.That(back.Rows[1][0]).IsEqualTo("plain");
    }

    [Test]
    public async Task ListOfListOfListInt_RoundTrips()
    {
        var doc = new TomlNestedTripleDoc
        {
            K = "k",
            Cube = new List<List<List<int>>>
            {
                new()
                {
                    new() { 1, 2 },
                    new() { 3 },
                },
                new() { new() { 4 } },
            },
        };

        var toml = TomlSerializer.Serialize(doc);

        await Assert.That(toml).Contains("Cube = [[[1, 2], [3]], [[4]]]");

        var back = TomlSerializer.Deserialize<TomlNestedTripleDoc>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Cube.Count).IsEqualTo(2);
        await Assert.That(back.Cube[0].Count).IsEqualTo(2);
        await Assert.That(back.Cube[0][0]).IsEquivalentTo(new List<int> { 1, 2 });
        await Assert.That(back.Cube[0][1]).IsEquivalentTo(new List<int> { 3 });
        await Assert.That(back.Cube[1].Count).IsEqualTo(1);
        await Assert.That(back.Cube[1][0]).IsEquivalentTo(new List<int> { 4 });
    }

    [Test]
    public async Task EmptyInnerAndFullyEmptyArrays_RoundTrip()
    {
        var doc = new TomlNestedIntGridDoc
        {
            K = "k",
            Grid = new List<List<int>>
            {
                new(),
                new() { 1 },
            },
        };

        var toml = TomlSerializer.Serialize(doc);
        await Assert.That(toml).Contains("Grid = [[], [1]]");

        var back = TomlSerializer.Deserialize<TomlNestedIntGridDoc>(Encoding.UTF8.GetBytes(toml));
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Grid.Count).IsEqualTo(2);
        await Assert.That(back.Grid[0].Count).IsEqualTo(0);
        await Assert.That(back.Grid[1]).IsEquivalentTo(new List<int> { 1 });

        var empty = new TomlNestedIntGridDoc { K = "k", Grid = new List<List<int>>() };
        var emptyToml = TomlSerializer.Serialize(empty);
        await Assert.That(emptyToml).Contains("Grid = []");

        var emptyBack = TomlSerializer.Deserialize<TomlNestedIntGridDoc>(
            Encoding.UTF8.GetBytes(emptyToml)
        );
        await Assert.That(emptyBack).IsNotNull();
        await Assert.That(emptyBack!.Grid.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The wire form has to be genuine TOML, not just something our own reader
    /// happens to accept: Tomlyn (an independent parser) must see an array whose
    /// items are arrays.
    /// </summary>
    /// <summary>
    /// Hand-written TOML is usually multi-line, with a trailing comma and comments
    /// between the levels — the reader must not care about the layout our writer
    /// happens to produce.
    /// </summary>
    [Test]
    public async Task HandWrittenMultilineNestedArray_IsRead()
    {
        const string toml = """
            K = "k"
            Grid = [
              [1, 2], # first
              []
              ,
              [3],
            ]
            """;

        var back = TomlSerializer.Deserialize<TomlNestedIntGridDoc>(Encoding.UTF8.GetBytes(toml));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.K).IsEqualTo("k");
        await Assert.That(back.Grid.Count).IsEqualTo(3);
        await Assert.That(back.Grid[0]).IsEquivalentTo(new List<int> { 1, 2 });
        await Assert.That(back.Grid[1].Count).IsEqualTo(0);
        await Assert.That(back.Grid[2]).IsEquivalentTo(new List<int> { 3 });
    }

    [Test]
    public async Task InlineArrayOfInlineArrays_IsParsedByTomlyn()
    {
        var doc = new TomlNestedIntGridDoc
        {
            K = "k",
            Grid = new List<List<int>>
            {
                new() { 1, 2 },
                new() { 3 },
            },
        };

        var toml = TomlSerializer.Serialize(doc);
        var table = Tomlyn.TomlSerializer.Deserialize<TomlTable>(toml);

        await Assert.That(table).IsNotNull();
        var grid = (TomlArray)table!["Grid"];
        await Assert.That(grid.Count).IsEqualTo(2);
        var first = (TomlArray)grid[0]!;
        var second = (TomlArray)grid[1]!;
        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That((long)first[0]!).IsEqualTo(1L);
        await Assert.That((long)first[1]!).IsEqualTo(2L);
        await Assert.That(second.Count).IsEqualTo(1);
        await Assert.That((long)second[0]!).IsEqualTo(3L);
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(4096)]
    public async Task Streaming_MatchesSpanReader(int chunk)
    {
        var doc = new TomlNestedIntGridDoc
        {
            K = "k",
            Grid = new List<List<int>>
            {
                new() { 1, 2 },
                new(),
                new() { 3 },
            },
        };

        var toml = TomlSerializer.Serialize(doc);
        using var stream = new ChunkedReadStream(Encoding.UTF8.GetBytes(toml), chunk);
        var back = await TomlSerializer.DeserializeFromStreamAsync<TomlNestedIntGridDoc>(stream);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.K).IsEqualTo("k");
        await Assert.That(back.Grid.Count).IsEqualTo(3);
        await Assert.That(back.Grid[0]).IsEquivalentTo(new List<int> { 1, 2 });
        await Assert.That(back.Grid[1].Count).IsEqualTo(0);
        await Assert.That(back.Grid[2]).IsEquivalentTo(new List<int> { 3 });
    }

    [Test]
    public async Task Streaming_ObjectElementArrayTable_StillUsesArrayTables()
    {
        var doc = new TomlNestedArrayTableDoc
        {
            K = "k",
            Rows = new List<TomlNestedArrayRow>
            {
                new() { Name = "a", Score = 1 },
                new() { Name = "b", Score = 2 },
            },
        };

        var toml = TomlSerializer.Serialize(doc);

        await Assert.That(toml).Contains("[[Rows]]").Because("object lists stay array tables");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(toml));
        var back = await TomlSerializer.DeserializeFromStreamAsync<TomlNestedArrayTableDoc>(stream);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Rows.Count).IsEqualTo(2);
        await Assert.That(back.Rows[1].Name).IsEqualTo("b");
        await Assert.That(back.Rows[1].Score).IsEqualTo(2);
    }

    /// <summary>Reads at most <paramref name="chunk"/> bytes per call to exercise the streaming reader.</summary>
    private sealed class ChunkedReadStream(byte[] data, int chunk) : Stream
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
            if (n <= 0)
                return 0;
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _pos);
            if (n <= 0)
                return ValueTask.FromResult(0);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return ValueTask.FromResult(n);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
