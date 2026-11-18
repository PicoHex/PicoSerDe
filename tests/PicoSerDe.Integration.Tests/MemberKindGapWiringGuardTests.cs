using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The <b>wiring</b> half of <c>PICOSERDE006</c>, and the reason it cannot be behavioural.
/// <para>
/// <b>What the ablation measured on <c>36c085f</c>.</b> Two edits to the diagnostic left all 1144
/// tests green: flipping <c>isEnabledByDefault: true → false</c>, and deleting the ONE
/// <c>ReportMemberKindNotEmittable</c> call — i.e. the diagnostic could be disabled, or unhooked
/// from the generator, without a single red line. <c>AnyMemberDiagnosticTests</c> could not see
/// either: it only asserts <c>DoesNotContain(Id)</c>, which pins the ABSENCE of the diagnostic on
/// the kinds that HAVE an emitter (the false-positive direction).
/// </para>
/// <para>
/// <b>Why no model can turn this into a behavioural test.</b> <c>MemberKindHasEmitter</c> is a
/// total function over the closed kind vocabulary the extraction can produce: every kind
/// <c>ExtractProperties</c> emits has an arm in the member write/read switches, so there is no DTO
/// that makes the pre-scan report. The true-positive direction is unreachable from source, which
/// is why the report SITE is executed directly in <c>MemberKindGapDiagnosticTests</c>
/// (PicoJetson suite: descriptor, message, guard both ways, dedupe — that file is red the moment
/// the report call inside the site is deleted) and why <i>this</i> file exists for the other
/// deletion: the call that puts the site on the generation path at all, which no runtime test can
/// observe either, because its only effect is "a function that today returns no reports is
/// called".
/// </para>
/// <para>
/// So this is a <b>structural</b> guard, in the same spirit as <c>VerdictMarkerGuardTests</c> and
/// <c>FusedMemberEmissionGuardTests</c>: "implemented, never called" is a state no behavioural
/// test can see, and this repository has been bitten by it before. It is deliberately about
/// <b>existence and placement</b>, not about the code's text: rename a variable freely, move the
/// call and the failure message tells you which invariant you broke.
/// </para>
/// </summary>
public class MemberKindGapWiringGuardTests
{
    /// <summary>The generator that owns the report chain (the only format that calls it).</summary>
    private const string GeneratorRelativePath = "PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs";

    /// <summary>The descriptor's definition, and the only file allowed to spell its id.</summary>
    private const string InfrastructureRelativePath = "shared/Gen/GenInfrastructure.cs";

    private const string PrescanName = "ReportMemberKindsWithoutAnEmitter";
    private const string SiteName = "ReportMemberKindWithoutAnEmitter";
    private const string ReportName = "ReportMemberKindNotEmittable";
    private const string GenerationEntry = "GenerateAll";
    private const string IdLiteral = "id: \"PICOSERDE006\"";

    // ── 1. the report site is called, not merely defined ──────────────────────────

    /// <summary>
    /// <c>GenerateAll</c> must still CALL the pre-scan, and the call must be in the method that
    /// also registers sources — an invocation that lives in an orphan helper is the "implemented,
    /// never called" state this file exists for. Deleting the call is the second measured
    /// ablation; it fails here.
    /// </summary>
    [Test]
    public async Task Prescan_IsCalledFromTheGenerationEntry()
    {
        var root = GeneratorRoot();
        var calls = Invocations(root, PrescanName);

        await Assert
            .That(calls.Length)
            .IsEqualTo(1)
            .Because(
                $"{PrescanName} is the ONLY thing in the JSON generator that reports PICOSERDE006. "
                    + "Deleting its call was measured to change nothing anybody could see; if the "
                    + "chain was moved, move this guard with it — do not delete it."
            );

        var owner = calls[0].FirstAncestorOrSelf<MethodDeclarationSyntax>();
        await Assert.That(owner).IsNotNull();
        await Assert
            .That(owner!.Identifier.Text)
            .IsEqualTo(GenerationEntry)
            .Because(
                "the pre-scan has to run on the generation path; inside any other method it is a "
                    + "helper nobody invokes"
            );

        // ...and that method is the one that emits: it adds at least one source.
        await Assert
            .That(
                owner.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(i => i.ToString().Contains("spc.AddSource"))
            )
            .IsTrue()
            .Because(
                $"{GenerationEntry} registers the generated serializers, so a source-adding call "
                    + "proves this is the emit entry and not a same-named lookalike"
            );
    }

    /// <summary>
    /// The site itself must still CALL the report function. This is the other measured ablation,
    /// and the one that turns the whole id into dead code: the dedupe and the
    /// <c>MemberKindHasEmitter</c> guard above it run, decide "report", and drop the answer.
    /// </summary>
    [Test]
    public async Task ReportSite_StillCallsTheReportFunction()
    {
        var root = GeneratorRoot();
        var calls = Invocations(root, ReportName);

        await Assert
            .That(calls.Length)
            .IsEqualTo(1)
            .Because(
                "exactly one site reports PICOSERDE006 (the pre-scan deduplicates per member); "
                    + "zero is the measured silent ablation, more than one is a duplicate warning"
            );

        var owner = calls[0].FirstAncestorOrSelf<MethodDeclarationSyntax>();
        await Assert.That(owner).IsNotNull();
        await Assert
            .That(owner!.Identifier.Text)
            .IsEqualTo(SiteName)
            .Because(
                "the report call belongs to the per-site helper that runs the "
                    + $"MemberKindHasEmitter guard; a call elsewhere has bypassed it"
            );
    }

    // ── 2. the id has one definition ──────────────────────────────────────────────

    /// <summary>
    /// One descriptor, in the shared infrastructure every generator links — a second copy would
    /// compile, be reachable from a different call site, and let the two drift in silence (the
    /// failure mode <c>VerdictMarkerGuardTests</c> was written for).
    /// </summary>
    [Test]
    public async Task TheDescriptorIsDeclaredOnce_AndInTheSharedInfrastructure()
    {
        var root = TestRepo.Root();
        var files = GeneratorFiles(root)
            .Append(Path.Combine(root, InfrastructureRelativePath))
            .ToArray();

        var sites = files
            .Where(File.Exists)
            .SelectMany(file =>
                File.ReadAllLines(file)
                    .Select((line, index) => (File: file, Line: index + 1, Text: line))
                    .Where(entry => entry.Text.Contains(IdLiteral, StringComparison.Ordinal))
                    .Select(entry =>
                        Path.GetRelativePath(root, entry.File).Replace('\\', '/')
                        + ":"
                        + entry.Line
                    )
            )
            .ToArray();

        await Assert
            .That(sites.Length)
            .IsEqualTo(1)
            .Because(
                $"PICOSERDE006 must be declared exactly once, as '{IdLiteral}'; found: "
                    + (sites.Length == 0 ? "nothing" : string.Join(" | ", sites))
            );

        await Assert
            .That(sites[0])
            .StartsWith(InfrastructureRelativePath + ":")
            .Because(
                "the descriptor lives in the infrastructure all five generators compile from; a "
                    + "copy in one format's generator is a second definition"
            );
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static SyntaxNode GeneratorRoot() =>
        CSharpSyntaxTree
            .ParseText(File.ReadAllText(Path.Combine(TestRepo.Root(), GeneratorRelativePath)))
            .GetRoot();

    /// <summary>
    /// Every invocation of <paramref name="name"/>, qualified or not: the generator calls the
    /// shared infrastructure qualified (<c>GenInfrastructure.ReportX(...)</c>) and its own helpers
    /// bare. Matching the NAME and not the spelling keeps a "fully qualify it for clarity" edit
    /// from breaking the guard.
    /// </summary>
    private static InvocationExpressionSyntax[] Invocations(SyntaxNode root, string name) =>
        root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(call =>
                call.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text == name,
                    MemberAccessExpressionSyntax access => access.Name.Identifier.Text == name,
                    GenericNameSyntax generic => generic.Identifier.Text == name,
                    _ => false,
                }
            )
            .ToArray();

    /// <summary>The five format generators plus the shared infrastructure they compile.</summary>
    private static IEnumerable<string> GeneratorFiles(string root) =>
        new[]
        {
            "PicoJetson/src/PicoJetson.Gen",
            "PicoIni/src/PicoIni.Gen",
            "PicoToml/src/PicoToml.Gen",
            "PicoYaml/src/PicoYaml.Gen",
            "PicoMsgPack/src/PicoMsgPack.Gen",
        }
            .Select(dir => Path.Combine(root, dir))
            .Where(Directory.Exists)
            .SelectMany(dir =>
                Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                    .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            );
}
