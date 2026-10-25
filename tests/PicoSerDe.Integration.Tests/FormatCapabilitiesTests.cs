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
        ("S5", "json", Effect.Supported),
        ("S5", "msgpack", Effect.Supported),
        ("S5", "toml", Effect.DropMember),
        ("S5", "yaml", Effect.DropMember),
        ("S5", "ini", Effect.DropMember),
        ("S6", "json", Effect.Supported),
        ("S6", "msgpack", Effect.Supported),
        ("S6", "toml", Effect.DropMember),
        ("S6", "yaml", Effect.DropMember),
        ("S6", "ini", Effect.DropMember),
        ("S7", "json", Effect.Supported),
        ("S7", "msgpack", Effect.DropMember),
        ("S7", "toml", Effect.DropMember),
        ("S7", "yaml", Effect.DropMember),
        ("S7", "ini", Effect.DropMember),
        ("S8", "json", Effect.DropMember),
        ("S8", "msgpack", Effect.DropMember),
        ("S8", "toml", Effect.DropMember),
        ("S8", "yaml", Effect.DropMember),
        ("S8", "ini", Effect.DropMember),
        ("S9", "json", Effect.Supported),
        ("S9", "msgpack", Effect.Supported),
        ("S9", "toml", Effect.Supported),
        ("S9", "yaml", Effect.Supported),
        ("S9", "ini", Effect.Supported),
        ("S10", "json", Effect.DropMember),
        ("S10", "msgpack", Effect.DropMember),
        ("S10", "toml", Effect.DropMember),
        ("S10", "yaml", Effect.DropMember),
        ("S10", "ini", Effect.DropMember),
        ("S11a", "json", Effect.Supported),
        ("S11a", "msgpack", Effect.Supported),
        ("S11a", "toml", Effect.DropMember),
        ("S11a", "yaml", Effect.DropMember),
        ("S11a", "ini", Effect.DropMember),
        ("S11b", "json", Effect.Supported),
        ("S11b", "msgpack", Effect.Supported),
        ("S11b", "toml", Effect.Supported),
        ("S11b", "yaml", Effect.DropMember),
        ("S11b", "ini", Effect.DropMember),
        ("S12", "json", Effect.Supported),
        ("S12", "msgpack", Effect.Supported),
        ("S12", "toml", Effect.DropElement),
        ("S12", "yaml", Effect.DropElement),
        ("S12", "ini", Effect.DropMember),
        ("S13", "json", Effect.Supported),
        ("S13", "msgpack", Effect.Supported),
        ("S13", "toml", Effect.DropMember),
        ("S13", "yaml", Effect.DropMember),
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

            var (_, generated, _) = run(source);

            await Assert
                .That(FormatCapabilities.AllowedDictKeyKinds(tag).Contains("float32"))
                .IsFalse()
                .Because($"float32 must not be in the {tag} key set");
            await Assert
                .That(generated)
                .DoesNotContain("KeyedItems")
                .Because($"{tag} must reject a float key");
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
    /// One entry needs its evidence spelled out, because two hand-counts of this set already
    /// disagreed: <c>NullableValueLeaf × yaml</c> IS promoted. The decision point is walked
    /// by S5, S6 and S13, and DecisionTables.Facts says true for all three in yaml (YAML can
    /// express null; only the generator does not do it yet). So "every shape using it is
    /// representable" holds and BuildTargetRules promotes the cell — the spec facts decide
    /// that, not a hand-written summary.
    /// </para>
    /// </summary>
    [Test]
    public async Task Target_promotes_exactly_the_pairs_that_are_dropped_today()
    {
        (string Fmt, Decision D)[] expected =
        [
            ("msgpack", Decision.ExtendedKindResolves),
            ("toml", Decision.ExtendedKindResolves),
            ("yaml", Decision.ExtendedKindResolves),
            ("ini", Decision.ExtendedKindResolves),
            ("json", Decision.ExtendedKindAsElement),
            ("msgpack", Decision.ExtendedKindAsElement),
            ("toml", Decision.ExtendedKindAsElement),
            ("yaml", Decision.ExtendedKindAsElement),
            ("toml", Decision.NestedObjectContainer),
            ("yaml", Decision.NullableValueLeaf),
            ("yaml", Decision.NullableRefLeaf),
            ("json", Decision.DictValueCollection),
            ("msgpack", Decision.DictValueCollection),
            ("toml", Decision.DictValueCollection),
            ("yaml", Decision.DictValueCollection),
            ("toml", Decision.RecursiveSelf),
            ("yaml", Decision.RecursiveSelf),
            ("yaml", Decision.RecursiveMutual),
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

    private static string Joined(IReadOnlyList<string> shapes) =>
        shapes.Count == 0 ? "<none>" : string.Join(", ", shapes);
}
