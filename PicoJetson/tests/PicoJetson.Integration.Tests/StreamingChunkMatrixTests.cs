using System.Text;

namespace PicoJetson.Tests;

public class StreamRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class StreamInner
{
    public string Name { get; set; } = "";
    public int N { get; set; }
}

public record StreamRec(int Id, string Name, StreamInner? Nested);

public class StreamRequired
{
    public required string A { get; set; }
    public required int B { get; set; }
    public string Tail { get; set; } = "";
}

public class StreamDoc
{
    public string Scalar { get; set; } = "";
    public List<int> Numbers { get; set; } = new();
    public List<StreamRow> Rows { get; set; } = new();
    public Dictionary<string, int> Map { get; set; } = new();
    public StreamInner? Nested { get; set; }
    public List<List<int>> Grid { get; set; } = new();
    public string Tail { get; set; } = "";
}

public sealed class ChunkedReadStream(byte[] data, int chunk) : Stream
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
        int n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        await Task.Yield();
        int n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _pos);
        data.AsSpan(_pos, n).CopyTo(buffer.Span);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

/// <summary>
/// Chunk-boundary contract: DeserializeFromStreamAsync must produce exactly the
/// same object as the synchronous path for any chunk size.
/// </summary>
public class StreamingChunkMatrixTests
{
    private static StreamDoc MakeDoc() =>
        new()
        {
            Scalar = "head",
            Numbers = { 1, 2, 3 },
            Rows =
            {
                new StreamRow { Id = 1, Name = "a" },
                new StreamRow { Id = 2, Name = "bb" },
            },
            Map = { ["k1"] = 1, ["k2"] = 2 },
            Nested = new StreamInner { Name = "in", N = 7 },
            Grid =
            {
                new List<int> { 1, 2 },
                new List<int> { 3 },
            },
            Tail = "END",
        };

    private static async Task AssertSame(StreamDoc expected, StreamDoc actual)
    {
        await Assert.That(actual.Scalar).IsEqualTo(expected.Scalar);
        await Assert.That(actual.Numbers).IsEquivalentTo(expected.Numbers);
        await Assert
            .That(actual.Rows.Select(r => r.Id + ":" + r.Name))
            .IsEquivalentTo(expected.Rows.Select(r => r.Id + ":" + r.Name));
        await Assert.That(actual.Map).IsEquivalentTo(expected.Map);
        await Assert.That(actual.Nested?.Name).IsEqualTo(expected.Nested?.Name);
        await Assert.That(actual.Nested?.N).IsEqualTo(expected.Nested?.N);
        await Assert
            .That(actual.Grid.Select(g => string.Join(",", g)))
            .IsEquivalentTo(expected.Grid.Select(g => string.Join(",", g)));
        await Assert.That(actual.Tail).IsEqualTo(expected.Tail);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(8)]
    [Arguments(16)]
    [Arguments(4096)]
    public async Task Json_Stream_MatchesSync_ForChunkSize(int chunk, CancellationToken ct)
    {
        var doc = MakeDoc();
        var json = JsonSerializer.Serialize(doc);
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(json), chunk);
        var streamed = await JsonSerializer.DeserializeFromStreamAsync<StreamDoc>(s, null, ct);
        await AssertSame(doc, streamed!);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8)]
    public async Task Json_Stream_CtorRecord_MatchesSync(int chunk, CancellationToken ct)
    {
        var rec = new StreamRec(7, "ctor", new StreamInner { Name = "deep", N = 3 });
        var json = JsonSerializer.Serialize(rec);
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(json), chunk);
        var back = await JsonSerializer.DeserializeFromStreamAsync<StreamRec>(s, null, ct);
        await Assert.That(back!.Id).IsEqualTo(7);
        await Assert.That(back.Name).IsEqualTo("ctor");
        await Assert.That(back.Nested?.Name).IsEqualTo("deep");
        await Assert.That(back.Nested?.N).IsEqualTo(3);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(4096)]
    public async Task Json_Stream_ObjectArray_MatchesSync(int chunk, CancellationToken ct)
    {
        var rows = new[]
        {
            new StreamRow { Id = 1, Name = "a" },
            new StreamRow { Id = 2, Name = "bb" },
            new StreamRow { Id = 3, Name = "ccc" },
        };
        var json = JsonSerializer.Serialize(rows);
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(json), chunk);
        var back = await JsonSerializer.DeserializeFromStreamAsync<StreamRow[]>(s, null, ct);
        await Assert
            .That(back!.Select(r => r.Id + ":" + r.Name))
            .IsEquivalentTo(rows.Select(r => r.Id + ":" + r.Name));
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8)]
    public async Task Json_Stream_RequiredProperties_AcrossChunks(int chunk, CancellationToken ct)
    {
        // Required-property flags must survive chunk resumes: a flag lost at a
        // boundary would raise a false "Missing required property" error.
        var doc = new StreamRequired
        {
            A = "first",
            B = 42,
            Tail = "END",
        };
        var json = JsonSerializer.Serialize(doc);
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(json), chunk);
        var back = await JsonSerializer.DeserializeFromStreamAsync<StreamRequired>(s, null, ct);
        await Assert.That(back!.A).IsEqualTo("first");
        await Assert.That(back.B).IsEqualTo(42);
        await Assert.That(back.Tail).IsEqualTo("END");
    }

    [Test]
    [Timeout(60_000)]
    public async Task Json_Stream_RealFileStream_2000Rows(CancellationToken ct)
    {
        var doc = new StreamDoc { Tail = "END" };
        for (int i = 0; i < 2000; i++)
            doc.Rows.Add(new StreamRow { Id = i, Name = "n" + i });
        var json = JsonSerializer.Serialize(doc);
        var path = Path.Combine(Path.GetTempPath(), "picoserde-plan1.json");
        await File.WriteAllTextAsync(path, json, ct);
        try
        {
            using var fs = File.OpenRead(path);
            var back = await JsonSerializer.DeserializeFromStreamAsync<StreamDoc>(fs, null, ct);
            await Assert.That(back!.Rows.Count).IsEqualTo(2000);
            await Assert.That(back.Tail).IsEqualTo("END");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
