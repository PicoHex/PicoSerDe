namespace PicoMsgPack.Tests;

public class NameHolder
{
    public string Name { get; set; } = "";
}

public class IntHolder
{
    public int Value { get; set; }
}

public class LongHolder
{
    public long Value { get; set; }
}

public class BytesHolder
{
    public byte[] Data { get; set; } = [];
}

public class PayloadRobustnessTests
{
    [Test]
    public void TruncatedMap_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<NameHolder>(
                new byte[] { 0x82, 0x00, 0xA1, (byte)'x', 0x01 }
            )
        );

    [Test]
    public void StringKeyMap_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<IntHolder>(new byte[] { 0x81, 0xA1, (byte)'k', 0x01 })
        );

    [Test]
    public void UInt64OutOfInt64Range_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<LongHolder>(
                new byte[] { 0x81, 0x00, 0xCF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }
            )
        );

    [Test]
    public void NegativeBinLength_ThrowsFormatException() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<BytesHolder>(
                new byte[] { 0x81, 0x00, 0xC6, 0xFF, 0xFF, 0xFF, 0xFF }
            )
        );

    /// <summary>
    /// A legal payload split across chunk boundaries must still deserialize —
    /// the closure guard runs only when the reader is NOT waiting for more data,
    /// so truncation and chunking stay distinguishable.
    /// </summary>
    [Test]
    public async Task StreamingChunkBoundaries_StillDeserialize()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new NameHolder { Name = "chunked-value" }
        );
        foreach (var chunkSize in new[] { 1, 2, 3, 5 })
        {
            using var chunked = new ChunkedStream(bytes, chunkSize);
            var result = await MsgPackSerializer.DeserializeFromStreamAsync<NameHolder>(chunked);
            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Name).IsEqualTo("chunked-value");
        }
    }

    /// <summary>TrySkip reports false while data is missing (no swallowed FormatException).</summary>
    [Test]
    public async Task TrySkip_TruncatedSequence_ReturnsFalse()
    {
        // fixarray(2) with only one element present, non-final block.
        var seq = new ReadOnlySequence<byte>(new byte[] { 0x92, 0x01 });
        var (needsMore, skipped) = SkipAfterTruncation(seq);
        await Assert.That(needsMore).IsTrue();
        await Assert.That(skipped).IsFalse();
    }

    /// <summary>A complete container is skipped and reports true.</summary>
    [Test]
    public async Task TrySkip_CompleteContainer_ReturnsTrue()
    {
        var (skipped, depth) = SkipCompleteContainer();
        await Assert.That(skipped).IsTrue();
        await Assert.That(depth).IsEqualTo(0);
    }

    // A ref struct reader cannot cross an await boundary, so the reader work
    // lives in its own non-async helper and only the results are asserted.
    private static (bool NeedsMore, bool Skipped) SkipAfterTruncation(ReadOnlySequence<byte> seq)
    {
        using var reader = new MsgPackReader(seq, isFinalBlock: false);
        reader.Read(); // ArrayStart
        reader.Read(); // 0x01
        reader.Read(); // hits end of a non-final block -> NeedsMoreData
        return (reader.NeedsMoreData, reader.TrySkip());
    }

    private static (bool Skipped, int Depth) SkipCompleteContainer()
    {
        using var reader = new MsgPackReader(new byte[] { 0x92, 0x01, 0x02 });
        reader.Read(); // ArrayStart
        var skipped = reader.TrySkip();
        return (skipped, reader.Depth);
    }

    /// <summary>Returns at most <c>chunkSize</c> bytes per read.</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(chunkSize, count), data.Length - _offset);
            Array.Copy(data, _offset, buffer, offset, n);
            _offset += n;
            return n;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
