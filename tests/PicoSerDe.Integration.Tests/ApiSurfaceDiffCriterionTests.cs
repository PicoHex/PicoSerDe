using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The <c>added=N removed=M</c> line and the exit code <b>are</b> the contract between
/// <c>scripts/api-surface diff</c> and <c>scripts/release.ps1</c> — nothing else in the pipeline reads the
/// +/- lines, and nothing at all reads the command's verdict from anywhere but those two places.
/// <see cref="Scripts_Release_Reads"/> below quotes what the script does with them. Two ablations were run
/// against the tool and <b>both came back green</b>:
/// <list type="number">
/// <item>the exit code pinned to <c>return 0</c> → the release still ran, "API unchanged";</item>
/// <item>the criterion line pinned to <c>added=0 removed=0</c> → exactly the same, because the script reads
/// the <i>line</i>, not the exit code, when it decides whether the API moved — so a release could be stamped
/// as a y bump ("nothing changed") while the public surface had in fact changed.</item>
/// </list>
/// Which means the thing that could lie was invisible to a 3900-test suite. These tests make it visible:
/// every assertion here is about the printed criterion line or the process exit code, the two artefacts the
/// reviewer of a release actually depends on.
/// <para>
/// <b>How it was made testable.</b> The derivation (<c>DiffCommand</c>) is a <c>private static int</c> in a
/// zero-dependency console app that is not in the solution, so an in-process test would need a
/// <c>ProjectReference</c> from this test project to <c>scripts/api-surface</c> plus an
/// <c>InternalsVisibleTo</c> — and every <c>ProjectReference</c> rewrites <c>packages.lock.json</c>
/// (<c>projectFileDependencyGroups</c>), which this repo's rules say must not move as a side effect of a
/// test change. Extracting the criterion into an <c>internal static</c> helper would have solved the
/// visibility problem and <b>left the real question untested</b>: the helper's return value is not what
/// <c>release.ps1</c> consumes — the printed line and the exit status are. So the road taken is the honest
/// one: build the tool the way the release builds it and drive the <b>real binary</b> as a process, asserting
/// on its stdout and its exit code. Slower than a unit test (one build, ~2–5 s, shared by the class) and
/// strictly stronger: a change in the criterion's text, its formatting, its position in the output, or the
/// process's exit status all fail here.
/// </para>
/// </summary>
public class ApiSurfaceDiffCriterionTests
{
    /// <summary>The tool's project, relative to <see cref="TestRepo.Root"/>.</summary>
    const string ToolProjectRelativePath = "scripts/api-surface/ApiSurface.csproj";

    /// <summary>
    /// Where the nested build puts the assembly (its <c>AssemblyName</c> is pinned in the csproj, so this
    /// path does not drift with the folder name). Always <c>Debug</c>: the tool's behaviour is
    /// configuration-independent, and a fixed configuration keeps one nested build for the whole class even
    /// when the suite is run in Release.
    /// </summary>
    const string ToolAssemblyRelativePath =
        "scripts/api-surface/bin/Debug/net10.0/picohex-api-surface.dll";

    /// <summary>A build or a run that has not finished in minutes is a hang, not a slow machine.</summary>
    static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The criterion line <c>release.ps1</c> matches with <c>^added=\d+ removed=\d+$</c>.</summary>
    static readonly Regex CriterionLine = new(@"^added=(?<added>\d+) removed=(?<removed>\d+)$");

    /// <summary>
    /// The nested build, run once for the class: four tests that each built the tool would spend the same
    /// seconds four times. A faulted build stays faulted (the exception is the report), so a machine without
    /// the CLI cannot turn into four identical mysteries.
    /// </summary>
    static readonly Lazy<Task<string>> ToolDll = new(
        BuildTool,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    // ── the contract, quoted ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What <c>scripts/release.ps1</c> does with the two artefacts these tests pin (quoted so the coupling is
    /// visible from here; <see cref="Scripts_Release_StillReadsTheCriterionLineAndTheExitCode"/> re-reads the
    /// script and fails if that changes):
    /// <code>
    /// $diff = Invoke-Tool @("diff", $baseline, $currentSurfaces[$packageId])
    /// $summary = @($diff | Where-Object { $_ -match "^added=\d+ removed=\d+$" })[-1]
    /// if (-not ($summary -match "added=(\d+) removed=(\d+)")) { Fail "unexpected diff output for $packageId" }
    /// $added = [int]$Matches[1]; $removed = [int]$Matches[2]
    /// if ($added -gt 0 -or $removed -gt 0) { $changed = $true }
    /// </code>
    /// </summary>
    [Test]
    public async Task Scripts_Release_StillReadsTheCriterionLineAndTheExitCode()
    {
        var script = Path.Combine(TestRepo.Root(), "scripts", "release.ps1");

        await Assert
            .That(File.Exists(script))
            .IsTrue()
            .Because($"the release script these tests guard must be at {script}");

        var text = await File.ReadAllTextAsync(script);

        // The line the script matches on, and the two numbers it pulls out of it.

        await Assert
            .That(text.Contains(@"^added=\d+ removed=\d+$", StringComparison.Ordinal))
            .IsTrue()
            .Because(
                "release.ps1 locates the criterion line with this exact pattern. If that pattern changed, the "
                    + "tests in this class are checking a line nobody reads any more — re-read the script and "
                    + "update them with it"
            );

        // ...and the decision it makes from those numbers.

        await Assert
            .That(text.Contains("if ($added -gt 0 -or $removed -gt 0)", StringComparison.Ordinal))
            .IsTrue()
            .Because(
                "the release decides \"API changed\" from `$added -gt 0 -or $removed -gt 0`. That is why a "
                    + "criterion line hard-coded to zeros is not cosmetic: it turns every change into a patch "
                    + "release, silently"
            );
    }

    // ── the criterion line, and the exit code ──────────────────────────────────────────────────────

    /// <summary>
    /// A surface that gained a member and lost one: both numbers must move, and the process must say
    /// "different" through its exit code as well (0 = identical, 1 = different — the tool's own usage text).
    /// </summary>
    [Test]
    public async Task ADifferenceInBothDirections_IsCountedOnTheCriterionLine_AndExitIsOne()
    {
        const string Baseline =
            "T:class Pico.Sample\nM:Pico.Sample.Keep() : void\nM:Pico.Sample.Gone() : void\n";

        const string Current =
            "T:class Pico.Sample\nM:Pico.Sample.Keep() : void\nM:Pico.Sample.Added() : void\n";

        var run = await Diff(Baseline, Current);

        var criterion = run.Criterion();

        await Assert
            .That(criterion.Added)
            .IsEqualTo(1)
            .Because(
                "one member was added to the surface, so the criterion line must count exactly one addition — "
                    + $"the whole output was:\n{run.ToolOutput}"
            );

        await Assert
            .That(criterion.Removed)
            .IsEqualTo(1)
            .Because(
                "one member was removed from the surface, so the criterion line must count exactly one removal "
                    + $"— the whole output was:\n{run.ToolOutput}"
            );

        await Assert
            .That(run.ExitCode)
            .IsEqualTo(1)
            .Because(
                "the tool documents `Exit 0 = identical, 1 = different`. A process that reports a difference and "
                    + $"exits 0 is the ablation that made a changed API look unchanged (exit was {run.ExitCode})"
            );

        // The two detail lines the script echoes at the reader, and the criterion line last (the script

        // takes the LAST match, [-1], so a second or reordered line would silently decide the release).

        await Assert
            .That(run.OutputLines.Contains("- M:Pico.Sample.Gone() : void"))
            .IsTrue()
            .Because($"a removed line must be printed as `- …`:\n{run.ToolOutput}");

        await Assert
            .That(run.OutputLines.Contains("+ M:Pico.Sample.Added() : void"))
            .IsTrue()
            .Because($"an added line must be printed as `+ …`:\n{run.ToolOutput}");

        await Assert
            .That(run.OutputLines[^1])
            .IsEqualTo("added=1 removed=1")
            .Because(
                "release.ps1 picks the LAST line matching `^added=\\d+ removed=\\d+$` — the criterion line must "
                    + $"be the final line of output:\n{run.ToolOutput}"
            );

        await Assert
            .That(run.OutputLines.Count(line => CriterionLine.IsMatch(line)))
            .IsEqualTo(1)
            .Because(
                "exactly one criterion line: two would make the release's `[-1]` a coin toss between them"
            );
    }

    /// <summary>An addition alone must still move the criterion line and the exit code.</summary>
    [Test]
    public async Task AnAdditionAlone_IsCounted_AndExitIsOne()
    {
        var run = await Diff(
            "T:class Pico.Sample\nM:Pico.Sample.Keep() : void\n",
            "T:class Pico.Sample\nM:Pico.Sample.Keep() : void\nM:Pico.Sample.Added() : void\n"
        );

        await Assert
            .That(run.Criterion().Added)
            .IsEqualTo(1)
            .Because($"one member was added:\n{run.ToolOutput}");

        await Assert
            .That(run.Criterion().Removed)
            .IsEqualTo(0)
            .Because($"nothing was removed:\n{run.ToolOutput}");

        await Assert
            .That(run.ExitCode)
            .IsEqualTo(1)
            .Because($"an added member is a changed API and must exit 1 (exit was {run.ExitCode})");
    }

    /// <summary>A removal alone must still move the criterion line and the exit code.</summary>
    [Test]
    public async Task ARemovalAlone_IsCounted_AndExitIsOne()
    {
        var run = await Diff(
            "T:class Pico.Sample\nM:Pico.Sample.Keep() : void\nM:Pico.Sample.Gone() : void\n",
            "T:class Pico.Sample\nM:Pico.Sample.Keep() : void\n"
        );

        await Assert
            .That(run.Criterion().Removed)
            .IsEqualTo(1)
            .Because($"one member was removed:\n{run.ToolOutput}");

        await Assert
            .That(run.Criterion().Added)
            .IsEqualTo(0)
            .Because($"nothing was added:\n{run.ToolOutput}");

        await Assert
            .That(run.ExitCode)
            .IsEqualTo(1)
            .Because(
                $"a removed member is a breaking change and must exit 1 (exit was {run.ExitCode}) — a release "
                    + "that misses it ships a patch bump for a breaking change"
            );
    }

    /// <summary>
    /// The positive control: identical surfaces must produce <c>added=0 removed=0</c> <b>and</b> exit 0. Without
    /// this, a tool that reports a difference for everything passes every test above and makes every release a
    /// minor bump.
    /// </summary>
    [Test]
    public async Task IdenticalSurfaces_AreZeroZero_AndExitIsZero()
    {
        const string Surface = "T:class Pico.Sample\nM:Pico.Sample.Keep(string value) : void\n";

        var run = await Diff(Surface, Surface);

        await Assert
            .That(run.Criterion().Added)
            .IsEqualTo(0)
            .Because($"the two surfaces are the same lines:\n{run.ToolOutput}");

        await Assert
            .That(run.Criterion().Removed)
            .IsEqualTo(0)
            .Because($"the two surfaces are the same lines:\n{run.ToolOutput}");

        await Assert
            .That(run.ExitCode)
            .IsEqualTo(0)
            .Because(
                $"identical surfaces are exit 0 by the tool's own usage text (exit was {run.ExitCode})"
            );

        await Assert
            .That(
                run.OutputLines.Any(line =>
                    line.StartsWith("+ ", StringComparison.Ordinal)
                    || line.StartsWith("- ", StringComparison.Ordinal)
                )
            )
            .IsFalse()
            .Because($"identical surfaces have nothing to show:\n{run.ToolOutput}");
    }

    /// <summary>
    /// The same surface written with the platform's line endings on one side only is <b>not</b> a change.
    /// Baselines are stored LF-terminated (the dumper writes <c>\n</c>), but a checkout with
    /// <c>core.autocrlf</c> — or a hand edit in an editor that normalises — can hand the tool CRLF. If the
    /// comparison counted that, every release would report the whole surface as removed-and-re-added and the
    /// criterion line is exactly where that lie would surface.
    /// </summary>
    [Test]
    public async Task TheSameSurfaceWithDifferentLineEndings_IsNotADifference()
    {
        const string Lf =
            "T:class Pico.Sample\nM:Pico.Sample.Keep() : void\nP:A.Value : int { get; }\n";

        var run = await Diff(Lf, Lf.Replace("\n", "\r\n", StringComparison.Ordinal));

        var criterion = run.Criterion();

        await Assert
            .That(criterion.Added + criterion.Removed)
            .IsEqualTo(0)
            .Because(
                "the same lines with CRLF instead of LF are the same surface — a criterion line that counted "
                    + $"the line endings would report the whole surface as changed:\n{run.ToolOutput}"
            );

        await Assert
            .That(run.ExitCode)
            .IsEqualTo(0)
            .Because(
                $"a line-ending-only difference is not an API change (exit was {run.ExitCode})"
            );
    }

    /// <summary>
    /// A missing baseline is an <b>error</b> (2), not "no differences". The script's <c>Fail</c> path exists for
    /// this, and a tool that answered "added=0 removed=0" for a path it could not read would be the same lie as
    /// a hard-coded criterion line.
    /// </summary>
    [Test]
    public async Task AMissingBaseline_IsAnError_AndPrintsNoCriterionLine()
    {
        var directory = NewTempDirectory();

        try
        {
            var missing = Path.Combine(directory, "does-not-exist.public.txt");

            var current = Path.Combine(directory, "current.public.txt");

            await File.WriteAllTextAsync(current, "T:class Pico.Sample\n");

            var run = await Run(await ToolDll.Value, "diff", missing, current);

            await Assert
                .That(run.ExitCode)
                .IsEqualTo(2)
                .Because(
                    $"the tool documents `2 = error`; an unreadable baseline must not pass for \"unchanged\" "
                        + $"(exit was {run.ExitCode}, output:\n{run.ToolOutput})"
                );

            await Assert
                .That(run.OutputLines.Any(line => CriterionLine.IsMatch(line)))
                .IsFalse()
                .Because(
                    "no criterion line may be printed for a failure: release.ps1 would match it and read zeros "
                        + $"out of a diff that never happened:\n{run.ToolOutput}"
                );
        }
        finally
        {
            TryDelete(directory);
        }
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>diff</c> run over two files written to a fresh temp directory — the same shape the release uses
    /// (a baseline file and a freshly dumped surface), so nothing here depends on the files' location.
    /// </summary>
    static async Task<ToolRun> Diff(string baselineText, string currentText)
    {
        var directory = NewTempDirectory();

        try
        {
            var baseline = Path.Combine(directory, "Pico.Sample.public.txt");

            var current = Path.Combine(directory, "current.public.txt");

            await File.WriteAllTextAsync(baseline, baselineText);

            await File.WriteAllTextAsync(current, currentText);

            return await Run(await ToolDll.Value, "diff", baseline, current);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>Runs the real tool and captures everything a reviewer of a release would see.</summary>
    static async Task<ToolRun> Run(string dll, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetPath(),

            WorkingDirectory = TestRepo.Root(),

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(dll);

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"`{dll} {string.Join(' ', arguments)}` started no process"
            );

        // Both pipes read before waiting: a full pipe buffer would block the child forever.

        var stdout = process.StandardOutput.ReadToEndAsync();

        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(RunTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            throw new InvalidOperationException(
                $"`{dll} {string.Join(' ', arguments)}` did not finish within {RunTimeout.TotalSeconds:F0} s and "
                    + "was killed — this test reports a hang instead of waiting for one"
            );
        }

        return new ToolRun(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Builds the tool exactly the way <c>release.ps1</c> does (its own project, quiet, no server reuse) and
    /// returns the assembly path. Failure here is a failure, never a skip: a test that silently skips when the
    /// tool cannot be built is a test that reports nothing on the day the tool is broken.
    /// </summary>
    static async Task<string> BuildTool()
    {
        var root = TestRepo.Root();

        var dll = Path.Combine(root, ToolAssemblyRelativePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetPath(),

            WorkingDirectory = root,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,
        };

        foreach (
            var argument in new[]
            {
                "build",
                Path.Combine(root, ToolProjectRelativePath),
                "-c",
                "Debug",
                "-m:1",
                "--nologo",
                "-v",
                "quiet",
            }
        )
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var commandLine =
            $"{startInfo.FileName} build {ToolProjectRelativePath} -c Debug -m:1 --nologo -v quiet";

        Process? started;

        try
        {
            started = Process.Start(startInfo);
        }
        catch (Exception failure)
            when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"could not start `{commandLine}` ({failure.Message}) — these tests drive the real api-surface "
                    + "binary, so they need the dotnet CLI on PATH (or DOTNET_HOST_PATH set). Nothing was "
                    + "compared",
                failure
            );
        }

        using var process =
            started
            ?? throw new InvalidOperationException(
                $"`{commandLine}` started no process, so the api-surface tool was never built"
            );

        var stdout = process.StandardOutput.ReadToEndAsync();

        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(BuildTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            throw new InvalidOperationException(
                $"`{commandLine}` did not finish within {BuildTimeout.TotalMinutes:F0} minutes and was killed"
            );
        }

        var log = await stdout + await stderr;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"`{commandLine}` failed with exit code {process.ExitCode}, so there is no tool to test. "
                    + $"Build output:\n{Tail(log, 40)}"
            );

        await Assert
            .That(File.Exists(dll))
            .IsTrue()
            .Because(
                $"`{commandLine}` succeeded but {ToolAssemblyRelativePath} is not there — the path is derived "
                    + "from the csproj's AssemblyName, so a rename has to move it here too"
            );

        return dll;
    }

    static string NewTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "picoserde-api-surface",
            Guid.NewGuid().ToString("N")
        );

        Directory.CreateDirectory(directory);

        return directory;
    }

    /// <summary>The CLI to build with: <c>DOTNET_HOST_PATH</c> first (the SDK sets it for exactly this).</summary>
    static string DotnetPath()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

        return !string.IsNullOrWhiteSpace(host) && File.Exists(host) ? host : "dotnet";
    }

    static string Tail(string log, int lines)
    {
        var all = log.Split('\n');

        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is harmless and is not in the repository;

            // failing a test over it would be a false red.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: best effort cleanup, never the verdict.
        }
    }

    /// <summary>The tool's exit code and both streams, normalised into the lines a reviewer reads.</summary>
    sealed record ToolRun(int ExitCode, string Output, string Error)
    {
        /// <summary>stdout then stderr, for a failure message that shows everything the tool said.</summary>
        public string ToolOutput => Output + Error;

        /// <summary>
        /// stdout as lines, CR of CRLF stripped, trailing empty line dropped — what
        /// <c>release.ps1</c>'s <c>Where-Object</c> sees.
        /// </summary>
        public IReadOnlyList<string> OutputLines =>
            Output
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => line.Length > 0)
                .ToList();

        /// <summary>
        /// The criterion line's two numbers. Throws with the whole output if there is no such line: a missing
        /// criterion line is the failure <c>release.ps1</c> turns into "unexpected diff output".
        /// </summary>
        public (int Added, int Removed) Criterion()
        {
            var line = OutputLines.LastOrDefault(candidate => CriterionLine.IsMatch(candidate));

            if (line is null)
                throw new InvalidOperationException(
                    "the tool printed no `added=N removed=M` line — release.ps1 fails the whole release on "
                        + $"this. Output:\n{ToolOutput}"
                );

            var match = CriterionLine.Match(line);

            return (
                int.Parse(
                    match.Groups["added"].Value,
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                int.Parse(
                    match.Groups["removed"].Value,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
        }
    }
}
