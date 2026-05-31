namespace PicoMsgPack.Tests;

public class IntArrayBenchModel
{
    public int[] Scores { get; set; } = [];
}

public class MsgPackIntArrayInlineTests
{
    [Test]
    public async Task IntArray_RoundTrip_Correctness()
    {
        var model = new IntArrayBenchModel { Scores =  [1, -5, int.MaxValue, int.MinValue, 0, 42] };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<IntArrayBenchModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores).HasCount(6);
        await Assert.That(result.Scores[0]).IsEqualTo(1);
        await Assert.That(result.Scores[1]).IsEqualTo(-5);
        await Assert.That(result.Scores[2]).IsEqualTo(int.MaxValue);
        await Assert.That(result.Scores[3]).IsEqualTo(int.MinValue);
        await Assert.That(result.Scores[4]).IsEqualTo(0);
        await Assert.That(result.Scores[5]).IsEqualTo(42);
    }

    [Test]
    public async Task EmptyIntArray_RoundTrip()
    {
        var model = new IntArrayBenchModel { Scores =  [] };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<IntArrayBenchModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores).HasCount(0);
    }

    [Test]
    public async Task LargeIntArray_RoundTrip()
    {
        var data = new int[1000];
        for (int i = 0; i < data.Length; i++)
            data[i] = i * 7 - 3500;
        var model = new IntArrayBenchModel { Scores = data };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<IntArrayBenchModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Scores).IsNotNull();
        await Assert.That(result.Scores).HasCount(1000);
        for (int i = 0; i < 1000; i++)
            await Assert.That(result.Scores[i]).IsEqualTo(data[i]);
    }
}
