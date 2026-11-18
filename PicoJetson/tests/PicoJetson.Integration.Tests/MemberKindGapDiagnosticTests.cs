using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoJetson.Gen;
using PicoSerDe.Gen;
using PropDescriptor = PicoSerDe.Gen.PropertyInfo;
using TypeDescriptor = PicoSerDe.Gen.TypeInfo;
using PicoSerDe.TestSupport;

namespace PicoJetson.Tests;

// PICOSERDE006 — "this generator has no emitter for this member's type kind" — had a hole big
// enough to lose the whole diagnostic, and `AnyMemberDiagnosticTests` could not see it: that file
// only ever asserts `DoesNotContain(Id)`, i.e. it pins the ABSENCE of the diagnostic on kinds that
// do have an emitter. Measured on 36c085f (the ablation behind this file):
//
//   * `isEnabledByDefault: true → false`  ⇒ 1144 tests green,
//   * deleting the ONE `ReportMemberKindNotEmittable` call ⇒ 1144 tests green.
//
// Both are red here (re-measured with this file in the tree: 7 red and 5 red of 4011, every one of
// them in this file or its wiring guard, 4006 elsewhere still green — the silence is the point). The reason a model cannot reach the diagnostic is worth stating, because it
// is what forces the shape of these tests: the JSON member switches have an arm for EVERY kind
// `ExtractProperties` can produce (`MemberKindHasEmitter` is a total function over that closed
// vocabulary), so "a real DTO with an unemittable member" does not exist — the false-positive
// direction is the reachable one and the true-positive direction is not. What can be exercised is
// therefore the report SITE itself, fed the descriptor the pre-scan would hand it, plus the
// wiring that makes anybody call it at all (`MemberKindGapWiringGuardTests`, in the integration
// suite, reads the source text for that half).

public class MemberKindGapDiagnosticTests
{
    /// <summary>
    /// One member whose kind has an emitter and one whose kind does not, built by hand: the pre-scan
    /// only ever passes kinds from the closed vocabulary, so the unemittable input has to be
    /// constructed. <c>PropDescriptor</c> is the record the extraction produces, so this is the same
    /// value the emit switches receive.
    /// </summary>
    static class Descriptors
    {
        internal const string UnemittableKind = "no-such-kind";

        internal static PropDescriptor Member(string name, string kind) =>
            new(
                Name: name,
                JsonName: name,
                TypeKind: kind,
                TypeFullName: "System.Object",
                IsNullable: false,
                ElementTypeKind: null,
                ElementTypeName: null,
                KeyTypeKind: null,
                KeyTypeName: null,
                NestedProperties: ImmutableArray<PropDescriptor>.Empty,
                ConverterTypeFullName: null
            );

        internal static TypeDescriptor Type(string name, params PropDescriptor[] properties) =>
            new(
                FullyQualifiedName: "global::Fixtures." + name,
                Namespace: "Fixtures",
                Name: name,
                Properties: properties.ToImmutableArray()
            );
    }

    /// <summary>
    /// Runs a callback inside a real <see cref="SourceProductionContext"/>: the report functions take
    /// one, and a generator driver is the only supported way to obtain one. This is the seam that
    /// makes the diagnostic observable WITHOUT compiling a model that would trigger it.
    /// </summary>
    sealed class ContextHarness(Action<SourceProductionContext> report) : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterSourceOutput(context.CompilationProvider, (spc, _) => report(spc));
    }

    private const string Id = "PICOSERDE006";

    /// <summary>
    /// The pre-scan and its per-site helper are `private static` on the generator: the only public
    /// door is <c>GenInfrastructure.ReportMemberKindNotEmittable</c>, which is the CALLEE. The
    /// mutation that matters — deleting the call — lives at the callee's call site, so the caller
    /// is the thing that has to be executed. Reflection is the price of pinning a private seam
    /// from the test side without widening the generator's surface for a test.
    /// </summary>
    private static readonly MethodInfo ReportOneSite =
        typeof(JsonSerializerGenerator).GetMethod(
            "ReportMemberKindWithoutAnEmitter",
            BindingFlags.NonPublic | BindingFlags.Static
        )
        ?? throw new InvalidOperationException(
            "JsonSerializerGenerator.ReportMemberKindWithoutAnEmitter is gone — the PICOSERDE006 "
                + "report site this file pins no longer exists"
        );

    private static readonly MethodInfo PrescanSite =
        typeof(JsonSerializerGenerator).GetMethod(
            "ReportMemberKindsWithoutAnEmitter",
            BindingFlags.NonPublic | BindingFlags.Static
        )
        ?? throw new InvalidOperationException(
            "JsonSerializerGenerator.ReportMemberKindsWithoutAnEmitter is gone — nothing calls "
                + "the PICOSERDE006 report site any more"
        );

    // ── 1. the descriptor itself ───────────────────────────────────────────────────

    /// <summary>
    /// The three properties that decide whether the diagnostic can ever be seen: the id a
    /// <c>#pragma</c> or a filter names, the severity the build log shows, and
    /// <see cref="DiagnosticDescriptor.IsEnabledByDefault"/> — the one that turns the report into
    /// a NO-OP when it flips to false, which is exactly the ablation that stayed green.
    /// </summary>
    [Test]
    public async Task Descriptor_IsAnEnabledByDefaultWarningNamedPICOSERDE006()
    {
        var diagnostics = OneReportedDiagnostic();

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        var descriptor = diagnostics[0].Descriptor;

        await Assert
            .That(descriptor.Id)
            .IsEqualTo(Id)
            .Because("the id is what a reader greps for, what a #pragma names, and what the README cites");

        await Assert
            .That(descriptor.DefaultSeverity)
            .IsEqualTo(DiagnosticSeverity.Warning)
            .Because(
                "a missing emitter is OUR gap, not a broken consumer: it must not fail the build "
                    + "of code that compiles, and it must not be silent either"
            );

        await Assert
            .That(descriptor.IsEnabledByDefault)
            .IsTrue()
            .Because(
                "a diagnostic that is disabled by default is reported into a void — flipping this "
                    + "flag to false makes every report site below a no-op while the whole suite "
                    + "stays green (the measured ablation)"
            );

        await Assert.That(descriptor.Category).IsEqualTo("PicoSerDe");
    }

    /// <summary>
    /// The sentence a reader gets: what vanished, from which type, in which format, and the
    /// <c>reopen:</c> tail that says whose debt it is (AGENTS.md: a generator gap must never read
    /// as a format limit).
    /// </summary>
    [Test]
    public async Task Message_NamesTheMemberTheTypeTheKindAndTheDebt()
    {
        var diagnostics = OneReportedDiagnostic();

        var message = diagnostics[0].GetMessage();
        await Assert.That(message).Contains("Weird");
        await Assert.That(message).Contains("global::Fixtures.Holder");
        await Assert.That(message).Contains(Descriptors.UnemittableKind);
        await Assert.That(message).Contains("json");
        await Assert
            .That(message)
            .Contains("reopen:")
            .Because("the verdict is 'we have not implemented it', so it carries a reopen pointer");
        await Assert
            .That(message)
            .Contains(FormatCapabilities.DeferredMarker)
            .Because("⏸ says the format CAN hold it — never ⛔, which would blame the format");
    }

    /// <summary>
    /// The emit site reports with <c>location: null</c> on purpose (the scan runs before any
    /// symbol is in scope and the message carries the qualified name instead). Pinned because a
    /// diagnostic with no location is exactly the kind that gets filtered out of a build log
    /// without anybody noticing.
    /// </summary>
    [Test]
    public async Task Diagnostic_HasNoLocation_AndCarriesTheNameInItsText()
    {
        var diagnostics = OneReportedDiagnostic();

        await Assert.That(diagnostics[0].Location).IsEqualTo(Location.None);
        await Assert.That(diagnostics[0].Location.IsInSource).IsFalse();
    }

    // ── 2. the guarded report site: reports when it must, silent when it must not ────

    /// <summary>
    /// THE assertion that dies when the single <c>ReportMemberKindNotEmittable</c> call is deleted
    /// from the report site: with a kind that has no arm, exactly one PICOSERDE006 must come out of
    /// it. Before this test, removing that call changed nothing anybody could see.
    /// </summary>
    [Test]
    public async Task ReportSite_ReportsAKindWithNoEmitter()
    {
        var diagnostics = ReportSite("Weird", Descriptors.UnemittableKind);

        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(new[] { Id });
        await Assert.That(diagnostics[0].GetMessage()).Contains("Weird");
        await Assert.That(diagnostics[0].GetMessage()).Contains(Descriptors.UnemittableKind);
    }

    /// <summary>
    /// The other direction, at the same site: a kind the switches DO emit must produce nothing.
    /// Without this, "reports every kind" would satisfy the test above — a warning on working code
    /// is a worse regression than a missing one.
    /// </summary>
    [Test]
    public async Task ReportSite_StaysSilentForAKindWithAnEmitter()
    {
        foreach (var kind in new[] { "int32", "boolean", "guid", "float64", "enum", "dict", "any" })
        {
            var diagnostics = ReportSite("Fine", kind);
            await Assert
                .That(diagnostics)
                .IsEmpty()
                .Because($"'{kind}' has a member arm, so '{Id}' must not fire on it");
        }
    }

    /// <summary>
    /// The site deduplicates by (type, member, kind): a DTO that is ALSO a member of another DTO
    /// reaches the scan twice, and a duplicate diagnostic is a warning a reader learns to ignore.
    /// Pinned as "twice in, once out" for the same member and "two members in, two out" — the
    /// second half is what stops "deduplicate" from degenerating into "report the first only".
    /// </summary>
    [Test]
    public async Task ReportSite_ReportsAMemberOncePerTypeMemberKind()
    {
        var diagnostics = Report(spc =>
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);

            // The same member reached twice (DTO + nested member): one warning.
            for (var pass = 0; pass < 2; pass++)
                ReportOneSite.Invoke(
                    null,
                    [spc, reported, "global::Fixtures.Holder", "Weird", "System.Object", Descriptors.UnemittableKind]
                );

            // A different member is a different site: two warnings.
            ReportOneSite.Invoke(
                null,
                [spc, reported, "global::Fixtures.Holder", "AlsoWeird", "System.Object", Descriptors.UnemittableKind]
            );
        });

        var messages = diagnostics.Select(d => d.GetMessage()).ToArray();
        await Assert.That(messages.Length).IsEqualTo(2);
        await Assert.That(messages.Count(m => m.Contains("'Weird'"))).IsEqualTo(1);
        await Assert.That(messages.Count(m => m.Contains("'AlsoWeird'"))).IsEqualTo(1);
    }

    // ── 3. the pre-scan itself, end to end on hand-built descriptors ────────────────

    /// <summary>
    /// <c>ReportMemberKindsWithoutAnEmitter</c> over a <see cref="TypeDescriptor"/> whose member has no
    /// emitter: one diagnostic naming the type and the member. This is the whole pre-scan loop —
    /// the same entry the generator calls from <c>GenerateAll</c> — fed the descriptor it would
    /// have received.
    /// </summary>
    [Test]
    public async Task Prescan_ReportsEveryMemberKindWithoutAnEmitter()
    {
        var types = new[]
        {
            Descriptors.Type(
                "Holder",
                Descriptors.Member("Weird", Descriptors.UnemittableKind),
                Descriptors.Member("Count", "int32"),
                Descriptors.Member("AlsoWeird", Descriptors.UnemittableKind)
            ),
        };

        var diagnostics = Prescan(
            types,
            new Dictionary<string, ImmutableArray<PropDescriptor>>(),
            new Dictionary<string, PropDescriptor>()
        );

        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(new[] { Id, Id });
        var messages = diagnostics.Select(d => d.GetMessage()).ToArray();
        await Assert.That(messages.Any(m => m.Contains("Weird"))).IsTrue();
        await Assert.That(messages.Any(m => m.Contains("AlsoWeird"))).IsTrue();
        await Assert
            .That(messages.Any(m => m.Contains("'Count'")))
            .IsFalse()
            .Because("'Count' is an int32 and has an arm; a false positive is a warning on working code");
    }

    /// <summary>
    /// A <see cref="TypeDescriptor"/> whose every kind has an emitter reports nothing at all — the
    /// "clean model" direction the pre-scan has to get right, since it runs over EVERY type the
    /// generator sees.
    /// </summary>
    [Test]
    public async Task Prescan_StaysSilentForATypeWhoseKindsAllHaveEmitters()
    {
        var types = new[]
        {
            Descriptors.Type(
                "Clean",
                Descriptors.Member("Str", "string"),
                Descriptors.Member("Count", "int32"),
                Descriptors.Member("Flag", "boolean"),
                Descriptors.Member("Id", "guid"),
                Descriptors.Member("Nested", "object")
            ),
        };

        var diagnostics = Prescan(
            types,
            new Dictionary<string, ImmutableArray<PropDescriptor>>(),
            new Dictionary<string, PropDescriptor>()
        );

        await Assert.That(diagnostics).IsEmpty();
    }

    // ── 4. the predicate the site asks ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="JsonSerializerGenerator.MemberKindHasEmitter"/> is the site's only question, and
    /// BOTH of its answers are load-bearing: true keeps working code quiet, false is the only thing
    /// that makes the report fire. `AnyMemberDiagnosticTests` pins the true side over the model
    /// vocabulary; this pins that the false side is not vacuous, i.e. that the site's guard can
    /// still be reached at all.
    /// </summary>
    [Test]
    public async Task Predicate_AnswersBothWays()
    {
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("int32")).IsTrue();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("string")).IsTrue();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("object")).IsTrue();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("dict")).IsTrue();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("list")).IsTrue();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("datetimeoffset")).IsTrue();

        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter(Descriptors.UnemittableKind)).IsFalse();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter("")).IsFalse();
        await Assert.That(JsonSerializerGenerator.MemberKindHasEmitter(null)).IsFalse();
        await Assert
            .That(JsonSerializerGenerator.MemberKindHasEmitter("Int32"))
            .IsFalse()
            .Because("the kinds are an ordinal vocabulary, not a case-insensitive one");
    }

    /// <summary>
    /// The report site's input when a descriptor carries no kind at all (<c>kind ?? "(none)"</c>):
    /// nothing claims it can be emitted, so the reader has to be told rather than left with a
    /// member that is written as a name and no value.
    /// </summary>
    [Test]
    public async Task ReportSite_TreatsAMissingKindAsUnemittable()
    {
        var diagnostics = ReportSite("Weird", "");
        await Assert.That(diagnostics.Select(d => d.Id)).IsEquivalentTo(new[] { Id });
    }

    // ── harness ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One PICOSERDE006, reported through the public function the site calls, for the tests that
    /// ask what the DESCRIPTOR and the MESSAGE say rather than whether the site wires them up.
    /// </summary>
    private static ImmutableArray<Diagnostic> OneReportedDiagnostic() =>
        Report(spc =>
            GenInfrastructure.ReportMemberKindNotEmittable(
                spc,
                "json",
                "global::Fixtures.Holder",
                "Weird",
                "System.Object",
                Descriptors.UnemittableKind
            )
        );

    private static ImmutableArray<Diagnostic> ReportSite(string member, string kind) =>
        Report(spc =>
        {
            ReportOneSite.Invoke(
                null,
                [
                    spc,
                    new HashSet<string>(StringComparer.Ordinal),
                    "global::Fixtures.Holder",
                    member,
                    "System.Object",
                    kind,
                ]
            );
        });

    private static ImmutableArray<Diagnostic> Prescan(
        IEnumerable<TypeDescriptor> types,
        Dictionary<string, ImmutableArray<PropDescriptor>> nestedTypes,
        Dictionary<string, PropDescriptor> nestedDictTypes
    ) =>
        Report(spc =>
            PrescanSite.Invoke(null, [spc, types, nestedTypes, nestedDictTypes])
        );

    /// <summary>Runs <paramref name="report"/> inside a real generator and returns what it reported.</summary>
    private static ImmutableArray<Diagnostic> Report(Action<SourceProductionContext> report)
    {
        var compilation = CSharpCompilation.Create(
            "MemberKindGapAsm",
            [],
            RoslynProbe.AllPlatformReferences(
                typeof(JsonSerializer).Assembly,
                typeof(PicoSerDe.Core.SerializerExtensions).Assembly
            ),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(new ContextHarness(report).AsSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult().Results.SelectMany(r => r.Diagnostics)
            .ToImmutableArray();
    }
}
