using System.Diagnostics;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The tests for <see cref="NestedBuildLock"/> — the cross-process mutex the two nested-build tests in this
/// project take before they run <c>dotnet build</c>.
/// <para>
/// <b>Why this exists at all.</b> Two test projects are multi-targeted (<c>net10.0;net11.0</c>), and
/// <c>dotnet test PicoSerDe.slnx</c> runs every TFM as its own <b>process</b>. A nested build writes into
/// <c>&lt;project&gt;/obj</c> and <c>&lt;project&gt;/bin</c> — paths that carry no TFM of their own in this
/// arrangement, because both processes invoke the <i>same</i> command line. Two processes running that build
/// at the same time therefore compile into one file, and the loser dies with
/// <c>CS2012: Cannot open '…/obj/Debug/net11.0/…dll' for writing … being used by another process</c> (and, for
/// a file the build then reads, with <c>IOException</c> on a plain <c>File.WriteAllText</c>). Measured in this
/// repository: one full-solution run produced six such failures on
/// <see cref="ApiSurfaceDiffCriterionTests"/> and one on the baseline gate, while each TFM on its own was
/// green. TUnit's <c>[NotInParallel]</c> cannot help — it serialises tests inside one process, and these are
/// two.
/// </para>
/// <para>
/// <b>These tests use identities no other test uses</b> (<see cref="Synthetic"/>). That is not tidiness: the
/// first version locked the <i>real</i> project paths, and in a full-solution run it then queued behind the
/// baseline gate's own rebuild and timed out at ten seconds — a test of the wait must not have to wait for the
/// thing it is testing.
/// </para>
/// </summary>
public class NestedBuildLockTests
{
    /// <summary>
    /// A lock identity that names no project on disk, so acquiring it can never queue behind a real nested
    /// build running in the same suite.
    /// </summary>
    static string Synthetic(string name) =>
        "tests/NestedBuildLockTests/never-built-" + name + ".csproj";

    /// <summary>
    /// The whole point: while one caller holds the lock, the second one <b>waits</b> — it does not proceed and
    /// it does not fail. A "lock" that lets both through is the bug it was added for, and a "lock" that throws
    /// instead of waiting would turn a slow machine into a red suite for no reason.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task ASecondCallerWaits_AndProceedsOnlyAfterTheFirstReleases(CancellationToken ct)
    {
        var root = TestRepo.Root();
        var project = Synthetic("one");

        var first = await NestedBuildLock.AcquireAsync(
            root,
            project,
            TimeSpan.FromSeconds(30),
            "the first holder in NestedBuildLockTests"
        );

        Task<IDisposable> second;
        try
        {
            second = NestedBuildLock.AcquireAsync(
                root,
                project,
                TimeSpan.FromSeconds(30),
                "the second holder in NestedBuildLockTests"
            );

            // Well past the lock's poll interval: a second caller that is "held" must still be held here.
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);

            await Assert
                .That(second.IsCompleted)
                .IsFalse()
                .Because(
                    "the first caller still holds the lock, so the second one must still be waiting — if this "
                        + "is false the lock lets two builds into obj/ at once, which is exactly the CS2012 race "
                        + "it exists to prevent"
                );
        }
        finally
        {
            first.Dispose();
        }

        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(20), ct);

        await Assert
            .That(acquired)
            .IsNotNull()
            .Because("the release has to hand the lock to the waiting caller, not lose it");
    }

    /// <summary>
    /// Two <b>different</b> projects must not serialise against each other: they write into different
    /// <c>obj/</c> directories, so a shared lock would make the suite pay for a wait it never needed. (One
    /// lock per project is also the safe direction — over-serialising cannot produce a false green, only
    /// seconds.)
    /// </summary>
    [Test]
    public async Task TwoDifferentProjects_DoNotWaitForEachOther()
    {
        var root = TestRepo.Root();

        using var first = await NestedBuildLock.AcquireAsync(
            root,
            Synthetic("first"),
            TimeSpan.FromSeconds(10),
            "one project in NestedBuildLockTests"
        );

        var stopwatch = Stopwatch.StartNew();
        using var second = await NestedBuildLock.AcquireAsync(
            root,
            Synthetic("second"),
            TimeSpan.FromSeconds(10),
            "another project in NestedBuildLockTests"
        );
        stopwatch.Stop();

        await Assert
            .That(stopwatch.Elapsed)
            .IsLessThan(TimeSpan.FromSeconds(5))
            .Because(
                "a different project writes to a different obj/ directory, so the second acquire must not have "
                    + "waited for the first — a shared lock here would serialise the whole suite"
            );
    }

    /// <summary>
    /// A caller that never gets the lock must <b>report</b> instead of hanging: the message has to name the
    /// lock file and what was waiting for it, because "the suite stopped for ten minutes" names nothing.
    /// </summary>
    [Test]
    public async Task ACallerThatNeverGetsTheLock_ReportsTheLockPathInsteadOfHanging()
    {
        var root = TestRepo.Root();
        var project = Synthetic("held-forever");
        var lockPath = NestedBuildLock.LockPath(root, project);

        using var held = await NestedBuildLock.AcquireAsync(
            root,
            project,
            TimeSpan.FromSeconds(10),
            "the holder in NestedBuildLockTests"
        );

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NestedBuildLock.AcquireAsync(
                root,
                project,
                TimeSpan.FromMilliseconds(300),
                "the probe rebuild the baseline gate needs"
            )
        );

        await Assert
            .That(failure!.Message)
            .Contains(lockPath)
            .Because("the failure must name the lock file a reader has to look at");

        await Assert
            .That(failure.Message)
            .Contains("the probe rebuild the baseline gate needs")
            .Because(
                "and it must name what was waiting, so the next reader knows which test to look at"
            );
    }

    /// <summary>
    /// The lock is keyed by the <b>checkout</b> as well as the project: two worktrees of this repository have
    /// two <c>obj/</c> directories, so they do not collide and must not queue behind one another. The key is
    /// derived from a normalised path, so the same checkout spelled with a trailing separator (or with
    /// <c>\</c> instead of <c>/</c>) resolves to the same lock — a lock that misses its own file is a lock
    /// that is not there.
    /// </summary>
    [Test]
    public async Task TheLockIsPerCheckoutAndProject_AndToleratesAReSpelledRoot()
    {
        var root = TestRepo.Root();
        var probe = "tests/FormatCapabilities.Probe/FormatCapabilities.Probe.csproj";
        var tool = "scripts/api-surface/ApiSurface.csproj";

        var canonical = NestedBuildLock.LockPath(root, probe);

        await Assert
            .That(NestedBuildLock.LockPath(root + Path.DirectorySeparatorChar, probe))
            .IsEqualTo(canonical)
            .Because("the same checkout with a trailing separator must resolve to the same lock");

        await Assert
            .That(NestedBuildLock.LockPath(root, tool))
            .IsNotEqualTo(canonical)
            .Because(
                "two projects must not share one lock (see TwoDifferentProjects_DoNotWaitForEachOther)"
            );

        await Assert
            .That(NestedBuildLock.LockPath(root, probe.Replace('/', '\\')))
            .IsEqualTo(canonical)
            .Because(
                "the repository spells project paths with `/` and Path.Combine hands back `\\`, so the two "
                    + "spellings of one file must be one lock"
            );

        await Assert
            .That(NestedBuildLock.LockPath(root + "-some-other-worktree", probe))
            .IsNotEqualTo(canonical)
            .Because(
                "a second worktree has its own obj/ directories, so it must not queue behind this checkout — "
                    + "the release process runs two worktrees at once"
            );

        await Assert
            .That(Path.GetFileName(Path.GetDirectoryName(canonical)))
            .IsEqualTo("picoserde-nested-build")
            .Because(
                "the lock files live in one directory of their own under the temp path, so a reader who finds a "
                    + "stuck suite knows where to look — and so this test never deletes a file it does not own"
            );
    }

    /// <summary>
    /// The tripwire: the two tests that run a nested build must <b>go through the lock</b>. Nothing else in
    /// the tree can notice a refactor that drops it — the suite stays green on a single TFM run and only goes
    /// red when two processes overlap, which is the failure mode this file exists for. So the coupling is
    /// pinned here, the way the gate's own "the verdict is still a verdict" test pins its call site.
    /// </summary>
    [Test]
    public async Task BothNestedBuildsInThisProject_GoThroughTheLock()
    {
        var root = TestRepo.Root();

        foreach (
            var file in new[]
            {
                "GeneratedBaselineRegenerationGateTests.cs",
                "ApiSurfaceDiffCriterionTests.cs",
            }
        )
        {
            var path = Path.Combine(root, "tests", "PicoSerDe.Integration.Tests", file);
            var source = await File.ReadAllTextAsync(path);

            await Assert
                .That(source.Contains("NestedBuildLock.AcquireAsync(", StringComparison.Ordinal))
                .IsTrue()
                .Because(
                    $"{file} runs a nested `dotnet build`, so it must hold NestedBuildLock while it does — "
                        + "without it, `dotnet test PicoSerDe.slnx` (both TFMs at once) fails with CS2012 in "
                        + "that project's obj/, while each TFM on its own is green"
                );
        }
    }
}
