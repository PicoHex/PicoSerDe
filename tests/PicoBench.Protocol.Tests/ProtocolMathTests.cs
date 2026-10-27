using PicoBench;

namespace PicoBench.Protocol.Tests;

// The measurement core (shared/BenchProtocol.cs) is what all six benchmark tables are
// computed with, so a bug in it is a bug in every published number — and the previous
// round proved that "look at the output once" does not catch arithmetic mistakes. These
// tests pin the four pieces that are pure arithmetic and therefore pinnable:
//
//   * Median            — odd / even / single / input not mutated
//   * StdDev            — the n-1 denominator (sample standard deviation, not population)
//   * OtherCpuFraction  — the fail-closed NaN branches, the clamp, and a known 50% case
//   * iteration floor    — SamplesPerRunFor x ResolveIterationsPerSample >= 100k, and the
//                          calibration clamp itself (IterationsFor, on synthetic costs)
//
// Test names are sentences: a failure should say what the protocol promises, not which
// method was called.
public class ProtocolMathTests
{
    // ── Median ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Median_of_an_odd_count_is_the_middle_element()
    {
        await Assert.That(BenchProtocol.Median([3.0, 1.0, 2.0])).IsEqualTo(2.0);
        await Assert.That(BenchProtocol.Median([9.0, 1.0, 5.0, 7.0, 3.0])).IsEqualTo(5.0);
    }

    [Test]
    public async Task Median_of_an_even_count_is_the_mean_of_the_two_middle_elements()
    {
        await Assert.That(BenchProtocol.Median([4.0, 1.0, 3.0, 2.0])).IsEqualTo(2.5);
        await Assert.That(BenchProtocol.Median([10.0, 2.0])).IsEqualTo(6.0);
    }

    [Test]
    public async Task Median_of_a_single_element_is_that_element()
    {
        await Assert.That(BenchProtocol.Median([7.5])).IsEqualTo(7.5);
    }

    [Test]
    public async Task Median_does_not_reorder_the_callers_array()
    {
        // The protocol reports five per-run medians; the caller still prints them in run
        // order (PICOBENCH_PROBE_TRACE). Sorting in place would silently reorder the audit
        // trail it is supposed to explain.
        var values = new[] { 3.0, 1.0, 2.0 };
        _ = BenchProtocol.Median(values);

        await Assert.That(values[0]).IsEqualTo(3.0);
        await Assert.That(values[1]).IsEqualTo(1.0);
        await Assert.That(values[2]).IsEqualTo(2.0);
    }

    // ── StdDev ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task StdDev_divides_by_n_minus_one_not_by_n()
    {
        // [1, 2, 3]: mean 2, sum of squared deviations 2.
        //   sample standard deviation (n-1 = 2) -> sqrt(1.0)      = 1.000000
        //   population standard deviation (n = 3) -> sqrt(0.6667)  = 0.816497
        // Only the n-1 answer is the "standard deviation of the runs" the protocol reports.
        await Assert.That(BenchProtocol.StdDev([1.0, 2.0, 3.0])).IsEqualTo(1.0).Within(1e-12);
    }

    [Test]
    public async Task StdDev_of_three_known_samples_matches_the_hand_computed_value()
    {
        // [2, 4, 4]: mean 10/3, sum of squared deviations 8/3.
        // sqrt((8/3) / 2) = sqrt(4/3) = 1.1547005383792515
        await Assert
            .That(BenchProtocol.StdDev([2.0, 4.0, 4.0]))
            .IsEqualTo(1.1547005383792515)
            .Within(1e-12);
    }

    [Test]
    public async Task StdDev_of_fewer_than_two_samples_is_zero_not_an_exception()
    {
        // A refused arm has no runs at all; a one-run arm has no dispersion to report.
        // Neither may divide by (n-1) = 0.
        await Assert.That(BenchProtocol.StdDev([])).IsEqualTo(0.0);
        await Assert.That(BenchProtocol.StdDev([42.0])).IsEqualTo(0.0);
    }

    // ── OtherCpuFraction: the fail-closed branches ──────────────────────────────────

    [Test]
    public async Task OtherCpuFraction_is_NaN_when_the_host_could_not_sample_system_cpu()
    {
        // This is the branch that used to fail OPEN: NaN compared with `>` is false, so a
        // host that cannot measure load quietly published. The fraction must stay NaN — the
        // decision to refuse is made on `!double.IsFinite(...)`, never on `NaN > limit`.
        var unknown = new BenchProtocol.MachineCpu.Sample(
            double.NaN,
            double.NaN,
            TimeSpan.FromSeconds(1)
        );
        var known = new BenchProtocol.MachineCpu.Sample(10, 100, TimeSpan.FromSeconds(1));

        await Assert
            .That(double.IsNaN(BenchProtocol.MachineCpu.OtherCpuFraction(unknown, known)))
            .IsTrue();
        await Assert
            .That(double.IsNaN(BenchProtocol.MachineCpu.OtherCpuFraction(known, unknown)))
            .IsTrue();
    }

    [Test]
    public async Task OtherCpuFraction_is_NaN_when_the_total_cpu_clock_did_not_advance()
    {
        // totalDelta <= 0 means the two samples are the same instant (or a counter that
        // went backwards): dividing by it would give +Infinity or a negative fraction, and
        // both would pass a `> limit` test in the wrong direction.
        var sample = new BenchProtocol.MachineCpu.Sample(10, 100, TimeSpan.FromSeconds(1));

        await Assert
            .That(double.IsNaN(BenchProtocol.MachineCpu.OtherCpuFraction(sample, sample)))
            .IsTrue();

        var rewound = new BenchProtocol.MachineCpu.Sample(9, 99, TimeSpan.FromSeconds(1));
        await Assert
            .That(double.IsNaN(BenchProtocol.MachineCpu.OtherCpuFraction(sample, rewound)))
            .IsTrue();
    }

    [Test]
    public async Task OtherCpuFraction_is_other_processes_share_of_the_machine_not_ours()
    {
        // From t0 to t1 the machine burned 4 s of CPU (100 -> 104) and we burned 1 s of it
        // (own CPU 5 s -> 6 s). The busy delta therefore contains our second: other processes
        // used 3 - 1 = 2 of the 4 seconds, i.e. 50%. Reading it as 75% is exactly the mistake
        // this subtraction exists to prevent.
        var from = new BenchProtocol.MachineCpu.Sample(10, 100, TimeSpan.FromSeconds(5));
        var to = new BenchProtocol.MachineCpu.Sample(13, 104, TimeSpan.FromSeconds(6));

        await Assert
            .That(BenchProtocol.MachineCpu.OtherCpuFraction(from, to))
            .IsEqualTo(0.5)
            .Within(1e-12);
    }

    [Test]
    public async Task OtherCpuFraction_is_clamped_into_zero_to_one()
    {
        // Measuring more own-CPU than the machine's busy delta is possible (USER_HZ
        // granularity, a counter that lags); the fraction may not go negative...
        var busyFrom = new BenchProtocol.MachineCpu.Sample(10, 100, TimeSpan.FromSeconds(0));
        var busyTo = new BenchProtocol.MachineCpu.Sample(10.5, 104, TimeSpan.FromSeconds(3));
        await Assert
            .That(BenchProtocol.MachineCpu.OtherCpuFraction(busyFrom, busyTo))
            .IsEqualTo(0.0);

        // ...and a busy delta larger than the total is a counter artefact, not 150% load.
        var overFrom = new BenchProtocol.MachineCpu.Sample(10, 100, TimeSpan.Zero);
        var overTo = new BenchProtocol.MachineCpu.Sample(30, 101, TimeSpan.Zero);
        await Assert
            .That(BenchProtocol.MachineCpu.OtherCpuFraction(overFrom, overTo))
            .IsEqualTo(1.0);
    }

    // ── The 100k floor: arithmetic, not a claim ─────────────────────────────────────

    [Test]
    public async Task Every_resolved_iteration_count_still_reaches_the_100k_floor()
    {
        // MeasureCore asserts exactly this product; the test walks the whole reachable range
        // of ResolveIterationsPerSample so a future change to either clamp or to
        // SamplesPerRunFor fails here instead of at run time (where it would throw).
        for (
            var iterationsPerSample = BenchProtocol.MinimumIterationsPerSample;
            iterationsPerSample <= BenchProtocol.MaximumIterationsPerSample;
            iterationsPerSample += 250
        )
        {
            var perRun =
                (long)iterationsPerSample * BenchProtocol.SamplesPerRunFor(iterationsPerSample);
            var perArm = perRun * BenchProtocol.RunsPerAction;

            await Assert
                .That(perArm)
                .IsGreaterThanOrEqualTo(BenchProtocol.MinimumIterationsPerAction);
        }
    }

    [Test]
    public async Task SamplesPerRunFor_never_drops_below_the_minimum_sample_count()
    {
        // 20,000 iterations/sample already covers a run by itself, but the protocol still
        // wants >= 3 samples so that one slow sample cannot become the run's median.
        await Assert.That(BenchProtocol.SamplesPerRunFor(20_000)).IsEqualTo(3);
        await Assert.That(BenchProtocol.SamplesPerRunFor(10_000)).IsEqualTo(3);
        await Assert.That(BenchProtocol.SamplesPerRunFor(1_000)).IsEqualTo(20);
    }

    // Calibration is split in two on purpose. The clamp arithmetic is pinned on synthetic
    // per-op costs (no clock involved, so no machine can make it fail); the one test that
    // still times a real action asserts only the contract a timer can honour anywhere — a
    // count inside the clamps — and never a relation between two measurements. Timing the
    // no-op instead was flaky on a busy box: 500 calls that normally cost ~1 us together
    // were measured at >= 3 us *each*, so it landed on the same floor as SlowAction and
    // "a cheaper action gets more iterations" read as a protocol bug.

    [Test]
    public async Task IterationsFor_hits_the_ceiling_for_a_cheap_action_and_the_floor_for_an_expensive_one()
    {
        // ~1 ns/op asks for 3,000,000 iterations to fill a 3 ms sample -> the ceiling.
        // ~50 us/op asks for 60 -> the floor: a sample that short measures the clock, not the
        // code.
        var cheap = BenchProtocol.IterationsFor(0.000001);
        var expensive = BenchProtocol.IterationsFor(0.05);

        await Assert.That(cheap).IsEqualTo(BenchProtocol.MaximumIterationsPerSample);
        await Assert.That(expensive).IsEqualTo(BenchProtocol.MinimumIterationsPerSample);
        await Assert.That(cheap > expensive).IsTrue();
    }

    [Test]
    public async Task IterationsFor_keeps_a_mid_range_cost_off_both_clamps()
    {
        // 3 ms / 1 us = 3,000 iterations, inside [1k, 20k], so it must pass through untouched.
        // Both equality tests above would also pass with a clamp that always returned one of
        // its ends — which would make every published sample the same length.
        var mid = BenchProtocol.IterationsFor(0.001);

        await Assert.That(mid).IsEqualTo(3_000);
        await Assert.That(mid).IsGreaterThan(BenchProtocol.MinimumIterationsPerSample);
        await Assert.That(mid).IsLessThan(BenchProtocol.MaximumIterationsPerSample);
    }

    [Test]
    public async Task IterationsFor_treats_a_cost_it_cannot_measure_as_free()
    {
        // A stopwatch reports 0 for a batch too short to resolve; dividing the target by that
        // is an infinity, and the cast of an infinity is not a count any caller could act on.
        await Assert
            .That(BenchProtocol.IterationsFor(0))
            .IsEqualTo(BenchProtocol.MaximumIterationsPerSample);
        await Assert
            .That(BenchProtocol.IterationsFor(double.NaN))
            .IsEqualTo(BenchProtocol.MaximumIterationsPerSample);
    }

    [Test]
    public async Task ResolveIterationsPerSample_always_returns_a_count_inside_the_clamps()
    {
        // The only remaining test that reads a real clock. What it can promise on any machine
        // is a count inside the clamps and nothing more; which end it takes is the arithmetic
        // pinned above, never a stopwatch.
        var cheap = BenchProtocol.ResolveIterationsPerSample(() => { });
        var expensive = BenchProtocol.ResolveIterationsPerSample(SlowAction);

        await Assert.That(cheap).IsGreaterThanOrEqualTo(BenchProtocol.MinimumIterationsPerSample);
        await Assert.That(cheap).IsLessThanOrEqualTo(BenchProtocol.MaximumIterationsPerSample);
        await Assert
            .That(expensive)
            .IsGreaterThanOrEqualTo(BenchProtocol.MinimumIterationsPerSample);
        await Assert.That(expensive).IsLessThanOrEqualTo(BenchProtocol.MaximumIterationsPerSample);
    }

    // ── The printed cell ────────────────────────────────────────────────────────────

    [Test]
    public async Task A_published_cell_carries_dispersion_the_measured_cpu_and_the_warmup_audit()
    {
        var sample = new ProtocolSample(0.25, 0.24, 0.31, 0.03, 5)
        {
            Label = "PicoYaml",
            Stable = true,
            OtherCpuFraction = 0.12,
            WarmupMilliseconds = 1234,
            WarmupBursts = 17,
            WarmupSettled = true,
        };

        await Assert
            .That(BenchProtocol.FormatCell(sample))
            .IsEqualTo("0.250 (0.240–0.310 ±0.030) 12% w1.2s/17b");
    }

    [Test]
    public async Task A_refused_cell_carries_no_number_at_all()
    {
        var refused = ProtocolSample.Unstable("PicoYaml");

        await Assert.That(BenchProtocol.FormatCell(refused)).IsEqualTo("UNSTABLE — not published");
        await Assert.That(double.IsNaN(BenchProtocol.Ratio(refused, refused))).IsTrue();
    }

    [Test]
    public async Task A_warmup_that_never_converged_is_marked_in_the_cell()
    {
        var sample = new ProtocolSample(1.0, 0.9, 1.4, 0.2, 5)
        {
            Label = "PicoYaml",
            Stable = true,
            OtherCpuFraction = double.NaN,
            WarmupMilliseconds = 2500,
            WarmupBursts = 31,
            WarmupSettled = false,
        };

        await Assert
            .That(BenchProtocol.FormatCell(sample))
            .IsEqualTo("1.000 (0.900–1.400 ±0.200) n/a w⚠2.5s/31b");
    }

    // ── Warm-up: a timeout must be reported, not hidden ────────

    [Test]
    public async Task A_warmup_that_never_settles_runs_out_of_budget_and_says_so()
    {
        // Alternating ~10 us / ~30 us bursts can never agree within 5% three times in a row,
        // so the only way out is the budget. Before this change the timeout returned silently
        // and the arm looked exactly like a converged one; now it carries Settled = false,
        // which the table prints as `w⚠…` and PICOBENCH_STRICT=1 refuses outright.
        var result = BenchProtocol.Warmup(FlappingAction, iterationsPerSample: 1_000);

        // Only protocol facts below — no throughput facts. Every call of FlappingAction spins
        // to the wall clock, so a preempted thread pays a whole scheduler quantum (~15.6 ms)
        // for a call that targets 10 us, and one burst grows from 30-90 ms to ~1.7 s. How many
        // bursts fit a 2.5 s budget is therefore a property of this host's load, not of the
        // protocol: `Bursts > 2` failed 7 times in 10 on a host oversubscribed 4x. What the
        // protocol does promise is the three facts below, none of which depends on speed.
        await Assert.That(result.Settled).IsFalse();

        // It tried: the budget is only read at a burst boundary, so a timed-out warm-up spent
        // at least one burst.
        await Assert.That(result.Bursts).IsGreaterThanOrEqualTo(1);

        // It stopped for the budget and not for some earlier reason: Settled = false has
        // exactly one exit — the one taken once the budget is spent — and no exit can happen
        // before the floor. (This is a lower bound on purpose: load only makes it larger,
        // whereas an upper bound here would be a throughput assertion in disguise.)
        await Assert.That(result.ElapsedMilliseconds).IsGreaterThanOrEqualTo(1_000);

        // …and it says so. The report is the other half of "reported, not hidden": the timeout
        // reaches the cell as `w⚠<elapsed>/<bursts>b`, the marker PICOBENCH_STRICT=1 refuses
        // on, carrying the elapsed time and burst count of this very warm-up. The cell format
        // itself is pinned by A_warmup_that_never_converged_is_marked_in_the_cell; what this
        // asserts is that a *real* timed-out warm-up lands in it instead of a converged one.
        var sample = new ProtocolSample(1.0, 1.0, 1.0, 0.0, 5)
        {
            Label = "PicoYaml",
            Stable = true,
            OtherCpuFraction = double.NaN,
            WarmupMilliseconds = (int)result.ElapsedMilliseconds,
            WarmupBursts = result.Bursts,
            WarmupSettled = result.Settled,
        };

        await Assert
            .That(BenchProtocol.FormatCell(sample))
            .Contains($"w⚠{result.ElapsedMilliseconds / 1000.0:F1}s/{result.Bursts}b");
    }

    // ── Exit code: the rule that decides whether a run of nothing is a failure ───────

    [Test]
    public async Task A_run_that_refused_nothing_exits_zero_whatever_else_is_set()
    {
        // No refusal means every arm was published, so there is nothing to report as a
        // failure — including under PICOBENCH_STRICT=1, which only escalates refusals.
        await Assert
            .That(
                BenchProtocol.ExitCodeFor(
                    published: 3,
                    rejected: 0,
                    strict: true,
                    loadNotMeasurable: false,
                    allowZeroPublish: false
                )
            )
            .IsEqualTo(0);

        await Assert
            .That(
                BenchProtocol.ExitCodeFor(
                    published: 0,
                    rejected: 0,
                    strict: false,
                    loadNotMeasurable: false,
                    allowZeroPublish: false
                )
            )
            .IsEqualTo(0);
    }

    [Test]
    public async Task Publishing_nothing_on_a_measurable_idle_host_exits_one()
    {
        // The case the summary line exists for: every arm refused, no exemption in force.
        // Exit 0 here is what used to make a broken run look like a completed one.
        await Assert
            .That(
                BenchProtocol.ExitCodeFor(
                    published: 0,
                    rejected: 6,
                    strict: false,
                    loadNotMeasurable: false,
                    allowZeroPublish: false
                )
            )
            .IsEqualTo(1);
    }

    [Test]
    public async Task The_zero_publish_exemptions_cover_only_a_total_loss_and_strict_outranks_both()
    {
        // Each exemption turns "published nothing" into 0 — and nothing more, because a run
        // that published even one arm already exits 0 on its own.
        foreach (var allowZeroPublish in new[] { true, false })
        foreach (var loadNotMeasurable in new[] { true, false })
        {
            var exemptionInForce = allowZeroPublish || loadNotMeasurable;

            await Assert
                .That(
                    BenchProtocol.ExitCodeFor(
                        published: 0,
                        rejected: 4,
                        strict: false,
                        loadNotMeasurable: loadNotMeasurable,
                        allowZeroPublish: allowZeroPublish
                    )
                )
                .IsEqualTo(exemptionInForce ? 0 : 1);

            // One published arm is enough for 0, whatever the exemptions say.
            await Assert
                .That(
                    BenchProtocol.ExitCodeFor(
                        published: 1,
                        rejected: 4,
                        strict: false,
                        loadNotMeasurable: loadNotMeasurable,
                        allowZeroPublish: allowZeroPublish
                    )
                )
                .IsEqualTo(0);

            // PICOBENCH_STRICT=1 outranks every exemption: a strict run that kept the
            // machine's excuses would not be strict, and that is why the check is ordered
            // before them rather than beside them.
            await Assert
                .That(
                    BenchProtocol.ExitCodeFor(
                        published: 0,
                        rejected: 4,
                        strict: true,
                        loadNotMeasurable: loadNotMeasurable,
                        allowZeroPublish: allowZeroPublish
                    )
                )
                .IsEqualTo(1);

            await Assert
                .That(
                    BenchProtocol.ExitCodeFor(
                        published: 9,
                        rejected: 1,
                        strict: true,
                        loadNotMeasurable: loadNotMeasurable,
                        allowZeroPublish: allowZeroPublish
                    )
                )
                .IsEqualTo(1);
        }
    }

    private static void FlappingAction()
    {
        var cost = ((_flapCalls++ / 3_000) % 2) == 0 ? 0.010 : 0.030;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed.TotalMilliseconds < cost)
            System.Threading.Thread.SpinWait(20);
    }

    private static int _flapCalls;

    private static void SlowAction()
    {
        // ~50 us of real work: SpinWait is not optimised away and does not allocate.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed.TotalMilliseconds < 0.05)
            System.Threading.Thread.SpinWait(50);
    }
}
