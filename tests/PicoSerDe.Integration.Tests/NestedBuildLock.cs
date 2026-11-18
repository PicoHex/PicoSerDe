using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// A <b>cross-process</b> mutex around a nested <c>dotnet build</c>, so that two instances of this test
/// assembly (one per target framework, run side by side by <c>dotnet test PicoSerDe.slnx</c>) cannot compile
/// into the same <c>obj/</c> directory at the same time.
/// <para>
/// <b>The failure it removes.</b> <see cref="GeneratedBaselineRegenerationGateTests"/> rebuilds
/// <c>tests/FormatCapabilities.Probe</c> and <see cref="ApiSurfaceDiffCriterionTests"/> builds
/// <c>scripts/api-surface</c>. Both command lines are <b>identical</b> whichever TFM runs them: the nested
/// build writes to <c>&lt;project&gt;/obj/Debug/&lt;tfm&gt;/</c> and <c>&lt;project&gt;/bin/Debug/&lt;tfm&gt;/</c>
/// of the <i>project being built</i>, and both processes build all of its TFMs. So the second process writes
/// over the first one's <c>*.dll</c> while the first still has it open, and MSBuild reports
/// <c>CS2012: Cannot open '…/obj/Debug/net11.0/…dll' for writing — The process cannot access the file …
/// because it is being used by another process</c>. Measured here (two full-solution runs): six
/// <see cref="ApiSurfaceDiffCriterionTests"/> failures, and the baseline gate dying with CS2012 on
/// <c>FormatCapabilities.Probe.dll</c> — while <c>-f net10.0</c> and <c>-f net11.0</c> each stayed green.
/// </para>
/// <para>
/// <b>Why a wait, and not "each TFM builds into its own directory".</b> Redirecting the intermediate output
/// (<c>-p:BaseIntermediateOutputPath=…</c>) is the obvious other answer and it does not work here: the
/// restore's <c>project.assets.json</c> lives in <c>obj/</c>, so a relocated <c>obj/</c> needs its own
/// restore (the gate deliberately runs <c>--no-restore</c>, one and a half seconds on every run), MSBuild
/// wants those properties fixed before the NuGet targets are imported, and the probe builds <i>all</i> of its
/// TFMs in one invocation, so a per-TFM directory would still need one build per TFM — multiplying the most
/// expensive thing in the suite. Serialising the build costs the second caller <b>one</b> build's wait
/// (measured 4–18 s for the probe) and changes nothing about what either caller compares.
/// </para>
/// <para>
/// <b>Why a lock file and not a named <see cref="Mutex"/>.</b> A <see cref="Mutex"/> has thread affinity:
/// <c>WaitOne</c> and <c>ReleaseMutex</c> must run on the same thread, and a <c>Mutex</c> acquired around an
/// <c>await</c> is released on a different thread-pool thread — <c>ApplicationException: Object
/// synchronization method was called from an unsynchronized block of code</c>. A <see cref="FileStream"/>
/// opened with <see cref="FileShare.None"/> holds the same cross-process exclusion with no owner thread: it is
/// taken when the stream is opened and released when the stream is disposed, and the operating system releases
/// it if the holding process dies (so a crashed run cannot leave the suite wedged). On Windows the share mode
/// is enforced by the file system; on Unix .NET emulates <see cref="FileShare.None"/> with <c>flock</c>.
/// </para>
/// <para>
/// <b>The lock file is never deleted</b>, and that is deliberate: a waiter may already have opened the file, and
/// deleting it underneath would let a third process create a fresh file and take a "free" lock while the waiter
/// takes the old one — two holders. The file is empty apart from the holder's pid, it lives under the temp path
/// (never in the repository, so <c>git status --porcelain</c> is untouched), and it is keyed by checkout
/// <b>and</b> project (<see cref="LockPath"/>) so worktrees and unrelated projects never queue behind each
/// other.
/// </para>
/// </summary>
internal static class NestedBuildLock
{
    /// <summary>
    /// How often a waiting caller retries. Small enough that the handoff is invisible next to a build that
    /// takes seconds, large enough that a ten-minute wait costs 6000 syscalls rather than a busy loop.
    /// </summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Where every lock file lives: one directory under the temp path, named for a reader to find.</summary>
    static readonly string LockDirectory = Path.Combine(
        Path.GetTempPath(),
        "picoserde-nested-build"
    );

    /// <summary>
    /// The lock file for "the nested build of <paramref name="projectRelativePath"/> in this checkout".
    /// <para>
    /// The identity is the checkout's full path plus the project path, case-folded and with the separator
    /// spelling normalised: Windows paths are case-insensitive, and the repository spells project paths with
    /// <c>/</c> while <see cref="Path.Combine"/> would hand back <c>\</c> — two spellings of one file must not
    /// become two locks, or the lock is decorative. It is hashed because the temp path plus a repository path
    /// is longer than a file name may be.
    /// </para>
    /// </summary>
    public static string LockPath(string root, string projectRelativePath)
    {
        var identity =
            Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + "|"
            + projectRelativePath.Replace('\\', '/');

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant()))
        );

        return Path.Combine(LockDirectory, digest[..16] + ".lock");
    }

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="timeout"/> for whoever holds it; dispose the result to
    /// release it. <paramref name="waitingFor"/> is spelled into the timeout message ("the probe rebuild the
    /// baseline gate needs"), because the reader of a stuck suite needs to know which test was in front.
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(
        string root,
        string projectRelativePath,
        TimeSpan timeout,
        string waitingFor
    )
    {
        var path = LockPath(root, projectRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return Hold(path);
            }
            catch (IOException failure)
            {
                // A share violation is the expected answer while another process builds: it means the lock is
                // working. Anything else that arrives as IOException (a full disk, a read-only temp) arrives
                // here too and is reported by the same message, with the timeout as the only unwind — which
                // is why the message carries the underlying failure.
                if (stopwatch.Elapsed >= timeout)
                {
                    throw new InvalidOperationException(
                        $"{waitingFor} waited {timeout.TotalSeconds:F0} s for {path} and never got it, so no "
                            + "nested build ran. Another test process is still holding it (its pid is in the "
                            + "file) — if nothing else is running, delete that file and run again",
                        failure
                    );
                }
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Opens the lock file exclusively and stamps it with this process's id, returning the stream that
    /// <b>is</b> the lock: dispose it to release.
    /// </summary>
    static FileStream Hold(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough
        );

        try
        {
            var stamp = Encoding.UTF8.GetBytes($"{Environment.ProcessId}{Environment.NewLine}");

            stream.SetLength(0);
            stream.Write(stamp, 0, stamp.Length);
            stream.Flush();
        }
        catch (IOException)
        {
            // The lock is what matters, the pid is a courtesy for the next reader; a temp directory that
            // refuses a one-line write must not turn into a failed build.
        }

        return stream;
    }
}
