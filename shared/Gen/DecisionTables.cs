// Pure DATA for the capability model — see FormatCapabilities.cs for the types and the
// entry points. Four tables, in dependency order:
//
//   Rules            (format, decision point) → Effect          [today, hand-written]
//   Paths            shape → ordered decision points            [hand-written]
//   Facts            (format, shape) → can the FORMAT express it [spec §3, hand-written]
//   TargetRules      (format, decision point) → Effect           [DERIVED from Facts]
//
// Rules is a snapshot of the CURRENT generator behaviour, not a wish list: every value
// below was reproduced cell-by-cell by the W0 spike (70/70 cells of the current-state
// matrix) and each group names the code that produces it, so a later "correction" of a
// value has to argue against the code rather than against this comment.
//
// TargetRules is NOT hand-written (see BuildTargetRules): a decision point is
// "Supported" in R2 exactly when every shape that passes through it is representable in
// that format. Two decision points are exempt because they only change the ENCODING,
// never whether the value can be expressed — see EncodingOnlyDecisions.

using System;
using System.Collections.Generic;

namespace PicoSerDe.Gen;

internal static class DecisionTables
{
    /// <summary>
    /// The five format tags in spec §3 column order. This order is what
    /// <see cref="FormatCapabilities.MatrixCells"/> emits, so the README matrix and this
    /// list cannot drift apart silently.
    /// </summary>
    internal static readonly string[] FormatTags = ["json", "msgpack", "toml", "yaml", "ini"];

    /// <summary>The 14 shapes in spec §3 row order.</summary>
    internal static readonly string[] ShapeIds =
    [
        "S1",
        "S2",
        "S3",
        "S4",
        "S5",
        "S6",
        "S7",
        "S8",
        "S9",
        "S10",
        "S11a",
        "S11b",
        "S12",
        "S13",
    ];

    // ── Rules: today's behaviour ───────────────────────────────────────────────────

    /// <summary>
    /// (format, decision point) → what the generator does TODAY.
    /// A missing entry is a bug, not "supported": the model must be able to answer for
    /// every 12x5 pair (that is what Every_blocked_decision_has_a_granularity pins).
    /// </summary>
    internal static readonly Dictionary<(string Fmt, Decision D), Effect> Rules = new()
    {
        // Extended collections as a plain member (S7). json always resolved the kind;
        // R2 Task 2 opened it for msgpack/toml/yaml and R2 Task 6 for ini (this row was
        // the last debt in the table), because TypeKindResolver asks THIS cell: a
        // format whose cell is Supported names the kind, and every format writes the
        // seven kinds as the same plain sequence (spec §6: S7 is ✅ in all five).
        [("json", Decision.ExtendedKindResolves)] = Effect.Supported,
        [("msgpack", Decision.ExtendedKindResolves)] = Effect.Supported,
        [("toml", Decision.ExtendedKindResolves)] = Effect.Supported,
        [("yaml", Decision.ExtendedKindResolves)] = Effect.Supported,
        [("ini", Decision.ExtendedKindResolves)] = Effect.Supported,
        // Nested containers — MUST be split scalar/object: a format can hold an array of
        // scalar arrays (S3) and still have no form for an array whose innermost elements
        // are objects (S4). TOML held only the first until R2 Task 6 taught the writer the
        // missing form — an inline table as an ARRAY ELEMENT (`V = [[{ Name = "a" }]]`) —
        // which is what spec §3 S4 / §9 already measured with tomllib. INI keeps
        // DropMember: its sections carry no nesting at all (a format FACT, not a debt).
        [("json", Decision.NestedScalarContainer)] = Effect.Supported,
        [("msgpack", Decision.NestedScalarContainer)] = Effect.Supported,
        [("toml", Decision.NestedScalarContainer)] = Effect.Supported,
        [("yaml", Decision.NestedScalarContainer)] = Effect.Supported,
        [("ini", Decision.NestedScalarContainer)] = Effect.DropMember,
        [("json", Decision.NestedObjectContainer)] = Effect.Supported,
        [("msgpack", Decision.NestedObjectContainer)] = Effect.Supported,
        [("toml", Decision.NestedObjectContainer)] = Effect.Supported,
        [("yaml", Decision.NestedObjectContainer)] = Effect.Supported,
        [("ini", Decision.NestedObjectContainer)] = Effect.DropMember,
        // Nullable VALUE leaf: toml/ini cannot hold null at all. yaml can (spec §3) and R2
        // Task 4 taught the writer the null literal (`YamlWriter.WriteNull`) AND the four
        // emit/read sites the nullable branch (List<int?>, List<List<int?>>, Dictionary<,int?>),
        // so its cell is the target value now — the difference between toml/ini and yaml is no
        // longer "can the format" but "does the generator", and it is closed.
        [("json", Decision.NullableValueLeaf)] = Effect.Supported,
        [("msgpack", Decision.NullableValueLeaf)] = Effect.Supported,
        [("toml", Decision.NullableValueLeaf)] = Effect.DropMember,
        [("yaml", Decision.NullableValueLeaf)] = Effect.Supported,
        [("ini", Decision.NullableValueLeaf)] = Effect.DropMember,
        // Nullable REFERENCE leaf: toml emits the member and loses the element (D-7a) — a
        // DropElement, which is why the effect is three-valued. yaml was DropElement for the
        // same reason (D-7b) and is Supported since R2 Task 4: a null ELEMENT is written as
        // `- null` instead of being skipped.
        [("json", Decision.NullableRefLeaf)] = Effect.Supported,
        [("msgpack", Decision.NullableRefLeaf)] = Effect.Supported,
        [("toml", Decision.NullableRefLeaf)] = Effect.DropElement,
        [("yaml", Decision.NullableRefLeaf)] = Effect.Supported,
        [("ini", Decision.NullableRefLeaf)] = Effect.DropMember,
        // Extended collection as an ELEMENT (S8). This row is the one that used to be
        // decided by a LITERAL kind list in the extractor, so the cell could say Supported
        // while the member stayed dropped — R2 Task 2 wired the site to this cell and
        // implemented the element position in the four formats. ini keeps DropMember
        // because S8 x ini is a format FACT (no nesting), not a gap.
        [("json", Decision.ExtendedKindAsElement)] = Effect.Supported,
        [("msgpack", Decision.ExtendedKindAsElement)] = Effect.Supported,
        [("toml", Decision.ExtendedKindAsElement)] = Effect.Supported,
        [("yaml", Decision.ExtendedKindAsElement)] = Effect.Supported,
        [("ini", Decision.ExtendedKindAsElement)] = Effect.DropMember,
        // Object list as a member: ini's convention carries no nesting inside a member.
        [("json", Decision.ObjectListInMember)] = Effect.Supported,
        [("msgpack", Decision.ObjectListInMember)] = Effect.Supported,
        [("toml", Decision.ObjectListInMember)] = Effect.Supported,
        [("yaml", Decision.ObjectListInMember)] = Effect.Supported,
        [("ini", Decision.ObjectListInMember)] = Effect.DropMember,
        // Recursion — MUST be split self/mutual: S11a is a self cycle (the member points
        // back at the type that owns it) and S11b a mutual one (it points at another type on
        // the descent stack). R2 Task 5 wired both emit sites to this cell pair and taught
        // the two section formats the shapes they were missing, so toml/yaml now say
        // Supported for both: TOML holds a cycle as nested tables addressed by their dotted
        // path (`[B]` -> `[B.A]` -> `[B.A.B]`) and YAML as nested mappings. ini keeps
        // DropMember — its sections are flat, so S11a/S11b x ini is a format FACT (spec §3),
        // not a gap.
        [("json", Decision.RecursiveSelf)] = Effect.Supported,
        [("msgpack", Decision.RecursiveSelf)] = Effect.Supported,
        [("toml", Decision.RecursiveSelf)] = Effect.Supported,
        [("yaml", Decision.RecursiveSelf)] = Effect.Supported,
        [("ini", Decision.RecursiveSelf)] = Effect.DropMember,
        [("json", Decision.RecursiveMutual)] = Effect.Supported,
        [("msgpack", Decision.RecursiveMutual)] = Effect.Supported,
        [("toml", Decision.RecursiveMutual)] = Effect.Supported,
        [("yaml", Decision.RecursiveMutual)] = Effect.Supported,
        [("ini", Decision.RecursiveMutual)] = Effect.DropMember,
        // Dict value is a collection (S10, D-3). This row used to be DropMember in all five
        // formats because the extraction site tested the value's KIND and never asked this
        // cell; R2 Task 3 wired the site to it and implemented the value position in the four
        // formats, so they now say Supported. ini keeps DropMember: its sections carry no
        // nesting at all, so S10 x ini is a format FACT (spec §3), not a gap.
        [("json", Decision.DictValueCollection)] = Effect.Supported,
        [("msgpack", Decision.DictValueCollection)] = Effect.Supported,
        [("toml", Decision.DictValueCollection)] = Effect.Supported,
        [("yaml", Decision.DictValueCollection)] = Effect.Supported,
        [("ini", Decision.DictValueCollection)] = Effect.DropMember,
        // Scalar kinds that only json resolves: appendix A command 2 lists every
        // `format == "json"` branch of TypeKindResolver.Resolve (char, half, biginteger,
        // int128, uint128, nint, datetimeoffset, uri, version). A non-json generator
        // cannot name those kinds, so the member is dropped.
        // NOTE: this point changes the ENCODING of a value, not whether a format can hold
        // it — see EncodingOnlyDecisions. Its R2 target therefore stays where it is.
        [("json", Decision.ScalarKindResolves)] = Effect.Supported,
        [("msgpack", Decision.ScalarKindResolves)] = Effect.DropMember,
        [("toml", Decision.ScalarKindResolves)] = Effect.DropMember,
        [("yaml", Decision.ScalarKindResolves)] = Effect.DropMember,
        [("ini", Decision.ScalarKindResolves)] = Effect.DropMember,
        // ConcurrentDictionary resolves as a dict in json only
        // (TypeKindResolver.Resolve → `format == "json" && … "ConcurrentDictionary"`).
        // Same note as ScalarKindResolves: encoding, not capability.
        [("json", Decision.ConcurrentDictResolves)] = Effect.Supported,
        [("msgpack", Decision.ConcurrentDictResolves)] = Effect.DropMember,
        [("toml", Decision.ConcurrentDictResolves)] = Effect.DropMember,
        [("yaml", Decision.ConcurrentDictResolves)] = Effect.DropMember,
        [("ini", Decision.ConcurrentDictResolves)] = Effect.DropMember,
    };

    // ── Paths: shape → ordered decision points ─────────────────────────────────────

    /// <summary>
    /// shapeId → the ordered decision points that shape must pass. The cell's effect is
    /// the STRICTEST one on the path. Only NECESSARY conditions belong here: how a
    /// format implements a shape (TOML array-of-tables, inline tables) is not a decision
    /// point, or S2xJSON would be judged dead.
    /// </summary>
    internal static readonly Dictionary<string, Decision[]> Paths = new(StringComparer.Ordinal)
    {
        ["S1"] = [], // flat scalar list — no decision point
        ["S2"] = [Decision.ObjectListInMember],
        ["S3"] = [Decision.NestedScalarContainer],
        ["S4"] = [Decision.NestedObjectContainer],
        ["S5"] = [Decision.NullableValueLeaf],
        ["S6"] = [Decision.NestedScalarContainer, Decision.NullableValueLeaf],
        ["S7"] = [Decision.ExtendedKindResolves],
        ["S8"] = [Decision.NestedScalarContainer, Decision.ExtendedKindAsElement],
        ["S9"] = [], // flat dictionary — no decision point
        ["S10"] = [Decision.DictValueCollection],
        ["S11a"] = [Decision.RecursiveSelf],
        ["S11b"] = [Decision.RecursiveMutual],
        ["S12"] = [Decision.NullableRefLeaf],
        ["S13"] = [Decision.NullableValueLeaf],
    };

    /// <summary>
    /// Reverse index of <see cref="Paths"/> (decision point → the shapes that use it).
    /// Derived, never hand-written; <see cref="FormatCapabilities.ShapesUsing"/> returns it.
    /// </summary>
    internal static readonly Dictionary<Decision, string[]> ShapesByDecision =
        BuildShapesByDecision();

    // ── Facts: what each format can express (spec §3) ──────────────────────────────

    /// <summary>
    /// (format, shape) → can the format express the shape at all (spec §3, measured with
    /// Python `tomllib` / PyYAML; INI's boundary is the spec's own convention).
    /// This is a FACT about the format, independent of what the generator does today:
    /// S7/S8 are ✅ everywhere although four formats drop them (D-1/D-2).
    /// </summary>
    internal static readonly Dictionary<(string Fmt, string Shape), bool> Facts = BuildFacts();

    // ── TargetRules: the R2 goal, derived ──────────────────────────────────────────

    /// <summary>
    /// Decision points that only change the ENCODING of a value, never whether the format
    /// can hold it: clearing them would swap a json-specific scalar/dict kind for a
    /// generic one, so their R2 target is exactly today's value.
    /// <para>
    /// Without this list the derivation below would vacuously mark both as Supported in
    /// every format (no shape walks through them), and a later reader would "fix" the
    /// target table into a capability claim the spec never makes.
    /// </para>
    /// </summary>
    internal static readonly Decision[] EncodingOnlyDecisions =
    [
        Decision.ScalarKindResolves,
        Decision.ConcurrentDictResolves,
    ];

    /// <summary>
    /// (format, decision point) → the R2 target. DERIVED, not hand-written:
    /// <c>Supported</c> exactly when every shape using the decision point is
    /// representable in that format; otherwise today's effect is kept (a blocked cell
    /// never becomes "less blocked", and keeping DropMember/DropElement is the
    /// conservative answer).
    /// </summary>
    internal static readonly Dictionary<(string Fmt, Decision D), Effect> TargetRules =
        BuildTargetRules();

    // ── Format limits (not three-valued effects) ───────────────────────────────────
    // Two questions that cannot be an Effect: they do not say "the member disappears" vs
    // "the element disappears", they say whether the GENERATED CODE has a construct for the
    // value at all. Both are per-format, and both are consumed through a
    // FormatCapabilities entry point rather than compared against a tag (I-3).

    /// <summary>
    /// The 11 key kinds accepted outside ini — the single hand-written copy of the limit;
    /// <see cref="FormatCapabilities.AllowedDictKeyKinds"/> hands it to the generators.
    /// </summary>
    private static readonly string[] ScalarDictKeyKinds =
    [
        "string",
        "int32",
        "int64",
        "int16",
        "uint16",
        "sbyte",
        "byte",
        "uint32",
        "uint64",
        "guid",
        "enum",
    ];

    /// <summary>
    /// format → the POSITIONS whose cycle the generated code knows how to close.
    /// <para>
    /// A cycle is not closed by the same emit branch in every position: a plain object member
    /// becomes a nested table (<c>[Child]</c>) or mapping, a collection ELEMENT becomes an
    /// array element, a dict VALUE becomes a table entry / mapping value. json and msgpack
    /// close all three because their object emit path calls the target's inner helper from
    /// every one of them; the section formats only have the member path so far — their
    /// element and value positions would have to nest a table inside an ARRAY, which no
    /// emitter here does yet. Recording the limit per position is what keeps the row from
    /// claiming a Support the code does not have (the "false green" R2 removed from rows
    /// ExtendedKindAsElement and DictValueCollection).
    /// </para>
    /// </summary>
    internal static readonly Dictionary<string, CyclePosition[]> CyclePositionsByFormat = new(
        StringComparer.Ordinal
    )
    {
        ["json"] = [CyclePosition.Member, CyclePosition.Element, CyclePosition.DictValue],
        ["msgpack"] = [CyclePosition.Member, CyclePosition.Element, CyclePosition.DictValue],
        ["toml"] = [CyclePosition.Member],
        ["yaml"] = [CyclePosition.Member],
        // ini cannot hold a cycle at all, so no position is reachable there; the empty
        // row keeps the model total (every format answers) without claiming anything.
        ["ini"] = [],
    };

    /// <summary>
    /// The formats whose emitters can close a recursion cycle through a type that CANNOT be
    /// instantiated (an abstract class or an interface).
    /// <para>
    /// This is not a capability FACT (spec §3 says nothing about it) and not a three-valued
    /// effect — it is a limit of the generated code, in the same family as
    /// <see cref="DictKeyKindsByFormat"/>: json and msgpack emit the cycle target's inner
    /// helper as a POLYMORPHIC dispatch (<c>JsonPolySer</c>/<c>JsonPolyDes</c> and the
    /// MsgPack equivalent), so <c>Base.Next : Base</c> round-trips there; toml, yaml and ini
    /// have no discriminator path at all, so the only way to close such a cycle is
    /// <c>new &lt;abstract&gt;(...)</c> — CS0144, i.e. invariant I-2 (spec §5.6 obligation ②).
    /// </para>
    /// <para>
    /// The extraction sites ask <see cref="FormatCapabilities.CanConstructCycleTarget"/>
    /// with the target's instantiability, so the answer for a CONCRETE target is always
    /// "yes" and this set only ever decides the abstract case. Keeping it here (and not as a
    /// format literal in the extractor) is what I-3 requires; when a section format grows a
    /// polymorphic emit path, add its tag here and nothing else.
    /// </para>
    /// </summary>
    internal static readonly HashSet<string> PolymorphicCycleFormats = new(StringComparer.Ordinal)
    {
        "json",
        "msgpack",
    };

    /// <summary>
    /// format — the LEAF kinds that format's INLINE nested-array emitter can write. A
    /// format with no entry here writes whatever kind the leaf resolves to (its emitters have
    /// a form per kind); a format listed here is LIMITED to the kinds listed.
    /// <para>
    /// The innermost element of a nested chain (<c>List&lt;List&lt;T&gt;&gt;</c>) is the only thing
    /// an inline-array emitter writes with a form of its own — every level above it is
    /// just another bracket pair. TOML writes a scalar leaf directly and an OBJECT leaf as an
    /// inline table (`V = [[{ Name = "a" }]]`, spec §3 S4 / §9), and nothing else:
    /// a <c>[table]</c> header cannot live inside an array. Naming "object" here is a claim
    /// about the EMITTER, not about the format — whether a particular object is
    /// inline-shaped is a second question (its members must be scalars; an object member or
    /// a collection member would need a table header or a value the inline reader cannot
    /// scan), asked by <c>GenInfrastructure</c> before it emits. A leaf this table does not
    /// name stays DROPPED and reported: never written as <c>ToString()</c> text, never as a
    /// header inside an array (invariant I-2/R2-R3).
    /// </para>
    /// This is a LIMIT, not an <see cref="Effect"/>: a blocked leaf drops the whole member,
    /// so there is no element-level answer to express. It is read through
    /// <see cref="FormatCapabilities.InlineNestedLeafKindSupported"/> — never compared
    /// against a format tag (I-3).
    /// </summary>
    internal static readonly Dictionary<string, HashSet<string>> InlineNestedLeafKindsByFormat =
        new(StringComparer.Ordinal)
        {
            ["toml"] = new HashSet<string>(
                [
                    "string",
                    "int32",
                    "int64",
                    "int16",
                    "uint16",
                    "sbyte",
                    "byte",
                    "uint32",
                    "uint64",
                    "float32",
                    "float64",
                    "boolean",
                    // an inline table: `[[{ Name = "a", N = 1 }]]`
                    "object",
                ],
                StringComparer.Ordinal
            ),
        };

    /// <summary>
    /// format → the key kinds that format accepts: ini takes "string" only (it reads a key
    /// as raw line text), the other four take all 11. This is the ONE definition of the
    /// limit — <c>GenInfrastructure</c> keeps no copy of it.
    /// </summary>
    internal static readonly Dictionary<string, HashSet<string>> DictKeyKindsByFormat = new(
        StringComparer.Ordinal
    )
    {
        ["json"] = new HashSet<string>(ScalarDictKeyKinds, StringComparer.Ordinal),
        ["msgpack"] = new HashSet<string>(ScalarDictKeyKinds, StringComparer.Ordinal),
        ["toml"] = new HashSet<string>(ScalarDictKeyKinds, StringComparer.Ordinal),
        ["yaml"] = new HashSet<string>(ScalarDictKeyKinds, StringComparer.Ordinal),
        ["ini"] = new HashSet<string>(["string"], StringComparer.Ordinal),
    };

    // ── builders ──────────────────────────────────────────────────────────────────

    private static Dictionary<Decision, string[]> BuildShapesByDecision()
    {
        var shapes = new Dictionary<Decision, List<string>>();
        // Enum.GetValues<T>() is .NET 5+; the Gen projects target netstandard2.0.
        foreach (var value in (Decision[])Enum.GetValues(typeof(Decision)))
            shapes[value] = [];

        foreach (var shapeId in ShapeIds)
        foreach (var d in Paths[shapeId])
            shapes[d].Add(shapeId);

        var result = new Dictionary<Decision, string[]>();
        foreach (var pair in shapes)
            result[pair.Key] = pair.Value.ToArray();
        return result;
    }

    private static Dictionary<(string Fmt, string Shape), bool> BuildFacts()
    {
        var facts = new Dictionary<(string Fmt, string Shape), bool>();
        // Row = one spec §3 row; the five flags are in FormatTags order.
        void Row(string shapeId, bool json, bool msgpack, bool toml, bool yaml, bool ini)
        {
            facts[("json", shapeId)] = json;
            facts[("msgpack", shapeId)] = msgpack;
            facts[("toml", shapeId)] = toml;
            facts[("yaml", shapeId)] = yaml;
            facts[("ini", shapeId)] = ini;
        }

        // S1  List<int>                  flat scalar list
        Row("S1", json: true, msgpack: true, toml: true, yaml: true, ini: true);
        // S2  List<Dto>                  object list (TOML: array of tables)
        Row("S2", json: true, msgpack: true, toml: true, yaml: true, ini: false);
        // S3  List<List<int>>            nested scalar list
        Row("S3", json: true, msgpack: true, toml: true, yaml: true, ini: false);
        // S4  List<List<Dto>>            nested object list (TOML: array of inline tables)
        Row("S4", json: true, msgpack: true, toml: true, yaml: true, ini: false);
        // S5  List<int?>                 nullable value leaf (TOML has no null literal)
        Row("S5", json: true, msgpack: true, toml: false, yaml: true, ini: false);
        // S6  List<List<int?>>           deep nullable value leaf
        Row("S6", json: true, msgpack: true, toml: false, yaml: true, ini: false);
        // S7  HashSet<int> as member     every format can express it as an array
        Row("S7", json: true, msgpack: true, toml: true, yaml: true, ini: true);
        // S8  List<HashSet<int>>         extended collection as element
        Row("S8", json: true, msgpack: true, toml: true, yaml: true, ini: false);
        // S9  Dictionary<string,int>     flat dictionary
        Row("S9", json: true, msgpack: true, toml: true, yaml: true, ini: true);
        // S10 Dictionary<string,List<int>>  dict value is a collection
        Row("S10", json: true, msgpack: true, toml: true, yaml: true, ini: false);
        // S11a self recursion (TOML: nested tables)
        Row("S11a", json: true, msgpack: true, toml: true, yaml: true, ini: false);
        // S11b mutual recursion
        Row("S11b", json: true, msgpack: true, toml: true, yaml: true, ini: false);
        // S12 List<string?>              null ELEMENT (YAML: `- null`)
        Row("S12", json: true, msgpack: true, toml: false, yaml: true, ini: false);
        // S13 Dictionary<string,int?>    null VALUE
        Row("S13", json: true, msgpack: true, toml: false, yaml: true, ini: false);
        return facts;
    }

    private static Dictionary<(string Fmt, Decision D), Effect> BuildTargetRules()
    {
        var target = new Dictionary<(string Fmt, Decision D), Effect>();
        var encodingOnly = new HashSet<Decision>(EncodingOnlyDecisions);

        // Same non-generic Enum.GetValues for netstandard2.0 (see BuildShapesByDecision).
        foreach (var d in (Decision[])Enum.GetValues(typeof(Decision)))
        {
            var shapes = ShapesByDecision[d];
            foreach (var fmt in FormatTags)
            {
                var current = Rules[(fmt, d)];
                // Encoding-only points keep today's value (see EncodingOnlyDecisions);
                // a decision point no shape walks through has nothing to derive from.
                if (encodingOnly.Contains(d) || shapes.Length == 0)
                {
                    target[(fmt, d)] = current;
                    continue;
                }

                var representable = true;
                foreach (var shapeId in shapes)
                {
                    if (Facts[(fmt, shapeId)])
                        continue;
                    representable = false;
                    break;
                }

                target[(fmt, d)] = representable ? Effect.Supported : current;
            }
        }

        return target;
    }
}
