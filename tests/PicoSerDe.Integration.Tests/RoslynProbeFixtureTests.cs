using Microsoft.CodeAnalysis;
using PicoSerDe.TestSupport;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The guard for <see cref="RoslynProbe"/> itself — the reference set every "does the generated
/// code compile?" test hands to <c>CSharpCompilation.Create</c>.
/// <para>
/// <b>Why the helper exists.</b> The same ~20-line method had been copied into 15 test files
/// across 3 projects (audit C5), and 3 of the 15 had already drifted to a different set. One table
/// in one place is the fix — but a single shared table is also a single place where a one-word
/// edit silently changes the input of every compile test in the solution at once. That is what
/// these tests exist to stop: they pin <b>which assemblies</b> each preset yields, so "the
/// reference set is the test" survives the consolidation.
/// </para>
/// <para>
/// What they deliberately do <b>not</b> assert: how many entries
/// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> has, or that a given assembly appears in it. That is a host
/// fact, not a contract — it differs per runtime and per machine. The contract is the <b>filter</b>
/// and the <b>appended assemblies</b>.
/// </para>
/// </summary>
public class RoslynProbeFixtureTests
{
    private static readonly string[] FivePlatformAssemblies =
    [
        "System.Collections.dll",
        "System.Memory.dll",
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "netstandard.dll",
    ];

    private static readonly string[] ThreePlatformAssemblies =
    [
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "netstandard.dll",
    ];

    private static string[] FileNames(IEnumerable<MetadataReference> references) =>
        references
            .Select(r => Path.GetFileName(((PortableExecutableReference)r).FilePath!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// ① The common preset is exactly the five platform assemblies — not four, not six, and not
    /// "whatever the shared framework happens to contain".
    /// </summary>
    [Test]
    public async Task PlatformReferences_IsExactlyTheFivePlatformAssemblies()
    {
        var names = FileNames(RoslynProbe.PlatformReferences());

        await Assert
            .That(string.Join(", ", names))
            .IsEqualTo(string.Join(", ", FivePlatformAssemblies))
            .Because(
                "a wider filter makes a 'should not compile' assertion pass, a narrower one makes "
                    + "it fail for the wrong reason"
            );
    }

    /// <summary>
    /// ② The assemblies the caller names are appended, in the order given, and resolve to the
    /// copy the test host loaded. Order matters to nobody in Roslyn, but "the last two entries are
    /// the two I asked for" is the property <see cref="RoslynProbe.AssemblyPath"/> has to keep.
    /// </summary>
    [Test]
    public async Task PlatformReferences_AppendsTheRequestedAssembliesInOrder()
    {
        var platform = typeof(PicoSerDe.Core.SerializerExtensions).Assembly;
        var format = typeof(PicoIni.IniSerializer).Assembly;

        var references = RoslynProbe
            .PlatformReferences(format, platform)
            .Select(r => ((PortableExecutableReference)r).FilePath!)
            .ToArray();

        var appended = references[^2..];
        await Assert.That(appended[0]).IsEqualTo(RoslynProbe.AssemblyPath(format));
        await Assert.That(appended[1]).IsEqualTo(RoslynProbe.AssemblyPath(platform));
        await Assert.That(File.Exists(appended[0])).IsTrue();
        await Assert.That(File.Exists(appended[1])).IsTrue();
    }

    /// <summary>
    /// ③ The narrow preset (<c>RecursionKindTests</c>) is a strict subset of the common one: three
    /// assemblies and no format. A test that classifies recursive shapes never binds emitted code,
    /// so it must not start paying for a format reference — and, more importantly, the preset must
    /// not drift into being "the common one with a different name".
    /// </summary>
    [Test]
    public async Task MinimalPlatformReferences_IsTheThreeAssemblySubset()
    {
        var minimal = FileNames(RoslynProbe.MinimalPlatformReferences());

        await Assert.That(string.Join(", ", minimal)).IsEqualTo(string.Join(", ", ThreePlatformAssemblies));
        await Assert
            .That(minimal.All(name => FivePlatformAssemblies.Contains(name, StringComparer.Ordinal)))
            .IsTrue();
        await Assert
            .That(minimal.Length < FivePlatformAssemblies.Length)
            .IsTrue()
            .Because("otherwise the two presets would be the same set under two names");
    }

    /// <summary>
    /// ④ The whole-framework preset (<c>JsonFusedElementEmissionTests</c>,
    /// <c>JsonScalarReadFastPathTests</c>) is strictly wider than the common one and still contains
    /// it: those harnesses resolve each member's <i>full</i> type and must not be the variable
    /// under test.
    /// </summary>
    [Test]
    public async Task AllPlatformReferences_IsStrictlyWiderThanPlatformReferences()
    {
        var all = FileNames(RoslynProbe.AllPlatformReferences());
        var filtered = FileNames(RoslynProbe.PlatformReferences());

        await Assert
            .That(all.Length)
            .IsGreaterThan(filtered.Length)
            .Because("the whole shared framework has more assemblies than the five-name filter");
        await Assert
            .That(filtered.All(name => all.Contains(name, StringComparer.Ordinal)))
            .IsTrue()
            .Because("the wider set is a superset, not a different set");
    }

    /// <summary>
    /// ⑤ <see cref="RoslynProbe.AssemblyPath"/> is the test output directory plus the assembly's
    /// simple name. Not <c>Assembly.Location</c> — the reference has to be the copy the host
    /// loaded, and the two differ as soon as anything is shadow-copied.
    /// </summary>
    [Test]
    public async Task AssemblyPath_PointsAtTheCopyTheHostLoaded()
    {
        var assembly = typeof(PicoJetson.JsonSerializer).Assembly;

        var path = RoslynProbe.AssemblyPath(assembly);

        await Assert.That(path).IsEqualTo(Path.Combine(AppContext.BaseDirectory, "PicoJetson.dll"));
        await Assert.That(File.Exists(path)).IsTrue();
    }
}
