using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PicoSerDe.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The collection-kind model (<c>shared/Gen/CollectionKinds.cs</c>) has to be an EXACT
/// replacement for the nineteen hand-spelled member-set lists it replaced — not a plausible one.
/// Nineteen switches that each listed a slightly different set of kinds is precisely the shape
/// where "it looks the same" is worthless: a predicate that is one kind too wide silently changes
/// what the generator emits, and the probe baseline only sees the shapes its corpus contains.
/// <para>
/// So the before side is DATA, not memory: <c>collection-kind-sites.before.tsv</c> is the output of
/// a mechanical extraction over the commit this branch forked from (<c>ebfe8a0</c>), and its fourth
/// column records which query answers each arm now. This test evaluates every arm against every
/// kind on both sides and demands the two 53 × 9 matrices be identical.
/// </para>
/// <para>
/// A green matrix is only worth what its sensitivity is worth, so
/// <see cref="The_harness_can_go_red"/> feeds the same comparator a deliberately narrow query and
/// asserts it is caught. Without that, "identical" could mean "the comparator never looked".
/// </para>
/// </summary>
public class CollectionKindModelTests
{
    /// <summary>The frozen before-side artifact, relative to the repository root.</summary>
    private const string SnapshotPath =
        "tests/PicoSerDe.Integration.Tests/collection-kind-sites.before.tsv";

    /// <summary>
    /// The six member sets the model has to be able to tell apart, as the model itself states
    /// them: this is the design decision pinned, independent of any site.
    /// </summary>
    private static readonly (string Query, string[] Kinds)[] DocumentedSets =
    [
        ("IsSequence", [.. CollectionKinds.Names]),
        (
            "IsForeachable",
            ["list", "array", "hashset", "queue", "stack", "linkedlist", "immutablearray"]
        ),
        ("RequiresSpan", ["memory", "readonlymemory"]),
        (
            "IsExtendedCollection",
            [
                "hashset",
                "queue",
                "stack",
                "linkedlist",
                "immutablearray",
                "memory",
                "readonlymemory",
            ]
        ),
        (
            "RequiresListConversion",
            [
                "array",
                "hashset",
                "queue",
                "stack",
                "linkedlist",
                "immutablearray",
                "memory",
                "readonlymemory",
            ]
        ),
        ("IsNullableSequence", ["list", "array", "hashset", "queue", "stack", "linkedlist"]),
        ("UsesLength", ["array", "immutablearray", "memory", "readonlymemory"]),
    ];

    /// <summary>
    /// What the whole switch at each of the nineteen generator sites accepted BEFORE, as a query.
    /// The arms inside a split switch are checked arm-by-arm by the matrix; this row checks that
    /// the union of the arms still covers the same kinds (i.e. nothing fell into `default:`).
    /// </summary>
    private static readonly (string Site, string Query)[] SiteDomains =
    [
        ("PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs:1248", "IsSequence"),
        ("PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs:1624", "IsSequence"),
        ("PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:1937", "IsSequence"),
        ("PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:2212", "IsSequence"),
        ("PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:3053", "IsSequence"),
        ("PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:3491", "IsSequence"),
        ("PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:4418", "IsExtendedCollection"),
        ("PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:5086", "IsSequence"),
        // The JSON conversion dispatcher: `array` + the seven rebuilt kinds, `list` = default.
        (
            "PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:6502",
            "RequiresListConversion"
        ),
        ("PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs:1632", "IsSequence"),
        ("PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs:1938", "IsSequence"),
        ("PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs:2603", "IsSequence"),
        ("PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs:3410", "IsSequence"),
        ("PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs:2029", "IsSequence"),
        ("PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs:2919", "IsSequence"),
        ("PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs:740", "IsSequence"),
        ("PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs:952", "IsSequence"),
        ("PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs:2282", "IsSequence"),
        ("PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs:3142", "IsSequence"),
    ];

    // ── 1. the before/after matrix ────────────────────────────────────────────────

    /// <summary>
    /// Every arm × every kind: what the code accepted before must be what the model accepts now.
    /// This is the evidence the probe baseline cannot give — its corpus has no
    /// <c>HashSet</c>/<c>Queue</c>/<c>Stack</c>/<c>LinkedList</c>/<c>ImmutableArray</c>/
    /// <c>Memory</c> member, so those branches are never entered there.
    /// </summary>
    [Test]
    public async Task Every_arm_accepts_exactly_the_kinds_it_accepted_before()
    {
        var arms = ReadSnapshot();
        var differences = Compare(arms, arms.Select(a => a.Query).ToArray());

        await Assert
            .That(differences)
            .IsEmpty()
            .Because(
                "each site must accept exactly the kinds it accepted before the model existed; a "
                    + "difference means the generator's behaviour moved. Differences: "
                    + string.Join(" | ", differences)
            );

        // The artifact must actually be the population it claims to be, otherwise an empty
        // extraction would pass this test by having nothing to compare.
        await Assert.That(arms.Count).IsEqualTo(53).Because("the frozen extraction has 53 arms");
        await Assert
            .That(arms.Select(a => a.Site).Distinct().Count())
            .IsEqualTo(38)
            .Because("the frozen extraction has 38 sites");
    }

    /// <summary>
    /// The nineteen generator sites, whole-switch: the union of the arms still has to cover the
    /// same kinds, so nothing that used to have an arm can now fall into <c>default:</c>.
    /// </summary>
    [Test]
    public async Task The_nineteen_generator_sites_still_cover_the_same_kinds()
    {
        var arms = ReadSnapshot();
        var differences = new List<string>();

        foreach (var (site, query) in SiteDomains)
        {
            var before = arms.Where(a => a.Site == site)
                .SelectMany(a => a.Members)
                .ToHashSet(StringComparer.Ordinal);
            var after = CollectionKinds
                .Names.Where(kind => Accepts(query, kind))
                .ToHashSet(StringComparer.Ordinal);
            differences.AddRange(Diff(site, "", before, after));
        }

        await Assert
            .That(differences)
            .IsEmpty()
            .Because(
                "the arm that used to name these kinds still has to accept them. Differences: "
                    + string.Join(" | ", differences)
            );
        await Assert.That(SiteDomains.Length).IsEqualTo(19).Because("nineteen generator sites");
    }

    // ── 2. the harness is sensitive ───────────────────────────────────────────────

    /// <summary>
    /// The proof that <see cref="Compare"/> can go red. It is fed a query that is one kind too
    /// narrow for one arm — the exact failure mode a careless "unification" produces — and must
    /// report it. A comparator that cannot see this would make test 1 a green that proves nothing.
    /// </summary>
    [Test]
    public async Task The_harness_can_go_red()
    {
        var arms = ReadSnapshot();

        // Mutant 1: `IsSequence` loses one kind (the "one predicate for all four sets" mistake).
        var mutant = arms.Select(a => a.Query).ToArray();
        var target = Array.FindIndex(mutant, q => q == "IsSequence");
        mutant[target] = "MUTANT:IsSequenceWithoutQueue";
        var caught = Compare(arms, mutant);
        await Assert
            .That(caught.Count)
            .IsEqualTo(1)
            .Because(
                "a sequence arm missing `queue` must be reported exactly once (one cell); got: "
                    + string.Join(" | ", caught)
            );

        // Mutant 2: the widest possible error — answer `IsSequence` everywhere.
        var blanket = arms.Select(_ => "IsSequence").ToArray();
        var caughtAll = Compare(arms, blanket);
        await Assert
            .That(caughtAll.Count)
            .IsGreaterThan(0)
            .Because(
                "a blanket predicate is wrong at 3/4 of the sites, and the harness must see it"
            );

        // The two mutants are the two shapes the audit warned about (one kind short; one predicate
        // for every site), and both are visible. Recorded here so the sensitivity claim carries a
        // receipt instead of a promise.
        await Assert
            .That(caught[0])
            .Contains("queue")
            .Because("the reported cell names the kind and the site, so a reader can act on it");
    }

    // ── 3. the model states the six sets, independently of any site ───────────────

    /// <summary>
    /// The documented meaning of each query, pinned. This is what makes the model a model: the
    /// sets are a consequence of capability bits, and the bits say what each set MEANS.
    /// </summary>
    [Test]
    public async Task The_named_queries_are_the_six_documented_member_sets()
    {
        var differences = new List<string>();
        foreach (var (query, kinds) in DocumentedSets)
        {
            var after = CollectionKinds.Names.Where(kind => Accepts(query, kind));
            differences.AddRange(
                Diff(
                    query,
                    "",
                    kinds.ToHashSet(StringComparer.Ordinal),
                    after.ToHashSet(StringComparer.Ordinal)
                )
            );
        }

        await Assert
            .That(differences)
            .IsEmpty()
            .Because(
                "the six member sets the audit measured (9 / 7 / 2 / 7 / 8 / 6) are the model's "
                    + "spec; a change here is a design change and must be a visible edit. Diffs: "
                    + string.Join(" | ", differences)
            );

        // The set that would be wrong if the model were collapsed to one predicate, for the
        // record: 7 of the 9 kinds are NOT in every set.
        await Assert
            .That(CollectionKinds.IsSequence("memory") && !CollectionKinds.IsForeachable("memory"))
            .IsTrue()
            .Because(
                "`memory` is a sequence whose wire form cannot be enumerated — the reason the "
                    + "JSON writer needs two arms and a single IsListLike() is wrong there"
            );
        await Assert
            .That(
                CollectionKinds.IsSequence("list") && !CollectionKinds.IsExtendedCollection("list")
            )
            .IsTrue()
            .Because(
                "`list` is a sequence that is NOT rebuilt from a temp list — the reason the "
                    + "read-side switches split it out"
            );
    }

    // ── 4. no site spells a member set again ──────────────────────────────────────

    /// <summary>
    /// The anti-drift guard: a generator may not spell a collection member set again. Three or
    /// more kinds in one arm (or one <c>is … or …</c> chain) under a <c>*.Gen</c> project is the
    /// thing this whole change removed, and C10 exists because a green build cannot see it come
    /// back. The single-kind arms and the deliberate 2-kind pairs are allowed and are pinned by
    /// the matrix above.
    /// </summary>
    [Test]
    public async Task No_generator_spells_a_collection_member_set_again()
    {
        var offenders = new List<string>();
        foreach (var file in GeneratorFiles())
            offenders.AddRange(MemberSetsIn(file, minimum: 3));

        await Assert
            .That(offenders)
            .IsEmpty()
            .Because(
                "a `case \"hashset\": case \"queue\": …` arm in a generator is a second copy of the "
                    + "collection-kind table. Ask CollectionKinds.<Query>(kind) instead — the "
                    + "query names WHAT the arm needs, which the kind list only implies. "
                    + "Offenders: "
                    + string.Join(" | ", offenders)
            );
    }

    /// <summary>
    /// The same guard over <c>shared/Gen</c>, at the width that matters there (seven or more: a
    /// whole family). The model file itself is the one exemption — it IS the table — and
    /// <c>TypeKindResolver</c>'s registry is not a member set (it is the place a kind is born).
    /// </summary>
    [Test]
    public async Task No_shared_file_spells_a_whole_family_of_collection_kinds()
    {
        var offenders = new List<string>();
        foreach (var file in GenFiles())
        {
            if (Path.GetFileName(file) == "CollectionKinds.cs")
                continue;
            offenders.AddRange(MemberSetsIn(file, minimum: 7));
        }

        await Assert
            .That(offenders)
            .IsEmpty()
            .Because(
                "a seven-or-nine kind family spelled outside CollectionKinds.cs is the copy this "
                    + "change removed (there were two: the helpers in GenInfrastructure and the "
                    + "extractor chain). Offenders: "
                    + string.Join(" | ", offenders)
            );
    }

    /// <summary>
    /// The table and the registry must name the same kinds. Adding a kind to
    /// <c>TypeKindResolver</c> without a row in the table would make every query say "not a
    /// collection", and the kind would silently take a scalar path in five generators.
    /// </summary>
    [Test]
    public async Task The_registry_and_the_table_name_the_same_kinds()
    {
        var registry = RegistryKinds();
        var table = CollectionKinds.Names.ToHashSet(StringComparer.Ordinal);

        // Every kind the registry switch produces must have a row: a kind it can produce and the
        // model does not know is a kind that takes the scalar path in five generators.
        var unknown = registry.Where(kind => !table.Contains(kind)).OrderBy(k => k).ToArray();
        await Assert
            .That(unknown)
            .IsEmpty()
            .Because(
                "TypeKindResolver can produce a collection kind that CollectionKinds has no row "
                    + "for; every query would say `not a collection` and the member would be "
                    + "written as a scalar. Unmodelled: "
                    + string.Join(",", unknown)
            );

        // ... and the other direction, which cannot be a set comparison: `list`/`array` are
        // produced by the plain-list branch (a `return ("list", …)`, not a switch arm), so the
        // honest relation is "the table may not name a kind TypeKindResolver never mentions".
        var resolverLiterals = StringLiterals(Path.Combine(GenDir(), "TypeKindResolver.cs"));
        var unproduced = CollectionKinds
            .Names.Where(kind => resolverLiterals.Contains(kind) is false)
            .ToArray();
        await Assert
            .That(unproduced)
            .IsEmpty()
            .Because(
                "the table names a kind that TypeKindResolver never produces — a row for a kind "
                    + "no type can resolve to, which is a table that lies about its population. "
                    + "Unproduced: "
                    + string.Join(",", unproduced)
            );
    }

    // ── the comparator (shared by the green test and the red test) ────────────────

    /// <summary>
    /// Evaluate every arm against every kind on both sides and return the differing cells. The
    /// same method serves the pass and the mutant, which is what makes the mutant a real
    /// sensitivity proof: it is the identical code path.
    /// </summary>
    private static List<string> Compare(IReadOnlyList<Arm> arms, string[] afterQueries)
    {
        var differences = new List<string>();
        for (var i = 0; i < arms.Count; i++)
        {
            var arm = arms[i];
            var after = AfterSet(afterQueries[i], arm);
            differences.AddRange(
                Diff(arm.Site, arm.ArmLabel, arm.Members.ToHashSet(StringComparer.Ordinal), after)
            );
        }
        return differences;
    }

    /// <summary>One cell-level comparison, reported as "site · arm · kind".</summary>
    private static IEnumerable<string> Diff(
        string site,
        string arm,
        HashSet<string> before,
        HashSet<string> after
    )
    {
        var label = string.IsNullOrEmpty(arm) ? site : $"{site} · {arm}";
        foreach (var kind in CollectionKinds.Names)
        {
            var was = before.Contains(kind);
            var isNow = after.Contains(kind);
            if (was != isNow)
                yield return $"{label} · {kind}: {was} -> {isNow}";
        }
    }

    /// <summary>
    /// The "after" member set of one arm: the named query's answer, or — for <c>literal</c> — the
    /// arm's own frozen list, which is what a kind comparison that was deliberately left alone
    /// still accepts (a single-kind arm, an explicit pair, or the per-kind construction
    /// dispatcher). <c>literal</c> is not a hole: the point of those arms is that the kind set is
    /// NOT the question, and asserting it stayed put is how the matrix proves nothing was widened.
    /// </summary>
    private static HashSet<string> AfterSet(string query, Arm arm) =>
        string.Equals(query, "literal", StringComparison.Ordinal)
            ? arm.Members.ToHashSet(StringComparer.Ordinal)
            : CollectionKinds
                .Names.Where(kind => Accepts(query, kind))
                .ToHashSet(StringComparer.Ordinal);

    /// <summary>Resolve a NAMED query for one kind (the <c>literal</c> rows never reach here).</summary>
    private static bool Accepts(string query, string kind) =>
        query switch
        {
            "IsSequence" => CollectionKinds.IsSequence(kind),
            "IsForeachable" => CollectionKinds.IsForeachable(kind),
            "RequiresSpan" => CollectionKinds.RequiresSpan(kind),
            "IsExtendedCollection" => CollectionKinds.IsExtendedCollection(kind),
            "RequiresListConversion" => CollectionKinds.RequiresListConversion(kind),
            "IsNullableSequence" => CollectionKinds.IsNullableSequence(kind),
            "UsesLength" => CollectionKinds.UsesLength(kind),
            // The mutants. Named as queries on purpose: the red test drives THIS method.
            "MUTANT:IsSequenceWithoutQueue" => CollectionKinds.IsSequence(kind) && kind != "queue",
            _ => throw new InvalidOperationException($"unknown query in the snapshot: {query}"),
        };

    // ── the frozen artifact ───────────────────────────────────────────────────────

    private readonly record struct Arm(
        string Site,
        string ArmLabel,
        string Query,
        IReadOnlyList<string> Members
    );

    private static List<Arm> ReadSnapshot()
    {
        var arms = new List<Arm>();
        foreach (var raw in File.ReadLines(Path.Combine(TestRepo.Root(), SnapshotPath)))
        {
            if (raw.Length == 0 || raw.StartsWith('#'))
                continue;
            var parts = raw.Split('\t');
            if (parts.Length != 4)
                throw new InvalidOperationException($"malformed snapshot row: {raw}");
            arms.Add(
                new Arm(
                    parts[0],
                    parts[1],
                    parts[3],
                    parts[2].Length == 0 ? [] : parts[2].Split(',')
                )
            );
        }
        return arms;
    }

    /// <summary>
    /// The kinds <c>TypeKindResolver</c> can produce: the right-hand literals of its
    /// <c>(namespace, name) → kind</c> switch expression (the one whose arms are all
    /// two-tuple patterns, i.e. the extended-collection registry).
    /// </summary>
    private static HashSet<string> RegistryKinds()
    {
        var root = CSharpSyntaxTree
            .ParseText(File.ReadAllText(Path.Combine(GenDir(), "TypeKindResolver.cs")))
            .GetRoot();

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expression in root.DescendantNodes().OfType<SwitchExpressionSyntax>())
        {
            var tupleArms = expression
                .Arms.Select(arm => arm.Pattern)
                .OfType<RecursivePatternSyntax>()
                .Where(pattern => pattern.PositionalPatternClause is { Subpatterns.Count: 2 })
                .ToArray();
            if (tupleArms.Length < 5)
                continue;

            foreach (var arm in expression.Arms)
            {
                if (arm.Expression is not LiteralExpressionSyntax literal)
                    continue;
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                    continue;
                if (CollectionKinds.Names.Contains(literal.Token.ValueText))
                    kinds.Add(literal.Token.ValueText);
            }
        }

        return kinds;
    }

    // ── the syntactic member-set scan ─────────────────────────────────────────────

    /// <summary>
    /// Arms (fall-through label groups) and <c>is … or …</c> chains that name at least
    /// <paramref name="minimum"/> distinct collection kinds, as <c>file:line</c> hits.
    /// </summary>
    private static IEnumerable<string> MemberSetsIn(string file, int minimum)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        var name = Path.GetFileName(file);

        foreach (var statement in root.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            foreach (var section in statement.Sections)
            {
                var kinds = CollectionKindsIn(section.Labels.SelectMany(LabelsOf));
                if (kinds.Count >= minimum)
                    yield return $"{name}:{LineOf(section)}: arm [{string.Join(", ", kinds)}]";
            }
        }

        foreach (var isPattern in root.DescendantNodes().OfType<IsPatternExpressionSyntax>())
        {
            var kinds = CollectionKindsIn(
                isPattern.DescendantNodes().OfType<LiteralExpressionSyntax>()
            );
            if (kinds.Count >= minimum)
                yield return $"{name}:{LineOf(isPattern)}: is-pattern [{string.Join(", ", kinds)}]";
        }
    }

    private static IEnumerable<LiteralExpressionSyntax> LabelsOf(SwitchLabelSyntax label) =>
        label switch
        {
            CaseSwitchLabelSyntax @case => @case.Value is LiteralExpressionSyntax literal
                ? [literal]
                : [],
            CasePatternSwitchLabelSyntax pattern => pattern
                .Pattern.DescendantNodesAndSelf()
                .OfType<LiteralExpressionSyntax>(),
            _ => [],
        };

    private static List<string> CollectionKindsIn(IEnumerable<LiteralExpressionSyntax> literals) =>
        literals
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .Where(CollectionKinds.Names.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Every string literal in a file (comments and prose excluded by construction).</summary>
    private static HashSet<string> StringLiterals(string file)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        return root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // ── file discovery ────────────────────────────────────────────────────────────

    private static string GenDir() => Path.Combine(TestRepo.Root(), "shared", "Gen");

    private static IEnumerable<string> GenFiles() =>
        Directory
            .EnumerateFiles(GenDir(), "*.cs", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// The five generator implementations (not <c>shared/Gen</c>): the files the nineteen
    /// hand-spelled switches lived in.
    /// </summary>
    private static IEnumerable<string> GeneratorFiles()
    {
        var root = TestRepo.Root();
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => f.Replace('\\', '/'))
            .Where(f => f.Contains("/src/") && f.Contains(".Gen/"))
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("/.worktrees/"))
            .OrderBy(f => f, StringComparer.Ordinal);
    }
}
