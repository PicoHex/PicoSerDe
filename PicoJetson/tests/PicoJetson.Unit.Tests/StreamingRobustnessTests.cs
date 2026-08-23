namespace PicoJetson.Tests;

/// <summary>
/// C1 regression tests: streaming deserialization must survive chunk
/// boundaries anywhere in the payload (property names, values, numbers,
/// arrays) without losing parsed state.
/// </summary>
public class StreamingRobustnessTests
{
    // Hand-written streaming funcs (the runtime API without the SG) so this
    // stays a pure unit test of DeserializeFromStreamAsync's state handling.
    private sealed class Pair
    {
        public int A;
        public string B = "";
    }

    private sealed class IntDeserializer : IDeserializer<int>
    {
        public int Deserialize(ReadOnlySpan<byte> data)
        {
            var r = new JsonReader(data);
            r.Read();
            r.TryGetInt32(out var v);
            return v;
        }
    }

    private static ReadStatus PairFunc(ref JsonReader r, Pair? partial, out Pair? v)
    {
        v = partial ?? new Pair();
        // Consume the opening token (the object start) on the first call only,
        // mirroring the SG-generated streaming deserializer structure.
        if (!r.IsResumed && !r.Read())
            return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;
        while (true)
        {
            if (!r.Read())
                return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;
            if (r.TokenType != TokenType.PropertyName)
                break;
            var name = r.GetStringRaw();
            if (!r.Read())
                return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;
            if (name.SequenceEqual("A"u8))
            {
                r.TryGetInt32(out v.A);
            }
            else if (name.SequenceEqual("B"u8))
            {
                v.B = Encoding.UTF8.GetString(r.GetStringRaw());
            }
            else
            {
                r.TrySkip();
            }
        }
        return ReadStatus.Success;
    }

    private static ReadStatus IntArrayFunc(ref JsonReader r, int[]? partial, out int[]? v)
    {
        var list = r.StreamState as List<int> ?? new List<int>();
        r.StreamState = list;
        v = default;
        while (r.Read())
        {
            if (r.TokenType == TokenType.ArrayEnd)
                break;
            if (r.TryGetInt32(out var iv))
                list.Add(iv);
        }
        if (r.NeedsMoreData)
            return ReadStatus.NeedMoreData;
        v = list.ToArray();
        return ReadStatus.Success;
    }

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _pos;

        public ChunkedStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length)
                return 0;
            int n = Math.Min(Math.Min(count, _chunk), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default
        )
        {
            if (_pos >= _data.Length)
                return 0;
            int n = Math.Min(Math.Min(buffer.Length, _chunk), _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Test]
    public async Task DeserializeFromStreamAsync_ObjectSplitAcrossChunks_PreservesAllValues()
    {
        JsonSerializer.RegisterStreaming<Pair>(PairFunc);
        try
        {
            var payload = "{\"A\":30,\"B\":\"Alice\"}"u8.ToArray();
            var stream = new ChunkedStream(payload, 5);
            var pair = await JsonSerializer.DeserializeFromStreamAsync<Pair>(stream);
            await Assert.That(pair.A).IsEqualTo(30);
            await Assert.That(pair.B).IsEqualTo("Alice");
        }
        finally
        {
            JsonSerializer.RegisterStreaming<Pair>(PairFunc);
        }
    }

    [Test]
    public async Task DeserializeFromStreamAsync_NumberSplitAcrossChunks_PreservesValue()
    {
        JsonSerializer.RegisterStreaming<Pair>(PairFunc);
        try
        {
            // "12345" split across 2-byte chunks: the number must not be
            // tokenized as a partial value.
            var payload = "{\"A\":12345}"u8.ToArray();
            var stream = new ChunkedStream(payload, 2);
            var pair = await JsonSerializer.DeserializeFromStreamAsync<Pair>(stream);
            await Assert.That(pair.A).IsEqualTo(12345);
        }
        finally
        {
            JsonSerializer.RegisterStreaming<Pair>(PairFunc);
        }
    }

    [Test]
    public async Task DeserializeFromStreamAsync_IntArraySplitAcrossChunks_PreservesAllElements()
    {
        JsonSerializer.RegisterStreaming<int[]>(IntArrayFunc);
        try
        {
            var payload = "[10,20,30]"u8.ToArray();
            var stream = new ChunkedStream(payload, 3);
            var arr = await JsonSerializer.DeserializeFromStreamAsync<int[]>(stream);
            await Assert.That(arr).Count().IsEqualTo(3);
            await Assert.That(arr[0]).IsEqualTo(10);
            await Assert.That(arr[1]).IsEqualTo(20);
            await Assert.That(arr[2]).IsEqualTo(30);
        }
        finally
        {
            JsonSerializer.RegisterStreaming<int[]>(IntArrayFunc);
        }
    }

    [Test]
    public async Task DeserializeAsyncEnumerable_ArrayMode_YieldsPrimitiveElements()
    {
        JsonSerializer.RegisterDeserializer(new IntDeserializer());
        try
        {
            var payload = "[1,2,3]"u8.ToArray();
            var stream = new MemoryStream(payload);
            var items = new List<int>();
            await foreach (
                var item in JsonSerializer.DeserializeAsyncEnumerable<int>(
                    stream,
                    topLevelValues: false
                )
            )
            {
                items.Add(item);
            }
            await Assert.That(items).Count().IsEqualTo(3);
            await Assert.That(items[0]).IsEqualTo(1);
            await Assert.That(items[1]).IsEqualTo(2);
            await Assert.That(items[2]).IsEqualTo(3);
        }
        finally
        {
            JsonSerializer.RegisterDeserializer(new IntDeserializer());
        }
    }
}
