using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PicoSerDe.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// I-3: make the illegal shape hard to write. The capability model (Task 1) gives the
/// generator one place to ask "can this format hold this shape"; this file is what stops
/// the next commit from bypassing it with a fresh <c>formatTag == "toml"</c>.
///
/// The model is <c>internal</c> on purpose (spec D5), so a type-level ban (an
/// <c>internal</c> property on the <c>public</c> <c>FormatConfig</c>) would be CS0053 or a
/// public-API change in five <c>api/*.Gen.public.txt</c> files. The guard is therefore a
/// static scan plus the structural invariants that a scan can check, and it never touches
/// production code:
///
/// <list type="number">
///   <item><b>No stray format literals</b> — a format tag may be spelled in the two table
///         files, in the out-of-scope anonymous-type pipeline, or on a line that carries
///         <c>// encoding, not capability</c>. Everything else is an offender.</item>
///   <item><b>No capability decision by literal comparison</b> — "does this tag equal/is
///         one of the literals" must go through <see cref="FormatCapabilities.Decide"/>,
///         <see cref="FormatCapabilities.DecideShape"/> or
///         <see cref="FormatCapabilities.AllowedDictKeyKinds"/>.</item>
///   <item><b>Registration is fourfold</b> — a new <see cref="Decision"/> value has to be
///         answered by <c>Rules</c> and <c>TargetRules</c> for all five formats, has to be
///         walked by a path (or be declared encoding-only), and has to be consulted by a
///         generator site unless it is on the reasoned exemption list below.</item>
/// </list>
///
/// Scope: <c>shared/Gen</c> only. That is where all five generators' capability logic
/// lives (the files are linked into five projects), so it is where a bypass can hide.
/// <c>tests/FormatCapabilities.Probe/Probe.cs</c> deliberately holds format strings as
/// probe DATA, not as predicates, and is outside this scan — widening the scan means
/// adding it to <see cref="OutOfScopeFiles"/> with a reason, not a bare skip.
/// </summary>
public class CapabilityGuardTests
{
    /// <summary>
    /// The marker that legitimizes a real format literal on its OWN physical line (the scan
    /// is line based). Today exactly one line carries it: the <c>byte[] → "bytes"</c>
    /// encoding choice in <c>TypeKindResolver</c>, which is not a capability question —
    /// msgpack can carry the same bytes as a binary value or as an array of integers.
    /// </summary>
    private const string EncodingMarker = "// encoding, not capability";

    /// <summary>Minimum length of a reason before it counts as documentation, not noise.</summary>
    private const int MinReasonLength = 40;

    /// <summary>The model itself: these files ARE the single source of truth.</summary>
    private static readonly (string File, string Reason)[] TableFiles =
    [
        ("FormatCapabilities.cs", "the model: Decision/Effect types and the entry points."),
        (
            "DecisionTables.cs",
            "the model: Rules, Paths, Facts, TargetRules and the dict-key limits."
        ),
    ];

    /// <summary>
    /// Files allowed to spell format tags for reasons outside I-3's scope. Each entry has to
    /// say WHY, otherwise the list turns into a place to dump offenders.
    /// </summary>
    private static readonly (string File, string Reason)[] OutOfScopeFiles =
    [
        (
            "AnonTypeHandler.cs",
            "a second extraction pipeline (anonymous types), out of I-3's scope in R1: its "
                + "literals name the writer/options types and gate SupportsAnonCollections "
                + "(PICOSERDE005). Converging it is R2 work; listing it here keeps the guard "
                + "from claiming coverage it does not have."
        ),
    ];

    /// <summary>
    /// Decision points that no generator site consults today, on purpose, WITH the reason.
    /// An entry without a reason is how a real bypass gets ignored, so the guard pins the
    /// list length (a fifth exemption has to be a visible edit) and requires every
    /// exemption to still be walked by a path (a dead cell is not an exception).
    /// </summary>
    private static readonly (Decision D, string Reason)[] UnwiredByDesign =
    [
        (
            Decision.RecursiveMutual,
            "R2 Task 1 landed the distinction (RecursionKind + RecursionClassifier), and both "
                + "recursion sites still ask Decision.RecursiveSelf for BOTH kinds: "
                + "RecursiveMutual x toml is Supported, so consuming it before the emitters can "
                + "close a mutual cycle turns today's dropped member into a table the TOML "
                + "Emitter writes as NotSupportedException (measured in R2 W1). The row is "
                + "live: it is walked by Paths[\"S11b\"], so DecideShape(\"S11b\") and the "
                + "README matrix read it. Flipping the sites is R2 Task 5."
        ),
        // ExtendedKindAsElement used to be exempt here with the reason "the extractor drops
        // it by an unconditional kind list, so there is no cell to ask". R2 Task 2 wired
        // that site to Decide(..., ExtendedKindAsElement) — which is what made S8 flip for
        // four formats — so the exemption is gone: the guard now proves the row is live.
        // DictValueCollection used to be exempt here with the reason "the whole row is
        // DropMember, so a site lookup would discard the answer". R2 Task 3 wired the
        // extractor's dict-value site to it (and the four formats learned the value
        // position), so the exemption is gone and the row is proven live: flipping a cell
        // back to DropMember now drops the member again.
        (
            Decision.NullableRefLeaf,
            "W3 R-5: the sites ask Decision.NullableValueLeaf on purpose. NullableRefLeaf is "
                + "DropElement in toml/yaml, and no member-level site can perform an "
                + "element-level drop - asking it there would turn S12 from 'member emitted, "
                + "element lost' into 'member dropped'. Consumed by DecideShape(\"S12\")."
        ),
    ];

    /// <summary>A format tag spelled as a string literal — the shape the tables replaced.</summary>
    private static readonly Regex FormatLiteral = new(
        "\"(json|msgpack|toml|yaml|ini)\"",
        RegexOptions.CultureInvariant
    );

    /// <summary>
    /// "does this format tag equal / is it one of the literals?" — the decision shape.
    /// Deliberately a narrow set of expressions: these are the variables the five
    /// generators and the shared infrastructure use for a format tag.
    /// </summary>
    private static readonly Regex FormatComparison = new(
        @"\b(format|formatTag|fmtTag|FormatTag)\b\s*(==|!=|is)\b",
        RegexOptions.CultureInvariant
    );

    // ── 1. literals ────────────────────────────────────────────────────────────────

    /// <summary>
    /// No format literal outside the tables, except on a line that documents itself as an
    /// encoding choice. This is the one that fails when someone re-adds
    /// <c>formatTag == "ini"</c> instead of asking <c>ObjectListInMember</c>.
    /// </summary>
    [Test]
    public async Task Format_literals_appear_only_in_the_tables_and_the_documented_encoding_line()
    {
        var offenders = new List<string>();
        var marked = new List<string>();

        foreach (var file in ScannedFiles())
        {
            var name = Path.GetFileName(file);
            foreach (var (lineNo, line) in LinesOf(file))
            {
                var (code, comment) = SplitCodeAndComment(line);
                if (!FormatLiteral.IsMatch(code))
                    continue;

                if (comment.Contains(EncodingMarker, StringComparison.Ordinal))
                {
                    marked.Add($"{name}: {code.Trim()}");
                    continue;
                }

                offenders.Add($"{name}:{lineNo}: {line.Trim()}");
            }
        }

        await Assert
            .That(offenders)
            .IsEmpty()
            .Because(
                "a format tag outside the tables is a capability decision that bypasses the "
                    + "single source of truth. Ask FormatCapabilities.Decide/DecideShape "
                    + "(adding a Decision value if the question is new), or - only for an "
                    + "encoding choice - put `"
                    + EncodingMarker
                    + "` on the same line. Offenders: "
                    + string.Join(" | ", offenders)
            );

        // The carve-out is not a loophole: exactly the byte[] choice carries the marker.
        await Assert
            .That(marked)
            .IsEquivalentTo(new[] { "TypeKindResolver.cs: format == \"msgpack\"" })
            .Because(
                "the encoding marker documents ONE site today (byte[] -> \"bytes\" is an "
                    + "encoding choice, both encodings express the same value). A second "
                    + "marked site has to be argued for here, which is the point: "
                    + string.Join(" | ", marked)
            );
    }

    // ── 2. decisions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The decision itself may not be made by literal: "can this format hold it?" is
    /// <see cref="FormatCapabilities"/>, and the dict-key limit is
    /// <see cref="FormatCapabilities.AllowedDictKeyKinds"/>.
    /// </summary>
    [Test]
    public async Task Capability_decisions_go_through_the_entry_points()
    {
        var offenders = new List<string>();

        foreach (var file in ScannedFiles())
        {
            var name = Path.GetFileName(file);
            foreach (var (lineNo, line) in LinesOf(file))
            {
                var (code, comment) = SplitCodeAndComment(line);
                // A comparison against a literal is the banned shape; a comparison against
                // something else (a variable, null) is not this guard's business.
                if (!FormatComparison.IsMatch(code) || !FormatLiteral.IsMatch(code))
                    continue;
                if (comment.Contains(EncodingMarker, StringComparison.Ordinal))
                    continue;

                offenders.Add($"{name}:{lineNo}: {code.Trim()}");
            }
        }

        await Assert
            .That(offenders)
            .IsEmpty()
            .Because(
                "a format-tag comparison IS a capability decision: it must come from "
                    + "DecisionTables.Rules via FormatCapabilities (or AllowedDictKeyKinds for "
                    + "the dict-key limit). Offenders: "
                    + string.Join(" | ", offenders)
            );
    }

    // ── 3. registration of a new decision point ────────────────────────────────────

    /// <summary>
    /// A new <see cref="Decision"/> value must be answerable for every format, in both
    /// tables. Without this the model throws at generation time, far away from the table
    /// edit that caused it.
    /// </summary>
    [Test]
    public async Task A_new_decision_point_is_answered_by_both_tables_for_every_format()
    {
        await AssertTheTablesAreNotVacuouslyEmpty();

        foreach (var d in Enum.GetValues<Decision>())
        foreach (var fmt in FormatCapabilities.AllFormatTags)
        {
            await Assert
                .That(FormatCapabilities.Rules.ContainsKey((fmt, d)))
                .IsTrue()
                .Because(
                    $"{d} x {fmt} is missing from Rules: every format has to answer every "
                        + "decision point (a missing entry is a bug, not 'supported')"
                );
            await Assert
                .That(DecisionTables.TargetRules.ContainsKey((fmt, d)))
                .IsTrue()
                .Because(
                    $"{d} x {fmt} is missing from TargetRules: the R2 column is derived from "
                        + "Rules, so a gap here means the derivation never saw the pair"
                );
        }
    }

    /// <summary>
    /// A new <see cref="Decision"/> value must also be walked by a path, or be declared
    /// encoding-only. This is the "in the table AND in a path" half of the invariant: a
    /// row no shape walks through can never be derived (it would be vacuous for R2), and a
    /// point declared encoding-only must not appear in a path (the derivation would skip it
    /// and never promote the cell).
    /// </summary>
    [Test]
    public async Task A_new_decision_point_is_walked_by_a_path_or_declared_encoding_only()
    {
        await AssertTheTablesAreNotVacuouslyEmpty();

        foreach (var d in Enum.GetValues<Decision>())
        {
            var shapes = FormatCapabilities.ShapesUsing(d);
            var encodingOnly = DecisionTables.EncodingOnlyDecisions.Contains(d);

            if (encodingOnly)
            {
                await Assert
                    .That(shapes)
                    .IsEmpty()
                    .Because(
                        $"{d} is declared encoding-only, so BuildTargetRules keeps today's "
                            + "value and never derives it. A path that walks it would make it "
                            + "both derived and exempt: pick one."
                    );
                continue;
            }

            await Assert
                .That(shapes)
                .IsNotEmpty()
                .Because(
                    $"{d} is on no path and is not in EncodingOnlyDecisions: no shape can "
                        + "reach it, so its TargetRules row cannot be derived and the model "
                        + "would promote it for free."
                );

            // ShapesByDecision is a DERIVED reverse index (DecisionTables). If it were ever
            // hand-written it would drift from Paths; pin the equivalence both ways.
            foreach (var shapeId in FormatCapabilities.AllShapeIds)
                await Assert
                    .That(shapes.Contains(shapeId))
                    .IsEqualTo(FormatCapabilities.Paths[shapeId].Contains(d))
                    .Because(
                        $"ShapesUsing({d}) disagrees with Paths[{shapeId}] - the reverse index "
                            + "must stay derived, never hand-written"
                    );
        }
    }

    // ── 4. wiring: the table must be read by somebody ──────────────────────────────

    /// <summary>
    /// The byte diff in Task 3 proves "the output did not change"; it cannot see "the site
    /// was rewired to the WRONG cell" or "the site was never rewired at all". This can:
    /// every decision point has to be consulted by a generator site, unless it is on the
    /// reasoned exemption list.
    /// </summary>
    [Test]
    public async Task Every_decision_point_is_consulted_by_a_generator_site_unless_documented()
    {
        // Pinned on purpose: a new exemption must be a visible edit in this file, argued
        // for in prose - not a quiet acceptance of a new unwired decision point. Two
        // today: R2 Task 1 wired RecursionMutual's classifier but not its sites, and
        // NullableRefLeaf is still an element-level answer no member site can ask. R2 Task 2
        // removed ExtendedKindAsElement and R2 Task 3 removed DictValueCollection from this
        // list, because both rows have a real site now.
        await Assert.That(UnwiredByDesign.Length).IsEqualTo(2);

        // Evidence is CODE, never prose (review I-2): the guard used to grep the whole
        // file text, so `// TODO ask Decision.X here` — a comment, i.e. no site at all —
        // was enough to make a decorative decision point look wired. Reading the SYNTAX
        // removes that whole class of false positive: a comment is trivia, a string is one
        // literal token, and `Decision.NullableValueLeafExtra` is a different name rather
        // than a prefix match.
        var consulted = ConsultedDecisionPoints();

        var unwired = Enum.GetValues<Decision>()
            .Where(d => !UnwiredByDesign.Any(e => e.D == d))
            .Where(d => !consulted.Contains(d.ToString()))
            .Select(d => d.ToString())
            .ToArray();

        await Assert
            .That(unwired)
            .IsEmpty()
            .Because(
                "these decision points are in the tables but no generator site asks for "
                    + "them, so their cells are decorative. Either wire the site or add a "
                    + "reasoned entry to UnwiredByDesign: "
                    + string.Join(", ", unwired)
            );

        foreach (var (d, reason) in UnwiredByDesign)
        {
            await Assert
                .That(reason.Length)
                .IsGreaterThan(MinReasonLength)
                .Because(
                    $"{d} is exempted from the wiring guard without a reason. An unexplained "
                        + "exemption is exactly how a real bypass survives review."
                );
            await Assert
                .That(FormatCapabilities.ShapesUsing(d).ToArray())
                .IsNotEmpty()
                .Because(
                    $"{d} is exempted from wiring AND walked by no path: nothing consumes it "
                        + "at all, so it is a dead cell rather than an exception."
                );
        }
    }

    // ── 5. the allow lists themselves ──────────────────────────────────────────────

    /// <summary>
    /// The allow lists may not rot: a renamed file would otherwise drop out of the scan
    /// silently, and a one-word "reason" would let the lists absorb anything.
    /// </summary>
    [Test]
    public async Task Every_allow_list_entry_carries_a_reason_and_names_a_real_file()
    {
        foreach (var (file, reason) in TableFiles.Concat(OutOfScopeFiles))
        {
            await Assert
                .That(File.Exists(Path.Combine(GenDir(), file)))
                .IsTrue()
                .Because(
                    $"{file} is on an allow list but does not exist in shared/Gen: a rename "
                        + "would silently widen (or void) the scan"
                );
            await Assert
                .That(reason.Length)
                .IsGreaterThan(MinReasonLength)
                .Because($"{file} is allowed without a documented reason");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Guards 3 and 4 are loops over the two tables, so an EMPTIED table would make them
    /// pass while scanning nothing — a green guard that checked no pair at all (spec
    /// review #3). Pin the width before looping: the numbers may grow (a sixth format, a
    /// new shape), never shrink.
    /// </summary>
    private static async Task AssertTheTablesAreNotVacuouslyEmpty()
    {
        await Assert
            .That(FormatCapabilities.AllFormatTags.Count)
            .IsGreaterThanOrEqualTo(5)
            .Because(
                "AllFormatTags drives the loops in guards 3 and 4: below five formats they "
                    + "silently check fewer pairs, and an empty one makes them vacuous"
            );
        await Assert
            .That(FormatCapabilities.AllShapeIds.Count)
            .IsGreaterThanOrEqualTo(14)
            .Because(
                "AllShapeIds drives the reverse-index comparison and the path walk: below "
                    + "fourteen shapes they check less than the spec's matrix"
            );
    }

    /// <summary>
    /// The decision points a generator site really consults, read from the SYNTAX of the
    /// scanned files: every <c>Decision.&lt;Name&gt;</c> member access that exists in code.
    /// A mention in a comment is trivia and a mention in a string is a literal, so neither
    /// can stand in for a site (review I-2).
    /// </summary>
    private static HashSet<string> ConsultedDecisionPoints()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in ScannedFiles())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                if (access.Expression is IdentifierNameSyntax { Identifier.Text: "Decision" })
                    names.Add(access.Name.Identifier.Text);
        }

        return names;
    }

    private static string GenDir() => Path.Combine(TestRepo.Root(), "shared", "Gen");

    /// <summary>Every <c>.cs</c> file in <c>shared/Gen</c>, minus the two table files.</summary>
    private static IEnumerable<string> ScannedFiles() =>
        Directory
            .EnumerateFiles(GenDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !TableFiles.Any(t => t.File == Path.GetFileName(f)))
            .Where(f => !OutOfScopeFiles.Any(t => t.File == Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static IEnumerable<(int LineNo, string Line)> LinesOf(string file) =>
        File.ReadAllLines(file).Select((line, i) => (LineNo: i + 1, Line: line));

    /// <summary>
    /// Split a source line into code and its trailing line comment, so a format tag that
    /// only appears in a comment (or in a doc comment) is not reported as code, while the
    /// marker - which IS a comment - stays available. <c>//</c> inside a string literal is
    /// left alone. Lines inside a multi-line raw string are scanned like any other, which
    /// only makes the guard slightly more lenient after a literal <c>//</c>.
    /// </summary>
    private static (string Code, string Comment) SplitCodeAndComment(string line)
    {
        var inString = false;
        var escaped = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                return (line[..i], line[i..]);
        }

        return (line, string.Empty);
    }
}
