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
//                               one fixed, deterministic busy loop twice over a window of
//                               at least ProbeMinimumMilliseconds each; if the two
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
//                               than MaxOtherCpuFraction (35%, or whatever
//                               PICOBENCH_MAX_OTHER_CPU says) of the machine.
//                               When the host cannot report system-wide CPU at all
//                               (macOS, a container without a readable /proc/stat) the
//                               fraction is NaN — and NaN must never mean "fine": the
//                               measurement is refused (fail-closed). PICOBENCH_ALLOW_
//                               UNKNOWN_CPU=1 is the only way to publish there, and it
//                               brands the table header and footer with a "load unknown,
//                               numbers not trustworthy" warning.
//   2. >=100k iterations per operation, >=5 independent runs
//                            -> RunsPerAction = 5 runs, each >=20'000 iterations
//                               (>=100'000 per operation per arm); every run is
//                               kept, none discarded. MeasureCore() asserts the
//                               product instead of trusting the constants, and the
//                               table footer prints the iterations actually executed,
//                               so the 100k floor is auditable rather than claimed.
//   3. Median + dispersion   -> ProtocolSample reports median + min + max + stddev
//                               (over the 5 run medians). There is no bare average
//                               anywhere in the printed output.
//   4. AOT and JIT never mixed
//                            -> The real guarantee is process isolation: a JIT run and an
//                               AOT run are different processes. On top of that, Mode is
//                               decided once (IL on disk + dynamic-code support), printed
//                               in every table header, stamped onto every sample, and
//                               ProtocolTable's mode check is a defensive assertion — in
//                               normal operation it cannot fire (every sample is stamped by
//                               the same process-wide Mode).
//   5. Equivalent competitor arms
//                            -> ProtocolTable(equivalent: false) prints "NON-EQUIVALENT" in
//                               every row's verdict column (the full explanation is printed
//                               once above the table, plus the caller's own evidence when it
//                               passes `nonEquivalentReason`) and computes no ratio (the INI
//                               competitor arm, and the Tommy reference arm, are hand-written
//                               DOM walks, not single library calls). The positive case is
//                               labelled just as loudly: equivalent: true prints "EQUIVALENT"
//                               plus the ratio's direction (Verdict = ours ÷ theirs — the
//                               README's own direction, where above 1.00 means we are slower, so
//                               no printed figure ever needs a reciprocal taken before it is
//                               quoted), so a table's divisibility is never asserted by
//                               silence. A program that needs both kinds of table prints two of
//                               them; the banner appears once per process and the counters in
//                               Print() are per table — the summary line names both scopes
//                               (`published=`, `process_published=`) so the two can never be
//                               conflated.
//
// Role semantics: `ours` is always PicoSerDe's own action and `theirs` the
// competitor's, in that argument order. BenchRoleGuard.cs (unchanged) still owns
// the "the candidate slot must hold a Pico* action" judgement, and
// Compare(..., oursLabel, ..., theirsLabel, ...) runs every pair through it — so the
// labels cannot be swapped without an exception. The static constructor additionally
// proves, once per process, that the guard really rejects an inverted pair.
//
// Exit code: a *single* refusal is a finding, not a crash, so it does not fail the
// process — CI's `dotnet run --no-build` smoke cannot go red because a runner was busy.
// Publishing *nothing at all* is a different thing: `published == 0 && refused > 0` means
// the run produced no data at all, which is a failure of the run itself, and exits 1.
// Two exceptions, both explicit: on a host that cannot measure load at all (every row was
// refused because the criterion is unavailable, so the environment failed, not the run),
// and when PICOBENCH_ALLOW_ZERO_PUBLISH=1 says so. PICOBENCH_STRICT=1 keeps its meaning:
// any refusal exits 1. Every table also ends with a fixed-format
// `PICOBENCH_SUMMARY scenarios=… published=… refused=… mode=… equivalent=… exit=…
// process_published=… process_refused=…` line so a CI gate can see a run that published
// nothing without parsing prose — `published=`/`refused=` are that table's, `process_*`
// are the whole process's, and `exit_scope=process-so-far` says that `exit=` is measured
// when the line is printed (a two-table program prints the line twice).

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

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
/// One measurement of one action, reported as the protocol requires.
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

    /// <summary>
    /// Share of the machine's CPU other processes used while this arm was measured, or NaN
    /// when the host offers no system-wide accounting. Printed per arm, so a reader can see
    /// how close the run came to <see cref="BenchProtocol.MaxOtherCpuFraction"/>.
    /// </summary>
    public double OtherCpuFraction { get; init; }

    /// <summary>How long the warm-up ran before this arm was measured.</summary>
    public int WarmupMilliseconds { get; init; }

    /// <summary>How many timed bursts the warm-up spent (each burst = 3 samples, median).</summary>
    public int WarmupBursts { get; init; }

    /// <summary>
    /// True when the warm-up reached its convergence criterion. False means the budget ran
    /// out first: the arm was measured before the code stopped heating up, so the figure is
    /// marked in the table (and refused outright under <c>PICOBENCH_STRICT=1</c>).
    /// </summary>
    public bool WarmupSettled { get; init; }

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
            WarmupSettled = false,
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
/// <param name="LoadUnknown">
/// True when <paramref name="OtherCpuFraction"/> is NaN — the machine's load could not be
/// verified at all (see <see cref="BenchProtocol.LoadNotMeasurable"/>).
/// </param>
/// <param name="Failure">Empty when stable, otherwise the criterion that failed.</param>
public readonly record struct LoadCheck(
    bool Stable,
    double Drift,
    double Spread,
    double ProbeRelativeStdDev,
    double OtherCpuFraction,
    bool LoadUnknown,
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
    /// Default share of the machine's CPU other processes may use before a figure is
    /// refused. The two relative criteria above cannot see a machine that is *uniformly*
    /// loaded — every probe is slowed by the same factor, so drift stays at zero while the
    /// published numbers are inflated (measured: 3x on a saturated 16-thread box). Only
    /// system-wide accounting tells "nothing else is running" apart from "nothing else
    /// started while we measured".
    /// <para>
    /// 35% is the midpoint between the observed idle floor of a loaded desktop (9-26%) and
    /// a saturated box (95%); the old 50% left no room above the floor and could not see a
    /// 30-49% uniform load at all. Override it with <c>PICOBENCH_MAX_OTHER_CPU</c> when a
    /// host's baseline is known to be different — the printed banner always shows the value
    /// actually in force.
    /// </para>
    /// </summary>
    public static double MaxOtherCpuFraction { get; } = ResolveOtherCpuLimit();

    /// <summary>What <see cref="MaxOtherCpuFraction"/> is when nothing overrides it.</summary>
    public const double DefaultMaxOtherCpuFraction = 0.35;

    internal const int MinimumIterationsPerSample = 1_000;
    internal const int MaximumIterationsPerSample = 20_000;
    internal const double TargetSampleMilliseconds = 3.0;
    internal const int MinimumSamplesPerRun = 3;

    // Fixed, deterministic, allocation-free work used by the load self-check. The probe
    // keeps running until ProbeMinimumMilliseconds has elapsed, so the system-wide CPU
    // window behind the load judgement is a real window (2 probes x >=200 ms) instead of
    // the ~3 ms it covered when the sample count was fixed.
    private const int BaselineWorkIterations = 100_000;
    private const int ProbeRuns = 5;
    private const int ProbeSamplesPerRun = 7;
    private const int ProbeIterationsPerSample = 3;
    private const int ProbeMinimumMilliseconds = 200;
    private const int BaselineWarmupInvocations = 1_000;

    // Warm-up convergence: a burst is the median of WarmupSamplesPerBurst timings (a single
    // timing of a ~1.3 ms burst is noise, not a measurement), and the arm counts as converged
    // after WarmupConsecutiveSettled bursts in a row agree within WarmupTolerance.
    private const int WarmupSamplesPerBurst = 3;
    private const int WarmupConsecutiveSettled = 3;
    private const double WarmupTolerance = 0.05;

    private static readonly IReadOnlyDictionary<string, string> EmptyTags = new ReadOnlyDictionary<
        string,
        string
    >(new Dictionary<string, string>());

    private static readonly bool TraceProbe = Flag("PICOBENCH_PROBE_TRACE");

    /// <summary>
    /// <c>PICOBENCH_ALLOW_UNKNOWN_CPU=1</c>: publish even when the host cannot report
    /// system-wide CPU load. Off by default, because "I cannot tell whether the machine is
    /// idle" must not read as "the machine is idle" — that is exactly the fail-open path
    /// that let a saturated box publish numbers inflated by 3x. When it is on, the banner
    /// and every table footer say so in plain words.
    /// </summary>
    public static bool AllowUnknownCpu { get; } = Flag("PICOBENCH_ALLOW_UNKNOWN_CPU");

    /// <summary>
    /// <c>PICOBENCH_ALLOW_ZERO_PUBLISH=1</c>: keep the exit code 0 even when the run
    /// published nothing at all (see <see cref="ExitCode"/>).
    /// </summary>
    public static bool AllowZeroPublish { get; } = Flag("PICOBENCH_ALLOW_ZERO_PUBLISH");

    /// <summary><c>PICOBENCH_STRICT=1</c>: any refusal fails the process.</summary>
    public static bool Strict { get; } = Flag("PICOBENCH_STRICT");

    /// <summary>
    /// <c>PICOBENCH_SIMULATE_UNKNOWN_CPU=1</c>: a test hook, never a production setting. It
    /// makes <see cref="MachineCpu.Take"/> answer "no system-wide accounting" on a host that
    /// does have it, so the fail-closed path can be exercised (and unit-tested) on Windows.
    /// </summary>
    internal static bool SimulateUnknownCpu { get; } = Flag("PICOBENCH_SIMULATE_UNKNOWN_CPU");

    private static bool _loadNotMeasurable;
    private static int _sink;
    private static bool _baselineWarmed;
    private static bool _selfCheckReported;
    private static bool _unknownCpuReported;
    private static bool _bannerReported;

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
    /// True once this process met a host that cannot report system-wide CPU load (NaN), i.e.
    /// the load criterion was unavailable rather than failing.
    /// </summary>
    public static bool LoadNotMeasurable => _loadNotMeasurable;

    /// <summary>
    /// 1 when <see cref="Strict"/> is on and anything was refused; also 1 when the run
    /// published <b>nothing at all</b> — that is a broken run, not a finding. The single
    /// grey area is a host that cannot measure load at all: there the criterion is
    /// unavailable rather than violated, so failing the process would report the environment,
    /// not the code — <see cref="LoadNotMeasurable"/> keeps that case at 0 (the summary line
    /// still says <c>published=0 refused=N cpu=unknown</c>) unless
    /// <c>PICOBENCH_ALLOW_ZERO_PUBLISH</c> or <c>PICOBENCH_STRICT</c> says otherwise.
    /// <para>
    /// This is the process-wide reading of <see cref="ExitCodeFor"/>, which holds the rule
    /// itself and is what the tests pin.
    /// </para>
    /// </summary>
    public static int ExitCode =>
        ExitCodeFor(PublishedCount, RejectedCount, Strict, LoadNotMeasurable, AllowZeroPublish);

    /// <summary>
    /// The exit-code rule as a pure function, so the policy can be asserted without touching
    /// the process-wide counters it is normally fed with. Order matters and is the contract:
    /// no refusal at all is 0 whatever else is set; <c>PICOBENCH_STRICT=1</c> then outranks
    /// both zero-publish exemptions (a strict run that kept the machine's excuses would not be
    /// strict); otherwise anything published, a host that cannot measure load, and
    /// <c>PICOBENCH_ALLOW_ZERO_PUBLISH=1</c> each turn a total loss into 0; and the remaining
    /// case — nothing published, something refused, on a host that can measure — is 1, because
    /// that is a broken run rather than a finding.
    /// </summary>
    /// <param name="published">Arms whose figures were published.</param>
    /// <param name="rejected">Arms the protocol refused.</param>
    /// <param name="strict">Whether <c>PICOBENCH_STRICT=1</c> is in force.</param>
    /// <param name="loadNotMeasurable">
    /// Whether this process met a host that cannot report system-wide CPU load.
    /// </param>
    /// <param name="allowZeroPublish">Whether <c>PICOBENCH_ALLOW_ZERO_PUBLISH=1</c> is set.</param>
    /// <returns>The process exit code.</returns>
    internal static int ExitCodeFor(
        int published,
        int rejected,
        bool strict,
        bool loadNotMeasurable,
        bool allowZeroPublish
    )
    {
        if (rejected == 0)
            return 0;
        if (strict)
            return 1;
        if (published > 0 || loadNotMeasurable || allowZeroPublish)
            return 0;
        return 1;
    }

    /// <summary>
    /// The protocol header every benchmark prints once: execution mode, timer
    /// resolution, iteration budget and what the reported numbers mean. Once per
    /// <b>process</b>, not once per table — the header describes the process-wide
    /// protocol (mode, clock, iteration budget, load limits), so a program with two
    /// tables (an equivalent one and a non-equivalent reference one) does not repeat
    /// the same six lines between them.
    /// </summary>
    public static void PrintBanner()
    {
        if (_bannerReported)
            return;
        _bannerReported = true;
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
                + $" (≥{MinimumIterationsPerAction:N0} per operation, asserted in code) | values"
                + " are median µs/op (min–max ±stddev over the runs)"
        );
        Console.WriteLine(
            "A run that is not idle — fixed-workload drift above"
                + $" {MaxMedianDrift:P0}, spread above {MaxSpreadRatio:F1}x, or other processes"
                + $" using more than {FormatLimit(MaxOtherCpuFraction)} of the CPU — is not"
                + " published."
        );
        Console.WriteLine(
            "The system-wide CPU criterion is the decisive one; when the host cannot report it,"
                + " measurement is refused (fail-closed) unless PICOBENCH_ALLOW_UNKNOWN_CPU=1."
                + " Knobs: PICOBENCH_MAX_OTHER_CPU (default"
                + $" {DefaultMaxOtherCpuFraction:P0}), PICOBENCH_STRICT=1 (a refusal exits 1),"
                + " PICOBENCH_ALLOW_ZERO_PUBLISH=1 (publishing nothing does not exit 1)."
        );

        if (AllowUnknownCpu)
            Console.WriteLine(
                "⚠ WARNING: PICOBENCH_ALLOW_UNKNOWN_CPU=1 — this run publishes although the"
                    + " machine's load cannot be verified. Every number below is NOT trustworthy"
                    + " and must not be quoted."
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
        // NaN is not "no load", it is "unknown load", and unknown must never pass a
        // `>` comparison — that is the fail-open path this protocol exists to kill.
        var loadUnknown = !double.IsFinite(otherCpu);
        if (loadUnknown)
            _loadNotMeasurable = true;

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
        else if (loadUnknown && !AllowUnknownCpu)
            failure = UnknownCpuFailure("during the check");
        else if (!loadUnknown && otherCpu > MaxOtherCpuFraction)
            failure =
                $"other processes used {otherCpu:P1} of the machine's CPU during the check"
                + $" (limit {FormatLimit(MaxOtherCpuFraction)}) — the machine is not idle";

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
            loadUnknown,
            failure
        );

        if (!check.Stable)
        {
            Console.WriteLine($"  ⚠ load self-check failed: {failure}");
            return check;
        }

        if (loadUnknown)
            ReportUnknownCpu();

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
    /// <returns>
    /// <c>0.198 (0.190–0.240 ±0.021) 12% w1.2s/17b</c> — median µs/op (min–max ±stddev over
    /// the runs), the other-CPU measured while that arm ran, and its warm-up audit (elapsed /
    /// bursts, prefixed with ⚠ when the warm-up never converged) — or
    /// <c>UNSTABLE — not published</c>.
    /// </returns>
    public static string FormatCell(ProtocolSample sample) =>
        sample.Stable
            ? $"{sample.MedianUs:F3} ({sample.MinUs:F3}–{sample.MaxUs:F3} ±{sample.StdDevUs:F3})"
                + $" {FormatCellCpu(sample.OtherCpuFraction)}"
                + $" w{(sample.WarmupSettled ? "" : "⚠")}"
                + $"{sample.WarmupMilliseconds / 1000.0:F1}s/{sample.WarmupBursts}b"
            : "UNSTABLE — not published";

    /// <summary>
    /// How many times slower ours is (ours / theirs) — deliberately the <b>README's</b>
    /// direction ("PicoSerDe ÷ competitor", where &gt;1.00 means we are slower) and the only
    /// one this repository prints. The harness used to report the reciprocal (theirs / ours,
    /// the old PicoBench "speedup", where &gt;1 meant PicoSerDe won), so every figure quoted
    /// from a run into a table had to be inverted first; two directions over one measurement
    /// is what let the old table come out backwards. Never called for non-equivalent arms.
    /// </summary>
    /// <param name="ours">PicoSerDe's sample.</param>
    /// <param name="theirs">The competitor's sample.</param>
    /// <returns>The ratio, or NaN when either side was refused.</returns>
    public static double Ratio(ProtocolSample ours, ProtocolSample theirs) =>
        ours.Stable && theirs.Stable ? ours.MedianUs / theirs.MedianUs : double.NaN;

    /// <summary>
    /// Cheap, dependency-free system-wide CPU accounting: how much of the machine
    /// processes other than this one used between two samples. Windows reads
    /// GetSystemTimes, Linux reads /proc/stat, and any other OS answers NaN — which
    /// <see cref="CheckLoad"/> and <see cref="MeasureCore"/> treat as "load unknown" and
    /// refuse to publish from (fail-closed), not as "load fine".
    /// </summary>
    internal static class MachineCpu
    {
        private static readonly Process Self = Process.GetCurrentProcess();

        public static Sample Take()
        {
            var own = OwnCpu();

            // Test hook only (see SimulateUnknownCpu): pretend this host cannot report
            // system-wide load, so the fail-closed path is exercisable on Windows.
            if (SimulateUnknownCpu)
                return new Sample(double.NaN, double.NaN, own);

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
                // No system-wide accounting on this host after all — that is a NaN answer,
                // and NaN is refused rather than passed (see OtherCpuFraction's callers).
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

    // P0 is right for the default (35%) but would round an override like 0.5% to "1%".
    private static string FormatLimit(double fraction) =>
        fraction == Math.Round(fraction, 2) ? fraction.ToString("P0") : fraction.ToString("P2");

    // The same value, short enough for a table cell.
    private static string FormatCellCpu(double fraction) =>
        double.IsNaN(fraction) ? "n/a" : fraction.ToString("P0");

    private static string UnknownCpuFailure(string window) =>
        $"system-wide CPU load cannot be read on this host {window}, so \"the machine is"
        + " idle\" cannot be verified — refusing to publish (fail-closed). Set"
        + " PICOBENCH_ALLOW_UNKNOWN_CPU=1 to publish anyway; those numbers are not"
        + " trustworthy.";

    private static void ReportUnknownCpu()
    {
        if (_unknownCpuReported)
            return;
        _unknownCpuReported = true;
        Console.WriteLine(
            "  ⚠ load unknown: this host exposes no system-wide CPU accounting (no"
                + " GetSystemTimes, no readable /proc/stat), so \"the machine is idle\" cannot be"
                + " verified. PICOBENCH_ALLOW_UNKNOWN_CPU=1 publishes anyway — these numbers are"
                + " NOT trustworthy and must not be quoted."
        );
    }

    private static bool Flag(string name) => Environment.GetEnvironmentVariable(name) == "1";

    // PICOBENCH_MAX_OTHER_CPU accepts "0.35", "35" and "35%" alike; anything unparsable
    // falls back to the default rather than silently disabling the criterion.
    private static double ResolveOtherCpuLimit()
    {
        var raw = Environment.GetEnvironmentVariable("PICOBENCH_MAX_OTHER_CPU");
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultMaxOtherCpuFraction;

        var text = raw.Trim();
        var percent = text.EndsWith('%');
        text = text.TrimEnd('%');
        if (
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value < 0
        )
            return DefaultMaxOtherCpuFraction;

        return percent || value > 1.0 ? value / 100.0 : value;
    }

    private static ProtocolMode DetectMode() =>
        !HasIlOnDisk && !DynamicCodeSupported ? ProtocolMode.Aot : ProtocolMode.Jit;

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static ProtocolSample MeasureCore(string label, Action action)
    {
        var iterationsPerSample = ResolveIterationsPerSample(action);
        var samplesPerRun = SamplesPerRunFor(iterationsPerSample);

        // The 100k floor is the README's rule 2. Assert it here instead of trusting the
        // arithmetic above: the banner used to *claim* >=100,000 per operation while nothing
        // read the number actually executed, so a broken product would have kept the claim
        // and dropped the fact.
        var plannedIterations = (long)iterationsPerSample * samplesPerRun * RunsPerAction;
        if (plannedIterations < MinimumIterationsPerAction)
            throw new InvalidOperationException(
                $"Protocol violation: this measurement would execute {plannedIterations:N0}"
                    + $" iterations ({iterationsPerSample} per sample x {samplesPerRun} samples x"
                    + $" {RunsPerAction} runs), below the {MinimumIterationsPerAction:N0} floor."
                    + " Fix the iteration arithmetic; do not publish a figure measured this way."
            );

        var before = MachineCpu.Take();
        var warmup = Warmup(action, iterationsPerSample);
        if (!warmup.Settled && Strict)
            return Reject(
                label,
                $"warm-up did not converge within {warmup.ElapsedMilliseconds:N0} ms"
                    + $" ({warmup.Bursts} bursts) and PICOBENCH_STRICT=1 is set — the code was"
                    + " still heating up when the runs started, so there is no trustworthy figure"
            );

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
        var loadUnknown = !double.IsFinite(otherCpu);
        if (loadUnknown)
            _loadNotMeasurable = true;

        if (TraceProbe)
            Console.WriteLine(
                $"  runs {label}: {string.Join(" | ", perRun.Select(v => v.ToString("F3")))}"
                    + $" (iters/sample {iterationsPerSample}, samples/run {samplesPerRun},"
                    + $" other-CPU {FormatOtherCpu(otherCpu)}, warm-up"
                    + $" {(warmup.Settled ? "settled" : "TIMED OUT")} {warmup.ElapsedMilliseconds}ms/"
                    + $"{warmup.Bursts}b)"
            );

        if (loadUnknown && !AllowUnknownCpu)
            return Reject(label, UnknownCpuFailure("while this action was measured"));

        if (!loadUnknown && otherCpu > MaxOtherCpuFraction)
            return Reject(
                label,
                $"other processes used {otherCpu:P1} of the machine's CPU while this action was"
                    + $" measured (limit {FormatLimit(MaxOtherCpuFraction)}) — every run is slowed"
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
            Iterations = (int)plannedIterations,
            Mode = Mode,
            Stable = true,
            OtherCpuFraction = otherCpu,
            WarmupMilliseconds = (int)warmup.ElapsedMilliseconds,
            WarmupBursts = warmup.Bursts,
            WarmupSettled = warmup.Settled,
        };
    }

    /// <summary>
    /// How many samples a run needs so that one run covers at least
    /// <see cref="MinimumIterationsPerRun"/> iterations.
    /// </summary>
    /// <param name="iterationsPerSample">Iterations inside one timed sample.</param>
    /// <returns>The samples per run, never below <see cref="MinimumSamplesPerRun"/>.</returns>
    internal static int SamplesPerRunFor(int iterationsPerSample) =>
        Math.Max(
            MinimumSamplesPerRun,
            (MinimumIterationsPerRun + iterationsPerSample - 1) / iterationsPerSample
        );

    /// <summary>
    /// Calls the action until its own timing stops falling. A single fixed warmup is
    /// not enough under tiered JIT: the first measured runs then carry tier-0 code
    /// cost and the run-to-run spread reaches 3-4x on code that is perfectly stable,
    /// which is a property of the harness, not of the library being measured.
    /// <para>
    /// "Settled" has to be provable, not lucky: each burst is the <b>median of 3</b>
    /// timings (a single timing of a ~1 ms burst is noise), and the arm only counts as
    /// converged after <b>3 consecutive</b> bursts agree within 5%. Running out of budget
    /// is reported, not hidden — the sample carries <c>WarmupSettled = false</c> and the
    /// table marks the arm.
    /// </para>
    /// </summary>
    /// <param name="action">The work to warm up.</param>
    /// <param name="iterationsPerSample">Iterations per burst sample.</param>
    /// <returns>Whether it converged, how long it took and how many bursts it used.</returns>
    internal static WarmupResult Warmup(Action action, int iterationsPerSample)
    {
        // Tiered JIT keeps promoting code for a while after a process starts — the
        // background compiler is busy with the whole program — so the JIT budget is
        // deliberately generous. NativeAOT has no tiers, so it needs far less.
        var minimumMilliseconds = BenchProtocol.Mode == ProtocolMode.Aot ? 150 : 1_000;
        var maximumMilliseconds = BenchProtocol.Mode == ProtocolMode.Aot ? 1_000 : 2_500;

        var samples = new double[WarmupSamplesPerBurst];
        var total = Stopwatch.StartNew();
        var previous = double.NaN;
        var settledInARow = 0;
        var bursts = 0;
        while (true)
        {
            for (var sample = 0; sample < samples.Length; sample++)
            {
                var burst = Stopwatch.StartNew();
                for (var i = 0; i < iterationsPerSample; i++)
                    action();
                burst.Stop();
                samples[sample] = burst.Elapsed.TotalMilliseconds * 1000.0 / iterationsPerSample;
            }

            bursts++;
            var perOp = Median(samples);
            var settled =
                double.IsFinite(previous)
                && Math.Abs(perOp - previous) / Math.Min(perOp, previous) <= WarmupTolerance;
            settledInARow = settled ? settledInARow + 1 : 0;

            if (
                settledInARow >= WarmupConsecutiveSettled
                && total.ElapsedMilliseconds >= minimumMilliseconds
            )
                return new WarmupResult(true, total.ElapsedMilliseconds, bursts);
            if (total.ElapsedMilliseconds >= maximumMilliseconds)
                return new WarmupResult(false, total.ElapsedMilliseconds, bursts);

            previous = perOp;
        }
    }

    private static void CollectBeforeRun()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
    }

    internal static int ResolveIterationsPerSample(Action action)
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

    // One probe covers a *window*, not a fixed sample count: the system-wide CPU judgement
    // reads the fraction of the machine used by other processes, and a 3 ms window is not a
    // window at all (it reported 57% on an idle box, then 9%, from one arm to the next).
    private static ProbeResult Probe()
    {
        var perRun = new List<double>(ProbeRuns * 8);
        var samples = new double[ProbeSamplesPerRun];
        var window = Stopwatch.StartNew();
        do
        {
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

                perRun.Add(Median(samples));
            }
        } while (window.ElapsedMilliseconds < ProbeMinimumMilliseconds);

        var runs = perRun.ToArray();
        var median = Median(runs);
        return new ProbeResult(median, runs.Max() / runs.Min(), StdDev(runs) / median * 100.0);
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

    internal static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return (sorted.Length & 1) == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    internal static double StdDev(double[] values)
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

    internal readonly record struct WarmupResult(
        bool Settled,
        long ElapsedMilliseconds,
        int Bursts
    );
}

/// <summary>
/// The printed comparison table: protocol banner, one row per scenario, and the two
/// publication rules a printer must never break — one execution mode per table, and
/// no number or ratio for a refused or non-equivalent row.
/// </summary>
public sealed class ProtocolTable
{
    private const int ScenarioWidth = 32;
    private const int ArmWidth = 46;
    private const int VerdictWidth = 17;

    // marker (1) + space + scenario + space + arm + space + arm + space + verdict
    private const int ComparisonDividerWidth =
        1 + 1 + ScenarioWidth + 1 + ArmWidth + 1 + ArmWidth + 1 + VerdictWidth;
    private const int SingleArmDividerWidth = ScenarioWidth + 1 + ArmWidth;

    private readonly string _oursLabel;
    private readonly string? _theirsLabel;
    private readonly bool _equivalent;
    private readonly int _dividerWidth;
    private readonly List<ProtocolSample> _samples = new();

    // The protocol's counters are process-wide, and a program may need more than one table
    // (an equivalent comparison plus a non-equivalent reference one). Snapshotting them at
    // construction keeps `scenarios N | arms published M` — and `published=`/`refused=` in
    // the summary line — describing *this* table instead of every table this process printed.
    private readonly int _publishedAtStart;
    private readonly int _rejectedAtStart;

    /// <summary>Creates a table.</summary>
    /// <param name="oursLabel">PicoSerDe's label (must be a Pico* name).</param>
    /// <param name="theirsLabel">Competitor label, or null for a self-benchmark.</param>
    /// <param name="equivalent">
    /// False when the two arms are not the same job — every row then prints
    /// "NON-EQUIVALENT" and no ratio is computed.
    /// </param>
    /// <param name="nonEquivalentReason">
    /// The evidence for a non-equivalent table, printed under the generic explanation (e.g.
    /// "the assembly exposes 0 public generic methods..."). Use it to state the finding
    /// rather than only the verdict; null keeps the generic two sentences alone.
    /// </param>
    public ProtocolTable(
        string oursLabel,
        string? theirsLabel,
        bool equivalent = true,
        string? nonEquivalentReason = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(oursLabel);

        _oursLabel = oursLabel;
        _theirsLabel = theirsLabel;
        _equivalent = equivalent;
        _dividerWidth = theirsLabel is null ? SingleArmDividerWidth : ComparisonDividerWidth;
        _publishedAtStart = BenchProtocol.PublishedCount;
        _rejectedAtStart = BenchProtocol.RejectedCount;

        BenchProtocol.PrintBanner();
        if (theirsLabel is null)
            Console.WriteLine($"Table: {oursLabel} — absolute timings, no competitor, no ratio.");
        else if (!equivalent)
        {
            Console.WriteLine(
                "NON-EQUIVALENT: the competitor arm is a hand-written DOM walk plus typed"
                    + " mapping, not a single library call. Timings are listed for reference;"
                    + " no ratio is computed or printed."
            );
            if (!string.IsNullOrWhiteSpace(nonEquivalentReason))
                Console.WriteLine(nonEquivalentReason);
        }
        else
        {
            // The positive label. Without it "these columns are divisible" was visible only by
            // the *absence* of the banner above, so silence carried the strongest claim in the
            // table. It also fixes the ratio's direction in the output itself: the Verdict column
            // is ours/theirs (see Ratio) — the same direction as the README's Pico ÷ competitor
            // tables, so a printed figure is quoted as it stands. There used to be two reciprocals
            // here (the harness one way, the README the other) plus a sentence asking the reader
            // to convert; one direction, stated where the numbers are, removes that source of
            // error — it is how the old table got inverted.
            Console.WriteLine(
                $"EQUIVALENT: both arms do one library call for the same job on the same document"
                    + $" (same POCO in and out), so {oursLabel} ÷ {theirsLabel} is a ratio, not"
                    + " two unrelated costs."
            );
            Console.WriteLine(
                $"  Verdict column = {oursLabel} ÷ {theirsLabel}: above 1.00 means we are"
                    + " slower, below 1.00 means we are faster — the README's direction, so a"
                    + " figure here is quoted as it stands, with no reciprocal to take."
                    + " ✓ = we are not slower, ✗ = we are."
            );
        }

        Console.WriteLine(new string('-', _dividerWidth));
        Console.WriteLine(
            theirsLabel is null
                ? $"{Pad("Scenario", ScenarioWidth)} {Pad(oursLabel, ArmWidth)}"
                : $"{Pad("Scenario", ScenarioWidth)} {Pad(oursLabel + " (ours)", ArmWidth)}"
                    + $" {Pad(theirsLabel + " (theirs)", ArmWidth)} {Pad("Verdict", VerdictWidth)}"
        );
        Console.WriteLine(
            "each cell: median µs/op (min–max ±stddev over the runs) | other-CPU measured while"
                + " that arm ran | warm-up w<elapsed>/<bursts>b (⚠ = did not converge)"
        );
        Console.WriteLine(new string('-', _dividerWidth));
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
            : ratio <= 1 ? "✓"
            : "✗";
        var verdict =
            !_equivalent ? "NON-EQUIVALENT"
            : double.IsFinite(ratio) ? $"{ratio:F2}x"
            : "—";

        Console.WriteLine(
            $"{marker} {Pad(scenario, ScenarioWidth)}"
                + $" {Pad(BenchProtocol.FormatCell(oursSample), ArmWidth)}"
                + $" {Pad(BenchProtocol.FormatCell(theirsSample), ArmWidth)}"
                + $" {Pad(verdict, VerdictWidth)}"
        );
    }

    /// <summary>Measures and prints one single-arm row (self-benchmarks).</summary>
    /// <param name="scenario">Scenario name.</param>
    /// <param name="action">The action to time.</param>
    public void Measure(string scenario, Action action)
    {
        var sample = BenchProtocol.Measure(_oursLabel, action);
        Record(sample);
        Console.WriteLine(
            $"{Pad(scenario, ScenarioWidth)} {Pad(BenchProtocol.FormatCell(sample), ArmWidth)}"
        );
    }

    /// <summary>Prints the audit footer, the machine-parseable summary line and the mode check.</summary>
    public void Print()
    {
        AssertSingleMode();
        var published = BenchProtocol.PublishedCount - _publishedAtStart;
        var refused = BenchProtocol.RejectedCount - _rejectedAtStart;
        Console.WriteLine(new string('-', _dividerWidth));
        var scenarios = _theirsLabel is null ? _samples.Count : _samples.Count / 2;
        Console.WriteLine(
            $"scenarios {scenarios} | arms published {published} | arms refused {refused}"
                + (refused > 0 ? " (UNSTABLE — not published)" : "")
        );
        PrintAudit();
        if (!_equivalent)
            Console.WriteLine("No ratio is reported for a NON-EQUIVALENT comparison.");
        if (BenchProtocol.LoadNotMeasurable)
            Console.WriteLine(
                "⚠ load unknown: this host cannot report system-wide CPU load, so \"the machine"
                    + " is idle\" was never verified (see PICOBENCH_ALLOW_UNKNOWN_CPU)."
            );

        // Fixed format, one line, always printed: a CI gate can grep this instead of
        // parsing prose. The counter fields say which scope they describe, because a program
        // may print two tables and confusing the two scopes produced a string that looked
        // exactly like the fail-open signature this line exists to expose:
        //   scenarios= / published= / refused=   -> THIS table only (snapshotted at construction)
        //   exit=                                 -> the whole process, as of the moment this
        //                                            line is printed (hence `exit_scope`)
        //   process_published= / process_refused= -> the whole process, same moment
        // So `published=0 refused=20 process_published=20 ... exit=0` reads as "this table
        // published nothing, the run published data", while a run that produced no data at all
        // now says `published=0 ... process_published=0 process_refused=20 ... exit=1`. The
        // unqualified names stay per table so `grep -qE 'PICOBENCH_SUMMARY .*published=[1-9]'`
        // (ci.yml) keeps working; the process_* fields are additive. Only the LAST summary line
        // a process prints is its final word — `exit=` on an earlier line is a live snapshot.
        Console.WriteLine(
            $"PICOBENCH_SUMMARY scenarios={scenarios}"
                + $" published={published}"
                + $" refused={refused}"
                + $" mode={BenchProtocol.ModeName}"
                + $" equivalent={(_equivalent ? "true" : "false")}"
                + $" cpu={(BenchProtocol.LoadNotMeasurable ? "unknown" : "known")}"
                + $" exit={BenchProtocol.ExitCode}"
                + " exit_scope=process-so-far"
                + $" process_published={BenchProtocol.PublishedCount}"
                + $" process_refused={BenchProtocol.RejectedCount}"
        );
    }

    /// <summary>
    /// Defensive assertion, normally unreachable: every sample is stamped by
    /// <see cref="BenchProtocol.MeasureCore"/> with the one process-wide
    /// <see cref="BenchProtocol.Mode"/>, so this can only fire if a sample is constructed by
    /// hand. The real guarantee that AOT and JIT are never mixed is process isolation — a JIT
    /// run and an AOT run are two different processes, and each prints its own
    /// <c>Mode:</c> banner.
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

    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value.PadRight(width);

    private void PrintAudit()
    {
        var published = _samples.Where(sample => sample.Stable).ToArray();
        if (published.Length == 0)
        {
            Console.WriteLine("iterations executed: none — nothing was published");
            return;
        }

        Console.WriteLine(
            $"iterations executed per arm: {published.Min(s => s.Iterations):N0}"
                + $"–{published.Max(s => s.Iterations):N0} (README floor"
                + $" {BenchProtocol.MinimumIterationsPerAction:N0}, asserted in MeasureCore)"
        );

        var timedOut = published.Count(sample => !sample.WarmupSettled);
        Console.WriteLine(
            $"warm-up: {published.Length - timedOut}/{published.Length} arms converged after"
                + $" {published.Min(s => s.WarmupMilliseconds) / 1000.0:F1}"
                + $"–{published.Max(s => s.WarmupMilliseconds) / 1000.0:F1} s"
                + $" ({published.Min(s => s.WarmupBursts)}–{published.Max(s => s.WarmupBursts)}"
                + " bursts)"
                + (timedOut > 0 ? $"; ⚠ {timedOut} arm(s) ran out of warm-up budget" : "")
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
