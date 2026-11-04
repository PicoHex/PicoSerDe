using System.Diagnostics;
using System.Text;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The <b>regeneration gate</b> for the checked-in baseline of generated code
/// (<c>tests/FormatCapabilities.Probe/generated-baseline/</c>: 96 <c>*.g.cs</c> files, one per emitted
/// serializer plus its inner helpers). It rebuilds that baseline's own project with the generators that
/// are in the tree <b>right now</b> and compares the two <b>byte for byte</b>.
/// <para>
/// <b>The gap this closes.</b> <c>docs/r1/gate-evidence.md</c> §3 describes the byte comparison as a
/// <i>manual</i> gate and says so in its own words — "<b>没有测试</b>会重新生成并比对它" ("no test
/// regenerates and compares it"), "<c>dotnet test</c> 全绿<b>不能</b>替代这一步" — and <c>AGENTS.md</c>
/// §Evidence agrees ("Nothing in the tree re-hashes <c>generated-baseline/</c> against a stored
/// manifest either, so before/after sha256 is a <i>manual</i> step"). So the snapshot only told the truth
/// on the days somebody remembered to rebuild it by hand.
/// Every other gate that reads the baseline inherits that staleness: <see cref="FusedMemberEmissionGuardTests"/>
/// asserts "this fused member is <b>called</b> in the generated code" from files that may predate the
/// emitter it is describing, and <see cref="GeneratedCodeSelfSufficiencyGuardTests"/> reads the same
/// bytes. A stale baseline makes those guards prove something that is <b>not true of the code that
/// ships</b> — the exact failure the registry's "登记表说不用、代码却可能在发射" wording exists for.
/// </para>
/// <para>
/// <b>What a pass means, and what it does not.</b> Byte equality says: for the corpus the probe compiles
/// (14 shapes × 5 formats + 2 observation shapes, all <b>attribute-free</b> plain POCOs —
/// <c>Models.cs:15-19</c>), today's generators emit exactly the bytes in the repository. It does
/// <b>not</b> say anything about branches the corpus never enters: an attribute lookup that only fires
/// for <c>[JsonKey("x")]</c>, an anonymous type, a polymorphic hierarchy — those are outside these 96
/// files by construction (this is the lesson <c>AGENTS.md</c> §Evidence records for <c>60e090a</c>).
/// A green run here is a gate, not a proof.
/// </para>
/// <para>
/// <b>The refresh is a human decision, never this test's side effect.</b> The regenerated files land in a
/// unique directory under the system temp path and are deleted in a <c>finally</c>. Nothing is copied
/// back over <c>generated-baseline/</c>; <c>git status --porcelain</c> is unchanged by a run. When this
/// test is red, the failure text carries the exact command to reproduce, and the decision — "the emitter
/// changed on purpose, refresh" versus "the emitter changed by accident, fix it" — stays with a person.
/// </para>
/// <para>
/// <b>Cost, measured</b> (win-x64, warm tree, <c>-c Debug</c>, nine runs): rebuilding <b>only</b> the probe
/// (<c>-p:BuildProjectReferences=false</c> — its eleven referenced libraries/generators were already built
/// by the build that produced this test assembly) took <b>3.5–18 s</b>, typically 4–11 s; the slow runs are
/// disk/antivirus noise, not the generator. The same command <i>with</i> project-reference rebuilding (what
/// <c>-t:Rebuild</c> propagates to those eleven projects) took <b>36 s and 76 s</b> on the same machine.
/// Those tens of seconds buy nothing here — same bytes, same verdict — and would make a single test the
/// most expensive thing in the suite, so the gate keeps <c>BuildProjectReferences=false</c> and instead
/// documents the one thing it costs: with <c>--no-build</c> after editing a generator, the nested build
/// reuses the <b>old</b> generator assembly. In that mode the test assembly above it is stale too — no test
/// in this suite can see an unbuilt change — so the trade is stated here instead of paid for on every run.
/// </para>
/// <para>
/// <b>Line endings, stated instead of assumed.</b> The generators build their text with
/// <c>StringBuilder.AppendLine</c>, so the emitted bytes carry <see cref="Environment.NewLine"/> — CRLF on
/// Windows, LF on Linux/macOS — while the checked-in baseline is stored with CRLF (<c>git ls-files --eol</c>:
/// <c>i/crlf  w/crlf  attr/-text</c>). A strict byte comparison is therefore only meaningful on Windows.
/// Off Windows, both sides are compared with CRLF folded to LF, so the verdict still sees every byte
/// except the platform's own newline; on Windows the comparison is strict, and an end-of-line-only
/// difference is reported as such.
/// </para>
/// </summary>
public class GeneratedBaselineRegenerationGateTests
{
    /// <summary>
    /// The project the baseline is a snapshot of. Named here (not copied) so the failure text can tell the
    /// next person exactly which project to rebuild by hand.
    /// </summary>
    const string ProbeProjectRelativePath =
        "tests/FormatCapabilities.Probe/FormatCapabilities.Probe.csproj";

    /// <summary>Where the committed snapshot lives, relative to <see cref="TestRepo.Root"/>.</summary>
    const string BaselineRelativePath = "tests/FormatCapabilities.Probe/generated-baseline";

    /// <summary>
    /// A floor on the baseline's file count <b>as it stands today</b> (96). Without it a moved, renamed or
    /// emptied directory would make this gate compare two empty sets and pass — the classic way a byte gate
    /// turns itself off. Growing is fine (a new shape adds files: raise the floor); <b>shrinking</b> is the
    /// case the floor exists for, because it silently narrows what the gate can see.
    /// </summary>
    const int MinimumBaselineFiles = 96;

    /// <summary>
    /// A nested build that has not finished in ten minutes is a hang, not a slow machine — and a test that
    /// waits forever is a test that reports nothing.
    /// </summary>
    static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The gate. One test on purpose: it is one measurement, and splitting it per format would rebuild the
    /// same project five times.
    /// </summary>
    [Test]
    [NotInParallel("FormatCapabilities.Probe rebuild")]
    public async Task Baseline_IsByteIdenticalToAFreshRegenerationFromTheCurrentGenerators()
    {
        var root = TestRepo.Root();
        var committed = GeneratedFiles(Path.Combine(root, BaselineRelativePath));

        await Assert
            .That(committed.Count)
            .IsGreaterThanOrEqualTo(MinimumBaselineFiles)
            .Because(
                $"{BaselineRelativePath} holds {committed.Count} *.g.cs file(s), and this gate treats fewer "
                    + $"than {MinimumBaselineFiles} as a broken lookup rather than a pass — comparing two empty "
                    + "sets proves nothing. If the corpus legitimately shrank, lower MinimumBaselineFiles in "
                    + "this test on purpose; otherwise the path above is wrong"
            );

        var freshDirectory = Path.Combine(
            Path.GetTempPath(),
            "picoserde-baseline-gate",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            Directory.CreateDirectory(freshDirectory);
            var build = await Regenerate(root, freshDirectory);
            var fresh = GeneratedFiles(freshDirectory);

            var problems = new List<string>();

            // 1. Every committed file must still be emitted, and byte for byte the same one.
            foreach (var (relativePath, committedPath) in committed)
            {
                if (!fresh.TryGetValue(relativePath, out var freshPath))
                {
                    problems.Add(
                        $"{relativePath}: in the baseline, NOT emitted by the current generators — the emitter "
                            + "stopped producing this file (a shape/format pair the corpus still declares), so "
                            + "the baseline is describing code that no longer exists"
                    );
                    continue;
                }

                var difference = DescribeDifference(committedPath, freshPath);
                if (difference is not null)
                    problems.Add($"{relativePath}: {difference}");
            }

            // 2. ...and nothing new may appear unnoticed: a new file is new code the baseline cannot see,
            //    which means the guards that read the baseline are silently under-covering it.
            foreach (
                var relativePath in fresh
                    .Keys.Except(committed.Keys)
                    .OrderBy(p => p, StringComparer.Ordinal)
            )
                problems.Add(
                    $"{relativePath}: emitted by the current generators, MISSING from the baseline — new "
                        + "generated code the committed snapshot does not carry"
                );

            var summary =
                $"{problems.Count} baseline file(s) disagree with a fresh rebuild of {ProbeProjectRelativePath} "
                + $"({committed.Count} committed, {fresh.Count} regenerated, {build.Elapsed.TotalSeconds:F1} s). "
                + "The regenerated files are compared in place and are NOT written back: refreshing "
                + $"{BaselineRelativePath} is a human decision. Reproduce with:\n  "
                + build.CommandLine
                + "\nthen copy the emitted tree over "
                + $"{BaselineRelativePath}/ (this is also what makes "
                + "docs/fused-emission-registry.md's `已发射` rows true again).";

            // Asserted on the count, not with IsEmpty(): TUnit prints the Because text plus the collection,
            // and every entry here is already a multi-line report — one copy of it is a report, two is noise.
            await Assert
                .That(problems.Count)
                .IsEqualTo(0)
                .Because(
                    problems.Count == 0
                        ? "every committed *.g.cs file is byte-identical to what today's generators emit"
                        : summary + "\n  - " + string.Join("\n  - ", problems)
                );
        }
        finally
        {
            TryDelete(freshDirectory);
        }
    }

    /// <summary>
    /// Rebuilds the probe project with its generator output redirected into <paramref name="outputDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <c>-t:Rebuild</c> is not optional: an incremental build reuses the previous
    /// <c>CompilerGeneratedFilesOutputPath</c> (and may skip the compiler outright), which is how a byte gate
    /// fakes a pass — <c>docs/r1/gate-evidence.md</c> spells this out. <c>-m:1</c> keeps the nested build to
    /// one project graph at a time, like the repo's own build instructions.
    /// </remarks>
    static async Task<BuildResult> Regenerate(string root, string outputDirectory)
    {
        var configuration = BuildConfiguration();
        var project = Path.Combine(root, ProbeProjectRelativePath);

        var arguments = new List<string>
        {
            "build",
            project,
            "-c",
            configuration,
            "-m:1",
            "-t:Rebuild",
            "--nologo",
            "-v:m",
            // The eleven referenced libraries/generators were built by the build that produced this test
            // assembly; rebuilding them here would cost tens of seconds and change nothing (see the class
            // comment).
            "-p:BuildProjectReferences=false",
            // Explicit, so a future default (or an inherited command-line property) cannot quietly switch
            // the probe onto packaged assemblies and compare the baseline with a different generator.
            "-p:UseProjectReferences=true",
            "-p:EmitCompilerGeneratedFiles=true",
            "-p:CompilerGeneratedFilesOutputPath=" + outputDirectory,
        };

        // A restore that has nothing to do is a second and a half on every run; the probe references only
        // projects, so an existing assets file is always current enough. Without one (fresh clone) the
        // restore has to run — it needs no network, there is not a single PackageReference in the probe.
        if (
            File.Exists(
                Path.Combine(
                    root,
                    "tests",
                    "FormatCapabilities.Probe",
                    "obj",
                    "project.assets.json"
                )
            )
        )
            arguments.Add("--no-restore");

        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetPath(),
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // No MSBuild node/server reuse: this build runs inside the test process, and a leftover node would
        // outlive it holding locks on the probe's obj/.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        // Quoted only where a quote is needed, so the failure text's command stays copy-pasteable from any
        // checkout path (repo paths have contained spaces before).
        var commandLine =
            $"{startInfo.FileName} "
            + string.Join(
                ' ',
                arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument)
            );
        var stopwatch = Stopwatch.StartNew();

        // Started with the reason attached: on a machine without the CLI this is the message the reader
        // gets, and "Win32Exception: the system cannot find the file specified" names no file.
        Process started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Exception failure)
            when (failure is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"could not start `{commandLine}` ({failure.Message}) — the baseline gate rebuilds the probe "
                    + "project, so it needs the dotnet CLI on PATH (or DOTNET_HOST_PATH set). Nothing was "
                    + "compared",
                failure
            );
        }

        using var process = started;

        // Read both pipes before waiting: a full pipe buffer would otherwise block the child forever.
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
                $"`{commandLine}` did not finish within {BuildTimeout.TotalMinutes:F0} minutes and was killed — "
                    + "this gate reports a hang instead of waiting for one"
            );
        }

        stopwatch.Stop();
        var result = new BuildResult(
            process.ExitCode,
            await stdout,
            await stderr,
            commandLine,
            stopwatch.Elapsed
        );

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`{commandLine}` failed with exit code {result.ExitCode}, so there is nothing to compare. "
                    + "The probe project must build for this gate to mean anything — its eleven referenced "
                    + "projects have to be built first (`dotnet build "
                    + $"tests/FormatCapabilities.Probe -c {configuration} -m:1`). Build output:\n"
                    + Tail(result.Log, 40)
            );
        }

        await Assert
            .That(Directory.GetFiles(outputDirectory, "*.g.cs", SearchOption.AllDirectories).Length)
            .IsGreaterThan(0)
            .Because(
                $"`{commandLine}` succeeded but emitted no *.g.cs into {outputDirectory} — "
                    + "-p:EmitCompilerGeneratedFiles / -p:CompilerGeneratedFilesOutputPath were not honoured, "
                    + "so an empty comparison would pass for the wrong reason"
            );

        return result;
    }

    /// <summary>
    /// Every <c>*.g.cs</c> under <paramref name="directory"/>, keyed by its path relative to that directory
    /// with <c>/</c> separators — so the same key means the same emitted file on every platform, and the
    /// failure text reads exactly like the repository tree.
    /// </summary>
    static Dictionary<string, string> GeneratedFiles(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
            return files;

        foreach (
            var path in Directory
                .GetFiles(directory, "*.g.cs", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
        )
            files[Path.GetRelativePath(directory, path).Replace('\\', '/')] = path;

        return files;
    }

    /// <summary>
    /// <see langword="null"/> when the two files agree; otherwise one sentence naming the first line that
    /// differs (and how many lines differ), which is what the next person needs to see the change.
    /// </summary>
    static string? DescribeDifference(string committedPath, string freshPath)
    {
        var committed = File.ReadAllBytes(committedPath);
        var fresh = File.ReadAllBytes(freshPath);

        if (committed.AsSpan().SequenceEqual(fresh))
            return null;

        // Off Windows the generator's own newline is LF while the committed blob is CRLF, so the platform's
        // newline is folded away on both sides before judging. On Windows nothing is folded: the bytes must
        // match exactly.
        var foldNewlines = Environment.NewLine != "\r\n";
        if (foldNewlines && FoldNewlines(committed).AsSpan().SequenceEqual(FoldNewlines(fresh)))
            return null;

        var committedLines = Lines(committed);
        var freshLines = Lines(fresh);
        var first = 0;
        while (
            first < committedLines.Length
            && first < freshLines.Length
            && committedLines[first] == freshLines[first]
        )
            first++;

        var differing = 0;
        for (var i = first; i < Math.Max(committedLines.Length, freshLines.Length); i++)
        {
            var left = i < committedLines.Length ? committedLines[i] : null;
            var right = i < freshLines.Length ? freshLines[i] : null;
            if (left != right)
                differing++;
        }

        // On Windows nothing was folded, so a file whose line endings changed reaches this point with zero
        // differing LINES and a different byte count. Saying "line 4, 0 lines differ" there would send the
        // reader hunting for a change that is not on any line.
        if (differing == 0)
            return $"{committed.Length} → {fresh.Length} bytes differ and no line does — the line endings "
                + "changed (the baseline is stored with CRLF; a refresh rewrites the file with the new ones)";

        var lineNumber = first + 1;
        var existsOnlyOnOneSide =
            first >= committedLines.Length ? " (the regenerated file has more lines)"
            : first >= freshLines.Length ? " (the committed file has more lines)"
            : "";

        return $"differs at line {lineNumber}{existsOnlyOnOneSide}, "
            + $"{differing} line(s) differ, {committed.Length} → {fresh.Length} bytes\n"
            + $"      baseline   {lineNumber, 4}: {Describe(committedLines, first)}\n"
            + $"      regenerated{lineNumber, 4}: {Describe(freshLines, first)}";
    }

    /// <summary>One line of the diff, quoted and clipped so one enormous generated line cannot flood the report.</summary>
    static string Describe(string[] lines, int index)
    {
        if (index >= lines.Length)
            return "<end of file>";

        const int Limit = 160;
        var text = lines[index].TrimEnd('\r');
        return text.Length <= Limit ? $"`{text}`" : $"`{text[..Limit]}`… ({text.Length} chars)";
    }

    /// <summary>The file's text as lines, with the BOM kept (both sides carry it) and CRLF made invisible.</summary>
    static string[] Lines(byte[] bytes) =>
        new UTF8Encoding(false)
            .GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

    /// <summary>CRLF → LF, as bytes, so a Unix regeneration can be compared with the CRLF blob.</summary>
    static byte[] FoldNewlines(byte[] bytes)
    {
        var folded = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
                continue;

            folded.Add(bytes[i]);
        }

        return folded.ToArray();
    }

    /// <summary>
    /// The configuration this test assembly was built with (<c>bin/&lt;Configuration&gt;/…</c>). The nested
    /// build has to use it: <c>-p:BuildProjectReferences=false</c> resolves the referenced projects' outputs
    /// by configuration, and a Release test run that rebuilt the probe into Debug would fail on assemblies
    /// that are not there.
    /// </summary>
    static string BuildConfiguration()
    {
        var segments = new DirectoryInfo(AppContext.BaseDirectory).FullName.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        );

        var bin = Array.FindIndex(
            segments,
            segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
        );
        return bin >= 0 && bin + 1 < segments.Length ? segments[bin + 1] : "Debug";
    }

    /// <summary>
    /// The CLI to rebuild with. <c>DOTNET_HOST_PATH</c> is set by the SDK for exactly this situation (a
    /// process that has to invoke the same dotnet that is running it); a bare <c>dotnet</c> resolved from
    /// <c>PATH</c> is the fallback, and a missing one fails the test loudly rather than skipping it.
    /// </summary>
    static string DotnetPath()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(host) && File.Exists(host) ? host : "dotnet";
    }

    /// <summary>The last few lines of a captured build log — enough to name the error, not a wall of text.</summary>
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
            // A temp directory that outlives the run is harmless (and it is not in the repository);
            // failing the gate over it would be a false red.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: best effort cleanup, never the verdict.
        }
    }

    /// <summary>Exit code, captured log and the command line of the nested build.</summary>
    sealed record BuildResult(
        int ExitCode,
        string Output,
        string Error,
        string CommandLine,
        TimeSpan Elapsed
    )
    {
        /// <summary>Everything the nested build said, in one string (stdout first, then stderr).</summary>
        public string Log => Output + Error;
    }
}
