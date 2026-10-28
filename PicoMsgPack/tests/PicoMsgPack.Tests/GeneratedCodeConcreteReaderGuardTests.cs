namespace PicoMsgPack.Tests;

/// <summary>
/// Task T10 — the generator guard for spec §3.4.3: <b>generated code calls the concrete reader type</b>.
/// <para>
/// Using <c>ref MsgPackReader</c> rather than <c>ITokenReader</c> is a deliberate advantage of this
/// repository (spec §1.3: <c>ITokenReader</c> does not appear in any of the five <c>.Gen</c> projects),
/// and the fused read work must not spend it: an interface call would turn "one dispatch" into "one
/// dispatch plus a body", under <c>allows ref struct</c> where devirtualisation is only proven for
/// hand-written code (spec §3.4.3, §7 option 3 ③). So both interface names — <c>ITokenReader</c> and
/// <c>IFusedTokenReader</c> — must stay out of the emitted text.
/// </para>
/// <para>
/// Two independent surfaces are checked, because either one alone leaves a window open: the
/// <b>checked-in baseline</b> (what the probe project compiled last) and the <b>emitter source</b>
/// (what the next build will emit — catches a drift before the baseline regeneration hides it
/// inside a large golden diff).
/// </para>
/// </summary>
public class GeneratedCodeConcreteReaderGuardTests
{
    /// <summary>Both names are banned; <c>ITokenReader</c> is the one spec §3.4.3 names explicitly.</summary>
    static readonly string[] BannedNames = ["ITokenReader", "IFusedTokenReader"];

    /// <summary>
    /// The MessagePack baseline under <c>tests/FormatCapabilities.Probe/generated-baseline/</c> must not
    /// mention either interface name. The file count is asserted first: an empty or mislocated directory
    /// would make a "zero hits" assertion pass for the wrong reason.
    /// </summary>
    [Test]
    public async Task GeneratedBaseline_NeverMentionsATokenReaderInterface()
    {
        var files = BaselineFiles();
        await Assert.That(files.Length).IsGreaterThanOrEqualTo(20);

        var offenders = files
            .Where(file => BannedNames.Any(name => File.ReadAllText(file).Contains(name)))
            .Select(Path.GetFileName)
            .ToArray();

        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    /// The generated MessagePack code must be typed on the concrete reader — every file that opens a
    /// reader has to name <c>MsgPackReader</c>.
    /// </summary>
    [Test]
    public async Task GeneratedBaseline_UsesTheConcreteMsgPackReader()
    {
        var files = BaselineFiles();
        await Assert.That(files.Length).IsGreaterThanOrEqualTo(20);

        var withoutConcreteReader = files
            .Where(file => !File.ReadAllText(file).Contains("MsgPackReader"))
            .Select(Path.GetFileName)
            .ToArray();

        await Assert.That(withoutConcreteReader).IsEmpty();
    }

    /// <summary>
    /// The emitter source itself is grep-clean, so a drift is caught even before the baseline is
    /// regenerated (spec §3.4.3's guard is "independent of the implementation under test").
    /// </summary>
    [Test]
    public async Task GeneratorEmitter_NeverMentionsATokenReaderInterface()
    {
        var source = Path.Combine(
            RepoRoot(),
            "PicoMsgPack",
            "src",
            "PicoMsgPack.Gen",
            "MsgPackSerializerGenerator.cs"
        );
        await Assert.That(File.Exists(source)).IsTrue();

        var text = File.ReadAllText(source);
        var hits = BannedNames.Where(text.Contains).ToArray();

        await Assert.That(hits).IsEmpty();
    }

    /// <summary>
    /// The rule that makes the guard a rule: the emitter of the counted loop (rule E-1, the first place
    /// that reaches for a fused reader member) names the concrete type. Pinned as text so T11 cannot
    /// quietly switch it to an interface cast.
    /// </summary>
    [Test]
    public async Task EmittedCountedLoop_CallsTheConcreteReader()
    {
        var file = BaselineFiles()
            .OrderByDescending(f => new FileInfo(f).Length)
            .First(f => File.ReadAllText(f).Contains("TryGetCollectionCount"));

        var baseline = File.ReadAllText(file);
        await Assert.That(baseline).Contains("reader.TryGetCollectionCount(out var __cn");
        await Assert.That(baseline).DoesNotContain("ITokenReader");
    }

    /// <summary>Every <c>*.g.cs</c> under the MessagePack baseline directory.</summary>
    static string[] BaselineFiles()
    {
        var directory = Path.Combine(
            RepoRoot(),
            "tests",
            "FormatCapabilities.Probe",
            "generated-baseline",
            "PicoMsgPack.Gen"
        );
        if (!Directory.Exists(directory))
            return [];
        return Directory.GetFiles(directory, "*.g.cs", SearchOption.AllDirectories);
    }

    /// <summary>Walks up from the test binary until the solution file (same rule as <c>TestRepo</c>).</summary>
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PicoSerDe.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
