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
        // Extended collections as a plain member: only json resolves the kind
        // (TypeKindResolver.Resolve → "Extended collections (JSON only for now)"). D-1.
        [("json", Decision.ExtendedKindResolves)] = Effect.Supported,
        [("msgpack", Decision.ExtendedKindResolves)] = Effect.DropMember,
        [("toml", Decision.ExtendedKindResolves)] = Effect.DropMember,
        [("yaml", Decision.ExtendedKindResolves)] = Effect.DropMember,
        [("ini", Decision.ExtendedKindResolves)] = Effect.DropMember,
        // Nested containers — MUST be split scalar/object: S3 and S4 disagree in TOML
        // (TOML holds an array of scalar arrays, but not an array of inline-table arrays
        // as a member: D-3).
        [("json", Decision.NestedScalarContainer)] = Effect.Supported,
        [("msgpack", Decision.NestedScalarContainer)] = Effect.Supported,
        [("toml", Decision.NestedScalarContainer)] = Effect.Supported,
        [("yaml", Decision.NestedScalarContainer)] = Effect.Supported,
        [("ini", Decision.NestedScalarContainer)] = Effect.DropMember,
        [("json", Decision.NestedObjectContainer)] = Effect.Supported,
        [("msgpack", Decision.NestedObjectContainer)] = Effect.Supported,
        [("toml", Decision.NestedObjectContainer)] = Effect.DropMember,
        [("yaml", Decision.NestedObjectContainer)] = Effect.Supported,
        [("ini", Decision.NestedObjectContainer)] = Effect.DropMember,
        // Nullable VALUE leaf: toml/ini cannot hold null at all; yaml could (spec §3) but
        // the generator does not implement it yet (D-7b) — the difference between the two
        // is exactly what Facts/TargetRules record.
        [("json", Decision.NullableValueLeaf)] = Effect.Supported,
        [("msgpack", Decision.NullableValueLeaf)] = Effect.Supported,
        [("toml", Decision.NullableValueLeaf)] = Effect.DropMember,
        [("yaml", Decision.NullableValueLeaf)] = Effect.DropMember,
        [("ini", Decision.NullableValueLeaf)] = Effect.DropMember,
        // Nullable REFERENCE leaf: same null story, but toml/yaml emit the member and lose
        // the element (D-7a/D-7b) — a DropElement, which is why the effect is three-valued.
        [("json", Decision.NullableRefLeaf)] = Effect.Supported,
        [("msgpack", Decision.NullableRefLeaf)] = Effect.Supported,
        [("toml", Decision.NullableRefLeaf)] = Effect.DropElement,
        [("yaml", Decision.NullableRefLeaf)] = Effect.DropElement,
        [("ini", Decision.NullableRefLeaf)] = Effect.DropMember,
        // Extended collection as an ELEMENT: json resolves the kind but the element
        // position still drops the member (D-2); the other four drop it earlier.
        [("json", Decision.ExtendedKindAsElement)] = Effect.DropMember,
        [("msgpack", Decision.ExtendedKindAsElement)] = Effect.DropMember,
        [("toml", Decision.ExtendedKindAsElement)] = Effect.DropMember,
        [("yaml", Decision.ExtendedKindAsElement)] = Effect.DropMember,
        [("ini", Decision.ExtendedKindAsElement)] = Effect.DropMember,
        // Object list as a member: ini's convention carries no nesting inside a member.
        [("json", Decision.ObjectListInMember)] = Effect.Supported,
        [("msgpack", Decision.ObjectListInMember)] = Effect.Supported,
        [("toml", Decision.ObjectListInMember)] = Effect.Supported,
        [("yaml", Decision.ObjectListInMember)] = Effect.Supported,
        [("ini", Decision.ObjectListInMember)] = Effect.DropMember,
        // Recursion — MUST be split self/mutual: S11a is dropped in TOML while S11b is
        // emitted there today (with D-5 overwriting the root member on read).
        [("json", Decision.RecursiveSelf)] = Effect.Supported,
        [("msgpack", Decision.RecursiveSelf)] = Effect.Supported,
        [("toml", Decision.RecursiveSelf)] = Effect.DropMember,
        [("yaml", Decision.RecursiveSelf)] = Effect.DropMember,
        [("ini", Decision.RecursiveSelf)] = Effect.DropMember,
        [("json", Decision.RecursiveMutual)] = Effect.Supported,
        [("msgpack", Decision.RecursiveMutual)] = Effect.Supported,
        [("toml", Decision.RecursiveMutual)] = Effect.Supported,
        [("yaml", Decision.RecursiveMutual)] = Effect.DropMember,
        [("ini", Decision.RecursiveMutual)] = Effect.DropMember,
        // Dict value is a collection: all five formats drop the member at extraction time
        // (GenInfrastructure, "Dict values that are collections …", D-3).
        [("json", Decision.DictValueCollection)] = Effect.DropMember,
        [("msgpack", Decision.DictValueCollection)] = Effect.DropMember,
        [("toml", Decision.DictValueCollection)] = Effect.DropMember,
        [("yaml", Decision.DictValueCollection)] = Effect.DropMember,
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

    // ── Dict key limits (not three-valued effects) ─────────────────────────────────

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
