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
        var data = new byte[] { 0x94, 0x7F, 0xD0, 0x80, 0xD1, 0x01, 0x2C, 0xD2, 0x00, 0x01, 0x86, 0xA0 };
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
        double v1 = 1.5, v2 = -3.0;
        var buf = new byte[1 + 2 * 9];
        buf[0] = 0x92;
        buf[1] = 0xCB;
        BitConverter.GetBytes(v1).CopyTo(buf, 2);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf, 2, 8);
        buf[10] = 0xCB;
        BitConverter.GetBytes(v2).CopyTo(buf, 11);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf, 11, 8);

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
}
