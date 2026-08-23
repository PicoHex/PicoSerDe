namespace PicoJetson.Tests;

public class StreamingDoc
{
    public string? Name { get; set; }
    public int Count { get; set; }
    public double Value { get; set; }
}

public class StreamingOptionsTests
{

    [Test]
    public async Task ConcurrentStreams_KeepTheirOwnOptions()
    {
        // P1 regression: options must not bleed between concurrent streaming
        // calls (the old [ThreadStatic] mechanism lost/leaked them across awaits).
        // NaN is the observable: strict (default) throws, lenient accepts it.
        var strictOpts = new JsonOptions();
        var lenient = new JsonOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var strictStream = new MemoryStream("{ \"Value\": NaN }"u8.ToArray());
        var lenientStream = new MemoryStream("{ \"Value\": NaN }"u8.ToArray());
        var strictTask = JsonSerializer.DeserializeFromStreamAsync<StreamingDoc>(strictStream, strictOpts);
        var lenientTask = JsonSerializer.DeserializeFromStreamAsync<StreamingDoc>(lenientStream, lenient);

        var strictThrew = false;
        try
        {
            await strictTask;
        }
        catch (FormatException)
        {
            strictThrew = true;
        }
        await Assert.That(strictThrew).IsTrue();

        // The lenient call must NOT throw — its options were not polluted.
        var ok = await lenientTask;
        await Assert.That(ok).IsNotNull();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(7)]
    [Arguments(16)]
    [Arguments(64)]
    public async Task Streaming_ParsesAtEveryChunkBoundary(int chunkSize)
    {
        var doc = new StreamingDoc { Name = "hello", Count = 42 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(doc);
        var stream = new ChunkedStream(new MemoryStream(bytes), chunkSize);
        var result = await JsonSerializer.DeserializeFromStreamAsync<StreamingDoc>(stream);
        Console.WriteLine($"[dbg] chunk={chunkSize} result={(result is null ? "NULL" : $"Name='{result.Name}' Count={result.Count}")}");
        await Assert.That(result!.Count).IsEqualTo(42);
        await Assert.That(result.Name).IsEqualTo("hello");
    }

    [Test]
    public async Task Streaming_WithMaxDepthOption_Respected()
    {
        var deep = "{\"a\":{\"a\":{\"a\":{\"a\":{\"a\":1}}}}}";
        var opts = new JsonOptions { MaxDepth = 3 };
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(deep));
        var threw = false;
        try
        {
            await JsonSerializer.DeserializeFromStreamAsync<StreamingDoc>(stream, opts);
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// Wraps a stream and returns at most chunkSize bytes per ReadAsync —
    /// deterministic chunk-boundary coverage for streaming tests.
    /// </summary>
    internal sealed class ChunkedStream(Stream inner, int chunkSize) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, chunkSize));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
