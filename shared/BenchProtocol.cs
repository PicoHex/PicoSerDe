// Shared measurement protocol — linked into every benchmark project (same
// mechanism as BenchRoleGuard.cs, i.e. <Compile Include="..\shared\BenchProtocol.cs" />).
//
// README "### Measurement protocol" says a run that misses any of the five rules
// "is not published, however good its numbers look". Until now those five rules
// lived only in the README: each benchmark called PicoBench.Benchmark.Compare and
// printed whatever came out, loaded machine or not, JIT or AOT, equivalent
// competitor or not. This file makes the rules executable, so the numbers this
// repository prints are the ones the README claims to require:
//
//   1. Idle machine          -> CheckLoad() runs before every measurement and times
//                               one fixed, deterministic busy loop twice; if the two
//                               medians differ by more than MaxMedianDrift (20%), or
//                               one probe's run-to-run max/min exceeds
//                               MaxSpreadRatio (3x), or the probe disperses by more
//                               than MaxProbeRelativeStdDev, the measurement is
//                               refused: the row prints "UNSTABLE — not published"
//                               and the sample carries NaN. No number, no ratio.
//                               Those three criteria are *relative* and blind to a
//                               machine that is uniformly loaded (every probe slows by
//                               the same factor, so drift reads zero while the numbers
//                               are inflated — measured 3x on a saturated box), so
//                               CheckLoad() and Measure() also take system-wide CPU
//                               accounting and refuse when other processes used more
//                               than MaxOtherCpuFraction of the machine.
//   2. >=100k iterations per operation, >=5 independent runs
//                            -> RunsPerAction = 5 runs, each >=20'000 iterations
//                               (>=100'000 per operation per arm); every run is
//                               kept, none discarded.
//   3. Median + dispersion   -> ProtocolSample reports median + min + max + stddev
//                               (over the 5 run medians). There is no bare average
//                               anywhere in the printed output.
//   4. AOT and JIT never mixed
//                            -> Mode is decided once (IL on disk + dynamic-code
//                               support), printed in every table header, stamped onto
//                               every sample, and ProtocolTable throws if a table
//                               would mix modes.
//   5. Equivalent competitor arms
//                            -> ProtocolTable(equivalent: false) prints
//                               "NON-EQUIVALENT — not comparable" and computes no
//                               ratio (the INI/TOML competitor arms are hand-written
//                               DOM walks, not single library calls).
//
// Role semantics: `ours` is always PicoSerDe's own action and `theirs` the
// competitor's, in that argument order. BenchRoleGuard.cs (unchanged) still owns
// the "the candidate slot must hold a Pico* action" judgement, and
// Compare(..., oursLabel, ..., theirsLabel, ...) runs every pair through it — so the
// labels cannot be swapped without an exception. The static constructor additionally
// proves, once per process, that the guard really rejects an inverted pair.
//
// Exit code: refusing to publish is a finding, not a crash, so the default exit code
// stays 0 and CI's `dotnet run --no-build` smoke cannot go red because a runner was
// busy. Set PICOBENCH_STRICT=1 to make a refused row fail the process.

using System.Collections.ObjectModel;
using System.Diagnostics;

namespace PicoBench;

/// <summary>The execution mode the measured code actually ran in.</summary>
public enum ProtocolMode
{
    /// <summary>Just-in-time compiled (the default <c>dotnet run</c> path).</summary>
    Jit,

    /// <summary>NativeAOT — no run-time code generation available.</summary>
    Aot,
}

/// <summary>
/// One measurement of one action, reported as the protocol requires: median with
/// dispersion, never a bare average.
/// </summary>
/// <param name="MedianUs">Median of the per-run medians, microseconds per operation.</param>
/// <param name="MinUs">Smallest per-run median.</param>
/// <param name="MaxUs">Largest per-run median.</param>
/// <param name="StdDevUs">Standard deviation of the per-run medians.</param>
/// <param name="Runs">Number of independent runs that produced the figure.</param>
public readonly record struct ProtocolSample(
    double MedianUs,
    double MinUs,
    double MaxUs,
    double StdDevUs,
    int Runs
)
{
    /// <summary>The action's name, e.g. <c>PicoJetson</c> or <c>System.Text.Json</c>.</summary>
    public string Label { get; init; } = "";

    /// <summary>Iterations actually executed for this action across all runs.</summary>
    public int Iterations { get; init; }

    /// <summary>The mode the action ran in — one table never mixes two.</summary>
    public ProtocolMode Mode { get; init; }

    /// <summary>False when the protocol refused to publish a figure.</summary>
    public bool Stable { get; init; }

    /// <summary>Run-to-run spread (<see cref="MaxUs"/> / <see cref="MinUs"/>).</summary>
    public double Spread => MinUs > 0 ? MaxUs / MinUs : double.PositiveInfinity;

    /// <summary>The sentinel a refused measurement returns: no numbers, only a label.</summary>
    /// <param name="label">The action that could not be published.</param>
    public static ProtocolSample Unstable(string label) =>
        new(double.NaN, double.NaN, double.NaN, double.NaN, 0)
        {
            Label = label,
            Mode = BenchProtocol.Mode,
            Stable = false,
        };
}

/// <summary>What the pre-measurement load self-check saw.</summary>
/// <param name="Stable">True when the machine looked idle enough to publish from.</param>
/// <param name="Drift">Relative difference between the two probe medians (0.20 = 20%).</param>
/// <param name="Spread">Worst run-to-run max/min of the two probes.</param>
/// <param name="ProbeRelativeStdDev">Worst run-to-run relative standard deviation of the probes.</param>
/// <param name="OtherCpuFraction">
/// Share of the machine's CPU other processes used during the window, or NaN when this OS
/// offers no cheap system-wide accounting.
/// </param>
/// <param name="Failure">Empty when stable, otherwise the criterion that failed.</param>
public readonly record struct LoadCheck(
    bool Stable,
    double Drift,
    double Spread,
    double ProbeRelativeStdDev,
    double OtherCpuFraction,
    string Failure
);

/// <summary>
/// The repository's measurement protocol (README "### Measurement protocol"),
/// implemented once so every benchmark measures the same way.
/// </summary>
public static class BenchProtocol
{
    /// <summary>Independent runs per action. Every run is kept.</summary>
    public const int RunsPerAction = 5;

    /// <summary>Total iterations per action (all runs combined) — the README's 100k floor.</summary>
    public const int MinimumIterationsPerAction = 100_000;

    /// <summary>Median drift between two identical probes that makes a run unpublishable.</summary>
    public const double MaxMedianDrift = 0.20;

    /// <summary>Run-to-run max/min that makes a run unpublishable.</summary>
    public const double MaxSpreadRatio = 3.0;

    /// <summary>Run-to-run dispersion of the probe that makes a run unpublishable (percent).</summary>
    public const double MaxProbeRelativeStdDev = 25.0;

    /// <summary>
    /// Share of the machine's CPU other processes may use before a figure is refused.
    /// The two relative criteria above cannot see a machine that is *uniformly* loaded —
    /// every probe is slowed by the same factor, so drift stays at zero while the
    /// published numbers are inflated (measured: 3x on a saturated 16-thread box). Only
    /// system-wide accounting tells "nothing else is running" apart from "nothing else
    /// started while we measured". The limit is deliberately coarse: a desktop with a
    /// chat client and a music player sits around 10-20%, a saturated box at 95%.
    /// </summary>
    public const double MaxOtherCpuFraction = 0.50;

    private const int MinimumIterationsPerSample = 1_000;
    private const int MaximumIterationsPerSample = 20_000;
    private const double TargetSampleMilliseconds = 3.0;
    private const int MinimumSamplesPerRun = 3;

    // Fixed, deterministic, allocation-free work used by the load self-check.
    private const int BaselineWorkIterations = 100_000;
    private const int ProbeRuns = 5;
    private const int ProbeSamplesPerRun = 7;
    private const int ProbeIterationsPerSample = 3;
    private const int BaselineWarmupInvocations = 1_000;

    private static readonly IReadOnlyDictionary<string, string> EmptyTags = new ReadOnlyDictionary<
        string,
        string
    >(new Dictionary<string, string>());

    private static readonly bool TraceProbe =
        Environment.GetEnvironmentVariable("PICOBENCH_PROBE_TRACE") == "1";

    private static int _sink;
    private static bool _baselineWarmed;
    private static bool _selfCheckReported;

    static BenchProtocol()
    {
        VerifyRoleGuard();
    }

    /// <summary>Whether the runtime can generate code on the fly (a feature switch, not a fact).</summary>
    public static bool DynamicCodeSupported =>
        System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// Whether the benchmark assembly itself is still IL on disk next to the executable.
    /// A NativeAOT publish contains native code only, so the .dll is simply absent there.
    /// (<c>Assembly.Location</c> answers the same question but makes the IL linker emit
    /// IL3000, which this repository treats as noise it does not want in a build log.)
    /// </summary>
    public static bool HasIlOnDisk
    {
        get
        {
            var name = typeof(BenchProtocol).Assembly.GetName().Name;
            return name is not null
                && File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
        }
    }

    /// <summary>How this process executes code — read once, never per row.</summary>
    public static ProtocolMode Mode { get; } = DetectMode();

    /// <summary><c>Jit</c> or <c>Aot</c>, for table headers.</summary>
    public static string ModeName => Mode == ProtocolMode.Aot ? "Aot" : "Jit";

    /// <summary>Measurements whose figures were published.</summary>
    public static int PublishedCount { get; private set; }

    /// <summary>Measurements the protocol refused to publish.</summary>
    public static int RejectedCount { get; private set; }

    /// <summary>Iterations per run implied by the protocol floor (100k over 5 runs).</summary>
    public static int MinimumIterationsPerRun => MinimumIterationsPerAction / RunsPerAction;

    /// <summary>
    /// 1 when <c>PICOBENCH_STRICT=1</c> and anything was refused, else 0. A refusal
    /// is a finding, not a crash, so the default stays 0 and CI smoke stays green.
    /// </summary>
    public static int ExitCode =>
        RejectedCount > 0 && Environment.GetEnvironmentVariable("PICOBENCH_STRICT") == "1" ? 1 : 0;

    /// <summary>
    /// The protocol header every benchmark prints once: execution mode, timer
    /// resolution, iteration budget and what the reported numbers mean.
    /// </summary>
    public static void PrintBanner()
    {
        // Both signals are printed because neither alone identifies the binary: a JIT
        // run of a project whose csproj sets PublishAot=true inherits the
        // IsDynamicCodeSupported=false feature switch from its runtimeconfig, so the
        // banner used to read "Aot" for a plainly JIT run. The IL-on-disk probe is
        // what distinguishes the two.
        Console.WriteLine(
            $"Measurement protocol | Mode: {ModeName} | IL on disk: {YesNo(HasIlOnDisk)}"
                + $" | IsDynamicCodeSupported: {YesNo(DynamicCodeSupported)}"
                + $" | clock {Stopwatch.Frequency}Hz"
                + (Stopwatch.IsHighResolution ? "/high-resolution" : "")
                + $" | runtime {Environment.Version}"
        );
        Console.WriteLine(
            $"{RunsPerAction} independent runs × ≥{MinimumIterationsPerRun:N0} iterations/run"
                + $" (≥{MinimumIterationsPerAction:N0} per operation) | values are median µs/op"
                + " (min–max ±stddev over the runs)"
        );
        Console.WriteLine(
            "A run that is not idle — fixed-workload drift above"
                + $" {MaxMedianDrift:P0}, spread above {MaxSpreadRatio:F1}x, or other processes"
                + $" using more than {MaxOtherCpuFraction:P0} of the CPU — is not published."
        );
    }

    /// <summary>
    /// The load self-check: times the same fixed workload twice and compares the two
    /// medians. Must pass before any figure is produced.
    /// </summary>
    /// <returns>What the probe saw; <see cref="LoadCheck.Stable"/> gates publication.</returns>
    public static LoadCheck CheckLoad()
    {
        // Give tiered JIT time to promote the baseline loop before the first probe;
        // otherwise probe 1 times untiered code and probe 2 tiered code, and the
        // drift criterion fires on a perfectly idle machine.
        if (!_baselineWarmed)
        {
            for (var i = 0; i < BaselineWarmupInvocations; i++)
                BaselineWork();
            _baselineWarmed = true;
        }

        var before = MachineCpu.Take();
        var first = Probe();
        var second = Probe();
        var after = MachineCpu.Take();
        var otherCpu = MachineCpu.OtherCpuFraction(before, after);

        var drift =
            Math.Abs(second.MedianUs - first.MedianUs) / Math.Min(first.MedianUs, second.MedianUs);
        var spread = Math.Max(first.Spread, second.Spread);
        var relativeStdDev = Math.Max(first.RelativeStdDevPercent, second.RelativeStdDevPercent);

        string failure = "";
        if (!double.IsFinite(drift) || drift > MaxMedianDrift)
            failure =
                $"repeating the same fixed workload twice moved its median by {drift:P1}"
                + $" (limit {MaxMedianDrift:P0}) — the machine is not idle";
        else if (spread > MaxSpreadRatio)
            failure =
                $"run-to-run max/min of the fixed workload is {spread:F2}x (limit"
                + $" {MaxSpreadRatio:F1}x) — the machine is not idle";
        else if (relativeStdDev > MaxProbeRelativeStdDev)
            failure =
                $"run-to-run dispersion of the fixed workload is {relativeStdDev:F1}% (limit"
                + $" {MaxProbeRelativeStdDev:F0}%) — the machine is not idle";
        else if (otherCpu > MaxOtherCpuFraction)
            failure =
                $"other processes used {otherCpu:P0} of the machine's CPU during the check"
                + $" (limit {MaxOtherCpuFraction:P0}) — the machine is not idle";

        if (TraceProbe)
            Console.WriteLine(
                $"  probe: {first.MedianUs:F1}µs vs {second.MedianUs:F1}µs | drift {drift:P1} |"
                    + $" spread {spread:F2}x (run-to-run) | dispersion {relativeStdDev:F1}% |"
                    + $" other-CPU {FormatOtherCpu(otherCpu)}"
            );

        var check = new LoadCheck(
            failure.Length == 0,
            drift,
            spread,
            relativeStdDev,
            otherCpu,
            failure
        );

        if (!check.Stable)
        {
            Console.WriteLine($"  ⚠ load self-check failed: {failure}");
            return check;
        }

        if (!_selfCheckReported)
        {
            _selfCheckReported = true;
            Console.WriteLine(
                $"  load self-check: idle enough — drift {drift:P1}, spread {spread:F2}x,"
                    + $" dispersion {relativeStdDev:F1}% on a {first.MedianUs:F1}µs fixed workload,"
                    + $" other-CPU {FormatOtherCpu(otherCpu)}"
            );
        }

        return check;
    }

    /// <summary>
    /// Measures one action under the protocol, refusing to publish when the machine
    /// is busy or when the action's own runs spread more than <see cref="MaxSpreadRatio"/>.
    /// </summary>
    /// <param name="label">Action name (printed and stamped on the sample).</param>
    /// <param name="action">The work to time.</param>
    /// <returns>The measurement, or a sentinel sample if the protocol refused it.</returns>
    public static ProtocolSample Measure(string label, Action action)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(action);

        var check = CheckLoad();
        if (!check.Stable)
            return Reject(label, check.Failure);

        var sample = MeasureCore(label, action);
        if (!sample.Stable)
            return sample;
        if (sample.Spread > MaxSpreadRatio)
            return Reject(
                label,
                $"the action's own {RunsPerAction} runs spread {sample.Spread:F2}x (limit"
                    + $" {MaxSpreadRatio:F1}x) — the code had not settled (tiered JIT, GC) or the"
                    + " machine is busy, so there is no trustworthy figure to report"
            );

        PublishedCount++;
        return sample;
    }

    /// <summary>
    /// Measures both arms of a comparison. Argument order is the contract:
    /// <paramref name="ours"/> is PicoSerDe's action, <paramref name="theirs"/> the
    /// competitor's — the same order the returned tuple is in.
    /// </summary>
    /// <param name="label">Scenario name.</param>
    /// <param name="ours">PicoSerDe's action.</param>
    /// <param name="theirs">The competitor's action.</param>
    /// <returns>Both samples, ours first.</returns>
    public static (ProtocolSample ours, ProtocolSample theirs) Compare(
        string label,
        Action ours,
        Action theirs
    ) => (Measure(label, ours), Measure(label, theirs));

    /// <summary>
    /// Measures both arms and enforces the role convention on their labels before
    /// running anything: <paramref name="oursLabel"/> must be a Pico* name and
    /// <paramref name="theirsLabel"/> must not be (see BenchRoleGuard.cs).
    /// </summary>
    /// <param name="label">Scenario name.</param>
    /// <param name="oursLabel">PicoSerDe's label, e.g. <c>PicoJetson</c>.</param>
    /// <param name="ours">PicoSerDe's action.</param>
    /// <param name="theirsLabel">Competitor label, e.g. <c>System.Text.Json</c>.</param>
    /// <param name="theirs">The competitor's action.</param>
    /// <returns>Both samples, ours first.</returns>
    public static (ProtocolSample ours, ProtocolSample theirs) Compare(
        string label,
        string oursLabel,
        Action ours,
        string theirsLabel,
        Action theirs
    )
    {
        GuardRoles(label, oursLabel, theirsLabel);
        return (Measure(oursLabel, ours), Measure(theirsLabel, theirs));
    }

    /// <summary>Formats a sample the way the protocol reports it.</summary>
    /// <param name="sample">The sample.</param>
    /// <returns><c>0.198 (0.190–0.240 ±0.021)</c>, or <c>UNSTABLE — not published</c>.</returns>
    public static string FormatCell(ProtocolSample sample) =>
        sample.Stable
            ? $"{sample.MedianUs:F3} ({sample.MinUs:F3}–{sample.MaxUs:F3} ±{sample.StdDevUs:F3})"
            : "UNSTABLE — not published";

    /// <summary>
    /// How many times faster ours is (theirs / ours) — the same direction as the old
    /// PicoBench speedup, where &gt;1 meant PicoSerDe won. Never called for
    /// non-equivalent arms.
    /// </summary>
    /// <param name="ours">PicoSerDe's sample.</param>
    /// <param name="theirs">The competitor's sample.</param>
    /// <returns>The ratio, or NaN when either side was refused.</returns>
    public static double Ratio(ProtocolSample ours, ProtocolSample theirs) =>
        ours.Stable && theirs.Stable ? theirs.MedianUs / ours.MedianUs : double.NaN;

    /// <summary>
    /// Cheap, dependency-free system-wide CPU accounting: how much of the machine
    /// processes other than this one used between two samples. Windows reads
    /// GetSystemTimes, Linux reads /proc/stat, and any other OS answers NaN — the
    /// drift/spread criteria still apply there, they just cannot see a machine that is
    /// uniformly loaded.
    /// </summary>
    private static class MachineCpu
    {
        private static readonly Process Self = Process.GetCurrentProcess();

        public static Sample Take()
        {
            var own = OwnCpu();
            try
            {
                if (
                    OperatingSystem.IsWindows()
                    && TryWindows(out var windowsBusy, out var windowsTotal)
                )
                    return new Sample(windowsBusy, windowsTotal, own);
                if (OperatingSystem.IsLinux() && TryLinux(out var linuxBusy, out var linuxTotal))
                    return new Sample(linuxBusy, linuxTotal, own);
            }
            catch (Exception)
            {
                // No system-wide accounting on this host after all — the relative
                // criteria still apply, they just cannot see uniform load.
            }

            return new Sample(double.NaN, double.NaN, own);
        }

        public static double OtherCpuFraction(Sample from, Sample to)
        {
            if (double.IsNaN(from.TotalSeconds) || double.IsNaN(to.TotalSeconds))
                return double.NaN;

            var totalDelta = to.TotalSeconds - from.TotalSeconds;
            if (totalDelta <= 0)
                return double.NaN;

            var busyDelta = to.BusySeconds - from.BusySeconds;
            var ownDelta = (to.OwnCpu - from.OwnCpu).TotalSeconds;
            return Math.Clamp((busyDelta - ownDelta) / totalDelta, 0.0, 1.0);
        }

        public readonly record struct Sample(
            double BusySeconds,
            double TotalSeconds,
            TimeSpan OwnCpu
        );

        private static TimeSpan OwnCpu()
        {
            try
            {
                return Self.TotalProcessorTime;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }

        // kernelTime already includes idleTime, so busy = kernel + user - idle.
        private static bool TryWindows(out double busySeconds, out double totalSeconds)
        {
            busySeconds = double.NaN;
            totalSeconds = double.NaN;
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return false;

            const double fileTimeTicksPerSecond = 10_000_000.0;
            totalSeconds = (kernel + user) / fileTimeTicksPerSecond;
            busySeconds = totalSeconds - idle / fileTimeTicksPerSecond;
            return true;
        }

        // USER_HZ is 100 on every configuration this repository runs on; the own-CPU
        // subtraction below is tuned to that and only ever corrects a small term.
        private static bool TryLinux(out double busySeconds, out double totalSeconds)
        {
            busySeconds = double.NaN;
            totalSeconds = double.NaN;
            try
            {
                using var reader = new StreamReader("/proc/stat");
                var line = reader.ReadLine();
                if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
                    return false;

                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var total = 0.0;
                var idle = 0.0;
                for (var i = 1; i < fields.Length && i <= 8; i++)
                {
                    if (!double.TryParse(fields[i], out var value))
                        return false;
                    total += value;
                    if (i is 4 or 5)
                        idle += value;
                }

                if (total <= 0)
                    return false;

                totalSeconds = total / 100.0;
                busySeconds = (total - idle) / 100.0;
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            ExactSpelling = true,
            SetLastError = true
        )]
        private static extern bool GetSystemTimes(
            out long idleTime,
            out long kernelTime,
            out long userTime
        );
    }

    private static string FormatOtherCpu(double fraction) =>
        double.IsNaN(fraction) ? "n/a on this OS" : fraction.ToString("P0");

    private static ProtocolMode DetectMode() =>
        !HasIlOnDisk && !DynamicCodeSupported ? ProtocolMode.Aot : ProtocolMode.Jit;

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static ProtocolSample MeasureCore(string label, Action action)
    {
        var iterationsPerSample = ResolveIterationsPerSample(action);
        var samplesPerRun = Math.Max(
            MinimumSamplesPerRun,
            (MinimumIterationsPerRun + iterationsPerSample - 1) / iterationsPerSample
        );

        var before = MachineCpu.Take();
        Warmup(action, iterationsPerSample);

        var perRun = new double[RunsPerAction];
        var samples = new double[samplesPerRun];
        for (var run = 0; run < RunsPerAction; run++)
        {
            // Equal starting conditions for every run: without this, the first run
            // measures a small heap and later runs pay for the garbage the earlier
            // runs produced, which shows up as run-to-run spread that has nothing to
            // do with the code under test.
            CollectBeforeRun();

            for (var sample = 0; sample < samplesPerRun; sample++)
            {
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < iterationsPerSample; i++)
                    action();
                watch.Stop();
                samples[sample] = watch.Elapsed.TotalMilliseconds * 1000.0 / iterationsPerSample;
            }

            perRun[run] = Median(samples);
        }

        var otherCpu = MachineCpu.OtherCpuFraction(before, MachineCpu.Take());
        if (TraceProbe)
            Console.WriteLine(
                $"  runs {label}: {string.Join(" | ", perRun.Select(v => v.ToString("F3")))}"
                    + $" (iters/sample {iterationsPerSample}, samples/run {samplesPerRun},"
                    + $" other-CPU {FormatOtherCpu(otherCpu)})"
            );

        if (otherCpu > MaxOtherCpuFraction)
            return Reject(
                label,
                $"other processes used {otherCpu:P0} of the machine's CPU while this action was"
                    + $" measured (limit {MaxOtherCpuFraction:P0}) — every run is slowed by an"
                    + " unknown factor, so the figure is not trustworthy"
            );

        return new ProtocolSample(
            Median(perRun),
            perRun.Min(),
            perRun.Max(),
            StdDev(perRun),
            RunsPerAction
        )
        {
            Label = label,
            Iterations = iterationsPerSample * samplesPerRun * RunsPerAction,
            Mode = Mode,
            Stable = true,
        };
    }

    /// <summary>
    /// Calls the action until its own timing stops falling. A single fixed warmup is
    /// not enough under tiered JIT: the first measured runs then carry tier-0 code
    /// cost and the run-to-run spread reaches 3-4x on code that is perfectly stable,
    /// which is a property of the harness, not of the library being measured.
    /// </summary>
    private static void Warmup(Action action, int iterationsPerSample)
    {
        const double settledTolerance = 0.05;
        // Tiered JIT keeps promoting code for a while after a process starts — the
        // background compiler is busy with the whole program — so the JIT budget is
        // deliberately generous. NativeAOT has no tiers, so it needs far less.
        var minimumMilliseconds = BenchProtocol.Mode == ProtocolMode.Aot ? 150 : 1_000;
        var maximumMilliseconds = BenchProtocol.Mode == ProtocolMode.Aot ? 1_000 : 2_500;

        var total = Stopwatch.StartNew();
        var previous = double.NaN;
        while (true)
        {
            var burst = Stopwatch.StartNew();
            for (var i = 0; i < iterationsPerSample; i++)
                action();
            burst.Stop();

            var perOp = burst.Elapsed.TotalMilliseconds * 1000.0 / iterationsPerSample;
            var settled =
                double.IsFinite(previous)
                && Math.Abs(perOp - previous) / Math.Min(perOp, previous) <= settledTolerance;

            if (settled && total.ElapsedMilliseconds >= minimumMilliseconds)
                return;
            if (total.ElapsedMilliseconds >= maximumMilliseconds)
                return;

            previous = perOp;
        }
    }

    private static void CollectBeforeRun()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
    }

    private static int ResolveIterationsPerSample(Action action)
    {
        const int calibrationCalls = 500;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < calibrationCalls; i++)
            action();
        watch.Stop();

        var perOpMilliseconds = watch.Elapsed.TotalMilliseconds / calibrationCalls;
        if (!(perOpMilliseconds > 0))
            return MaximumIterationsPerSample;

        var iterations = (int)(TargetSampleMilliseconds / perOpMilliseconds);
        return Math.Clamp(iterations, MinimumIterationsPerSample, MaximumIterationsPerSample);
    }

    private static ProtocolSample Reject(string label, string reason)
    {
        RejectedCount++;
        Console.WriteLine($"  UNSTABLE — not published: {label}");
        Console.WriteLine($"    reason: {reason}");
        Console.WriteLine("    re-run on an idle machine; this row carries no number and no ratio");
        return ProtocolSample.Unstable(label);
    }

    private static ProbeResult Probe()
    {
        var perRun = new double[ProbeRuns];
        var samples = new double[ProbeSamplesPerRun];
        for (var run = 0; run < ProbeRuns; run++)
        {
            for (var sample = 0; sample < ProbeSamplesPerRun; sample++)
            {
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < ProbeIterationsPerSample; i++)
                    BaselineWork();
                watch.Stop();
                samples[sample] =
                    watch.Elapsed.TotalMilliseconds * 1000.0 / ProbeIterationsPerSample;
            }

            perRun[run] = Median(samples);
        }

        var median = Median(perRun);
        return new ProbeResult(
            median,
            perRun.Max() / perRun.Min(),
            StdDev(perRun) / median * 100.0
        );
    }

    // Fixed amount of allocation-free work; the load self-check times this twice.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static void BaselineWork()
    {
        var value = _sink;
        for (var i = 0; i < BaselineWorkIterations; i++)
            value = unchecked(value * 31 + i);
        _sink = value;
    }

    private static void GuardRoles(string label, string oursLabel, string theirsLabel)
    {
        // Reuse the existing, unmodified guard: build a throwaway comparison whose
        // candidate is ours and whose baseline is theirs, and let it judge the roles.
        var stats = new Statistics { Avg = 1.0 };
        var baseline = new BenchmarkResult(
            theirsLabel,
            stats,
            1,
            1,
            null,
            EmptyTags,
            Array.Empty<TimingSample>(),
            null
        );
        var candidate = new BenchmarkResult(
            oursLabel,
            stats,
            1,
            1,
            null,
            EmptyTags,
            Array.Empty<TimingSample>(),
            null
        );
        _ = BenchmarkRoles.Guard(new ComparisonResult(label, baseline, candidate, null, EmptyTags));
    }

    private static void VerifyRoleGuard()
    {
        var rejected = false;
        try
        {
            GuardRoles("protocol self-test", "System.Text.Json", "PicoJetson");
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        if (!rejected)
            throw new InvalidOperationException(
                "BenchProtocol self-test failed: BenchRoleGuard accepted an inverted pair"
                    + " (candidate='System.Text.Json', baseline='PicoJetson'), so the"
                    + " ours/theirs labels of every benchmark can no longer be trusted."
            );
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return (sorted.Length & 1) == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static double StdDev(double[] values)
    {
        if (values.Length < 2)
            return 0.0;
        var mean = values.Average();
        var sum = 0.0;
        foreach (var value in values)
            sum += (value - mean) * (value - mean);
        return Math.Sqrt(sum / (values.Length - 1));
    }

    private readonly record struct ProbeResult(
        double MedianUs,
        double Spread,
        double RelativeStdDevPercent
    );
}

/// <summary>
/// The printed comparison table: protocol banner, one row per scenario, and the two
/// publication rules a printer must never break — one execution mode per table, and
/// no number or ratio for a refused or non-equivalent row.
/// </summary>
public sealed class ProtocolTable
{
    private const int DividerWidth = 116;

    private readonly string _oursLabel;
    private readonly string? _theirsLabel;
    private readonly bool _equivalent;
    private readonly List<ProtocolSample> _samples = new();

    /// <summary>Creates a table.</summary>
    /// <param name="oursLabel">PicoSerDe's label (must be a Pico* name).</param>
    /// <param name="theirsLabel">Competitor label, or null for a self-benchmark.</param>
    /// <param name="equivalent">
    /// False when the two arms are not the same job — every row then prints
    /// "NON-EQUIVALENT — not comparable" and no ratio is computed.
    /// </param>
    public ProtocolTable(string oursLabel, string? theirsLabel, bool equivalent = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(oursLabel);

        _oursLabel = oursLabel;
        _theirsLabel = theirsLabel;
        _equivalent = equivalent;

        BenchProtocol.PrintBanner();
        if (theirsLabel is null)
            Console.WriteLine($"Table: {oursLabel} — absolute timings, no competitor, no ratio.");
        else if (!equivalent)
            Console.WriteLine(
                "NON-EQUIVALENT — not comparable: the competitor arm is a hand-written DOM"
                    + " walk plus typed mapping, not a single library call. Timings are listed"
                    + " for reference; no ratio is computed or printed."
            );

        Console.WriteLine(new string('-', DividerWidth));
        Console.WriteLine(
            theirsLabel is null
                ? $"{"Scenario", -40} {oursLabel, -40}"
                : $"{"Scenario", -40} {oursLabel + " (ours)", -36} {theirsLabel + " (theirs)", -36}"
        );
        Console.WriteLine(new string('-', DividerWidth));
    }

    /// <summary>Measures and prints one comparison row.</summary>
    /// <param name="scenario">Scenario name.</param>
    /// <param name="ours">PicoSerDe's action.</param>
    /// <param name="theirs">The competitor's action.</param>
    public void Compare(string scenario, Action ours, Action theirs)
    {
        if (_theirsLabel is null)
            throw new InvalidOperationException(
                "This table was created without a competitor; call Measure(scenario, action)."
            );

        var (oursSample, theirsSample) = BenchProtocol.Compare(
            scenario,
            _oursLabel,
            ours,
            _theirsLabel,
            theirs
        );
        Record(oursSample);
        Record(theirsSample);

        var ratio = _equivalent ? BenchProtocol.Ratio(oursSample, theirsSample) : double.NaN;
        var refused = !oursSample.Stable || !theirsSample.Stable;
        var marker =
            refused ? "⚠"
            : !_equivalent ? "·"
            : ratio >= 1 ? "✓"
            : "✗";
        var verdict =
            !_equivalent ? "NON-EQUIVALENT — not comparable"
            : double.IsFinite(ratio) ? $"{ratio:F2}x"
            : "—";

        Console.WriteLine(
            $"{marker} {scenario, -38} {BenchProtocol.FormatCell(oursSample), -36} "
                + $"{BenchProtocol.FormatCell(theirsSample), -36} {verdict}"
        );
    }

    /// <summary>Measures and prints one single-arm row (self-benchmarks).</summary>
    /// <param name="scenario">Scenario name.</param>
    /// <param name="action">The action to time.</param>
    public void Measure(string scenario, Action action)
    {
        var sample = BenchProtocol.Measure(_oursLabel, action);
        Record(sample);
        Console.WriteLine($"{scenario, -40} {BenchProtocol.FormatCell(sample), -40}");
    }

    /// <summary>Prints the footer and enforces the single-mode rule.</summary>
    public void Print()
    {
        AssertSingleMode();
        Console.WriteLine(new string('-', DividerWidth));
        var scenarios = _theirsLabel is null ? _samples.Count : _samples.Count / 2;
        Console.WriteLine(
            $"scenarios {scenarios} | arms published {BenchProtocol.PublishedCount} | arms refused"
                + $" {BenchProtocol.RejectedCount}"
                + (BenchProtocol.RejectedCount > 0 ? " (UNSTABLE — not published)" : "")
        );
        if (!_equivalent)
            Console.WriteLine("No ratio is reported for a NON-EQUIVALENT comparison.");
    }

    /// <summary>
    /// Throws if this table would show more than one execution mode — the README
    /// forbids merging AOT and JIT into one figure or one ratio.
    /// </summary>
    public void AssertSingleMode()
    {
        foreach (var sample in _samples)
            if (sample.Mode != BenchProtocol.Mode)
                throw new InvalidOperationException(
                    $"Table would mix execution modes: '{sample.Label}' was measured under"
                        + $" {sample.Mode} but this process runs {BenchProtocol.Mode}."
                );
    }

    private void Record(ProtocolSample sample)
    {
        if (sample.Mode != BenchProtocol.Mode)
            throw new InvalidOperationException(
                $"Table would mix execution modes: '{sample.Label}' is {sample.Mode}, process is"
                    + $" {BenchProtocol.Mode}."
            );
        _samples.Add(sample);
    }
}
