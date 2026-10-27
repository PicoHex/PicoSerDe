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
//   * iteration floor    — SamplesPerRunFor x ResolveIterationsPerSample >= 100k
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

    [Test]
    public async Task ResolveIterationsPerSample_keeps_a_slow_action_at_the_floor()
    {
        // ~50 us/op would need only ~60 iterations to fill a 3 ms sample, which is below the
        // floor: a sample that short measures the clock, not the code.
        var slow = BenchProtocol.ResolveIterationsPerSample(SlowAction);

        await Assert.That(slow).IsEqualTo(BenchProtocol.MinimumIterationsPerSample);
    }

    [Test]
    public async Task ResolveIterationsPerSample_calibrates_instead_of_returning_a_constant()
    {
        // A cheaper action must get MORE iterations per sample than an expensive one, and
        // both ends stay inside the clamps. (The ceiling itself is only reached by an action
        // below ~0.15 us/op, so it is asserted as a bound rather than as an equality.)
        var fast = BenchProtocol.ResolveIterationsPerSample(() => { });
        var slow = BenchProtocol.ResolveIterationsPerSample(SlowAction);

        await Assert.That(fast > slow).IsTrue();
        await Assert.That(fast).IsGreaterThanOrEqualTo(BenchProtocol.MinimumIterationsPerSample);
        await Assert.That(fast).IsLessThanOrEqualTo(BenchProtocol.MaximumIterationsPerSample);
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

        await Assert.That(result.Settled).IsFalse();
        await Assert.That(result.Bursts).IsGreaterThan(2);
        await Assert.That(result.ElapsedMilliseconds).IsGreaterThanOrEqualTo(1_000);
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
