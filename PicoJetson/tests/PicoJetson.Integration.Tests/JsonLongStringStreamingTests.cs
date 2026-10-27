using System.Text;

namespace PicoJetson.Tests;

/// <summary>Long-string DTO for the chunked (streaming) scan tests (P0-3).</summary>
public class ScanLongString
{
    public string Body { get; set; } = "";
}

/// <summary>
/// End-to-end guard for the vectorized string scan on the chunked reader: a string
/// longer than the pooled 256-byte scratch buffer, read across chunk boundaries
/// (including boundaries inside an escape sequence), must deserialize to exactly
/// the same value as the synchronous path.
/// </summary>
public class JsonLongStringStreamingTests
{
    private static string Bodies(string kind, int length)
    {
        var unit = kind switch
        {
            "plain" => "x",
            "escapes" => "a\"b\\c\nd\te",
            "unicode" => "aé😀z",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var sb = new StringBuilder(length + unit.Length);
        while (sb.Length < length)
            sb.Append(unit);
        var body = sb.ToString();
        // Never split a surrogate pair when trimming to the requested length.
        var cut = length;
        if (cut > 0 && cut < body.Length && char.IsHighSurrogate(body[cut - 1]))
            cut--;
        return body[..cut];
    }

    [Test]
    [Timeout(120_000)]
    [Arguments("plain", 1024, 1)]
    [Arguments("plain", 1024, 7)]
    [Arguments("plain", 1024, 256)]
    [Arguments("plain", 65536, 1)]
    [Arguments("plain", 65536, 256)]
    [Arguments("plain", 65536, 4096)]
    [Arguments("escapes", 1024, 1)]
    [Arguments("escapes", 1024, 3)]
    [Arguments("escapes", 1024, 256)]
    [Arguments("escapes", 65536, 256)]
    [Arguments("unicode", 1024, 1)]
    [Arguments("unicode", 1024, 5)]
    [Arguments("unicode", 65536, 4096)]
    public async Task StreamedLongString_MatchesTheSyncPath(
        string kind,
        int length,
        int chunk,
        CancellationToken ct
    )
    {
        var doc = new ScanLongString { Body = Bodies(kind, length) };
        var json = JsonSerializer.Serialize(doc);
        var sync = JsonSerializer.Deserialize<ScanLongString>(Encoding.UTF8.GetBytes(json));

        using var s = new ScanChunkedStream(Encoding.UTF8.GetBytes(json), chunk);
        var streamed = await JsonSerializer.DeserializeFromStreamAsync<ScanLongString>(s, null, ct);

        await Assert.That(sync!.Body.Length).IsEqualTo(doc.Body.Length);
        await Assert.That(sync.Body).IsEqualTo(doc.Body);
        await Assert.That(streamed!.Body.Length).IsEqualTo(doc.Body.Length);
        await Assert.That(streamed.Body).IsEqualTo(doc.Body);
    }

    private sealed class ScanChunkedStream(byte[] data, int chunk) : Stream
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
            var n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
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
            var n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
