namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The repository root, for the tests that have to read repository files (the capability
/// guard in Task 4, the README matrix check in Task 8).
/// <para>
/// Walks up from the test binary until it finds <c>PicoSerDe.slnx</c>. Use this one and
/// only this one — task-local <c>RepoRoot()</c> copies drift.
/// </para>
/// </summary>
internal static class TestRepo
{
    /// <summary>Absolute path of the directory that contains <c>PicoSerDe.slnx</c>.</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PicoSerDe.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
