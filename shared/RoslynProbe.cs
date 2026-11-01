// Shared test fixture — the Roslyn metadata reference set a compilation over GENERATED code is
// given. Linked into every test project that drives a source generator in-proc, by the same
// mechanism as shared/TestText.cs and shared/CrossValidationModels.cs:
//     <Compile Include="..\..\..\shared\RoslynProbe.cs" Link="RoslynProbe.cs" />
// It must never be compiled into a shipping assembly: `internal` here means "one private copy per
// test assembly", which is exactly what a test fixture wants (no InternalsVisibleTo, no public API).
//
// Why this file exists (audit C5): the same ~20-line method had been copied into 15 test files
// across 3 test projects — 212 lines — and had already been copied *between* files by hand
// (`ShapeCompilationTests` called `SilentDropDiagnosticTests.PlatformReferences()` directly). The
// copies were not even faithful: 3 of the 15 had drifted to a different platform filter and a
// different set of extra assemblies. One table, in one place, is the fix.
//
// What this file must NOT do:
//   * It must not WIDEN or NARROW anyone's reference set. The reference set IS the test: these
//     compilations answer "does the generated code compile?", so one assembly too many turns a
//     should-fail test green, and one too few makes it fail for the wrong reason (the reason
//     GeneratedCodeSelfSufficiencyGuardTests deliberately uses the whole shared framework instead
//     of PlatformReferences — see its ConsumerReferences()).
//   * It must not dedupe. It passes TRUSTED_PLATFORM_ASSEMBLIES through entry by entry, in host
//     order, exactly as the 15 copies did — including the host's own duplicate
//     System.Private.CoreLib.dll entry. Deduping is a separate decision about what those
//     compilations should reference, not a refactor: it changes the input of every compile test in
//     the solution at once.
//   * It must not become the place that decides *which format* a test compiles against: the format
//     assemblies arrive as arguments, because this file is linked into test projects that each
//     reference a different subset of the five formats (PicoJetson.Integration.Tests does not even
//     reference PicoIni/PicoToml/PicoYaml/PicoMsgPack, so a `typeof(PicoIni.IniSerializer)` in here
//     would not compile there).

using System.Reflection;
using Microsoft.CodeAnalysis;

namespace PicoSerDe.TestSupport;

/// <summary>
/// The metadata references a <c>CSharpCompilation</c> over generated code is given, in the three
/// shapes the test suite actually uses.
/// <para>
/// <b>The three shapes are three facts, not an oversight.</b> Every call site used to spell its own
/// reference set out; measuring them (sorted, before the consolidation) showed three distinct sets,
/// not one:
/// </para>
/// <list type="table">
/// <item>
///   <term><see cref="PlatformReferences"/></term>
///   <description>
///   Five platform assemblies — <c>System.Private.CoreLib</c>, <c>System.Runtime</c>,
///   <c>System.Collections</c>, <c>netstandard</c>, <c>System.Memory</c> — plus the assemblies the
///   caller names (the format, usually with <c>PicoSerDe.Core</c>). The common shape: "enough to
///   bind the shapes the generator emits, and nothing else". 12 of the 15 sites.
///   </description>
/// </item>
/// <item>
///   <term><see cref="MinimalPlatformReferences"/></term>
///   <description>
///   The same idea, three assemblies wide (<c>System.Private.CoreLib</c>, <c>System.Runtime</c>,
///   <c>netstandard</c>) and no format assembly. One site — <c>RecursionKindTests</c> — which
///   classifies recursive shapes and never compiles emitted code, so it needs neither
///   <c>System.Collections</c> nor a format binding.
///   </description>
/// </item>
/// <item>
///   <term><see cref="AllPlatformReferences"/></term>
///   <description>
///   Every <c>TRUSTED_PLATFORM_ASSEMBLIES</c> entry, unfiltered: the generator has to resolve the
///   <i>full</i> type of members such as <c>Dictionary&lt;string, List&lt;int&gt;&gt;</c>, and a
///   reference set that is merely "enough to parse" silently degrades those members. Two sites
///   (<c>JsonFusedElementEmissionTests</c>, <c>JsonScalarReadFastPathTests</c>).
///   </description>
/// </item>
/// </list>
/// <para>
/// Each preset's set is pinned by <c>RoslynProbeFixtureTests</c>, so a future edit here cannot
/// quietly relax every compile test in the solution at once.
/// </para>
/// </summary>
internal static class RoslynProbe
{
    /// <summary>
    /// The filtered set: the platform assemblies a generated serializer names, and no others.
    /// <c>System.Memory.dll</c> and <c>System.Collections.dll</c> are here because emitted code
    /// binds <c>Memory&lt;T&gt;</c>/<c>ReadOnlyMemory&lt;T&gt;</c> and the collection interfaces.
    /// </summary>
    private static readonly string[] CorePlatformAssemblies =
    [
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "System.Collections.dll",
        "netstandard.dll",
        "System.Memory.dll",
    ];

    /// <summary>The narrowest set any test in the suite uses — see the class remarks.</summary>
    private static readonly string[] MinimalPlatformAssemblies =
    [
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "netstandard.dll",
    ];

    /// <summary>
    /// Where a referenced assembly lives in the test output directory. The path is derived from the
    /// assembly's simple name (not from its location) because the reference has to be the copy the
    /// test host actually loaded.
    /// </summary>
    internal static string AssemblyPath(Assembly assembly) =>
        Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll");

    /// <summary>
    /// The common reference set: the five <see cref="CorePlatformAssemblies"/> out of
    /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, then one reference per assembly in
    /// <paramref name="assemblies"/>, in the order given.
    /// </summary>
    internal static IEnumerable<MetadataReference> PlatformReferences(params Assembly[] assemblies) =>
        FromTrustedPlatformAssemblies(CorePlatformAssemblies, assemblies);

    /// <summary>
    /// The narrow set: <c>System.Private.CoreLib</c>, <c>System.Runtime</c>, <c>netstandard</c>,
    /// then <paramref name="assemblies"/>. For a compilation that does not bind emitted code.
    /// </summary>
    internal static IEnumerable<MetadataReference> MinimalPlatformReferences(
        params Assembly[] assemblies
    ) => FromTrustedPlatformAssemblies(MinimalPlatformAssemblies, assemblies);

    /// <summary>
    /// The whole shared framework: every <c>TRUSTED_PLATFORM_ASSEMBLIES</c> entry, then
    /// <paramref name="assemblies"/>. For a compilation whose references must not be the variable
    /// under test.
    /// </summary>
    internal static IEnumerable<MetadataReference> AllPlatformReferences(
        params Assembly[] assemblies
    ) => FromTrustedPlatformAssemblies(null, assemblies);

    private static IEnumerable<MetadataReference> FromTrustedPlatformAssemblies(
        string[]? platformAssemblies,
        Assembly[] assemblies
    )
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            // `is "A" or "B"` in every pre-consolidation copy: ordinal, case-sensitive, and
            // deliberately so — a comparer change here is a reference-set change.
            if (
                platformAssemblies is not null
                && !platformAssemblies.Contains(Path.GetFileName(path), StringComparer.Ordinal)
            )
                continue;
            yield return MetadataReference.CreateFromFile(path);
        }
        foreach (var assembly in assemblies)
            yield return MetadataReference.CreateFromFile(AssemblyPath(assembly));
    }
}
