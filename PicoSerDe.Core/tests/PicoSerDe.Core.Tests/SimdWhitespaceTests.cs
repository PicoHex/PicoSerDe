namespace PicoSerDe.Core.Tests;

public class SimdWhitespaceTests
{
    [Test]
    public async Task SkipWhitespace_AllSpaces_SkipsAll()
    {
        var data = "        hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        var nextChar = (char)data[pos];
        await Assert.That(pos).IsEqualTo(8);
        await Assert.That(nextChar).IsEqualTo('h');
    }

    [Test]
    public async Task SkipWhitespace_NoWhitespace_ReturnsSamePosition()
    {
        var data = "hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(0);
    }

    [Test]
    public async Task SkipWhitespace_MixedTypes_SkipsAll()
    {
        var data = " \t\n\r  hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        var nextChar = (char)data[pos];
        await Assert.That(pos).IsEqualTo(6);
        await Assert.That(nextChar).IsEqualTo('h');
    }

    [Test]
    public async Task SkipWhitespace_EmptyInput_ReturnsZero()
    {
        var data = Array.Empty<byte>();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(0);
    }

    [Test]
    public async Task SkipWhitespace_AllWhitespace_ReturnsLength()
    {
        var data = "   \t\n\r  "u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(data.Length);
    }

    [Test]
    public async Task SkipWhitespace_OffsetStart_SkipsFromOffset()
    {
        var data = "xxx   hello"u8.ToArray();
        var pos = SimdHelpers.SkipWhitespace(data, 3);
        var nextChar = (char)data[pos];
        await Assert.That(pos).IsEqualTo(6);
        await Assert.That(nextChar).IsEqualTo('h');
    }

    // ── Wide SIMD path tests (Vector256/512) ──

    [Test]
    public async Task SkipWhitespace_LargeAllSpaces_SkipsAll_ExercisesWideSimd()
    {
        // 256+ bytes to ensure Vector256/512 paths are exercised
        var data = new byte[512];
        Array.Fill(data, (byte)' ');
        data[511] = (byte)'x';
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(511);
        await Assert.That((char)data[pos]).IsEqualTo('x');
    }

    [Test]
    public async Task SkipWhitespace_LargeMix_SkipsAll_ExercisesWideSimd()
    {
        // 300 bytes mixed whitespace: 4-char pattern repeating
        var data = new byte[300];
        for (int i = 0; i < 299; i++)
        {
            data[i] = (byte)(
                (i % 4) switch
                {
                    0 => 0x20, // space
                    1 => 0x09, // tab
                    2 => 0x0A, // newline
                    _ => 0x0D, // carriage return
                }
            );
        }
        data[299] = (byte)'x';
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(299);
        await Assert.That((char)data[pos]).IsEqualTo('x');
    }

    [Test]
    public async Task SkipSpacesAndTabs_LargeMix_SkipsAll_ExercisesWideSimd()
    {
        var data = new byte[300];
        for (int i = 0; i < 299; i++)
            data[i] = (byte)(i % 2 == 0 ? 0x20 : 0x09);
        data[299] = (byte)'x';
        var pos = SimdHelpers.SkipSpacesAndTabs(data, 0);
        await Assert.That(pos).IsEqualTo(299);
        await Assert.That((char)data[pos]).IsEqualTo('x');
    }

    [Test]
    public async Task SkipWhitespace_LargeBlock_AllWhitespace_ReturnsLength()
    {
        var data = new byte[512];
        Array.Fill(data, (byte)0x20);
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(data.Length);
    }

    [Test]
    public async Task SkipWhitespace_ExactEdge_SixteenBytes()
    {
        // 16 bytes = Vector128 width edge case
        var data = "                x"u8.ToArray(); // 16 spaces + x
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(16);
    }

    [Test]
    public async Task SkipWhitespace_ExactEdge_ThirtyTwoBytes()
    {
        // 32 bytes = Vector256 width edge case
        var data = new byte[33];
        Array.Fill(data, (byte)' ');
        data[32] = (byte)'x';
        var pos = SimdHelpers.SkipWhitespace(data, 0);
        await Assert.That(pos).IsEqualTo(32);
    }

    [Test]
    public async Task SkipWhitespace_OffsetIntoLargeBuffer_SkipsCorrectly()
    {
        var data = new byte[512];
        Array.Fill(data, (byte)'a');
        for (int i = 100; i < 400; i++)
            data[i] = (byte)' ';
        data[400] = (byte)'x';
        var pos = SimdHelpers.SkipWhitespace(data, 100);
        await Assert.That(pos).IsEqualTo(400);
    }

    // ── Performance throughput tests (large data) ──

    // These are REGRESSION FENCES, not benchmarks: the solution runs 9 test
    // assemblies concurrently, so the mean of one timed batch is not a stable
    // signal. Any OS preemption that lands inside the window is charged to every
    // iteration - observed in 1 of 17 full-suite runs: a ~0.5 s deschedule turned
    // a healthy ~50 μs/call scan into 578 μs/call and tripped the old 500 μs
    // bound (see .superpowers/sdd/followups/flake-report.md). Measurement hygiene:
    //   * discarded warm-up -> JIT tier-0 never lands inside a sample;
    //   * MEDIAN of batches -> a preempted batch is rejected instead of
    //                          poisoning the number the assertion sees;
    //   * wide bound        -> only a catastrophic (super-linear) regression can
    //                          trip it, which is the only claim a wall-clock
    //                          assertion inside a unit suite can honestly make.
    private const int WarmupIterations = 256;
    private const int SampleBatches = 5;
    private const int IterationsPerBatch = 256;

    // 5 ms/call is ~100x the healthy ~50 μs/call reading measured on the dev box.
    private const double MaxNanosPerCall = 5_000_000.0;

    /// <summary>
    /// Runs <paramref name="scan"/> after a discarded warm-up and returns the
    /// MEDIAN nanoseconds per call across <see cref="SampleBatches"/> timed
    /// batches. The median (not the mean) keeps a single scheduler stall from
    /// being smeared over the whole sample and read as a throughput regression.
    /// </summary>
    private static double MedianNanosPerCall(Action scan)
    {
        for (var i = 0; i < WarmupIterations; i++)
            scan();

        var samples = new double[SampleBatches];
        for (var batch = 0; batch < SampleBatches; batch++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < IterationsPerBatch; i++)
                scan();
            sw.Stop();
            samples[batch] = sw.Elapsed.TotalNanoseconds / IterationsPerBatch;
        }

        Array.Sort(samples);
        return samples[SampleBatches / 2];
    }

    [Test]
    public async Task SkipWhitespace_Throughput_LargeBuffer_64KB()
    {
        // 64KB of whitespace to exercise wide SIMD paths
        var data = new byte[65536];
        Array.Fill(data, (byte)0x20);
        data[65535] = (byte)'x';

        // Correctness first, so the wall-clock fence below is never the only signal.
        await Assert.That(SimdHelpers.SkipWhitespace(data, 0)).IsEqualTo(65535);

        var perCall = MedianNanosPerCall(() => SimdHelpers.SkipWhitespace(data, 0));

        // Log for manual inspection
        Console.WriteLine(
            $"SkipWhitespace 64KB: {perCall / 1000:F1}μs/call "
                + $"(median of {SampleBatches}x{IterationsPerBatch}, {WarmupIterations} warm-up)"
        );
        await Assert.That(perCall).IsLessThan(MaxNanosPerCall);
    }

    [Test]
    public async Task SkipSpacesAndTabs_Throughput_LargeBuffer_64KB()
    {
        var data = new byte[65536];
        for (var i = 0; i < 65535; i++)
            data[i] = (byte)(i % 2 == 0 ? 0x20 : 0x09);
        data[65535] = (byte)'x';

        await Assert.That(SimdHelpers.SkipSpacesAndTabs(data, 0)).IsEqualTo(65535);

        var perCall = MedianNanosPerCall(() => SimdHelpers.SkipSpacesAndTabs(data, 0));

        Console.WriteLine(
            $"SkipSpacesAndTabs 64KB: {perCall / 1000:F1}μs/call "
                + $"(median of {SampleBatches}x{IterationsPerBatch}, {WarmupIterations} warm-up)"
        );
        await Assert.That(perCall).IsLessThan(MaxNanosPerCall);
    }
}
