// Capability model for the five wire formats — the ONE internal source of truth for
// "which shape can which format express" (spec §3) and "what does the generator do
// with it today".
//
// This file holds the TYPES and the ENTRY POINTS; the values live in DecisionTables.cs.
//
//   Rules       — what the generator does TODAY for a (format, decision point) pair.
//                 Three-valued: Supported / DropMember / DropElement.
//                 NOT an (allows?, granularity) pair: YAML's null element is
//                 "emit the member, lose the element", which only an effect can say.
//   Facts       — what the FORMAT can express, measured with independent
//                 implementations (Python `tomllib`, PyYAML). `CanRepresent` queries it.
//   TargetRules — the R2 goal, DERIVED from Facts via `ShapesUsing` (never hand-copied).
//
// A shape reaches its cell by walking an ordered PATH of decision points (`Paths`) and
// taking the strictest effect encountered. A path holds NECESSARY conditions only —
// "how a format implements it" (TOML array-of-tables) must never enter a path, or
// S2xJSON would be judged dead.
//
// Every capability decision must come through this class. `FormatConfig.FormatTag` is
// for diagnostic text only; do not compare it against format literals to decide
// whether something can be emitted (that is what DecisionTables.Rules is for).
//
// Both this file and DecisionTables.cs are linked into all five *.Gen projects and
// into the integration test project, hence `internal` (spec D5): `api/*.public.txt`
// must not change.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PicoSerDe.Gen;

/// <summary>
/// One place where the extraction pipeline can decide to drop something.
/// Taken one-by-one from the format literals scattered over the generators
/// (TypeKindResolver 12 + GenInfrastructure 13 sites), not derived from the shapes.
/// </summary>
internal enum Decision
{
    /// <summary>Extended collection kind (hashset/queue/stack/…) used as a plain member.</summary>
    ExtendedKindResolves,

    /// <summary>Extended collection kind used as a collection ELEMENT.</summary>
    ExtendedKindAsElement,

    /// <summary>
    /// Scalar kinds only json resolves (char/half/biginteger/int128/uint128/nint and the
    /// format-gated datetimeoffset/uri/version).
    /// </summary>
    ScalarKindResolves,

    /// <summary><c>ConcurrentDictionary</c> resolves as a dict (json only today).</summary>
    ConcurrentDictResolves,

    /// <summary>Nested container whose innermost element is a scalar (<c>List&lt;List&lt;int&gt;&gt;</c>).</summary>
    NestedScalarContainer,

    /// <summary>Nested container whose innermost element is an object (<c>List&lt;List&lt;Dto&gt;&gt;</c>).</summary>
    NestedObjectContainer,

    /// <summary>Nullable VALUE leaf (<c>int?</c>) in a sequence or member.</summary>
    NullableValueLeaf,

    /// <summary>Nullable REFERENCE leaf (<c>string?</c>) in a sequence or member.</summary>
    NullableRefLeaf,

    /// <summary>Object list as a member (<c>List&lt;Dto&gt;</c> property).</summary>
    ObjectListInMember,

    /// <summary>A dict whose VALUE is a collection (<c>Dictionary&lt;string, List&lt;int&gt;&gt;</c>).</summary>
    DictValueCollection,

    /// <summary>Self recursion (<c>Node.Child : Node</c>).</summary>
    RecursiveSelf,

    /// <summary>Mutual recursion (<c>M1.Child : M2</c>).</summary>
    RecursiveMutual,
}

/// <summary>
/// What happens to the shape when a decision point blocks it. The granularity IS the
/// effect — there is no separate "granularity" field.
/// </summary>
internal enum Effect
{
    /// <summary>Emitted normally.</summary>
    Supported,

    /// <summary>The whole member is dropped (and diagnosed).</summary>
    DropMember,

    /// <summary>Only the element/value is dropped, the member is still emitted (and diagnosed).</summary>
    DropElement,
}

/// <summary>
/// Entry points of the capability model. Task 3 rewires the generators onto these;
/// Task 8 derives the README matrix from <see cref="MatrixCells"/>.
/// </summary>
internal static class FormatCapabilities
{
    /// <summary>
    /// The five format tags, in the column order used by spec §3 (json, msgpack, toml,
    /// yaml, ini).
    /// </summary>
    internal static IReadOnlyList<string> AllFormatTags => DecisionTables.FormatTags;

    /// <summary>The 14 modelled shapes, in spec §3 row order (S1..S10, S11a, S11b, S12, S13).</summary>
    internal static IReadOnlyList<string> AllShapeIds => DecisionTables.ShapeIds;

    /// <summary>Today's value of (format, decision point).</summary>
    internal static IReadOnlyDictionary<(string Fmt, Decision D), Effect> Rules =>
        DecisionTables.Rules;

    /// <summary>shapeId → the ORDERED decision points that shape must pass.</summary>
    internal static IReadOnlyDictionary<string, Decision[]> Paths => DecisionTables.Paths;

    // ── today ──────────────────────────────────────────────────────────────────────

    /// <summary>What the generator does today for one decision point in one format.</summary>
    internal static Effect Decide(string formatTag, Decision d) =>
        DecisionTables.Rules[Key(formatTag, d)];

    /// <summary>
    /// What the generator does today for a whole shape: walk its path and take the
    /// strictest effect (DropMember &gt; DropElement &gt; Supported).
    /// </summary>
    internal static Effect DecideShape(string formatTag, string shapeId)
    {
        var effect = Effect.Supported;
        foreach (var d in PathOf(shapeId))
            effect = Strictest(effect, Decide(formatTag, d));
        return effect;
    }

    // ── R2 target ──────────────────────────────────────────────────────────────────

    /// <summary>The R2 target for one decision point in one format (see DecisionTables.TargetRules).</summary>
    internal static Effect DecideTarget(string formatTag, Decision d) =>
        DecisionTables.TargetRules[Key(formatTag, d)];

    /// <summary>The R2 target for a whole shape — same path walk, target values.</summary>
    internal static Effect DecideShapeTarget(string formatTag, string shapeId)
    {
        var effect = Effect.Supported;
        foreach (var d in PathOf(shapeId))
            effect = Strictest(effect, DecideTarget(formatTag, d));
        return effect;
    }

    // ── format facts (spec §3) ─────────────────────────────────────────────────────

    /// <summary>Can this format express this shape at all? (spec §3, the measured facts.)</summary>
    internal static bool CanRepresent(string formatTag, string shapeId) =>
        DecisionTables.Facts[FactsKey(formatTag, shapeId)];

    /// <summary>
    /// Can this format express every shape that goes through <paramref name="d"/>?
    /// Decision points that no shape uses (the encoding-only ones) answer <c>true</c>
    /// vacuously — the format can represent the values, only the encoding differs.
    /// </summary>
    internal static bool CanRepresent(string formatTag, Decision d) =>
        ShapesUsing(d).All(shapeId => CanRepresent(formatTag, shapeId));

    // ── diagnostics (I-5 / D-4) ────────────────────────────────────────────────────

    /// <summary>
    /// The TERMINAL marker: the FORMAT cannot express the shape. No generator change can
    /// help — the reader's only ways out are a different format or a different type.
    /// </summary>
    internal const string ImpossibleMarker = "\u26D4";

    /// <summary>
    /// The DEBT marker: the format CAN express the shape, this generator has not
    /// implemented it yet. Reopenable (spec R4), so the message must not blame the format
    /// and must not send the reader off to change a type that is perfectly expressible
    /// (spec C4 / D-4 ②).
    /// </summary>
    internal const string DeferredMarker = "\u23F8";

    /// <summary>
    /// The marker a drop of decision point <paramref name="d"/> carries in
    /// <paramref name="formatTag"/>: ⛔ when the FORMAT cannot express it, ⏸ when it can
    /// and the drop is our gap. The FACTS answer — never a format literal and never the
    /// implementation status (spec C1: progress must not be written as a format fact).
    /// </summary>
    internal static string MarkerFor(string formatTag, Decision d) =>
        CanRepresent(formatTag, d) ? DeferredMarker : ImpossibleMarker;

    // ── reverse index ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every shape whose path contains <paramref name="d"/> — derived from
    /// <see cref="Paths"/>, never hand-written.
    /// </summary>
    internal static IReadOnlyList<string> ShapesUsing(Decision d) =>
        DecisionTables.ShapesByDecision.TryGetValue(d, out var shapes)
            ? shapes
            : Array.Empty<string>();

    // ── limits (NOT three-valued effects) ──────────────────────────────────────────

    /// <summary>
    /// The key kinds this format accepts for a dictionary — the ONE definition of that
    /// limit: <c>GenInfrastructure</c> asks THIS, so an extra kind has to be added here
    /// (DecisionTables.DictKeyKindsByFormat) and nowhere else.
    /// <para>
    /// INI reads a key as raw line text, so only <c>string</c> keys are representable
    /// there; the restriction is scoped to that format on purpose — narrowing the shared
    /// set would silently drop JSON <c>Dictionary&lt;int, string&gt;</c> support.
    /// </para>
    /// This is a LIMIT, not an effect — that is why it is not a <see cref="Decision"/>.
    /// Callers must not mutate the returned set.
    /// </summary>
    internal static ISet<string> AllowedDictKeyKinds(string formatTag)
    {
        if (DecisionTables.DictKeyKindsByFormat.TryGetValue(formatTag, out var kinds))
            return kinds;

        throw new ArgumentOutOfRangeException(
            nameof(formatTag),
            formatTag,
            "unknown format tag; expected one of " + string.Join(", ", DecisionTables.FormatTags)
        );
    }

    // ── README matrix (Task 8) ─────────────────────────────────────────────────────

    /// <summary>
    /// The five cells of a shape's row as a token string ("✅✅✅✅❌"), in
    /// <see cref="AllFormatTags"/> order, using TODAY's effects. Two tokens exist:
    /// ✅ for <see cref="Effect.Supported"/> and ❌ for any drop.
    /// </summary>
    internal static string MatrixCells(string shapeId) =>
        string.Concat(
            AllFormatTags.Select(f =>
                DecideShape(f, shapeId) == Effect.Supported ? "\u2705" : "\u274C"
            )
        );

    // ── helpers ────────────────────────────────────────────────────────────────────

    /// <summary>DropMember wins over DropElement, which wins over Supported.</summary>
    private static Effect Strictest(Effect left, Effect right)
    {
        if (left == Effect.DropMember || right == Effect.DropMember)
            return Effect.DropMember;
        if (left == Effect.DropElement || right == Effect.DropElement)
            return Effect.DropElement;
        return Effect.Supported;
    }

    private static Decision[] PathOf(string shapeId)
    {
        if (DecisionTables.Paths.TryGetValue(shapeId, out var path))
            return path;

        throw new ArgumentOutOfRangeException(
            nameof(shapeId),
            shapeId,
            "unknown shape id; expected one of " + string.Join(", ", DecisionTables.ShapeIds)
        );
    }

    private static (string Fmt, Decision D) Key(string formatTag, Decision d)
    {
        if (!DecisionTables.Rules.ContainsKey((formatTag, d)))
            throw new ArgumentOutOfRangeException(
                nameof(formatTag),
                formatTag,
                "unknown format tag; expected one of "
                    + string.Join(", ", DecisionTables.FormatTags)
            );
        return (formatTag, d);
    }

    private static (string Fmt, string Shape) FactsKey(string formatTag, string shapeId)
    {
        if (!DecisionTables.Facts.ContainsKey((formatTag, shapeId)))
            throw new ArgumentOutOfRangeException(
                nameof(shapeId),
                shapeId,
                "unknown shape/format pair: " + shapeId + " x " + formatTag
            );
        return (formatTag, shapeId);
    }
}
