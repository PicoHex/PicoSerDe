using System.Text;

namespace PicoSerDe.Integration.Tests;

public class SkDoc
{
    public string Scalar { get; set; } = "";
    public int Count { get; set; }
    public List<int> Numbers { get; set; } = new();
    public SkInner? Nested { get; set; }
}

public class SkInner
{
    public string Name { get; set; } = "";
    public int N { get; set; }
}

public class SkListDoc
{
    public string Scalar { get; set; } = "";
    public List<SkRow> Rows { get; set; } = new();
}

public class SkRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class SkChunkedStream(byte[] data, int chunk) : Stream
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
/// TOML/YAML/INI chunk-boundary contract: streaming must equal the synchronous
/// result for any chunk size, including nested objects and collections.
/// </summary>
public class StreamingContractTests
{
    private static SkDoc MakeDoc() =>
        new()
        {
            Scalar = "head",
            Count = 7,
            Numbers = { 1, 2, 3 },
            Nested = new SkInner { Name = "in", N = 42 },
        };

    private static async Task AssertSame(SkDoc expected, SkDoc? actual)
    {
        await Assert.That(actual).IsNotNull();
        await Assert.That(actual!.Scalar).IsEqualTo(expected.Scalar);
        await Assert.That(actual.Count).IsEqualTo(expected.Count);
        await Assert.That(actual.Numbers).IsEquivalentTo(expected.Numbers);
        await Assert.That(actual.Nested?.Name).IsEqualTo(expected.Nested?.Name);
        await Assert.That(actual.Nested?.N).IsEqualTo(expected.Nested?.N);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(4096)]
    public async Task Toml_Stream_MatchesSync(int chunk, CancellationToken ct)
    {
        var doc = MakeDoc();
        var text = TomlSerializer.Serialize(doc);
        using var s = new SkChunkedStream(Encoding.UTF8.GetBytes(text), chunk);
        var back = await TomlSerializer.DeserializeFromStreamAsync<SkDoc>(s, ct);
        await AssertSame(doc, back);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(4096)]
    public async Task Yaml_Stream_MatchesSync(int chunk, CancellationToken ct)
    {
        var doc = MakeDoc();
        var text = YamlSerializer.Serialize(doc);
        using var s = new SkChunkedStream(Encoding.UTF8.GetBytes(text), chunk);
        var back = await YamlSerializer.DeserializeFromStreamAsync<SkDoc>(s, ct);
        await AssertSame(doc, back);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(4096)]
    public async Task Ini_Stream_MatchesSync(int chunk, CancellationToken ct)
    {
        var doc = MakeDoc();
        var text = IniSerializer.Serialize(doc);
        using var s = new SkChunkedStream(Encoding.UTF8.GetBytes(text), chunk);
        var back = await IniSerializer.DeserializeFromStreamAsync<SkDoc>(s, ct);
        await AssertSame(doc, back);
    }
}
