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
//                 implementations (Python `tomllib`, PyYAML). `CapabilityOf` queries it;
//                 a cell is Representable / Deferred / Impossible (three states, not a bool:
//                 "the format cannot" and "we have not implemented it" are different answers).
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
/// Where in a member the guarded descent ran into a cycle. The emit path is not the same in
/// all three, and neither is what each format can close (see
/// <see cref="FormatCapabilities.CanCloseCycle"/>).
/// </summary>
internal enum CyclePosition
{
    /// <summary>A plain object member: <c>Node.Child</c>, <c>A.B : B</c> (S11a / S11b).</summary>
    Member,

    /// <summary>A collection element: <c>List&lt;Node&gt;</c>, <c>List&lt;A&gt;</c>.</summary>
    Element,

    /// <summary>A dictionary value: <c>Dictionary&lt;string, Node&gt;</c>.</summary>
    DictValue,
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
/// What a format can do with a shape — THREE states, because "the FORMAT cannot express it"
/// and "we have not implemented it" are different answers, and the <c>bool</c> this enum
/// replaces forced both into the same <c>false</c>. One rule per state, and they are not
/// interchangeable.
/// <para>
/// The two NEGATIVE answers stay apart on purpose, because they name different owners
/// (spec C1/C4): <see cref="Impossible"/> is the FORMAT's answer and no generator change can
/// help, while <see cref="Deferred"/> is OURS — reopenable, and its way out may be a new wire
/// CONVENTION (an <c>x+1</c>-level agreement) rather than generator plumbing.
/// </para>
/// </summary>
internal enum Capability
{
    /// <summary>
    /// ⛔ The FORMAT cannot express the shape (spec §3): terminal, and the drop is not our
    /// fault — another format or another type is the reader's only way out.
    /// </summary>
    Impossible,

    /// <summary>
    /// ⏸ The format CAN express the shape and this generator has not implemented it yet —
    /// reopenable (spec R4), so the diagnostic must carry a <c>reopen:</c> pointer and must not
    /// blame the format (spec C1/C4). Its matrix cell is still ❌: ✅ means "the generated code
    /// both writes and reads the shape", and a debt is not an implementation.
    /// </summary>
    Deferred,

    /// <summary>
    /// ✅ The format can express the shape AND the generated code does — the only state that
    /// keeps a ✅ in the terminal matrix, and the only one that may be promoted to
    /// <see cref="Effect.Supported"/>.
    /// </summary>
    Representable,
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

    /// <summary>
    /// The three-state answer for one (format, shape) cell (spec §3, hand-written — the ONE
    /// place "can the format" and "have we implemented it" are recorded separately).
    /// </summary>
    internal static Capability CapabilityOf(string formatTag, string shapeId) =>
        DecisionTables.Facts[FactsKey(formatTag, shapeId)];

    /// <summary>
    /// The three-state answer for a decision point: the WORST answer among the shapes that walk
    /// through it (<see cref="Capability.Impossible"/> &gt; <see cref="Capability.Deferred"/>
    /// &gt; <see cref="Capability.Representable"/>), because a point is only as available as the
    /// shapes it has to carry.
    /// <para>
    /// A decision point no shape uses (the encoding-only ones) answers
    /// <see cref="Capability.Representable"/> vacuously — the format can represent the values,
    /// only the encoding differs.
    /// </para>
    /// </summary>
    internal static Capability CapabilityOf(string formatTag, Decision d)
    {
        var capability = Capability.Representable;
        foreach (var shapeId in ShapesUsing(d))
            capability = Worse(capability, CapabilityOf(formatTag, shapeId));
        return capability;
    }

    /// <summary>
    /// Is this shape covered by the generated code in this format — the format CAN express it
    /// and we HAVE implemented it (<see cref="Capability.Representable"/>)?
    /// <para>
    /// This is the MATRIX/target question, not the verdict question, and the difference IS the
    /// third state: a <see cref="Capability.Deferred"/> cell answers <c>false</c> here, because
    /// ✅ is defined as "the generated code both writes and reads the shape" and a deferred
    /// cell's member is still dropped. For "does the FORMAT have the shape at all?" (⛔ vs ⏸,
    /// the DIAGNOSTIC question) ask <see cref="CapabilityOf(string, string)"/> and compare with
    /// <see cref="Capability.Impossible"/> — asking this one instead would turn our gap into
    /// "the format cannot" (spec C1).
    /// </para>
    /// </summary>
    internal static bool IsRepresentable(string formatTag, string shapeId) =>
        CapabilityOf(formatTag, shapeId) == Capability.Representable;

    // ── diagnostics (I-5 / D-4) ────────────────────────────────────────────────────
    // The ONE definition of the two capability verdict glyphs. `GenInfrastructure`'s
    // messageFormats REFERENCE these constants (never a literal) and no other file in
    // `shared/Gen` may spell ⛔ or ⏸ — `VerdictMarkerGuardTests` enforces both halves.

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
    /// The <c>reopen:</c> tail a DEBT carries when nothing more specific is known: the condition
    /// is a row of this matrix, so the pointer sends the reader to the table that holds it.
    /// </summary>
    internal const string MatrixDebtReopenPointer =
        "the condition is tracked as debt in the format-capabilities matrix (spec §3/§6)";

    /// <summary>
    /// The <c>reopen:</c> tail for a DEFERRED cell whose way out is not a generator change at
    /// all: a member-level object list (spec §3 S2) inside an INI member needs a <b>new wire
    /// convention</b> — an <c>x+1</c>-level public agreement, recorded in spec §8.1's
    /// next-release list — because an INI section header is a FLAT, member-named slot and a
    /// composite name (<c>[Rows.0]</c>) is a convention no reader has, not a code shape we
    /// forgot to emit. Saying "add a numbered section" would dress a public wire change up as
    /// generator plumbing.
    /// <para>
    /// The sentence asserts something about <b>INI</b> — its section header is a flat,
    /// member-named slot — so it may only be handed to a reader whose format IS ini; see
    /// <see cref="WireConventionDebt"/>. Selecting it by the SHAPE alone would let it describe
    /// an INI boundary inside another format's diagnostic.
    /// </para>
    /// </summary>
    private const string MemberLevelObjectListReopenPointer =
        "a member-level object list needs a new wire convention — an `x+1`-level public agreement, not a generator change, recorded in spec §8.1's next-release list (the INI section header is a flat, member-named slot, so a composite name like `[Rows.0]` would be that convention)";

    /// <summary>
    /// The <c>reopen:</c> tail for a cell whose missing piece is the spec's own “no nesting
    /// inside a member” convention (spec §3), <b>without</b> a shape of its own being named in
    /// §8.1 yet.
    /// <para>
    /// S2 keeps the more precise sentence above because §8.1 S-1 spells its form out
    /// (<c>[&lt;member&gt;.&lt;index&gt;]</c>). S3/S4/S8/S10 are the same convention reached through a
    /// different shape and have no entry of their own, so the honest pointer is the convention
    /// itself: naming §8.1 S-1 for them would send the reader to a form that does not describe
    /// their shape, and the generic matrix pointer would withhold the one thing they do know —
    /// that the boundary is a convention rather than a limit of the format.
    /// </para>
    /// </summary>
    private const string NestedInMemberConventionReopenPointer =
        "this spec's INI convention carries no nesting inside a member (spec §3) — lifting it is an `x+1`-level public agreement about a composite section name, not a generator change";

    /// <summary>
    /// The <c>reopen:</c> tail for a cell waiting on <b>spec §8.1 V-6</b>: the value-level null
    /// token INI has no form for.
    /// <para>
    /// Its own sentence because the missing piece is a VALUE convention rather than a nesting
    /// one, and because it is already registered in §8.1 — a reader of a ⏸ is owed the
    /// entry name as well as the reason.
    /// </para>
    /// </summary>
    private const string IniValueNullTokenReopenPointer =
        "INI has no value-level null token; adding one is spec §8.1 V-6, an `x+1`-level public agreement rather than a generator change (an empty value cannot stand in — it is a valid empty string)";

    /// <summary>
    /// The <c>reopen:</c> tail for <c>toml</c> losing only the NULL PART of a nullable string
    /// leaf.
    /// <para>
    /// Distinct from TOML's ⏸-forbidden cells: TOML 1.0 has no null literal, but a string has a
    /// legal empty form and a member can be omitted, so a value-level agreement exists to be
    /// written — whereas <c>S5</c>/<c>S6</c> (“value leaf is <c>int?</c>”) have no carrier to
    /// borrow at all and stay ⏸, because a ⏸ there would promise a mechanism that does not
    /// exist.
    /// </para>
    /// </summary>
    private const string TomlNullableStringReopenPointer =
        "TOML has no null literal, but an omitted member or an empty string value is a published way to say “no value” for a string; agreeing which one means null here is an `x+1`-level value convention (spec §8.1)";

    /// <summary>
    /// The shape whose deferral is a WIRE-CONVENTION decision rather than an emitter gap (spec
    /// §3 S2). Named here so the pointer below cannot quietly start describing a different row.
    /// </summary>
    private const string ObjectListShape = "S2";

    /// <summary>
    /// The (format, shape) pairs whose <c>reopen:</c> tail is a SPECIFIC convention instead of
    /// the generic matrix row. Keyed by BOTH halves on purpose: the specific text is a sentence
    /// about one format's boundary, so "which shape is Deferred" does not answer "whose" — a
    /// shape-only lookup would hand INI's section-header explanation to whichever format landed
    /// on that shape next.
    /// <para>
    /// Every cell recorded here must also be <see cref="Capability.Deferred"/> in the facts
    /// table — the pair decides WHOSE WORK the pointer describes, the facts decide WHETHER the
    /// debt exists (I-3) — but not every <see cref="Capability.Deferred"/> cell is recorded here:
    /// a cell reclassified to a debt whose convention §8.1 has not spelled out gets
    /// <see cref="UnnamedConventionReopenPointer"/> instead of borrowing a sentence about
    /// someone else's convention.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A MAP rather than a set plus a switch: the sentence and the pair it explains are one
    /// entry, so a pair cannot be recorded without naming its convention, and a convention
    /// cannot be written without naming the pair it belongs to. The earlier shape — a set of
    /// pairs, a switch keyed by the pair, and a catch-all arm — would have handed the
    /// catch-all sentence to a pair added to the set but forgotten in the switch, which is
    /// exactly the class of silent mismatch this whole file exists to prevent.
    /// </remarks>
    private static readonly Dictionary<(string Format, string Shape), string> WireConventionDebt =
        new()
        {
            [("ini", ObjectListShape)] = MemberLevelObjectListReopenPointer,
            [("ini", "S3")] = NestedInMemberConventionReopenPointer,
            [("ini", "S4")] = NestedInMemberConventionReopenPointer,
            [("ini", "S8")] = NestedInMemberConventionReopenPointer,
            [("ini", "S10")] = NestedInMemberConventionReopenPointer,
            [("ini", "S5")] = IniValueNullTokenReopenPointer,
            [("ini", "S6")] = IniValueNullTokenReopenPointer,
            [("ini", "S13")] = IniValueNullTokenReopenPointer,
            [("toml", "S13")] = TomlNullableStringReopenPointer,
        };

    /// <summary>
    /// The <c>reopen:</c> tail for one <b>already-selected</b> (format, shape) pair whose cell is
    /// <see cref="Capability.Deferred"/> but which is NOT in <see cref="WireConventionDebt"/> —
    /// i.e. a cell that was reclassified to a debt without §8.1 gaining an entry for it.
    /// <para>
    /// ⏸ must not blame the format (C1) and must point somewhere (C4), so a cell in this state
    /// gets the navigation pointer rather than the INI-specific sentence: the reader is told the
    /// cell is recorded as a debt in the matrix and that the way out is an agreement, without being
    /// handed a claim about a format they are not using.
    /// </para>
    /// </summary>
    private const string UnnamedConventionReopenPointer =
        "the format can hold this shape and no agreement for it exists yet; it is recorded as debt in the format-capabilities matrix (spec §3/§6)";

    /// <summary>
    /// The <c>reopen:</c> tail the drop of <paramref name="point"/> carries: the specific
    /// pointer when the (format, shape) behind that point is a recorded WIRE-CONVENTION debt
    /// (<see cref="WireConventionDebt"/>), the matrix pointer otherwise.
    /// <para>
    /// Derived from the same cells that decide the VERDICT, so the two halves of a ⏸ (what is
    /// missing, where it is recorded) cannot drift apart: a cell that stops being Deferred stops
    /// promising its convention.
    /// </para>
    /// </summary>
    internal static string ReopenPointer(string formatTag, Decision point)
    {
        foreach (var shapeId in ShapesUsing(point))
            if (CapabilityOf(formatTag, shapeId) == Capability.Deferred)
                return ReopenPointerFor(formatTag, shapeId);

        return MatrixDebtReopenPointer;
    }

    /// <summary>
    /// The <c>reopen:</c> tail for one DEFERRED (format, shape) pair: the specific wire-convention
    /// pointer only where that PAIR is recorded in <see cref="WireConventionDebt"/>, the generic
    /// matrix-row pointer everywhere else.
    /// <para>
    /// An entry point of its own (rather than an inline ternary) because it is the pairing that
    /// has to be testable: the specific text is a sentence about INI, so the property that
    /// matters is "a SECOND format landing on the same shape gets the generic pointer", and the
    /// callers cannot produce that case while only one cell is Deferred.
    /// </para>
    /// </summary>
    internal static string ReopenPointerFor(string formatTag, string shapeId) =>
        WireConventionDebt.TryGetValue((formatTag, shapeId), out var recorded)
            ? recorded
            : CapabilityOf(formatTag, shapeId) == Capability.Deferred
                ? UnnamedConventionReopenPointer
                : MatrixDebtReopenPointer;

    /// <summary>
    /// The recorded (format, shape) → sentence pairs — for the test that checks every one of
    /// them is a <see cref="Capability.Deferred"/> cell and that no two conventions were merged.
    /// </summary>
    internal static IEnumerable<(string Format, string Shape)> RecordedConventions =>
        WireConventionDebt.Keys;

    // The marker a specific drop carries is NOT derived here: only the extraction site
    // knows which decision point fired, so it asks `CapabilityOf(formatTag, point)` and
    // records the answer as a `DropVerdict` (`GenInfrastructure.SkippedBy`). There is no
    // `MarkerFor`: it had zero callers (review W5-2, dead code).
    //
    // The question that splits ⛔ from ⏸ is `Capability.Impossible` — NOT
    // `IsRepresentable`: a DEFERRED cell is the format's "yes" and our "not yet", so it is a
    // debt with a `reopen:` pointer, never a sentence about what the format cannot do (C1).

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
    /// Can this format's generated code close a cycle that was reached in
    /// <paramref name="position"/>?
    /// <para>
    /// Two independent limits, both of the generated code rather than of the format
    /// (spec §3 says nothing about either): the position must have an emit branch
    /// (<see cref="DecisionTables.CyclePositionsByFormat"/>), and a target that cannot be
    /// instantiated needs the format's polymorphic dispatch
    /// (<see cref="DecisionTables.PolymorphicCycleFormats"/>). The recursion CELL is asked
    /// separately by the caller — this method never decides whether the row is Supported,
    /// only whether the code can carry what the row allows.
    /// </para>
    /// </summary>
    internal static bool CanCloseCycle(
        string formatTag,
        CyclePosition position,
        bool targetIsInstantiable
    ) =>
        DecisionTables.CyclePositionsByFormat.TryGetValue(formatTag, out var positions)
        && Array.IndexOf(positions, position) >= 0
        && (targetIsInstantiable || DecisionTables.PolymorphicCycleFormats.Contains(formatTag));

    /// <summary>
    /// Can this format's generated code close a recursion cycle through a target that
    /// cannot be instantiated (an abstract class or an interface)?
    /// <para>
    /// A cycle is closed by calling the TARGET's generated helper, and that helper ends in
    /// <c>new T(...)</c> unless the format has a polymorphic dispatch to route the value
    /// through (json and msgpack do; toml/yaml/ini have no discriminator path at all). The
    /// answer therefore has two halves: the target must be constructible, OR the format must
    /// be able to dispatch it — <see cref="DecisionTables.PolymorphicCycleFormats"/>.
    /// </para>
    /// <para>
    /// This is the extraction sites' question, not a <see cref="Decision"/>: it is a limit of
    /// the generated code (like <see cref="AllowedDictKeyKinds"/>), and it does not have an
    /// element-level answer — a blocked cycle always drops the whole member. Invariant I-2 is
    /// what it protects: R1 W3's build died on <c>new &lt;abstract&gt;(...)</c> (CS0144) the
    /// moment the recursion cells were read without asking this.
    /// </para>
    /// </summary>
    /// <param name="formatTag">The format being generated.</param>
    /// <param name="targetIsInstantiable">
    /// Whether the cycle target is a class/struct that is not abstract
    /// (<c>RecursionClassifier.IsInstantiable</c>).
    /// </param>
    internal static bool CanConstructCycleTarget(string formatTag, bool targetIsInstantiable) =>
        targetIsInstantiable || DecisionTables.PolymorphicCycleFormats.Contains(formatTag);

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

    /// <summary>
    /// Does this format's INLINE (nested-array) emitter have a form for this LEAF kind? A
    /// format missing from <see cref="DecisionTables.InlineNestedLeafKindsByFormat"/> has no
    /// limit, i.e. every kind the leaf resolves to is written.
    /// <para>
    /// The question only exists for the INNERMOST element of a chain: the levels above it
    /// are bracket pairs in every format. A false answer drops the member (never a silent
    /// <c>ToString()</c>), so this is a LIMIT and not an <see cref="Effect"/> — the same
    /// family as <see cref="AllowedDictKeyKinds"/>.
    /// </para>
    /// </summary>
    internal static bool InlineNestedLeafKindSupported(string formatTag, string leafKind) =>
        !DecisionTables.InlineNestedLeafKindsByFormat.TryGetValue(formatTag, out var kinds)
        || kinds.Contains(leafKind);

    /// <summary>
    /// Is this format's inline nested-array emitter limited to a closed set of leaf kinds?
    /// <para>
    /// Separate from <see cref="InlineNestedLeafKindSupported"/> because a limited emitter is
    /// also the one whose READ path cannot express a nullable collection level inside the
    /// chain, so the extractor applies the null rule to exactly those formats instead of
    /// narrowing formats whose emitters never had the limit.
    /// </para>
    /// </summary>
    internal static bool InlineNestedLeafKindsAreClosed(string formatTag) =>
        DecisionTables.InlineNestedLeafKindsByFormat.ContainsKey(formatTag);

    /// <summary>
    /// Can this format's generated code write an object with ZERO members — a
    /// <c>[PicoSerializable]</c> type that declares no public instance member, held as a
    /// member or as a collection element?
    /// <para>
    /// The question BU-4 was hiding: without it the shape resolver had no answer for a type
    /// with no members, so the whole MEMBER disappeared and PICOSERDE001 said ⏸ ("this format
    /// can hold it, we have not implemented it"). The answer is a per-format value in
    /// <see cref="DecisionTables.EmptyObjectFormats"/>, read through THIS entry point, so no
    /// generator compares a format tag (I-3) and no second copy of the list exists. All five
    /// formats answer yes today (yaml was the last, once its writer grew the empty-mapping
    /// flow form), which the table's own doc explains — and a missing format is never a claim
    /// that the FORMAT cannot.
    /// </para>
    /// <para>
    /// A <c>false</c> is not a verdict on its own: the shape resolver keeps the member
    /// unresolvable, i.e. the drop and its diagnostic are exactly what they are today (⏸ for a
    /// format that can hold it, ⛔ never). This method answers "does our code have the form".
    /// </para>
    /// </summary>
    internal static bool EmptyObjectSupported(string formatTag) =>
        DecisionTables.EmptyObjectFormats.Contains(formatTag);

    /// <summary>
    /// Does this format's ANONYMOUS-TYPE emitter have a form for a COLLECTION member?
    /// <para>
    /// The question the anonymous interceptor path (the sixth emit surface, spec D-8) used to
    /// answer with <c>fmtTag is "json"</c>. It is a LIMIT of our emitter — every format can
    /// hold a <c>List&lt;int&gt;</c> (spec §3 S1) — so a "no" here means the member is dropped
    /// with the ⏸ debt verdict, never with a claim about the format. Kept out of
    /// <see cref="Decision"/> on purpose: a Decision value is a point on a SHAPE's path, and
    /// an anonymous type is not one of the modelled shapes.
    /// </para>
    /// </summary>
    internal static bool AnonCollectionSupported(string formatTag) =>
        DecisionTables.AnonCollectionFormats.Contains(formatTag);

    /// <summary>
    /// Is the anonymous-type collection drop a FORMAT fact (⛔) or our gap (⏸)?
    /// <para>
    /// Derived from the facts table instead of being written into the message: the shape being
    /// refused is a flat scalar collection (<see cref="FlatScalarListShape"/>), whose
    /// representability every one of the five formats already answers. Today all five answer
    /// "yes", so the drop is uniformly ⏸ — and the day a format gains a "no" (a sixth format,
    /// or a corrected fact), the wording follows the table without anyone editing a sentence
    /// (spec C1: our progress must never be written as a format fact).
    /// </para>
    /// </summary>
    internal static bool AnonCollectionIsFormatFact(string formatTag) =>
        CapabilityOf(formatTag, FlatScalarListShape) == Capability.Impossible;

    /// <summary>
    /// The shape an anonymous-type collection member has: a flat list of scalars (spec §3 S1).
    /// Named here so the derivation above cannot quietly point at a different row.
    /// </summary>
    private const string FlatScalarListShape = "S1";

    // ── README matrix (Task 8) ─────────────────────────────────────────────────────

    /// <summary>
    /// The five cells of a shape's row as a token string ("✅✅✅✅❌"), in
    /// <see cref="AllFormatTags"/> order, using the <b>TERMINAL</b> effects
    /// (<see cref="DecideShapeTarget"/>) — spec §6's口径. Two tokens exist: ✅ for
    /// <see cref="Effect.Supported"/> and ❌ for any drop.
    /// <para>
    /// Why the target table and not today's (<see cref="DecideShape"/>): the README matrix
    /// answers "what can this format express", which is the promise, not a changelog. A shape
    /// the format can hold but this generator still drops is a ⏸ DEBT (spec §5's diagnostic
    /// buckets: the message says "not implemented yet", with a <c>reopen:</c> pointer), and
    /// printing ❌ there would turn a temporary gap into a false statement about the FORMAT —
    /// the same class of lie as the R1 <c>toml × S11b</c> cell, which said ✅ while the guard
    /// still dropped the member. That rule covers the debt THIS GENERATOR can pay on its own,
    /// and it is why the two口径 differ: such a debt is ✅ here and dropped by
    /// <see cref="DecideShape"/>.
    /// </para>
    /// <para>
    /// The third state goes the OTHER way, and deliberately: a
    /// <see cref="Capability.Deferred"/> cell (spec §3 S2 × ini today) is ❌ in BOTH口径 —
    /// today, because the member is dropped; and here, because what is missing is not code we
    /// owe this release but a wire CONVENTION (an <c>x+1</c>-level public agreement, spec §8.1).
    /// ✅ promises that "the generated code both writes and reads the shape"; no such code
    /// exists, and none can be written without that agreement. So the ⏸ rule above is scoped to
    /// a debt payable in code — a debt whose way out crosses the wire format stays ❌ until the
    /// convention lands.
    /// </para>
    /// <para>
    /// The other direction cannot happen by construction: <c>DecisionTables.BuildTargetRules</c>
    /// never marks a pair Supported unless every shape using it is
    /// <see cref="Capability.Representable"/> (a Deferred shape is not), so this matrix can
    /// never claim a cell the format facts deny, and can never promote a deferred one. After R2
    /// Tasks 2–6 the two tables agree on every pair, which is why this口径 change moves no token
    /// today — the cells it moves are the README's, which still documented the R1 release (R2
    /// Task 7).
    /// </para>
    /// </summary>
    internal static string MatrixCells(string shapeId) =>
        string.Concat(
            AllFormatTags.Select(f =>
                DecideShapeTarget(f, shapeId) == Effect.Supported ? "\u2705" : "\u274C"
            )
        );

    // ── helpers ────────────────────────────────────────────────────────────────────

    /// <summary>Impossible wins over Deferred, which wins over Representable — the same
    /// strictest-wins fold <see cref="Strictest"/> uses for effects.</summary>
    private static Capability Worse(Capability left, Capability right)
    {
        if (left == Capability.Impossible || right == Capability.Impossible)
            return Capability.Impossible;
        if (left == Capability.Deferred || right == Capability.Deferred)
            return Capability.Deferred;
        return Capability.Representable;
    }

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
