using PicoSerDe.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The capability model (<c>shared/Gen/FormatCapabilities.cs</c> + <c>DecisionTables.cs</c>)
/// has to answer three different questions, and this file pins all three:
///
/// <list type="number">
///   <item><b>What does the generator do today?</b> — the 70 measured cells of the
///         current-state matrix (14 shapes × 5 formats) must be reproduced by
///         <see cref="FormatCapabilities.DecideShape"/> exactly. If a table edit moves a
///         cell, this test fails instead of the byte diff (Task 3) having to find it.</item>
///   <item><b>What can each format express?</b> — <see cref="FormatCapabilities.CanRepresent"/>
///         is the code form of spec §3; Task 2's probe measures the same thing
///         independently, so the two are cross-checked rather than trusted.</item>
///   <item><b>What is the R2 target?</b> — derived from the facts, never hand-copied, and
///         "supported exactly where every affected shape is representable".</item>
/// </list>
///
/// The dict-key limit is pinned behaviourally too — in BOTH directions: a kind that is
/// not in <see cref="FormatCapabilities.AllowedDictKeyKinds"/> must be rejected by every
/// format, and a kind that is there must be emitted outside INI. The generators have no
/// copy of that set any more, so the table is the only place a kind can be added.
/// </summary>
public class FormatCapabilitiesTests
{
    /// <summary>
    /// The id a membership drop carries when the format cannot express the shape
    /// (unrepresentable dict key, nested list in TOML, ...).
    /// </summary>
    private const string DictKeySkipId = "PICOSERDE004";

    // Current-state matrix (spec §3.1, baseline 84a9560; proven 70/70 by the W0 spike).
    // Supported = emitted, DropMember = the member disappears (⛔/⏸),
    // DropElement = the member is emitted but the element/value is lost (🐞).
    private static readonly (string Shape, string Fmt, Effect Expected)[] Observed =
    [
        ("S1", "json", Effect.Supported),
        ("S1", "msgpack", Effect.Supported),
        ("S1", "toml", Effect.Supported),
        ("S1", "yaml", Effect.Supported),
        ("S1", "ini", Effect.Supported),
        ("S2", "json", Effect.Supported),
        ("S2", "msgpack", Effect.Supported),
        ("S2", "toml", Effect.Supported),
        ("S2", "yaml", Effect.Supported),
        ("S2", "ini", Effect.DropMember),
        ("S3", "json", Effect.Supported),
        ("S3", "msgpack", Effect.Supported),
        ("S3", "toml", Effect.Supported),
        ("S3", "yaml", Effect.Supported),
        ("S3", "ini", Effect.DropMember),
        ("S4", "json", Effect.Supported),
        ("S4", "msgpack", Effect.Supported),
        ("S4", "toml", Effect.DropMember),
        ("S4", "yaml", Effect.Supported),
        ("S4", "ini", Effect.DropMember),
        // S5/S6/S12/S13 x yaml: R2 Task 4 gave the writer the null literal (WriteNull) and
        // the four nullable emit sites their null branch, so the four cells moved to
        // Supported. S5/S6/S13 x toml stay dropped (no null literal in TOML) and x ini stay
        // dropped (INI's empty value is indistinguishable from ""); S12 x toml keeps
        // DropElement (member emitted, element lost).
        ("S5", "json", Effect.Supported),
        ("S5", "msgpack", Effect.Supported),
        ("S5", "toml", Effect.DropMember),
        ("S5", "yaml", Effect.Supported),
        ("S5", "ini", Effect.DropMember),
        ("S6", "json", Effect.Supported),
        ("S6", "msgpack", Effect.Supported),
        ("S6", "toml", Effect.DropMember),
        ("S6", "yaml", Effect.Supported),
        ("S6", "ini", Effect.DropMember),
        // S7/S8 x {json,msgpack,toml,yaml}: R2 Task 2 implemented the extended-collection
        // family, so these eight cells moved to Supported. S7/S8 x ini stay dropped — ini
        // has no nesting (S8 is a format FACT) and its member position is R2 Task 6.
        ("S7", "json", Effect.Supported),
        ("S7", "msgpack", Effect.Supported),
        ("S7", "toml", Effect.Supported),
        ("S7", "yaml", Effect.Supported),
        ("S7", "ini", Effect.DropMember),
        ("S8", "json", Effect.Supported),
        ("S8", "msgpack", Effect.Supported),
        ("S8", "toml", Effect.Supported),
        ("S8", "yaml", Effect.Supported),
        ("S8", "ini", Effect.DropMember),
        ("S9", "json", Effect.Supported),
        ("S9", "msgpack", Effect.Supported),
        ("S9", "toml", Effect.Supported),
        ("S9", "yaml", Effect.Supported),
        ("S9", "ini", Effect.Supported),
        // S10 x {json,msgpack,toml,yaml}: R2 Task 3 implemented the dict-VALUE position, so
        // the four cells moved to Supported. S10 x ini stays dropped — its sections carry no
        // nesting inside a member, which is a format FACT (spec §3).
        ("S10", "json", Effect.Supported),
        ("S10", "msgpack", Effect.Supported),
        ("S10", "toml", Effect.Supported),
        ("S10", "yaml", Effect.Supported),
        ("S10", "ini", Effect.DropMember),
        // S11a/S11b x {toml,yaml}: R2 Task 5 taught the two section formats the cycle shapes
        // (TOML nested tables addressed by their dotted path, YAML nested mappings) and wired
        // both emit sites to the KIND's own row, so the three cells that were still dropped
        // moved to Supported — S11b x toml already said Supported, which was the lie the
        // single-row gate produced. ini keeps DropMember (flat sections: a format FACT).
        ("S11a", "json", Effect.Supported),
        ("S11a", "msgpack", Effect.Supported),
        ("S11a", "toml", Effect.Supported),
        ("S11a", "yaml", Effect.Supported),
        ("S11a", "ini", Effect.DropMember),
        ("S11b", "json", Effect.Supported),
        ("S11b", "msgpack", Effect.Supported),
        ("S11b", "toml", Effect.Supported),
        ("S11b", "yaml", Effect.Supported),
        ("S11b", "ini", Effect.DropMember),
        ("S12", "json", Effect.Supported),
        ("S12", "msgpack", Effect.Supported),
        ("S12", "toml", Effect.DropElement),
        ("S12", "yaml", Effect.Supported),
        ("S12", "ini", Effect.DropMember),
        ("S13", "json", Effect.Supported),
        ("S13", "msgpack", Effect.Supported),
        ("S13", "toml", Effect.DropMember),
        ("S13", "yaml", Effect.Supported),
        ("S13", "ini", Effect.DropMember),
    ];

    private static readonly Decision[] Decisions = Enum.GetValues<Decision>();

    [Test]
    public async Task Model_reproduces_all_70_observed_cells()
    {
        // 14 × 5 — a shape or a format going missing must not silently shrink the corpus.
        await Assert.That(Observed.Length).IsEqualTo(70);

        var wrong = Observed
            .Select(o => (o, actual: FormatCapabilities.DecideShape(o.Fmt, o.Shape)))
            .Where(x => x.actual != x.o.Expected)
            .Select(x => $"{x.o.Shape}x{x.o.Fmt}: expected {x.o.Expected}, model said {x.actual}")
            .ToArray();

        await Assert.That(wrong).IsEmpty().Because(string.Join(" | ", wrong));
    }

    [Test]
    public async Task Every_shape_has_a_decision_path()
    {
        // Two counts that drifted apart once already: the model count (Task 2 builds one
        // probe type per shape) and the path count.
        await Assert.That(FormatCapabilities.AllShapeIds.Count).IsEqualTo(14);
        await Assert.That(FormatCapabilities.Paths.Count).IsEqualTo(14);

        foreach (var shapeId in FormatCapabilities.AllShapeIds)
            await Assert
                .That(FormatCapabilities.Paths.ContainsKey(shapeId))
                .IsTrue()
                .Because(shapeId);

        // A decision point that no path walks through can never be reached — the only
        // legitimate ones are the encoding-only pair, which is declared as such.
        foreach (var d in Decisions)
        {
            if (DecisionTables.EncodingOnlyDecisions.Contains(d))
                continue;
            await Assert.That(FormatCapabilities.ShapesUsing(d)).IsNotEmpty().Because(d.ToString());
        }
    }

    /// <summary>
    /// I-7: every blocked decision must be able to answer its granularity. No decision
    /// point may resolve to a fourth state, and no (format, decision) pair may be missing
    /// from either table — a missing entry would throw at generation time, far from here.
    /// </summary>
    [Test]
    public async Task Every_blocked_decision_has_a_granularity()
    {
        foreach (var d in Decisions)
        foreach (var fmt in FormatCapabilities.AllFormatTags)
        {
            await Assert
                .That(FormatCapabilities.Rules.ContainsKey((fmt, d)))
                .IsTrue()
                .Because($"{d}x{fmt} missing from Rules");
            await Assert
                .That(DecisionTables.TargetRules.ContainsKey((fmt, d)))
                .IsTrue()
                .Because($"{d}x{fmt} missing from TargetRules");
        }

        foreach (var shapeId in FormatCapabilities.AllShapeIds)
        foreach (var fmt in FormatCapabilities.AllFormatTags)
        {
            var actual = FormatCapabilities.DecideShape(fmt, shapeId);
            await Assert
                .That(actual is Effect.Supported or Effect.DropMember or Effect.DropElement)
                .IsTrue()
                .Because($"{shapeId}x{fmt} must resolve to a granularity, got {actual}");
        }
    }

    /// <summary>
    /// The executable definition of the target table: a decision point is "supported" in
    /// R2 exactly when every shape that passes through it is representable in that format.
    /// </summary>
    [Test]
    public async Task Target_is_supported_exactly_where_every_affected_shape_is_representable()
    {
        foreach (var d in Decisions)
        foreach (var fmt in FormatCapabilities.AllFormatTags)
        {
            var target = FormatCapabilities.DecideTarget(fmt, d);
            var shapes = FormatCapabilities.ShapesUsing(d);

            if (DecisionTables.EncodingOnlyDecisions.Contains(d))
            {
                // Encoding, not capability: clearing these two would swap a json-specific
                // scalar/dict kind for a generic one and change no answer to "can this
                // format express the value". Their target is today's value ON PURPOSE.
                await Assert
                    .That(target)
                    .IsEqualTo(FormatCapabilities.Decide(fmt, d))
                    .Because($"{d}x{fmt} is encoding-only; the target must not move");
                continue;
            }

            // Otherwise the derivation would be vacuous and would "promote" the pair for
            // free. A new decision point must earn a path or be declared encoding-only.
            await Assert
                .That(shapes.Count)
                .IsGreaterThan(0)
                .Because($"{d} has no shape and is not declared encoding-only");

            var allRepresentable = shapes.All(s => FormatCapabilities.CanRepresent(fmt, s));
            if (allRepresentable)
                await Assert
                    .That(target)
                    .IsEqualTo(Effect.Supported)
                    .Because(
                        $"{d}x{fmt}: every shape using it is representable ({Joined(shapes)})"
                    );
            else
                // Not every affected shape is representable, so the decision point cannot
                // be opened: the target keeps today's effect. Note that "keeps today's
                // effect" is NOT the same as "is dropped" — NestedScalarContainer x toml is
                // Supported today and stays Supported, because the only shape it blocks
                // there (S6) is blocked by NullableValueLeaf, further down the same path.
                // Demanding a drop effect here would kill S3 (representable in TOML) to
                // satisfy a coarse decision point; the shape-level test below is what
                // pins the actual cells.
                await Assert
                    .That(target)
                    .IsEqualTo(FormatCapabilities.Decide(fmt, d))
                    .Because(
                        $"{d}x{fmt}: some shape using it is not representable ({Joined(shapes)}), so the target must not move"
                    );
        }
    }

    /// <summary>
    /// The target table must agree with the format facts (spec §3) cell by cell. The facts
    /// themselves are cross-checked by Task 2's independent probe — two evidence chains,
    /// not one table asserted against itself.
    /// </summary>
    [Test]
    public async Task Target_table_agrees_with_spec_section3()
    {
        foreach (var shapeId in FormatCapabilities.AllShapeIds)
        foreach (var fmt in FormatCapabilities.AllFormatTags)
        {
            var target = FormatCapabilities.DecideShapeTarget(fmt, shapeId);
            if (FormatCapabilities.CanRepresent(fmt, shapeId))
                await Assert
                    .That(target)
                    .IsEqualTo(Effect.Supported)
                    .Because($"{shapeId}x{fmt} is representable, so R2 must support it");
            else
                await Assert
                    .That(target)
                    .IsNotEqualTo(Effect.Supported)
                    .Because($"{shapeId}x{fmt} is not representable, so R2 must not claim it");
        }
    }

    /// <summary>
    /// The dict-key limit is pinned behaviourally: drive the INI generator with a dictionary
    /// whose key kind is (or is not) allowed and look at what the user gets.
    /// <para>
    /// The plan listed <c>int32</c> as accepted by INI; the code disagrees — INI reads a
    /// key as raw line text, so
    /// <see cref="FormatCapabilities.AllowedDictKeyKinds"/> gives it nothing but
    /// <c>string</c>, and the generator (which now asks that very method) says the same.
    /// </para>
    /// </summary>
    [Test]
    [Arguments("string", true)]
    [Arguments("int32", false)]
    [Arguments("int64", false)]
    public async Task Dict_key_kinds_behave_as_the_table_promises(string kind, bool iniAccepts)
    {
        var dictType = kind switch
        {
            "string" => "Dictionary<string, int>",
            "int32" => "Dictionary<int, int>",
            "int64" => "Dictionary<long, int>",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // The member is named "KeyedItems", not "V": the generated boilerplate always
        // contains "ReadValue", so a one-letter marker would assert nothing.
        var source = $$"""
            using PicoIni;

            [PicoIniSerializable]
            public partial class Doc
            {
                public System.Collections.Generic.{{dictType}} KeyedItems { get; set; } = new();
            }
            """;

        var (ids, generated, _) = SilentDropDiagnosticTests.RunIni(source);

        // The table must say the same thing the generator does.
        await Assert
            .That(FormatCapabilities.AllowedDictKeyKinds("ini").Contains(kind))
            .IsEqualTo(iniAccepts)
            .Because($"AllowedDictKeyKinds(ini) disagrees with the generator for {kind}");

        if (iniAccepts)
        {
            await Assert
                .That(generated)
                .Contains("KeyedItems")
                .Because($"{kind} keys are accepted by INI");
        }
        else
        {
            await Assert
                .That(generated)
                .DoesNotContain("KeyedItems")
                .Because($"{kind} keys must be rejected by INI");
            await Assert
                .That(ids)
                .IsNotEmpty()
                .Because("a rejected member must carry a diagnostic (R2)");
        }
    }

    /// <summary>
    /// Structural facts about the limit table that do not re-copy the 11 key kinds:
    /// INI is the only narrowing format, and "string" survives everywhere.
    /// </summary>
    [Test]
    public async Task Ini_is_the_only_format_that_narrows_the_dict_key_kinds()
    {
        var ini = FormatCapabilities.AllowedDictKeyKinds("ini");

        await Assert.That(ini.Count).IsEqualTo(1);
        await Assert.That(ini.Contains("string")).IsTrue();

        foreach (var fmt in FormatCapabilities.AllFormatTags)
        {
            await Assert
                .That(FormatCapabilities.AllowedDictKeyKinds(fmt).Contains("string"))
                .IsTrue()
                .Because($"{fmt} must accept string keys");

            if (fmt == "ini")
                continue;

            var kinds = FormatCapabilities.AllowedDictKeyKinds(fmt);
            await Assert.That(kinds.Count).IsGreaterThan(ini.Count).Because(fmt);
            foreach (var kind in ini)
                await Assert
                    .That(kinds.Contains(kind))
                    .IsTrue()
                    .Because($"{fmt} must accept {kind}");
        }
    }

    /// <summary>
    /// W1 review I-1, negative direction: a key kind the table does not know must be
    /// rejected by EVERY format. Widening <see cref="FormatCapabilities.AllowedDictKeyKinds"/>
    /// without the generators fails here, and so does widening the generators without the
    /// table — that drift is what the deleted hand-written predicate allowed.
    /// <para>
    /// It also pins HOW LOUD the rejection is, in all five formats: the extractor used to
    /// report a rejected dict key only from the format with the narrowest key set (INI) and
    /// let json/msgpack/toml/yaml drop the member silently (spec review D2). A rejected
    /// member must carry a diagnostic everywhere — this is the assertion that replaced
    /// "only INI reports".
    /// </para>
    /// </summary>
    [Test]
    public async Task Kind_absent_from_the_table_is_rejected_by_every_format()
    {
        // float32 is not one of the 11 key kinds the model knows
        // (DecisionTables.DictKeyKindsByFormat).
        var formats = new (
            string Tag,
            string Ns,
            string Attr,
            Func<string, (string[] Ids, string Generated, string[] Msgs)> Run
        )[]
        {
            ("json", "PicoJetson", "PicoJsonSerializable", SilentDropDiagnosticTests.RunJson),
            (
                "msgpack",
                "PicoMsgPack",
                "PicoMsgPackSerializable",
                SilentDropDiagnosticTests.RunMsgPack
            ),
            ("toml", "PicoToml", "PicoTomlSerializable", SilentDropDiagnosticTests.RunToml),
            ("yaml", "PicoYaml", "PicoYamlSerializable", SilentDropDiagnosticTests.RunYaml),
            ("ini", "PicoIni", "PicoIniSerializable", SilentDropDiagnosticTests.RunIni),
        };

        foreach (var (tag, ns, attr, run) in formats)
        {
            var source = $$"""
                using {{ns}};

                [{{attr}}]
                public partial class Doc
                {
                    public System.Collections.Generic.Dictionary<float, int> KeyedItems { get; set; } = new();
                }
                """;

            var (ids, generated, messages) = run(source);

            await Assert
                .That(FormatCapabilities.AllowedDictKeyKinds(tag).Contains("float32"))
                .IsFalse()
                .Because($"float32 must not be in the {tag} key set");
            await Assert
                .That(generated)
                .DoesNotContain("KeyedItems")
                .Because($"{tag} must reject a float key");
            await Assert
                .That(ids)
                .Contains(DictKeySkipId)
                .Because(
                    $"{tag} drops the member, so it must report it — a silent drop in any "
                        + $"format is the D2 gap (ids: {string.Join(",", ids)})"
                );
            await Assert
                .That(messages.Any(m => m.Contains("KeyedItems")))
                .IsTrue()
                .Because(
                    $"the diagnostic must name the member (messages: {string.Join(" | ", messages)})"
                );
        }
    }

    /// <summary>
    /// W1 review I-1, positive direction: a kind the table DOES hold is emitted outside
    /// INI — <c>long</c> (= int64) × json must produce the member. A missing member means
    /// the table and the generator disagree about the same set.
    /// </summary>
    [Test]
    public async Task Kind_in_the_table_is_emitted_outside_ini()
    {
        const string JsonLongKey = """
            using PicoJetson;

            [PicoJsonSerializable]
            public partial class Doc
            {
                public System.Collections.Generic.Dictionary<long, int> KeyedItems { get; set; } = new();
            }
            """;

        await Assert
            .That(FormatCapabilities.AllowedDictKeyKinds("json").Contains("int64"))
            .IsTrue();

        var (_, generated, _) = SilentDropDiagnosticTests.RunJson(JsonLongKey);
        await Assert
            .That(generated)
            .Contains("KeyedItems")
            .Because(
                "json emits int64 keys; a missing member means the table and the generator disagree"
            );
    }

    /// <summary>
    /// W1 review I-2: the else-branch of
    /// <see cref="Target_is_supported_exactly_where_every_affected_shape_is_representable"/>
    /// asserts <c>DecideTarget == Decide</c>, which <c>BuildTargetRules</c> guarantees by
    /// construction when nothing is representable — so it cannot notice a derivation that
    /// never ran. This test pins the DIFFERENCES instead: the (format, decision) pairs that
    /// are dropped today and Supported in the target. Replacing TargetRules with a copy of
    /// Rules goes red here immediately.
    /// <para>
    /// R2 Task 4 consumed the two yaml nullable entries this set used to carry, and R2 Task 5
    /// the three recursion entries: the cells are flipped, so "dropped today" no longer holds
    /// for them. The remaining entries are the debts the model still records (TOML's object
    /// container, ini's member positions).
    /// </para>
    /// </summary>
    [Test]
    public async Task Target_promotes_exactly_the_pairs_that_are_dropped_today()
    {
        (string Fmt, Decision D)[] expected =
        [
            // ExtendedKindResolves x {msgpack,toml,yaml} and ExtendedKindAsElement x
            // {json,msgpack,toml,yaml} used to sit here; R2 Task 2 flipped those eight
            // cells, so they are no longer "dropped today". ini keeps its entry (S7 x ini
            // is Task 6); ExtendedKindAsElement x ini was never promoted (S8 is a fact).
            ("ini", Decision.ExtendedKindResolves),
            ("toml", Decision.NestedObjectContainer),
            // NullableValueLeaf x yaml and NullableRefLeaf x yaml used to sit here; R2 Task 4
            // flipped those two cells, so they are no longer "dropped today".
            // DictValueCollection x {json,msgpack,toml,yaml} used to sit here; R2 Task 3
            // flipped those four cells, so they are no longer "dropped today". ini keeps its
            // entry because S10 x ini is a format FACT (no nesting inside a member).
            // RecursiveSelf x {toml,yaml} and RecursiveMutual x yaml used to sit here; R2
            // Task 5 flipped those three cells, so the recursion row is no longer a debt in
            // any format that can express it.
        ];

        var actual = (
            from d in Decisions
            from f in FormatCapabilities.AllFormatTags
            where
                FormatCapabilities.Decide(f, d) != Effect.Supported
                && FormatCapabilities.DecideTarget(f, d) == Effect.Supported
            select (Fmt: f, D: d)
        ).ToArray();

        var missing = expected.Except(actual).ToArray();
        var extra = actual.Except(expected).ToArray();

        await Assert
            .That(missing)
            .IsEmpty()
            .Because(
                "these pairs are dropped today and representable in R2, so the derivation "
                    + "must promote them: "
                    + string.Join(", ", missing.Select(x => $"{x.D}x{x.Fmt}"))
            );
        await Assert
            .That(extra)
            .IsEmpty()
            .Because(
                "the derivation promoted pairs it must not: "
                    + string.Join(", ", extra.Select(x => $"{x.D}x{x.Fmt}"))
            );

        // Non-vacuous by construction, but state it: at least one pair per direction.
        await Assert
            .That(FormatCapabilities.Decide("ini", Decision.ObjectListInMember))
            .IsEqualTo(Effect.DropMember);
        await Assert
            .That(FormatCapabilities.DecideTarget("ini", Decision.ObjectListInMember))
            .IsEqualTo(Effect.DropMember)
            .Because("ini cannot express S2, so the target must stay blocked");
    }

    // ── README matrix (R1 exit criterion: the README is generated from the table) ────────

    /// <summary>
    /// The README's limits matrix has <b>five</b> rows and <b>no S-id column</b>, and every
    /// cell carries an annotation (<c>✅ [[Table]]</c>, <c>✅ block sequence of mappings</c>,
    /// <c>❌ see ¹</c>). So a row cannot be compared whole and cannot be compared by suffix;
    /// this fixture is the missing link — the description prefix of each README row to the
    /// shapeId it documents. Order matters: it is the README's own order, so a reordered
    /// matrix shows up as a mismatch instead of passing by accident.
    /// <para>
    /// The README documents <b>TODAY's</b> limits, so the expected tokens come from the
    /// current-state table via <see cref="FormatCapabilities.MatrixCells"/>; deriving them
    /// from <c>DecideShapeTarget</c> would write R2's capabilities into an R1 document
    /// (spec C2).
    /// </para>
    /// <para>
    /// A row whose cells have ALREADY moved in R2 carries its own frozen token string instead
    /// (<c>R1ReadmeTokens</c>), because the README is a RELEASE document: it describes the
    /// published release, and re-deriving it from a table that R2 has moved would demand a
    /// README edit in every task that flips a cell. R2 Task 5 flipped S11a in toml/yaml and
    /// deliberately did <b>not</b> touch the README — the matrix is regenerated from the spec
    /// §6 terminal table by R2 Task 7, which also removes the frozen value here. Until then
    /// this fixture still catches an accidental README edit, and it says so out loud.
    /// </para>
    /// </summary>
    private static readonly (string Prefix, string ShapeId, string? R1ReadmeTokens)[] ReadmeRows =
    [
        ("| `List<Dto>`", "S2", null),
        ("| `List<List<int>>`", "S3", null),
        ("| `List<List<Dto>>`", "S4", null),
        ("| `List<List<int?>>`", "S6", null),
        // R2 Task 5 turned S11a into ✅✅✅✅❌ (toml/yaml now hold a cycle). The README still
        // documents the R1 release: ❌ for toml and yaml.
        ("| Recursive member", "S11a", "✅✅❌❌❌"),
    ];

    /// <summary>
    /// The exit criterion "README's limits matrix is generated from the table", made
    /// executable without regenerating the README: per row, compare the <b>verdict glyph</b>
    /// that starts each of the five format cells with the five tokens of
    /// <see cref="FormatCapabilities.MatrixCells"/>. The annotations stay untouched — they
    /// are the part of a cell a reader actually needs (which diagnostic, which syntax).
    /// </summary>
    [Test]
    public async Task Readme_matrix_cells_match_the_tables()
    {
        var matrix = ReadmeMatrix();
        var header = Cells(matrix[0]);
        var dataRows = matrix.Skip(2).ToArray();

        // Row set: the README may not gain a limits row that this fixture does not cover —
        // an uncovered row is an unchecked claim, not a smaller test.
        await Assert
            .That(dataRows.Length)
            .IsEqualTo(ReadmeRows.Length)
            .Because(
                "the README matrix gained or lost a row: extend ReadmeRows (prefix → shapeId) "
                    + "so the new row is checked too"
            );

        // Column order: MatrixCells emits its tokens in AllFormatTags order, so a reordered
        // README column would compare a cell against the wrong format and still pass.
        await Assert
            .That(string.Join("|", header.Skip(1).Select(c => c.ToLowerInvariant())))
            .IsEqualTo(string.Join("|", FormatCapabilities.AllFormatTags))
            .Because(
                "the matrix columns must be in AllFormatTags order: "
                    + string.Join(", ", FormatCapabilities.AllFormatTags)
            );

        foreach (var (prefix, shapeId, r1Tokens) in ReadmeRows)
        {
            var row = dataRows.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
            await Assert
                .That(row)
                .IsNotNull()
                .Because($"README has no row for {shapeId}; expected one starting with '{prefix}'");

            // Cells 1..5 are the formats, cell 0 is the shape description. Only the first
            // character of a cell is a verdict — the rest is the annotation.
            var verdicts = string.Concat(
                Cells(row!).Skip(1).Take(5).Select(c => c.Length == 0 ? '?' : c[0])
            );
            var expected = r1Tokens ?? FormatCapabilities.MatrixCells(shapeId);

            await Assert
                .That(verdicts)
                .IsEqualTo(expected)
                .Because(
                    $"README's {shapeId} row and the current-state tables disagree: cells "
                        + $"'{verdicts}' vs tables '{expected}' (column order "
                        + string.Join(", ", FormatCapabilities.AllFormatTags)
                        + ")"
                        + (
                            r1Tokens is null
                                ? ""
                                : " [frozen R1 tokens: R2 Task 7 regenerates this row from spec §6]"
                        )
                );
        }
    }

    /// <summary>
    /// The limits matrix as markdown rows: the header line plus everything up to the first
    /// non-<c>|</c> line, so the check cannot accidentally match a different table.
    /// </summary>
    private static string[] ReadmeMatrix()
    {
        var lines = File.ReadAllLines(Path.Combine(TestRepo.Root(), "README.md"));
        var headerAt = Array.FindIndex(
            lines,
            l => l.StartsWith("| Shape (C#)", StringComparison.Ordinal)
        );

        if (headerAt < 0)
            throw new InvalidOperationException("README.md has no '| Shape (C#)' limits matrix");

        return lines.Skip(headerAt).TakeWhile(l => l.StartsWith('|')).ToArray();
    }

    /// <summary>The trimmed cells of one markdown table row (the outer pipes drop out).</summary>
    private static string[] Cells(string row) =>
        row.Split('|').Skip(1).SkipLast(1).Select(c => c.Trim()).ToArray();

    private static string Joined(IReadOnlyList<string> shapes) =>
        shapes.Count == 0 ? "<none>" : string.Join(", ", shapes);
}
