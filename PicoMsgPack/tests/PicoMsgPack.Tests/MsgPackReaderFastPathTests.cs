namespace PicoMsgPack.Tests;

public class MsgPackReaderFastPathTests
{
    // ── int32 ──

    [Test]
    public async Task TryReadInt32Array_FixInt_Positive()
    {
        // fixarray(3) + [0x01, 0x2A, 0x7F] → [1, 42, 127]
        var data = new byte[] { 0x93, 0x01, 0x2A, 0x7F };
        var reader = new MsgPackReader(data);
        var __dest = new int[4];
        var n = reader.TryReadInt32Array(__dest);
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(__dest[0]).IsEqualTo(1);
        await Assert.That(__dest[1]).IsEqualTo(42);
        await Assert.That(__dest[2]).IsEqualTo(127);
    }

    [Test]
    public async Task TryReadInt32Array_FixInt_Negative()
    {
        // fixarray(2) + [0xFF, 0xE0] → [-1, -32]
        var data = new byte[] { 0x92, 0xFF, 0xE0 };
        var reader = new MsgPackReader(data);
        // TryReadXxxArrayFast reads the array header internally
        var __dest = new int[2];
        var n = reader.TryReadInt32Array(__dest);
        await Assert.That(n).IsEqualTo(2);
        await Assert.That(__dest[0]).IsEqualTo(-1);
        await Assert.That(__dest[1]).IsEqualTo(-32);
    }

    [Test]
    public async Task TryReadInt32Array_MixedFormats()
    {
        // fixarray(4) + [fixint 127, int8 -128, int16 300, int32 100000]
        var data = new byte[]
        {
            0x94,
            0x7F,
            0xD0,
            0x80,
            0xD1,
            0x01,
            0x2C,
            0xD2,
            0x00,
            0x01,
            0x86,
            0xA0,
        };
        var reader = new MsgPackReader(data);
        // TryReadXxxArrayFast reads the array header internally
        var __dest = new int[4];
        var n = reader.TryReadInt32Array(__dest);
        await Assert.That(n).IsEqualTo(4);
        await Assert.That(__dest[0]).IsEqualTo(127);
        await Assert.That(__dest[1]).IsEqualTo(-128);
        await Assert.That(__dest[2]).IsEqualTo(300);
        await Assert.That(__dest[3]).IsEqualTo(100000);
    }

    [Test]
    public async Task TryReadInt32Array_Empty()
    {
        var data = new byte[] { 0x90 }; // fixarray(0)
        var reader = new MsgPackReader(data);
        // TryReadXxxArrayFast reads the array header internally
        var __dest = new int[1];
        var n = reader.TryReadInt32Array(__dest);
        await Assert.That(n).IsEqualTo(0);
    }

    // ── int64 ──

    [Test]
    public async Task TryReadInt64Array_Basic()
    {
        var data = new byte[] { 0x92, 0x01, 0xD3, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
        var reader = new MsgPackReader(data);
        // TryReadXxxArrayFast reads the array header internally
        var __dest = new long[2];
        var n = reader.TryReadInt64Array(__dest);
        await Assert.That(n).IsEqualTo(2);
        await Assert.That(__dest[0]).IsEqualTo(1);
        await Assert.That(__dest[1]).IsEqualTo(0x100000000); // int64: 4294967296
    }

    // ── double ──

    [Test]
    public async Task TryReadDoubleArray_Basic()
    {
        // fixarray(2) + [float64 1.5, float64 -3.0]
        double v1 = 1.5,
            v2 = -3.0;
        var buf = new byte[1 + 2 * 9];
        buf[0] = 0x92;
        buf[1] = 0xCB;
        BitConverter.GetBytes(v1).CopyTo(buf, 2);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(buf, 2, 8);
        buf[10] = 0xCB;
        BitConverter.GetBytes(v2).CopyTo(buf, 11);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(buf, 11, 8);

        var reader = new MsgPackReader(buf);
        // TryReadXxxArrayFast reads the array header internally
        var __dest = new double[2];
        var n = reader.TryReadDoubleArray(__dest);
        await Assert.That(n).IsEqualTo(2);
        await Assert.That(__dest[0]).IsEqualTo(1.5);
        await Assert.That(__dest[1]).IsEqualTo(-3.0);
    }

    // ── bool ──

    [Test]
    public async Task TryReadBoolArray_Basic()
    {
        var data = new byte[] { 0x93, 0xC3, 0xC2, 0xC3 }; // [true, false, true]
        var reader = new MsgPackReader(data);
        // TryReadXxxArrayFast reads the array header internally
        var __dest = new bool[3];
        var n = reader.TryReadBoolArray(__dest);
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(__dest[0]).IsTrue();
        await Assert.That(__dest[1]).IsFalse();
        await Assert.That(__dest[2]).IsTrue();
    }

    // ── 不支持的格式回退 ──

    [Test]
    public async Task TryReadInt32Array_Int64Overflow_ReturnsZero()
    {
        // fixarray(1) + int64 value too large for int32 → should return 0 (fallback)
        var data = new byte[] { 0x91, 0xD3, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
        var reader = new MsgPackReader(data);
        // TryReadXxxArrayFast reads the array header internally
        var __dest = new int[1];
        var n = reader.TryReadInt32Array(__dest);
        await Assert.That(n).IsEqualTo(0); // fallback — caller should use reader loop
    }

    // ── the second entry position: the header already came through Read() ──

    /// <summary>
    /// A generated counted loop reads the start token first (<c>Read()</c>), asks the count with
    /// <c>TryGetCollectionCount</c>, and only then hands the elements to the bulk read. That call
    /// must consume exactly the owed elements, spend the counted scope so the caller's end-token
    /// <c>Read()</c> still produces <see cref="TokenType.ArrayEnd"/>, and leave the position where
    /// <c>count</c> × <see cref="MsgPackReader.Read"/> would have left it.
    /// </summary>
    [Test]
    public async Task TryReadInt32Array_AfterTheHeaderToken_ReadsTheOwedElementsOnly()
    {
        // fixarray(3) + [1, 2, 3] as the whole document (a root array may not be followed by
        // anything: that is trailing garbage, see the span reader's own rule).
        var data = new byte[] { 0x93, 0x01, 0x02, 0x03 };
        var reader = new MsgPackReader(data);
        bool started = reader.Read();
        var startedType = reader.TokenType;
        bool counted = reader.TryGetCollectionCount(out var count);
        var dest = new int[3];
        int n = reader.TryReadInt32Array(dest);
        long consumedAfterBulk = reader.BytesConsumed;
        bool ended = reader.Read();
        var endedType = reader.TokenType;
        int depthAfterEnd = reader.Depth;
        long consumedAfterEnd = reader.BytesConsumed;

        await Assert.That(started).IsTrue();
        await Assert.That(startedType).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(counted).IsTrue();
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(dest).IsEquivalentTo(new[] { 1, 2, 3 });
        await Assert.That(consumedAfterBulk).IsEqualTo(4L);
        // The level counter is spent, so the next Read() owes the end token — not the next element.
        await Assert.That(ended).IsTrue();
        await Assert.That(endedType).IsEqualTo(TokenType.ArrayEnd);
        await Assert.That(depthAfterEnd).IsEqualTo(0);
        await Assert.That(consumedAfterEnd).IsEqualTo(4L);
    }

    /// <summary>A destination smaller than the array reads what fits and leaves the rest owed.</summary>
    [Test]
    public async Task TryReadInt32Array_ShortDestination_ReadsWhatFits()
    {
        var data = new byte[] { 0x93, 0x01, 0x02, 0x03 };
        var reader = new MsgPackReader(data);
        reader.Read();
        var dest = new int[2];
        int n = reader.TryReadInt32Array(dest);
        bool more = reader.Read();
        var moreType = reader.TokenType;
        bool got = reader.TryGetInt32(out var last);

        await Assert.That(n).IsEqualTo(2);
        await Assert.That(dest).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(more).IsTrue();
        await Assert.That(moreType).IsEqualTo(TokenType.Int32);
        await Assert.That(got).IsTrue();
        await Assert.That(last).IsEqualTo(3);
    }

    /// <summary>
    /// A non-int32 element refuses the whole run and consumes <b>nothing</b>: the caller's loop then
    /// reports the failure from the same offset, with its own message.
    /// </summary>
    [Test]
    public async Task TryReadInt32Array_AfterTheHeaderToken_NonIntegerElement_ConsumesNothing()
    {
        // fixarray(2) + [7, "ab"]
        var data = new byte[] { 0x92, 0x07, 0xA2, 0x61, 0x62 };
        var reader = new MsgPackReader(data);
        reader.Read();
        long consumedBefore = reader.BytesConsumed;
        int n = reader.TryReadInt32Array(new int[2]);
        long consumedAfter = reader.BytesConsumed;
        // …and the per-element loop still decodes element 7 and then lands on the string.
        bool first = reader.Read();
        bool got = reader.TryGetInt32(out var v);
        bool second = reader.Read();
        var secondType = reader.TokenType;

        await Assert.That(n).IsEqualTo(0);
        await Assert.That(consumedAfter).IsEqualTo(consumedBefore);
        await Assert.That(first).IsTrue();
        await Assert.That(got).IsTrue();
        await Assert.That(v).IsEqualTo(7);
        await Assert.That(second).IsTrue();
        await Assert.That(secondType).IsEqualTo(TokenType.String);
    }

    /// <summary>Sequence-mode readers never take the bulk path (they keep their own state machine).</summary>
    [Test]
    public async Task TryReadInt32Array_SequenceMode_StillReturnsZero()
    {
        var data = new byte[] { 0x93, 0x01, 0x02, 0x03 };
        var reader = new MsgPackReader(new System.Buffers.ReadOnlySequence<byte>(data));
        reader.Read();
        int n = reader.TryReadInt32Array(new int[3]);
        await Assert.That(n).IsEqualTo(0);
    }
}
