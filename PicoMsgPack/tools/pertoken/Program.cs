using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PicoMsgPack.Tools.PerToken;

/// <summary>
/// Task T4 — the per-token micro-benchmark gate (spec §9 U2/U3, plan §5 T4).
/// <para>
/// <b>Why this exists.</b> Spec §1.2's "13.4 ns/token" is <i>arithmetic</i> (a published µs divided by a
/// hand-enumerated token count), and §9 U2 names the way to replace it with a measurement: "one reader, 10⁶ calls
/// of <c>Read()</c> + <c>TryGetInt32</c>". The §1 stopping rule (RK-6: stop when the gain is under half of the
/// expected 30 %) cannot be executed on an arithmetic figure, so the three numbers this program prints are what the
/// rule is actually decided on.
/// </para>
/// <para>
/// <b>The three arms</b> (identical payload, identical pass count, identical division; only the inner call differs):
/// <list type="number">
/// <item><c>Read()</c> alone — classify + materialise one token;</item>
/// <item><c>Read() + TryGetInt32()</c> — the two-stage shape generated code emits today, and the exact pair §9 U2
/// asks for;</item>
/// <item><c>TryReadInt32()</c> — the fused shape (T6): one dispatch per token.</item>
/// </list>
/// All three loops issue the <i>same</i> token operations — a fixed-length pass, not <c>while (Read())</c> and not
/// <c>while (TryReadInt32())</c> (the latter returns <c>false</c> for every non-int token, so it would stop at the
/// first map header and measure nothing). No loop-shape gain can therefore leak into the "fused is faster" column.
/// </para>
/// <para>
/// <b>Block interleaving, and why it is not optional.</b> A round is not "run arm 1 for 20 ms, then arm 2 for
/// 20 ms": each round cuts the work into blocks and rotates the arm order block by block, so background load, a
/// frequency ramp or a GC pause lands on every arm instead of on whichever arm happened to be running. The first
/// version of this file timed each arm to completion in turn — it reported <b>−7.7 %</b> recovery on a quiet machine
/// and <b>+33 %</b> on the same binary twenty minutes later, purely because the box got busy between two arms. A
/// harness that can produce that spread is worse than none, because the number will be quoted.
/// </para>
/// <para>
/// <b>CONTROL</b> is arm 1 measured a second time, in its own rotating slot: the disagreement between two runs of the
/// same code is this harness's noise floor, and a recovery figure smaller than it is not a figure.
/// </para>
/// <para>
/// <b>What it does not do</b>: it never asserts a throughput. Every check here is a <i>protocol</i> fact (token
/// count, int count, int sum, ledger); timings are printed as observations. That split is why these numbers can be
/// quoted while the test suite stays non-flaky.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>Token operations targeted per arm per round: one million, as spec §9 U2 asks.</summary>
    private const long TargetTokenOps = 1_000_000;

    /// <summary>Odd, so the median is a measured value rather than an interpolated one.</summary>
    private const int DefaultRounds = 21;

    /// <summary>Timing blocks per round per arm. Larger = finer interleaving, smaller = more Stopwatch overhead.</summary>
    private const int DefaultBlocks = 16;

    /// <summary>Spec §9 U1's floor is 30 % recovery; §1's stopping rule (RK-6) stops at half of it.</summary>
    private const double Threshold = 15.0;

    /// <summary>spec §1.2's arithmetic per-token figure — a comparison target, not a measurement.</summary>
    private const double SpecArithmeticNsPerToken = 13.4;

    /// <summary>Accumulated every decoded value: the JIT cannot elide a loop whose result escapes.</summary>
    private static long _sink;

    /// <summary>What the last arm call saw — read by <see cref="Gate"/> so the arms cannot diverge silently.</summary>
    private static long _lastTokens;
    private static long _lastIntTokens;
    private static long _lastIntSum;

    private static int Main(string[] args)
    {
        var rounds = DefaultRounds;
        var blocks = DefaultBlocks;
        var dump = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rounds" when i + 1 < args.Length:
                    rounds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--blocks" when i + 1 < args.Length:
                    blocks = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--dump":
                    dump = true;
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("usage: PerToken [--rounds N] [--blocks N] [--dump]");
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 64;
            }
        }

        if (rounds <= 0 || rounds % 2 == 0 || blocks <= 0)
        {
            Console.Error.WriteLine("--rounds must be odd and > 0, --blocks must be > 0");
            return 64;
        }

        Console.WriteLine("PicoMsgPack per-token micro-benchmark (task T4, spec §9 U2/U3)");
        Console.WriteLine(new string('=', 96));
        Console.WriteLine($"mode    : {Mode()}");
        Console.WriteLine($"runtime : {Environment.Version}   os: {Environment.OSVersion}");
        Console.WriteLine(
            $"target  : ~{TargetTokenOps:N0} token operations per arm per round, split into {blocks} interleaved "
                + "blocks so load hits every arm"
        );
        Console.WriteLine(
            $"rounds  : {rounds} measured (median of per-round ratios), after untimed warm-up rounds that end when "
                + "two consecutive rounds agree within 3 %"
        );
        Console.WriteLine(new string('=', 96));

        // One number for the whole run: what a fresh reader costs per pass. Every arm builds exactly one per pass, so
        // it cancels in the arm-to-arm difference and dilutes every per-token figure.
        var constructShape = Shapes.Collection;
        var constructPasses = PassesFor(constructShape.Tokens);
        var constructPerPass = Median(
            Enumerable
                .Range(0, rounds)
                .Select(_ => ArmConstructNs(constructShape.Bytes, constructPasses) / constructPasses)
                .ToArray()
        );
        var constructPerToken = constructPerPass / constructShape.Tokens;

        Console.WriteLine();
        Console.WriteLine(
            $"reader construction (diagnostic, not an arm): {constructPerPass:F2} ns/pass = "
                + $"{constructPerToken:F3} ns/token — paid by all three arms, cancels in their difference"
        );

        var collectionRecovery = double.NaN;
        var allPublishable = true;

        foreach (var shape in Shapes.All)
        {
            if (dump)
            {
                Console.WriteLine();
                Console.WriteLine($"-- token stream, {shape.Key} --");
                Payload.Dump(shape.Bytes);
            }

            var passes = PassesFor(shape.Tokens);
            var histogram = Payload.Verify(shape);
            Gate(shape);

            var results = Measure(shape, passes, blocks, rounds, out var warmUpRounds, out var cpuShare);

            Console.WriteLine();
            Console.WriteLine(new string('-', 96));
            Console.WriteLine($"shape: {shape.Key} — {shape.Origin}");
            Console.WriteLine(
                $"       {shape.Bytes.Length} bytes, {shape.Tokens} tokens, {shape.IntTokens} int tokens "
                    + $"summing {shape.IntSum}; {passes} passes × {shape.Tokens} tokens = "
                    + $"{passes * shape.Tokens:N0} token ops per arm per round; warm-up {warmUpRounds} rounds"
            );
            Console.WriteLine(
                $"       load  : cpu share {cpuShare:F2} of one core over the measured rounds "
                    + "(1.00 = never preempted; lower means the absolute ns are inflated)"
            );
            Console.WriteLine(
                "       ledger: "
                    + string.Join(
                        ", ",
                        histogram.Select(kv => $"{kv.Key}x{kv.Value.ToString(CultureInfo.InvariantCulture)}")
                    )
            );
            Console.WriteLine(new string('-', 96));

            var read = results.Select(r => r.Read).ToArray();
            var readGet = results.Select(r => r.ReadGet).ToArray();
            var fused = results.Select(r => r.Fused).ToArray();
            var control = results.Select(r => r.Control).ToArray();

            PrintRows(
                [
                    ("Read()", read),
                    ("Read() + TryGetInt32()", readGet),
                    ("TryReadInt32()", fused),
                    ("Read() [CONTROL]", control),
                ]
            );

            // Median of the per-round RATIOS, not the ratio of the per-round medians: every round measured all three
            // arms in the same interleaved blocks, so a round's ratio is the load-insensitive quantity. Two medians
            // taken from different halves of a drifting machine are not.
            var perRoundRecovery = results.Select(r => (r.ReadGet - r.Fused) / r.ReadGet * 100).ToArray();
            var perRoundControl = results.Select(r => r.Control / r.Read).ToArray();

            var readMedian = Median(read);
            var readGetMedian = Median(readGet);
            var fusedMedian = Median(fused);
            var raw = Median(perRoundRecovery);

            // Construction cancels in the arm-to-arm difference but sits in the readGet denominator; correcting for it
            // can only ever raise the percentage, so it is the favourable reading, printed next to the raw one.
            var corrected = raw * readGetMedian / (readGetMedian - constructPerToken);

            Console.WriteLine(
                $"  CONTROL  Read() vs Read()            : ratio {Median(perRoundControl):F4}"
                    + $" ({(Median(perRoundControl) - 1) * 100:+0.0;-0.0;0.0} %; per-round "
                    + $"{perRoundControl.Min():F3}...{perRoundControl.Max():F3}) — the harness's own noise floor"
            );
            Console.WriteLine(
                $"  RECOVERY TryReadInt32 vs Read()+TryGetInt32: {readGetMedian:F3} -> {fusedMedian:F3} ns/token"
                    + $" = {readGetMedian - fusedMedian:F3} ns/token => {raw:F1} % median-of-rounds "
                    + $"(per-round {perRoundRecovery.Min():F1}...{perRoundRecovery.Max():F1} %)"
            );

            var triggered = raw < Threshold;
            Console.WriteLine(
                $"  STOPPING RULE (plan §5 T6 criterion 6: recovery < {Threshold:F0} %): "
                    + $"{(triggered ? "TRIGGERED" : "NOT TRIGGERED")} — {raw:F1} % vs {Threshold:F0} %"
            );
            Console.WriteLine(
                $"  spec §1.2 arithmetic {SpecArithmeticNsPerToken:F1} ns/token vs measured "
                    + $"{readGetMedian:F2} ns/token = "
                    + $"{(readGetMedian / SpecArithmeticNsPerToken - 1) * 100:+0.0;-0.0;0.0} % "
                    + "(difference only — the two figures measure different things, see README)"
            );

            // The fused member can legitimately come out a little cheaper than Read() alone (its integer path has a
            // shorter prologue than ReadSpan's), but not a quarter cheaper: the same token stream is still being
            // classified and materialised. Beyond that the two arms were measured in different JIT states.
            var comparable = fusedMedian >= readMedian * 0.75;
            if (!comparable)
                Console.WriteLine(
                    $"  !! ARMS NOT COMPARABLE: fused {fusedMedian:F3} < 0.75 x Read() {readMedian:F3}. No "
                        + "implementation can classify the same tokens a quarter cheaper than the reader it contains — "
                        + "the arms were measured in different JIT states. Discard this run."
                );

            // A number measured while the box was busy is not a measurement. Both conditions are printed, so a
            // rejected run still documents why it was rejected instead of being silently dropped.
            var publishable = cpuShare >= 0.95 && Math.Abs(Median(perRoundControl) - 1) <= 0.03 && comparable;
            allPublishable &= publishable;
            Console.WriteLine(
                $"  PUBLISHABLE: {(publishable ? "yes" : "NO")} (requires cpu share >= 0.95, |CONTROL - 1| <= 0.03 "
                    + $"and a comparable arm set; got {cpuShare:F2}, {Median(perRoundControl) - 1:+0.000;-0.000;0.000}"
                    + $", {(comparable ? "comparable" : "NOT comparable")})"
            );

            if (double.IsNaN(collectionRecovery))
                collectionRecovery = raw;

            Console.WriteLine(
                $"SUMMARY shape={shape.Key} mode={ModeTag()} tokens={shape.Tokens} bytes={shape.Bytes.Length}"
                    + $" passes={passes} blocks={blocks} token_ops={passes * shape.Tokens} rounds={rounds}"
                    + $" read={readMedian:F3} read_get={readGetMedian:F3} fused={fusedMedian:F3}"
                    + $" control={Median(control):F3} cpu_share={cpuShare:F2}"
                    + $" spread_read_pct={Spread(read):F1} spread_read_get_pct={Spread(readGet):F1}"
                    + $" spread_fused_pct={Spread(fused):F1} recovery_pct={raw:F2}"
                    + $" recovery_min_pct={perRoundRecovery.Min():F2}"
                    + $" recovery_max_pct={perRoundRecovery.Max():F2}"
                    + $" control_ratio={Median(perRoundControl):F3}"
                    + $" recovery_ex_construct_pct={corrected:F2} trigger_15pct={(triggered ? "yes" : "no")}"
                    + $" comparable={(comparable ? "yes" : "no")} publishable={(publishable ? "yes" : "no")}"
            );
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 96));
        Console.WriteLine(
            "VERDICT "
                + $"Collection_shape_recovery_pct={collectionRecovery:F2} "
                + $"threshold_pct={Threshold:F0} "
                + $"stopping_rule={(collectionRecovery < Threshold ? "TRIGGERED" : "NOT TRIGGERED")} "
                + $"publishable={(allPublishable ? "yes" : "no")}"
        );

        GC.KeepAlive(_sink);

        // Exit 3 = "the timing is not quotable". A run cannot fail on a busy machine by accident and then be quoted
        // by mistake: whoever pipes the output into a report sees the status.
        return allPublishable ? 0 : 3;
    }

    /// <summary>
    /// Protocol gate, run before any timing: the three arms must see the same token stream. A fused read that
    /// consumed a different number of tokens, or decoded different integers, would make every ns/token figure below
    /// compare two different workloads.
    /// </summary>
    private static void Gate(Shape shape)
    {
        const int passes = 1;

        _ = ArmReadNs(shape.Bytes, shape.Tokens, passes);
        if (_lastTokens != shape.Tokens)
            throw new InvalidOperationException(
                $"gate: Read() consumed {_lastTokens} tokens, the ledger says {shape.Tokens}"
            );

        _ = ArmReadThenGetNs(shape.Bytes, shape.Tokens, passes);
        var twoStageInts = _lastIntTokens;
        var twoStageSum = _lastIntSum;
        if (twoStageInts != shape.IntTokens || twoStageSum != shape.IntSum)
            throw new InvalidOperationException(
                $"gate: Read()+TryGetInt32() decoded {twoStageInts} int tokens summing {twoStageSum}; the ledger "
                    + $"says {shape.IntTokens} summing {shape.IntSum}"
            );

        _ = ArmFusedNs(shape.Bytes, shape.Tokens, passes);
        if (_lastIntTokens != twoStageInts || _lastIntSum != twoStageSum)
            throw new InvalidOperationException(
                $"gate: TryReadInt32() decoded {_lastIntTokens} int tokens summing {_lastIntSum}, the two-stage arm "
                    + $"decoded {twoStageInts} summing {twoStageSum} — the arms are not walking the same stream"
            );
    }

    /// <summary>
    /// A round: <paramref name="passes"/> passes per arm, cut into <paramref name="blocks"/> blocks, with the arm
    /// order rotated per block. Everything else in this file is reporting; this is the measurement.
    /// </summary>
    private static RoundResult MeasureRound(Shape shape, int passes, int blocks)
    {
        var blockPasses = Math.Max(1, passes / blocks);
        var payload = shape.Bytes;
        var tokens = shape.Tokens;

        double readNs = 0,
            readGetNs = 0,
            fusedNs = 0,
            controlNs = 0;

        Span<int> order = stackalloc int[4];
        for (var b = 0; b < blocks; b++)
        {
            for (var i = 0; i < 4; i++)
                order[i] = (b + i) % 4;

            foreach (var arm in order)
            {
                switch (arm)
                {
                    case 0:
                        readNs += ArmReadNs(payload, tokens, blockPasses);
                        break;
                    case 1:
                        readGetNs += ArmReadThenGetNs(payload, tokens, blockPasses);
                        break;
                    case 2:
                        fusedNs += ArmFusedNs(payload, tokens, blockPasses);
                        break;
                    default:
                        // Same code as arm 0 by construction: the CONTROL must not be a second implementation.
                        controlNs += ArmReadNs(payload, tokens, blockPasses);
                        break;
                }
            }
        }

        var tokenOps = blockPasses * (double)blocks * tokens;
        return new RoundResult(readNs / tokenOps, readGetNs / tokenOps, fusedNs / tokenOps, controlNs / tokenOps);
    }

    private static RoundResult[] Measure(
        Shape shape,
        int passes,
        int blocks,
        int rounds,
        out int warmUpRounds,
        out double cpuShare
    )
    {
        RoundResult? previous = null;
        warmUpRounds = 0;
        for (var w = 0; w < 12; w++)
        {
            var current = MeasureRound(shape, passes, blocks);
            warmUpRounds = w + 1;
            if (previous is { } p && current.Within(p, 0.03))
                break;
            previous = current;
        }

        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = Stopwatch.GetTimestamp();

        var results = new RoundResult[rounds];
        for (var r = 0; r < rounds; r++)
            results[r] = MeasureRound(shape, passes, blocks);

        var wall = Stopwatch.GetElapsedTime(wallBefore);
        cpuShare = (process.TotalProcessorTime - cpuBefore) / wall;

        return results;
    }

    private static int PassesFor(int tokensPerPass) =>
        Math.Max(1, (int)Math.Round((double)TargetTokenOps / tokensPerPass));

    // -- arms ------------------------------------------------------------------------------------------
    // Identical structure, identical pass loop, identical division by the token count. Only the inner call differs,
    // so the difference between two rows is the difference between two call shapes. Each returns elapsed ns.

    /// <summary>Arm 1 — <c>Read()</c> alone: classify + materialise one token.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmReadNs(byte[] payload, int tokensPerPass, int passes)
    {
        long tokens = 0;
        var start = Stopwatch.GetTimestamp();
        for (var p = 0; p < passes; p++)
        {
            var reader = new MsgPackReader(payload);
            for (var t = 0; t < tokensPerPass; t++)
            {
                if (reader.Read())
                    tokens++;
            }
        }

        var ns = ElapsedNs(start);
        _lastTokens = tokens / passes;
        _sink += tokens;
        return ns;
    }

    /// <summary>Arm 2 — <c>Read() + TryGetInt32()</c>: the two-stage shape generated code emits today.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmReadThenGetNs(byte[] payload, int tokensPerPass, int passes)
    {
        long tokens = 0;
        long intTokens = 0;
        long intSum = 0;
        var start = Stopwatch.GetTimestamp();
        for (var p = 0; p < passes; p++)
        {
            var reader = new MsgPackReader(payload);
            for (var t = 0; t < tokensPerPass; t++)
            {
                if (reader.Read())
                    tokens++;

                if (reader.TryGetInt32(out var v))
                {
                    intTokens++;
                    intSum += v;
                }
            }
        }

        var ns = ElapsedNs(start);
        _lastTokens = tokens / passes;
        _lastIntTokens = intTokens / passes;
        _lastIntSum = intSum / passes;
        _sink += intSum;
        return ns;
    }

    /// <summary>Arm 3 — <c>TryReadInt32()</c>: the fused shape (T6), one dispatch per token.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmFusedNs(byte[] payload, int tokensPerPass, int passes)
    {
        long intTokens = 0;
        long intSum = 0;
        var start = Stopwatch.GetTimestamp();
        for (var p = 0; p < passes; p++)
        {
            var reader = new MsgPackReader(payload);
            for (var t = 0; t < tokensPerPass; t++)
            {
                // Contract (IFusedTokenReader.TryReadInt32): one token is consumed whether or not it decodes as an
                // int, so this loop issues exactly the token operations the two arms above issue.
                if (reader.TryReadInt32(out var v))
                {
                    intTokens++;
                    intSum += v;
                }
            }
        }

        var ns = ElapsedNs(start);
        _lastIntTokens = intTokens / passes;
        _lastIntSum = intSum / passes;
        _sink += intSum;
        return ns;
    }

    /// <summary>Diagnostic, not an arm: what one <c>new MsgPackReader(payload)</c> costs per pass.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmConstructNs(byte[] payload, int passes)
    {
        long touched = 0;
        var start = Stopwatch.GetTimestamp();
        for (var p = 0; p < passes; p++)
        {
            var reader = new MsgPackReader(payload);
            touched += reader.Depth;
        }

        var ns = ElapsedNs(start);
        _sink += touched;
        return ns;
    }

    // -- plumbing --------------------------------------------------------------------------------------

    private readonly record struct RoundResult(double Read, double ReadGet, double Fused, double Control)
    {
        public bool Within(RoundResult other, double tolerance) =>
            Relative(this.Read, other.Read) <= tolerance
            && Relative(this.ReadGet, other.ReadGet) <= tolerance
            && Relative(this.Fused, other.Fused) <= tolerance;

        private static double Relative(double a, double b) => Math.Abs(a - b) / Math.Min(a, b);
    }

    private static double ElapsedNs(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalNanoseconds;

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>Peak-to-peak dispersion as a percentage of the median — the "how repeatable" number.</summary>
    private static double Spread(double[] values)
    {
        var median = Median(values);
        return (values.Max() - values.Min()) / median * 100;
    }

    /// <summary>
    /// The runtime configuration the numbers belong to. It is part of the result, not decoration: with tiering and
    /// DynamicPGO on (the shipped default) the two-stage arm keeps <c>TryGetInt32</c> inlinable and the fused member
    /// does not win; with <c>DOTNET_TieredCompilation=0</c> — the setting the T6/T7/T8 reports measured under — it
    /// does. Reporting a recovery figure without this label is how two sessions end up quoting opposite numbers for
    /// the same code.
    /// </summary>
    private static string ModeTag() =>
        RuntimeFeature.IsDynamicCodeSupported
            ? Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0"
                ? "jit-notiered"
                : "jit-tiered"
            : "aot";

    private static string Mode() =>
        RuntimeFeature.IsDynamicCodeSupported
            ? Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0"
                ? "JIT, tiering DISABLED (DOTNET_TieredCompilation=0 — the T6/T7/T8 protocol)"
                : "JIT, tiering on (the shipped default; TryGetInt32 stays inlinable)"
            : "NativeAOT (no dynamic code in this process)";

    private static void PrintRows((string Name, double[] Values)[] rows)
    {
        Console.WriteLine();
        Console.WriteLine($"{"arm",-26}{"median",10}{"min",9}{"max",9}{"spread",9}   per-round ns/token");
        foreach (var (name, values) in rows)
        {
            var median = Median(values);
            var perRound = string.Join(
                " ",
                values.Select(v => v.ToString("F2", CultureInfo.InvariantCulture))
            );
            Console.WriteLine(
                $"{name,-26}{median,10:F3}{values.Min(),9:F3}{values.Max(),9:F3}{Spread(values),8:F1}%   {perRound}"
            );
        }
    }
}
