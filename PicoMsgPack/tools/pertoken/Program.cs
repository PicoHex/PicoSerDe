using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PicoMsgPack.Tools.PerToken;

/// <summary>
/// Task T4 — the per-token micro-benchmark gate (spec §9 U2/U3, plan §5 T4).
/// <para>
/// <b>Why this exists.</b> Spec §1.2's "13.4 ns/token" is <i>arithmetic</i> (a published µs divided by a
/// hand-enumerated token count), and §9 U2 names the way to replace it with a measurement: "one reader,
/// 10⁶ calls of <c>Read()</c> + <c>TryGetInt32</c>". The §1 stopping rule (RK-6: stop when the gain is under
/// half of the expected 30 %) cannot be executed on an arithmetic figure, so the three numbers this program
/// prints are what the rule is actually decided on.
/// </para>
/// <para>
/// <b>The three arms</b> (identical payload, identical pass loop, identical division; only the inner call
/// differs):
/// <list type="number">
/// <item><c>Read()</c> alone — classify + materialise one token;</item>
/// <item><c>Read() + TryGetInt32()</c> — the two-stage shape generated code emits today, and the exact pair
/// §9 U2 asks for;</item>
/// <item><c>TryReadInt32()</c> — the fused shape (T6): one dispatch per token.</item>
/// </list>
/// Both loops issue the <i>same</i> token operations (a fixed-length pass, not <c>while (Read())</c> and not
/// <c>while (TryReadInt32())</c>), so no loop-shape gain can leak into the "fused is faster" column:
/// <c>TryReadInt32</c> returns <c>false</c> for every non-int token, so a <c>while</c> loop would stop at the
/// first map header and measure nothing.
/// </para>
/// <para>
/// <b>CONTROL</b>: arm 2's method is measured <i>twice per round</i>, alternating which run goes first, so an
/// order effect, a tiering step or a drift inside the round shows up as a disagreement between two runs of
/// the same code. Signal smaller than the control's own disagreement is not signal.
/// </para>
/// <para>
/// <b>Two payload shapes</b>: the mandated <c>Collection</c> shape (spec §1.2's ledger, the row that ships)
/// and the all-integer <c>FixInts1000</c> array that T6's now-deleted harness measured its 10 % on. Reporting
/// both is what separates "the fused member is worthless" from "the fused member is worthless <i>on a mixed
/// payload</i>": the second shape isolates the fallback cost the mixed shape pays on its 8 non-int tokens.
/// </para>
/// <para>
/// <b>What it does not do</b>: it never asserts a throughput. Every check here is a <i>protocol</i> fact (token
/// count, int count, int sum, ledger); timings are printed as observations. That split is why these numbers can
/// be quoted while the test suite stays non-flaky.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>Token operations targeted per arm per round: one million, as spec §9 U2 asks.</summary>
    private const long TargetTokenOps = 1_000_000;

    /// <summary>Odd, so the median is a measured value rather than an interpolated one.</summary>
    private const int DefaultRounds = 21;

    /// <summary>Spec §9 U1's floor is 30 % recovery; §1's stopping rule (RK-6) stops at half of it.</summary>
    private const double Threshold = 15.0;

    /// <summary>spec §1.2's arithmetic per-token figure — a comparison target, not a measurement.</summary>
    private const double SpecArithmeticNsPerToken = 13.4;

    /// <summary>Accumulated every decoded value: the JIT cannot elide a loop whose result escapes.</summary>
    private static long _sink;

    private static int Main(string[] args)
    {
        var rounds = DefaultRounds;
        var dump = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rounds" when i + 1 < args.Length:
                    rounds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--dump":
                    dump = true;
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("usage: PerToken [--rounds N] [--dump]");
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 64;
            }
        }

        if (rounds <= 0 || rounds % 2 == 0)
        {
            Console.Error.WriteLine("--rounds must be odd and > 0");
            return 64;
        }

        Console.WriteLine("PicoMsgPack per-token micro-benchmark (task T4, spec §9 U2/U3)");
        Console.WriteLine(new string('=', 92));
        Console.WriteLine($"mode    : {Mode()}");
        Console.WriteLine($"runtime : {Environment.Version}   os: {Environment.OSVersion}");
        Console.WriteLine(
            "target  : "
                + $"{TargetTokenOps:N0} token operations per arm per round "
                + "(passes = round(target / tokens-per-pass))"
        );
        Console.WriteLine(
            $"rounds  : {rounds} measured (median) after an untimed warm-up that ends when two consecutive "
                + "rounds agree within 3 %"
        );
        Console.WriteLine(new string('=', 92));

        // One number for the whole run: what a fresh reader costs per pass. Every arm builds exactly one per
        // pass, so it cancels in the arm-to-arm difference and dilutes every per-token figure.
        var constructShape = Shapes.Collection;
        var constructPasses = PassesFor(constructShape.Tokens);
        WarmUp(constructShape, constructPasses);
        var constructPerPass = Median(
            Enumerable
                .Range(0, rounds)
                .Select(_ => ArmConstructOnly(constructShape.Bytes, constructPasses))
                .ToArray()
        );
        var constructPerToken = constructPerPass / constructShape.Tokens;

        Console.WriteLine();
        Console.WriteLine(
            $"reader construction (diagnostic, not an arm): {constructPerPass:F2} ns/pass = "
                + $"{constructPerToken:F3} ns/token — paid by all three arms, cancels in their difference"
        );

        double? collectionRecovery = null;

        foreach (var shape in Shapes.All)
        {
            if (dump)
            {
                Console.WriteLine();
                Console.WriteLine($"── token stream, {shape.Key} ──");
                Payload.Dump(shape.Bytes);
            }

            var passes = PassesFor(shape.Tokens);
            var histogram = Payload.Verify(shape);
            var results = Measure(shape, passes, rounds, out var warmUpRounds, out var cpuShare);

            Console.WriteLine();
            Console.WriteLine(new string('-', 92));
            Console.WriteLine($"shape: {shape.Key} — {shape.Origin}");
            Console.WriteLine(
                $"       {shape.Bytes.Length} bytes, {shape.Tokens} tokens, {shape.IntTokens} int tokens "
                    + $"summing {shape.IntSum}; {passes} passes × {shape.Tokens} tokens = "
                    + $"{passes * shape.Tokens:N0} token ops per arm per round; warm-up {warmUpRounds} rounds"
            );
            Console.WriteLine(
                $"       load  : cpu share {cpuShare:F2} of one core over the measured rounds "
                    + "(1.00 = never preempted; lower means the wall-clock rows below are inflated)"
            );
            Console.WriteLine(
                "       ledger: "
                    + string.Join(
                        ", ",
                        histogram.Select(kv =>
                            $"{kv.Key}×{kv.Value.ToString(CultureInfo.InvariantCulture)}"
                        )
                    )
            );
            Console.WriteLine(new string('-', 92));

            var read = results.Select(r => r.Read).ToArray();
            var readGet = results.Select(r => r.ReadGet).ToArray();
            var fused = results.Select(r => r.Fused).ToArray();
            var control = results.Select(r => r.Control).ToArray();

            PrintRows([
                ("Read()", read),
                ("Read() + TryGetInt32()", readGet),
                ("TryReadInt32()", fused),
                ("Read() [CONTROL]", control),
            ]);

            var readGetMedian = Median(readGet);
            var fusedMedian = Median(fused);
            var delta = readGetMedian - fusedMedian;
            var raw = delta / readGetMedian * 100;
            var corrected = delta / (readGetMedian - constructPerToken) * 100;

            Console.WriteLine(
                $"  CONTROL  Read() vs Read()            : ratio {Median(control) / Median(read):F4}"
                    + $" ({(Median(control) / Median(read) - 1) * 100:+0.0;-0.0;0.0} %) "
                    + "— the harness's own noise floor"
            );
            Console.WriteLine(
                $"  RECOVERY TryReadInt32 vs Read()+TryGetInt32: {readGetMedian:F3} → {fusedMedian:F3} ns/token"
                    + $" = {delta:F3} ns/token ({raw:F1} %; {corrected:F1} % with construction removed)"
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

            collectionRecovery ??= raw;

            Console.WriteLine(
                $"SUMMARY shape={shape.Key} mode={ModeTag()} tokens={shape.Tokens} bytes={shape.Bytes.Length}"
                    + $" passes={passes} token_ops={passes * shape.Tokens} rounds={rounds}"
                    + $" read={Median(read):F3} read_get={readGetMedian:F3} fused={fusedMedian:F3}"
                    + $" control={Median(control):F3} cpu_share={cpuShare:F2} spread_read_pct={Spread(read):F1}"
                    + $" spread_read_get_pct={Spread(readGet):F1} spread_fused_pct={Spread(fused):F1}"
                    + $" recovery_pct={raw:F2} recovery_ex_construct_pct={corrected:F2}"
                    + $" trigger_15pct={(triggered ? "yes" : "no")}"
            );
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 92));
        Console.WriteLine(
            "VERDICT "
                + $"Collection_shape_recovery_pct={collectionRecovery:F2} "
                + $"threshold_pct={Threshold:F0} "
                + $"stopping_rule={(collectionRecovery < Threshold ? "TRIGGERED" : "NOT TRIGGERED")}"
        );

        GC.KeepAlive(_sink);
        return 0;
    }

    /// <summary>One round of every arm on one shape. The only place the arm order is decided.</summary>
    private static RoundResult MeasureRound(
        byte[] payload,
        int tokens,
        int passes,
        bool controlFirst
    )
    {
        var control = controlFirst ? ArmRead(payload, tokens, passes) : double.NaN;

        var read = ArmRead(payload, tokens, passes);
        var readGet = ArmReadThenGetInt32(payload, tokens, passes);
        var fused = ArmFusedReadInt32(payload, tokens, passes);

        if (!controlFirst)
            control = ArmRead(payload, tokens, passes);

        return new RoundResult(read, readGet, fused, control);
    }

    /// <summary>
    /// Untimed rounds until the machine has settled: two consecutive rounds whose four arms all agree within
    /// 3 %. Without this the first rounds carry tier-0 code and a tiered-JIT run reports a 30 ns/token median
    /// disguised as a 5 ns/token one.
    /// </summary>
    private static void WarmUp(Shape shape, int passes)
    {
        RoundResult? previous = null;
        for (var w = 0; w < 12; w++)
        {
            var current = MeasureRound(shape.Bytes, shape.Tokens, passes, controlFirst: w % 2 == 1);
            if (previous is { } p && current.Within(p, 0.03))
                return;
            previous = current;
        }
    }

    private static RoundResult[] Measure(
        Shape shape,
        int passes,
        int rounds,
        out int warmUpRounds,
        out double cpuShare
    )
    {
        RoundResult? previous = null;
        warmUpRounds = 0;
        for (var w = 0; w < 12; w++)
        {
            var current = MeasureRound(shape.Bytes, shape.Tokens, passes, controlFirst: w % 2 == 1);
            warmUpRounds = w + 1;
            if (previous is { } p && current.Within(p, 0.03))
                break;
            previous = current;
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = Stopwatch.GetTimestamp();

        var results = new RoundResult[rounds];
        for (var r = 0; r < rounds; r++)
            results[r] = MeasureRound(shape.Bytes, shape.Tokens, passes, controlFirst: r % 2 == 1);

        var wall = Stopwatch.GetElapsedTime(wallBefore);
        cpuShare = (process.TotalProcessorTime - cpuBefore) / wall;

        return results;
    }

    private static int PassesFor(int tokensPerPass) =>
        Math.Max(1, (int)Math.Round((double)TargetTokenOps / tokensPerPass));

    // ── arms ──────────────────────────────────────────────────────────────────────────────────────────
    // Identical structure, identical pass loop, identical division by the measured token count. Only the
    // inner call differs, so the difference between two rows is the difference between two call shapes.

    /// <summary>Arm 1 — <c>Read()</c> alone: classify + materialise one token.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmRead(byte[] payload, int tokensPerPass, int passes)
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
        var expected = passes * (long)tokensPerPass;
        if (tokens != expected)
            throw new InvalidOperationException(
                $"Read() arm consumed {tokens} tokens, expected {expected}"
            );

        _sink += tokens;
        return ns / expected;
    }

    /// <summary>Arm 2 — <c>Read() + TryGetInt32()</c>: the two-stage shape generated code emits today.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmReadThenGetInt32(byte[] payload, int tokensPerPass, int passes)
    {
        long intTokens = 0;
        long intSum = 0;
        var start = Stopwatch.GetTimestamp();
        for (var p = 0; p < passes; p++)
        {
            var reader = new MsgPackReader(payload);
            for (var t = 0; t < tokensPerPass; t++)
            {
                if (!reader.Read())
                    throw new InvalidOperationException("Read() ended early in the two-stage arm");

                if (reader.TryGetInt32(out var v))
                {
                    intTokens++;
                    intSum += v;
                }
            }
        }

        var ns = ElapsedNs(start);
        _sink += intSum;
        return ns / (passes * (double)tokensPerPass);
    }

    /// <summary>Arm 3 — <c>TryReadInt32()</c>: the fused shape (T6), one dispatch per token.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmFusedReadInt32(byte[] payload, int tokensPerPass, int passes)
    {
        long intTokens = 0;
        long intSum = 0;
        var start = Stopwatch.GetTimestamp();
        for (var p = 0; p < passes; p++)
        {
            var reader = new MsgPackReader(payload);
            for (var t = 0; t < tokensPerPass; t++)
            {
                // Contract (IFusedTokenReader.TryReadInt32): one token is consumed whether or not it decodes as
                // an int, so this loop issues exactly the token operations the two arms above issue.
                if (reader.TryReadInt32(out var v))
                {
                    intTokens++;
                    intSum += v;
                }
            }
        }

        var ns = ElapsedNs(start);
        _sink += intSum;
        return ns / (passes * (double)tokensPerPass);
    }

    /// <summary>
    /// Diagnostic, not an arm: what one <c>new MsgPackReader(payload)</c> costs per pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double ArmConstructOnly(byte[] payload, int passes)
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
        return ns / passes;
    }

    // ── plumbing ──────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct RoundResult(
        double Read,
        double ReadGet,
        double Fused,
        double Control
    )
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
    /// The runtime configuration the numbers belong to. It is part of the result, not decoration: with tiering
    /// and DynamicPGO on (the shipped default) the two-stage arm keeps <c>TryGetInt32</c> inlined and the fused
    /// member does not win; with <c>DOTNET_TieredCompilation=0</c> — the setting the T6/T7/T8 reports measured
    /// under — it does. Reporting a recovery figure without this label is how two sessions end up quoting
    /// opposite numbers for the same code.
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
        Console.WriteLine(
            $"{"arm", -26}{"median", 10}{"min", 9}{"max", 9}{"spread", 9}   per-round ns/token"
        );
        foreach (var (name, values) in rows)
        {
            var median = Median(values);
            var perRound = string.Join(
                " ",
                values.Select(v => v.ToString("F2", CultureInfo.InvariantCulture))
            );
            Console.WriteLine(
                $"{name, -26}{median, 10:F3}{values.Min(), 9:F3}{values.Max(), 9:F3}{Spread(values), 8:F1}%   {perRound}"
            );
        }
    }
}
