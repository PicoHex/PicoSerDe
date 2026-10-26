// Shared source generator infrastructure for PicoSerDe
// Included via <Compile Include> in all 5 format SG projects.

namespace PicoSerDe.Gen;

/// <summary>Format context passed from each SG to shared infrastructure.</summary>
public readonly record struct FormatConfig(
    string SerializerClassName,
    string Namespace,
    string FormatTag, // "json" | "ini" | "msgpack" | "toml" | "yaml"
    string ConstructorAttributeName // e.g. "JsonConstructorAttribute"
);

/// <summary>Shared type descriptor used by all 5 format SGs.</summary>
internal readonly record struct TypeInfo(
    string FullyQualifiedName,
    string Namespace,
    string Name,
    ImmutableArray<PropertyInfo> Properties,
    ImmutableArray<CtorParamInfo> CtorParams = default,
    string? TypeTag = null,
    bool IsRefLikeType = false,
    bool IsValueType = false,
    string? DiscriminatorPropertyName = null,
    ImmutableArray<DerivedTypeInfo> DerivedTypes = default,
    // Non-null when this TypeInfo represents a top-level array or list type (e.g. string[], List<int>).
    // The element kind drives code gen (EmitArraySerializer / EmitArrayDeserializer / EmitListSerializer / EmitListDeserializer).
    string? ArrayElementKind = null,
    string? ArrayElementName = null,
    ImmutableArray<PropertyInfo> ArrayElementNestedProps = default,
    // True when this TypeInfo represents a top-level List<T> (not T[]).
    // Deserialization returns List<T> directly instead of calling .ToArray().
    bool IsTopLevelList = false,
    // Members skipped because they are recursive/self-referencing for this
    // format (TOML/YAML/INI); surfaced as PICOSERDE003 by GenerateAll.
    // True for abstract classes (a polymorphic base that cannot be instantiated).
    bool IsAbstract = false,
    ImmutableArray<SkippedMember> SkippedRecursiveMembers = default,
    // Members skipped because this generator drops them (nested lists in TOML/YAML/INI, a
    // dict value that is a collection, ...); surfaced as PICOSERDE004. Each record also
    // says which side of the ⛔/⏸ line the drop is on (D-4 ②).
    ImmutableArray<SkippedMember> SkippedUnsupportedMembers = default,
    // Members dropped because their TYPE could not be resolved for this format at
    // all; surfaced as PICOSERDE001. Carried on the TypeInfo (not on a shared
    // List<Diagnostic>) because the extraction runs inside the incremental
    // transform, which must stay pure: the record travels with the value and the
    // generator reports it from GenerateAll (D-6).
    ImmutableArray<UnsupportedTypeMember> UnsupportedTypeMembers = default
)
{
    public bool Equals(TypeInfo other) =>
        FullyQualifiedName == other.FullyQualifiedName
        && Namespace == other.Namespace
        && Name == other.Name
        && Properties.SequenceEqual(other.Properties)
        && CtorParams.SequenceEqual(other.CtorParams)
        && TypeTag == other.TypeTag
        && IsRefLikeType == other.IsRefLikeType
        && IsValueType == other.IsValueType
        && IsAbstract == other.IsAbstract
        && IsTopLevelList == other.IsTopLevelList
        && DiscriminatorPropertyName == other.DiscriminatorPropertyName
        && DerivedTypes.SequenceEqual(other.DerivedTypes)
        && SkippedRecursiveMembers.SequenceEqual(other.SkippedRecursiveMembers)
        && SkippedUnsupportedMembers.SequenceEqual(other.SkippedUnsupportedMembers)
        && UnsupportedTypeMembers.SequenceEqual(other.UnsupportedTypeMembers);

    public override int GetHashCode()
    {
        var hash = FullyQualifiedName.GetHashCode();
        hash = (hash * 397) ^ Namespace.GetHashCode();
        hash = (hash * 397) ^ Name.GetHashCode();
        foreach (var p in Properties)
            hash = (hash * 397) ^ p.GetHashCode();
        foreach (var cp in CtorParams)
            hash = (hash * 397) ^ cp.GetHashCode();
        hash = (hash * 397) ^ IsTopLevelList.GetHashCode();
        hash = (hash * 397) ^ IsAbstract.GetHashCode();
        hash = (hash * 397) ^ (DiscriminatorPropertyName?.GetHashCode() ?? 0);
        foreach (var dt in DerivedTypes)
            hash = (hash * 397) ^ dt.GetHashCode();
        foreach (var m in SkippedRecursiveMembers)
            hash = (hash * 397) ^ m.GetHashCode();
        foreach (var m in SkippedUnsupportedMembers)
            hash = (hash * 397) ^ m.GetHashCode();
        foreach (var m in UnsupportedTypeMembers)
            hash = (hash * 397) ^ m.GetHashCode();
        return hash;
    }
}

/// <summary>
/// Why a member was dropped — THREE classes, because "dropped" has three different
/// causes and only one of them is about the FORMAT (spec C4, defects D-4 ② / I-2).
/// <para>
/// ⛔ <see cref="DropVerdict.FormatCannot"/> — the FORMAT cannot express the shape: terminal, the
/// reader has to change the format or the type. ⏸ <see cref="GeneratorGap"/> — the format
/// CAN express it and this generator has not implemented it yet: our debt (spec R4),
/// reopenable, and the reader must not be told to change anything. ⚠
/// <see cref="MemberUnreadable"/> — neither: no format can serialize a value it cannot
/// read, so the member itself has to change (it is not a format limit and it is not our
/// debt; sending the reader to another format would be a dead end).
/// </para>
/// The verdict is decided at the extraction site — the only place that knows WHICH rule
/// fired — by asking the facts table (<see cref="FormatCapabilities.CanRepresent"/>) for
/// that rule's decision point. It is never derived from a format literal (I-3) and never
/// from our implementation status (C1).
/// </summary>
internal enum DropVerdict
{
    /// <summary>⛔ The format cannot express the shape — nothing on our side can fix it.</summary>
    FormatCannot,

    /// <summary>⏸ The format can express it; this generator has not implemented it yet.</summary>
    GeneratorGap,

    /// <summary>⚠ The member itself is not serializable in ANY format (no getter to read).</summary>
    MemberUnreadable,
}

/// <summary>
/// One member this generator dropped during extraction, together with the side of the
/// line it fell on (<see cref="DropVerdict"/>).
/// <para>
/// The verdict travels with the record so the reporting site (GenerateAll) does not have
/// to re-derive it: only the extraction site knows which rule fired.
/// </para>
/// </summary>
internal readonly record struct SkippedMember(
    // "Namespace.Type.Member", as the user declared it.
    string Name,
    // The member's declared type text, so the message can name the SHAPE (I-5) without
    // the reader having to look the member up.
    string DeclaredType,
    // ⛔ / ⏸ / ⚠ — see <see cref="DropVerdict"/>.
    DropVerdict Verdict
);

/// <summary>
/// One member that never reached the generated code because its declared type is
/// not resolvable for the current format (a class with no public members, a
/// collection whose element kind this format cannot express, a cycle the format
/// cannot hold, ...). These are exactly the values the PICOSERDE001 message
/// needs, so the diagnostic can name the member, the reason and the way out.
/// </summary>
internal readonly record struct UnsupportedTypeMember(
    string TypeName,
    string MemberName,
    string DeclaredTypeName,
    string Reason,
    // Where the member is declared, so PICOSERDE001 is navigable (file:line in the
    // build log and the IDE error list) and suppressible per site with
    // `#pragma warning disable PICOSERDE001` instead of a project-wide NoWarn — which
    // also silences the next member that is dropped for a real mistake (I-1). A
    // Location describes a span and compares by (tree, span); it holds no symbol and
    // no compilation, so the model stays pure and cacheable.
    Location? Location = null,
    // Which side of the line this drop is on; see <see cref="DropVerdict"/>.
    DropVerdict Verdict = DropVerdict.GeneratorGap
);

/// <summary>Describes one derived type in a polymorphic hierarchy.</summary>
internal readonly record struct DerivedTypeInfo(
    string FullyQualifiedName,
    string TypeDiscriminator
);

/// <summary>Constructor parameter info for [JsonConstructor] support.</summary>
internal readonly record struct CtorParamInfo(
    string Name,
    string TypeKind,
    string TypeFullName,
    string? DateTimeFormat = null,
    string? TypeFullNameAnnotated = null
);

/// <summary>Shared property descriptor used by all 5 format SGs.</summary>
internal readonly record struct PropertyInfo(
    string Name,
    string JsonName,
    string TypeKind,
    string TypeFullName,
    bool IsNullable,
    string? ElementTypeKind,
    string? ElementTypeName,
    string? KeyTypeKind,
    string? KeyTypeName,
    ImmutableArray<PropertyInfo> NestedProperties,
    string? ConverterTypeFullName,
    string? DateTimeFormat = null,
    int? IntKey = null,
    string? SectionName = null,
    string? Comment = null,
    string? NestedElementTypeKind = null,
    byte? ExtensionTag = null,
    bool IsNullableReference = false,
    bool IsRequired = false,
    bool ElementIsNullableReference = false,
    string? IgnoreCondition = null,
    // Declaration-position type text (keeps "?") — null falls back to the identity
    // form (TypeFullName / ElementTypeName). Never use for identifiers or new T().
    string? TypeFullNameAnnotated = null,
    string? ElementTypeNameAnnotated = null,
    // True when this member points back into the type currently being extracted
    // (recursive/cyclic type hierarchy). NestedProperties are empty; the emitter
    // references the target type's own generated helper instead.
    bool IsRecursiveRef = false,
    // Fully-qualified name of the element/value type for container kinds
    // (list/array/dict). For an IsRecursiveRef container this is the actual
    // cycle target used to seed the helper (TypeFullName is only the container).
    string? ElementTypeFullName = null,
    // True when the element/value type is a nullable value type (List<int?>).
    bool ElementIsNullableValue = false
);

/// <summary>Attribute detection helpers — each SG provides its own attribute class names.</summary>
public readonly record struct AttributeHelpers(
    Func<ITypeSymbol, bool> HasCamelCase,
    Func<IPropertySymbol, string?> GetCustomName,
    Func<IPropertySymbol, bool> HasIgnore,
    Func<IPropertySymbol, string?> GetConverterType,
    Func<IPropertySymbol, string?> GetDateTimeFormat,
    Func<IPropertySymbol, int?>? GetIntKey = null,
    Func<IPropertySymbol, string?>? GetSectionName = null,
    Func<ITypeSymbol, string?>? GetComment = null,
    Func<IPropertySymbol, string?>? GetPropertyComment = null,
    bool OverrideKindWithStringOnConverter = false
);

internal static class GenInfrastructure
{
    // ── verdict markers ────────────────────────────────────────────────────────────────
    // ⛔ and ⏸ are NOT defined here. "The FORMAT cannot" vs "we have not implemented it"
    // IS a capability verdict, so both glyphs have their ONE definition in
    // FormatCapabilities (shared by all five generators) and every messageFormat below
    // REFERENCES them:
    //
    //     FormatCapabilities.ImpossibleMarker   ⛔
    //     FormatCapabilities.DeferredMarker     ⏸
    //
    // Never spell a glyph here. A second copy compiles, emits the same bytes and breaks
    // no behavioural test, so `VerdictMarkerGuardTests` fails on any literal ⛔/⏸ outside
    // FormatCapabilities.cs (a rename or a value change in one copy would otherwise leave
    // the two sites disagreeing silently).
    //
    // ⚠ stays local because it is not a capability verdict at all: it says the MEMBER is
    // unreadable, i.e. no format and no generator change can help (review I-2).

    /// <summary>
    /// ⚠ the MEMBER is not serializable in any format (no getter), so neither a format
    /// fact nor our debt: the member has to change. A distinct marker on purpose — the
    /// ⛔ bucket is reserved for "the format cannot", and telling the reader to try another
    /// format here would be a dead end (review I-2).
    /// </summary>
    private const string MemberErrorMarker = "\u26A0";

    /// <summary>
    /// Why a member's declared type could not be resolved for this format. Kept as
    /// constants so the two extraction paths (public field, property) cannot drift
    /// into two different explanations of the same drop.
    /// <para>
    /// A reason says WHAT happened, never which side of the line the drop is on: the same
    /// fact ("it closes a type cycle") is a format fact in INI and our debt in TOML, so
    /// the marker comes from the facts table instead of from the wording (see
    /// <see cref="DropVerdict"/>).
    /// </para>
    /// </summary>
    private const string UnresolvableTypeReason =
        "no shape could be resolved for its declared type";

    private const string RecursiveFieldReason = "it closes a type cycle";

    /// <summary>
    /// A set-only property has no getter, so there is no value to write out — the member
    /// never reaches the generated code. Silent until now (spec review W4 read R2
    /// literally: every dropped member reports).
    /// </summary>
    private const string WriteOnlyPropertyReason =
        "it has no getter, so there is no value to serialize";

    /// <summary>
    /// Defensive: the kind resolver answered "dict" but the declared type is not an
    /// <c>INamedTypeSymbol</c> with two type arguments, so its key and value types cannot
    /// be inspected. Reported instead of a bare <c>continue</c> (R2 has no silent exit).
    /// </summary>
    private const string UninspectableDictionaryReason =
        "its declared type resolves to a dictionary whose key and value types cannot be inspected";

    /// <summary>
    /// A member whose TYPE could not be resolved for the current format never reached the
    /// generated code. Reported as PICOSERDE001 (D-6) with the member name, the declared
    /// type (the shape), the format, a reason and a way out — a warning the reader cannot
    /// act on is noise.
    /// <para>
    /// THREE templates under ONE id (D4: the id is the public contract, the wording is
    /// not): {0} member, {1} declared type, {2} format, {3} reason. ⛔ says the format
    /// cannot express it (terminal); ⏸ says the format can and the generator has not
    /// implemented it yet (our debt, spec R4); ⚠ says the MEMBER cannot be read at all, so
    /// no format and no generator change can help (review I-2). All three are greppable on
    /// their marker, and each marker is a constant, not a literal in the template.
    /// </para>
    /// </summary>
    private static readonly DiagnosticDescriptor UnsupportedTypeImpossibleWarning = new(
        id: "PICOSERDE001",
        title: "Member dropped: this format cannot represent its type",
        messageFormat: FormatCapabilities.ImpossibleMarker
            + " Member '{0}' (declared type '{1}') was dropped by the {2} source generator: {3}. The {2} format cannot represent it, so nothing on the generator side can change that — use a format that can, or change the member's type.",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// The ⏸ sibling of <see cref="UnsupportedTypeImpossibleWarning"/>: the same drop, but
    /// the format CAN express the shape — the gap is ours (spec R4), with the same id so
    /// `NoWarn PICOSERDE001` keeps working for both. The tail is the REOPEN POINTER C4 asks
    /// for (<c>reopen:</c>): the condition lives in spec §3/§6, and the message has to say
    /// where — "recorded gap" alone tells the reader it is reopenable but not where the
    /// condition is written down (spec review D2).
    /// </summary>
    private static readonly DiagnosticDescriptor UnsupportedTypeDeferredWarning = new(
        id: "PICOSERDE001",
        title: "Member dropped: not implemented yet (this format can hold it)",
        messageFormat: FormatCapabilities.DeferredMarker
            + " Member '{0}' (declared type '{1}') was dropped by the {2} source generator: {3}. The {2} format can express this shape, but this generator has not implemented it yet — the drop is a recorded gap, not a format limit. reopen: the condition is tracked as debt in the format-capabilities matrix (spec §3/§6).",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// The ⚠ third class (review I-2): the member has no getter, so there is no value to
    /// write in ANY format. It is neither a format limit (the type may be perfectly
    /// expressible) nor our debt (no generator change can serialize what cannot be read),
    /// so it must not borrow either wording: "the json format cannot represent `int`" is a
    /// false sentence, and "use a format that can" sends the reader to four dead ends.
    /// Same id, same severity, same suppressions.
    /// </summary>
    private static readonly DiagnosticDescriptor UnsupportedTypeMemberErrorWarning = new(
        id: "PICOSERDE001",
        title: "Member dropped: it has no getter, so there is no value to serialize",
        messageFormat: MemberErrorMarker
            + " Member '{0}' (declared type '{1}') was dropped by the {2} source generator: {3}. No format can serialize a member it cannot read, so another format will not help — make the member readable (add a getter) or remove it.",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// Two distinct fully-qualified names can normalize to the same generated
    /// file name (SafeName replaces '.', '&lt;', ... with '_'). Without this
    /// guard the second AddSource throws and the generator drops every file.
    /// </summary>
    private static readonly DiagnosticDescriptor HintNameCollisionWarning = new(
        id: "PICOSERDE002",
        title: "Generated file name collision",
        messageFormat: "Type '{0}' was skipped: generated file name '{1}' collides with another type",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// A recursive/self-referencing member is skipped by the section-based generators
    /// (TOML/YAML/INI); this shared diagnostic makes the skip visible (never silent).
    /// <para>
    /// {0} member, {1} format, {2} declared type. A cycle is a format fact only where the
    /// facts table says so (INI); TOML and YAML CAN hold one (spec §3: S11a/S11b), so their
    /// skip is ⏸ — the old single wording ("cannot be represented by this format")
    /// told those users to change a type that is perfectly expressible (D-4 ②).
    /// </para>
    /// </summary>
    private static readonly DiagnosticDescriptor RecursiveMemberImpossibleWarning = new(
        id: "PICOSERDE003",
        title: "Recursive member dropped: this format cannot hold a type cycle",
        messageFormat: FormatCapabilities.ImpossibleMarker
            + " Member '{0}' (declared type '{2}') closes a type cycle, and the {1} format cannot represent one. Use a format that can hold recursion, or remove the cycle.",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// The ⏸ sibling of <see cref="RecursiveMemberImpossibleWarning"/>: same id, same
    /// member, but the format can hold the cycle (TOML/YAML today). The <c>reopen:</c> tail
    /// is the pointer spec C4 asks for — the condition lives in spec §3/§6.
    /// </summary>
    private static readonly DiagnosticDescriptor RecursiveMemberDeferredWarning = new(
        id: "PICOSERDE003",
        title: "Recursive member dropped: not implemented yet (this format can hold a cycle)",
        messageFormat: FormatCapabilities.DeferredMarker
            + " Member '{0}' (declared type '{2}') closes a type cycle; the {1} format can represent one, but this generator has not implemented it yet. The drop is a recorded gap, not a format limit. reopen: the condition is tracked as debt in the format-capabilities matrix (spec §3/§6).",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// Reports PICOSERDE003 for every recursive member skipped during extraction, in the
    /// ⛔ or the ⏸ wording the extraction already decided (it is the only place that
    /// knows which rule fired). Called by the TOML/YAML/INI generators from GenerateAll
    /// (JSON/MsgPack support recursion and never record skips).
    /// </summary>
    public static void ReportSkippedRecursiveMembers(
        SourceProductionContext spc,
        IEnumerable<TypeInfo> typeInfos,
        string formatTag
    )
    {
        // A type discovered from several usage sites produces one TypeInfo per
        // pipeline entry, each carrying the same skip: report every member once.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in typeInfos)
        {
            if (t.SkippedRecursiveMembers.IsDefaultOrEmpty)
                continue;
            foreach (var member in t.SkippedRecursiveMembers)
                if (reported.Add(member.Name))
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            DescribeRecursive(member.Verdict),
                            null,
                            member.Name,
                            formatTag,
                            member.DeclaredType
                        )
                    );
        }
    }

    /// <summary>
    /// PICOSERDE003's template for a verdict. A cycle is only ever ⛔ or ⏸ (a member with no
    /// getter never reaches this list); the third class falls back to the ⏸ wording rather
    /// than inventing a fourth sentence, and the switch keeps the mapping exhaustive.
    /// </summary>
    private static DiagnosticDescriptor DescribeRecursive(DropVerdict verdict) =>
        verdict == DropVerdict.FormatCannot
            ? RecursiveMemberImpossibleWarning
            : RecursiveMemberDeferredWarning;

    /// <summary>
    /// Leading keyword of an if/else-if dispatch chain: "if" for the first
    /// emitted branch, "else if" afterwards. Track with a boolean, never with
    /// the loop index — an index-based check emits a dangling "else if" when an
    /// earlier iteration was skipped (e.g. complex/recursive members).
    /// </summary>
    public static string ChainKeyword(ref bool first)
    {
        var keyword = first ? "if" : "else if";
        first = false;
        return keyword;
    }

    /// <summary>
    /// A member this generator drops instead of emitting non-compiling or lossy code
    /// (a nested list in TOML/YAML/INI, a dict value that is a collection, ...).
    /// <para>
    /// {0} member, {1} format, {2} declared type. TWO templates under ONE id (D4: the id
    /// is the public contract, the wording is not): ⛔ when the format cannot express the
    /// shape, ⏸ when it can and the drop is our gap (spec R4). This is D-4 ②: the old
    /// single "cannot be represented by this format" wording told the user of a ⏸ cell
    /// (S14 x yaml, a dict value that is a collection) to change a type that is fine.
    /// </para>
    /// </summary>
    private static readonly DiagnosticDescriptor UnsupportedMemberImpossibleWarning = new(
        id: "PICOSERDE004",
        title: "Member dropped: this format cannot represent its shape",
        messageFormat: FormatCapabilities.ImpossibleMarker
            + " Member '{0}' (declared type '{2}') cannot be represented by the {1} format. Use a format that can, or change the member's type.",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// The ⏸ sibling of <see cref="UnsupportedMemberImpossibleWarning"/>. The <c>reopen:</c>
    /// tail is the pointer spec C4 asks for — the condition lives in spec §3/§6.
    /// </summary>
    private static readonly DiagnosticDescriptor UnsupportedMemberDeferredWarning = new(
        id: "PICOSERDE004",
        title: "Member dropped: not implemented yet (this format can represent it)",
        messageFormat: FormatCapabilities.DeferredMarker
            + " Member '{0}' (declared type '{2}') is dropped by the {1} generator even though the {1} format can represent it: this generator has not implemented it yet. The drop is a recorded gap, not a format limit. reopen: the condition is tracked as debt in the format-capabilities matrix (spec §3/§6).",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// Reports PICOSERDE004 for every unsupported member skipped during extraction, in the
    /// ⛔ or the ⏸ wording the extraction already decided. Called by all five
    /// generators from GenerateAll.
    /// </summary>
    public static void ReportUnsupportedMembers(
        SourceProductionContext spc,
        IEnumerable<TypeInfo> typeInfos,
        string formatTag
    )
    {
        // A type discovered from several usage sites carries the same skip on
        // every entry: report each member once.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in typeInfos)
        {
            if (t.SkippedUnsupportedMembers.IsDefaultOrEmpty)
                continue;
            foreach (var member in t.SkippedUnsupportedMembers)
                if (reported.Add(member.Name))
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            DescribeUnsupported(member.Verdict),
                            null,
                            member.Name,
                            formatTag,
                            member.DeclaredType
                        )
                    );
        }
    }

    /// <summary>PICOSERDE004's template for a verdict (never ⚠: a member that reaches this
    /// list was resolved, it is the shape that is dropped).</summary>
    private static DiagnosticDescriptor DescribeUnsupported(DropVerdict verdict) =>
        verdict == DropVerdict.FormatCannot
            ? UnsupportedMemberImpossibleWarning
            : UnsupportedMemberDeferredWarning;

    /// <summary>PICOSERDE001's template for a verdict — the only id that has all three.</summary>
    private static DiagnosticDescriptor DescribeUnsupportedType(DropVerdict verdict) =>
        verdict switch
        {
            DropVerdict.FormatCannot => UnsupportedTypeImpossibleWarning,
            DropVerdict.MemberUnreadable => UnsupportedTypeMemberErrorWarning,
            _ => UnsupportedTypeDeferredWarning,
        };

    /// <summary>
    /// Accumulates the members dropped because their type is unresolvable. The
    /// extraction runs inside the incremental transform, which must stay pure, so
    /// it cannot hold a <see cref="SourceProductionContext"/> or a shared
    /// <c>List&lt;Diagnostic&gt;</c>; it records data on the <see cref="TypeInfo"/>
    /// instead and GenerateAll reports it (D-6).
    /// </summary>
    private sealed class UnsupportedTypeMemberCollector
    {
        private readonly List<UnsupportedTypeMember> _members = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        /// <summary>
        /// Records one dropped member. Keyed by (type, member): the same type is
        /// extracted once per pipeline entry, per nesting depth and per generic
        /// instantiation, but a member must warn exactly once.
        /// </summary>
        public void Add(
            string typeName,
            string memberName,
            string declaredTypeName,
            string reason,
            Location? location,
            DropVerdict verdict
        )
        {
            if (_seen.Add(typeName + "." + memberName))
                _members.Add(
                    new UnsupportedTypeMember(
                        typeName,
                        memberName,
                        declaredTypeName,
                        reason,
                        location ?? Location.None,
                        verdict
                    )
                );
        }

        public ImmutableArray<UnsupportedTypeMember> ToImmutable() => _members.ToImmutableArray();
    }

    /// <summary>
    /// The declaration site to attribute to a dropped member. A symbol carries one
    /// location per declaration part: a partial member contributes the generated and the
    /// implementation part, and a symbol that only exists in a referenced assembly
    /// contributes a metadata file. Neither of those is a place the user can click or wrap
    /// in a pragma, so the first <b>source</b> location wins. Falls back to
    /// <see cref="Location.None"/> rather than <c>null</c>: a null location is exactly what
    /// made PICOSERDE001 unlocatable and unsuppressable per site (I-1).
    /// </summary>
    private static Location SourceLocationOf(ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
            if (location.IsInSource)
                return location;

        return Location.None;
    }

    /// <summary>
    /// How a dropped member's declared type is spelled in a diagnostic: source-like (no
    /// <c>global::</c> noise) but nullability-preserving, because <c>Row</c> and <c>Row?</c>
    /// are different SHAPES — the difference between a nesting drop and a null drop (I-5).
    /// </summary>
    private static readonly SymbolDisplayFormat DeclaredTypeFormat =
        SymbolDisplayFormat.CSharpErrorMessageFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.CSharpErrorMessageFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    /// <summary>
    /// How the DECLARING type of a dropped member is spelled: as the type is declared, i.e.
    /// without the `?` that belongs to the property's use of it. `Doc2?.Child` is not a name
    /// a reader can search for, nor one they ever wrote (spec §4 D-4 ①).
    /// </summary>
    private static readonly SymbolDisplayFormat DeclaringTypeNameFormat =
        SymbolDisplayFormat.CSharpErrorMessageFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.CSharpErrorMessageFormat.MiscellaneousOptions
                & ~SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    /// <summary>The declared-type text for a diagnostic — see <see cref="DeclaredTypeFormat"/>.</summary>
    private static string DeclaredTypeOf(ITypeSymbol type) =>
        type.ToDisplayString(DeclaredTypeFormat);

    /// <summary>
    /// The verdict for "no shape could be resolved for this declared type", the one channel
    /// with no decision point of its own.
    /// <para>
    /// It is ⏸ by default — an unnameable kind is an encoding gap (char/Half/... resolve only
    /// for json), so no fact says the format cannot hold the value. The one exception is a
    /// collection whose ELEMENT is ITSELF a collection: <c>List&lt;HashSet&lt;int&gt;&gt;</c> (S8)
    /// makes the resolver give up one level too early, and the element-position cell
    /// (<see cref="Decision.ExtendedKindAsElement"/>) is a format fact there — INI has no
    /// nesting at all (spec §3 S8 x ini = ❌), so reporting ⏸ with "the ini format can
    /// express this shape" was the mirror image of D-4 ② (review I-1).
    /// </para>
    /// <para>
    /// The predicate is deliberately narrow: it asks the element cell only when the element
    /// is itself a collection, so S7 (<c>HashSet&lt;int&gt;</c>, whose element is <c>int</c>) is
    /// never asked and keeps its ⏸ in every format — guessing a decision point from "the
    /// declared type is a collection" would have flipped a ✅ cell to ⛔ and swapped one wrong
    /// message for another. Both candidate points (element / nested chain) agree on all five
    /// formats, so an unresolvable chain cannot be judged the wrong way round.
    /// </para>
    /// </summary>
    private static DropVerdict UnresolvableDropVerdict(ITypeSymbol type, string formatTag) =>
        ElementIsItselfACollection(type)
        && !FormatCapabilities.CanRepresent(formatTag, Decision.ExtendedKindAsElement)
            ? DropVerdict.FormatCannot
            : DropVerdict.GeneratorGap;

    /// <summary>
    /// True when <paramref name="type"/> is a collection whose element is itself a collection
    /// (<c>List&lt;HashSet&lt;int&gt;&gt;</c>, <c>int[][]</c>, <c>List&lt;List&lt;int&gt;&gt;</c>). Precision
    /// matters: <c>HashSet&lt;int&gt;</c> is a collection whose element is <c>int</c>, so it does
    /// NOT qualify (see <see cref="UnresolvableDropVerdict"/>).
    /// </summary>
    private static bool ElementIsItselfACollection(ITypeSymbol type)
    {
        ITypeSymbol? element = type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { TypeArguments.Length: 1 } named => named.TypeArguments[0],
            _ => null,
        };

        return element is IArrayTypeSymbol
            || element is INamedTypeSymbol { TypeArguments.Length: 1 };
    }

    /// <summary>
    /// Records one member this generator drops from a list/dict path, together with the
    /// side of the ⛔/⏸ line it fell on: ⛔ when the FACTS table says the format
    /// cannot express decision point <paramref name="point"/>, ⏸ when it can and the
    /// drop is our gap (spec R4).
    /// <para>
    /// Asking here — at the site that knows WHICH rule fired — is what keeps the message
    /// honest: the same member is a ⛔ in one format and a ⏸ in the next, and the
    /// answer must come from the facts (C1), never from a format literal (I-3).
    /// </para>
    /// </summary>
    private static SkippedMember SkippedBy(
        INamedTypeSymbol type,
        IPropertySymbol member,
        string formatTag,
        Decision point
    ) =>
        new(
            $"{type.ToDisplayString(DeclaringTypeNameFormat)}.{member.Name}",
            DeclaredTypeOf(member.Type),
            !FormatCapabilities.CanRepresent(formatTag, point)
                ? DropVerdict.FormatCannot
                : DropVerdict.GeneratorGap
        );

    /// <summary>
    /// Records a member dropped because our emitters cannot key its dictionary's key kind,
    /// with the verdict the capability TABLE gives that drop (review I-3).
    /// <para>
    /// The key set in <see cref="FormatCapabilities.AllowedDictKeyKinds"/> is what the
    /// generated code implements, so the fact that a kind is missing from it does NOT by
    /// itself make the drop a format fact: a format that accepts fewer key kinds than the
    /// model's widest set is the one narrowing (INI reads a key as raw line text, so only
    /// <c>string</c> can be a key there) ⇒ ⛔. A format whose key set is the widest one has
    /// narrowed nothing; a kind outside it (an object, a float, a list) is a case our
    /// emitters never wrote ⇒ our gap ⏸, never "the yaml format cannot represent it" (C1:
    /// our progress must not be written as a format fact).
    /// </para>
    /// </summary>
    private static SkippedMember SkippedByDictKeyKind(
        INamedTypeSymbol type,
        IPropertySymbol member,
        string formatTag
    ) =>
        new(
            $"{type.ToDisplayString(DeclaringTypeNameFormat)}.{member.Name}",
            DeclaredTypeOf(member.Type),
            FormatNarrowsDictKeySet(formatTag) ? DropVerdict.FormatCannot : DropVerdict.GeneratorGap
        );

    /// <summary>
    /// Did THIS format narrow the dictionary key set, or is the missing key kind ours?
    /// <para>
    /// Derived from the table (<see cref="FormatCapabilities.AllowedDictKeyKinds"/>) — a
    /// format that declares fewer key kinds than the widest format does is stating a limit
    /// of its own (INI); one that declares the widest set is simply mirroring what our
    /// emitters implement, and a kind beyond that set is unimplemented code, not an
    /// unrepresentable format. No second copy of the key sets, no format literal.
    /// </para>
    /// </summary>
    private static bool FormatNarrowsDictKeySet(string formatTag)
    {
        var narrowed = FormatCapabilities.AllowedDictKeyKinds(formatTag).Count;
        foreach (var fmt in FormatCapabilities.AllFormatTags)
            if (FormatCapabilities.AllowedDictKeyKinds(fmt).Count > narrowed)
                return true;

        return false;
    }

    /// <summary>
    /// Turns the per-<see cref="TypeInfo"/> record of dropped members into
    /// PICOSERDE001 diagnostics. Separate from
    /// <see cref="ReportCollectedDiagnostics"/> so a generator expresses the
    /// intent ("report what extraction collected") in two short calls, and so the
    /// dedup below is the single place that decides when a member has already been
    /// reported.
    /// </summary>
    public static IEnumerable<Diagnostic> CollectUnsupportedTypeMemberDiagnostics(
        IEnumerable<TypeInfo> typeInfos,
        string formatTag
    )
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in typeInfos)
        {
            if (t.UnsupportedTypeMembers.IsDefaultOrEmpty)
                continue;
            foreach (var m in t.UnsupportedTypeMembers)
            {
                if (!reported.Add(m.TypeName + "." + m.MemberName))
                    continue;
                yield return Diagnostic.Create(
                    DescribeUnsupportedType(m.Verdict),
                    m.Location ?? Location.None,
                    m.TypeName + "." + m.MemberName,
                    m.DeclaredTypeName,
                    formatTag,
                    m.Reason
                );
            }
        }
    }

    /// <summary>
    /// Reports an already-collected diagnostic list. Every generator calls this
    /// next to <see cref="ReportUnsupportedMembers"/>; the (id, message) guard
    /// keeps a member from being reported twice when several pipelines discovered
    /// the same type.
    /// </summary>
    public static void ReportCollectedDiagnostics(
        SourceProductionContext spc,
        IEnumerable<Diagnostic> diagnostics
    )
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in diagnostics)
            if (reported.Add(d.Id + "|" + d.GetMessage()))
                spc.ReportDiagnostic(d);
    }

    /// <summary>
    /// An anonymous-type collection member can only be emitted by the JSON writer:
    /// MsgPack's array/map headers require an element count up front, TOML/INI embed
    /// the key in every scalar call and YAML writes per-item sequence entries. Such a
    /// member is dropped with this diagnostic instead of vanishing silently (review
    /// Task 2 / Important 2 — anonymous collection members are not supported by the
    /// section-based formats).
    /// </summary>
    private static readonly DiagnosticDescriptor UnsupportedAnonCollectionMemberWarning = new(
        id: "PICOSERDE005",
        title: "Anonymous-type collection member skipped by this format",
        messageFormat: "Anonymous-type member '{0}' cannot be serialized by {1} — it will be ignored; use a named DTO instead",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// Reports PICOSERDE005 for an anonymous-type collection member the format's
    /// writer cannot emit. Called from <c>AnonTypeHandler</c> while generating the
    /// interceptor, so the drop is visible in the build log instead of silently
    /// losing the member's data.
    /// </summary>
    public static void ReportUnsupportedAnonCollectionMember(
        SourceProductionContext spc,
        string formatTag,
        string memberName
    ) =>
        spc.ReportDiagnostic(
            Diagnostic.Create(UnsupportedAnonCollectionMemberWarning, null, memberName, formatTag)
        );

    /// <summary>Reports a skipped type whose generated file name collided.</summary>
    public static void ReportHintNameCollision(
        SourceProductionContext spc,
        string hintName,
        string? fullyQualifiedName
    )
    {
        spc.ReportDiagnostic(
            Diagnostic.Create(
                HintNameCollisionWarning,
                null,
                fullyQualifiedName ?? "(unknown)",
                hintName
            )
        );
    }

    public static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: var expr })
            return false;

        var name = expr switch
        {
            MemberAccessExpressionSyntax { Name: var n } => n,
            MemberBindingExpressionSyntax { Name: var n } => n,
            _ => null,
        };

        var methodName = name switch
        {
            GenericNameSyntax gn => gn.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => null,
        };

        return methodName
            is "Serialize"
                or "SerializeToUtf8Bytes"
                or "Deserialize"
                or "SerializeLines"
                or "DeserializeLines"
                or "DeserializeFromStreamAsync"
                or "DeserializeAsyncEnumerable";
    }

    /// <summary>
    /// Escapes \\, \", \n, \r, \t for safe embedding in generated C# string literals.
    /// </summary>
    public static string EscapeCSharpString(string s) =>
        s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");

    /// <summary>Converts a fully qualified type name to a safe identifier (replaces . and :: with _).</summary>
    public static string SafeName(string fullName)
    {
        var cleaned = fullName
            .Replace("global::", "")
            .Replace('.', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace(',', '_')
            .Replace(' ', '_')
            .Replace('[', '_')
            .Replace(']', '_')
            .Replace('(', '_')
            .Replace(')', '_');
        // Final pass: any remaining char that is not identifier-safe becomes '_'
        // (guards weird type display names, e.g. ValueTuple '(int, string)').
        var span = cleaned.ToCharArray();
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
                span[i] = '_';
        }
        return new string(span);
    }

    /// <summary>
    /// Per-generation assembly prefix for namespace isolation of generated helpers.
    /// Set by each SG before invoking GenerateAll. Backed by AsyncLocal so
    /// concurrent generator runs in the same process (VBCSCompiler builds
    /// projects in parallel) cannot observe each other's prefix.
    /// InnerClassName prepends this as a namespace prefix to avoid CS0436
    /// conflicts when two assemblies reference the same nested types.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<string?> _assemblyPrefix = new();

    public static string? AssemblyPrefix
    {
        get => _assemblyPrefix.Value;
        set => _assemblyPrefix.Value = value;
    }

    /// <summary>Returns the fully qualified inner helper class name (e.g. "global::Ns.Sub_TypeJsonInner").</summary>
    /// <summary>
    /// Collision-free generated identifier for a type: <see cref="SafeName"/> plus
    /// a stable FNV-1a 64-bit hash of the normalized fully-qualified name.
    /// Distinct FQNs can normalize to the same SafeName ('.' → '_'), so every
    /// generated file/class name must use this instead of SafeName.
    /// The input is normalized (global:: stripped) so declaration and reference
    /// sites always derive the same name from the same type.
    /// </summary>
    public static string UniqueName(string fullName)
    {
        var normalized = (fullName ?? "").Replace("global::", "");
        return SafeName(normalized) + "_" + StableHash(normalized);
    }

    /// <summary>
    /// Deterministic, culture-invariant FNV-1a 64-bit hash as 16 hex chars.
    /// Stable across processes and builds (safe for generated names).
    /// </summary>
    public static string StableHash(string value)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static string InnerClassName(string suffix, string typeFullName)
    {
        // For generic type names (containing '<'), emit at global scope.
        if (typeFullName.Contains('<'))
            return $"global::{UniqueName(typeFullName)}{suffix}";

        var safeName = UniqueName(typeFullName);
        // With an assembly prefix, the inner class lives directly under the
        // prefix namespace (flat). Without one, it lives in the type's own
        // namespace — the legacy layout still emitted by the non-JSON
        // generators, whose helper files declare the type's namespace.
        if (AssemblyPrefix is not null)
            return $"{AssemblyPrefix}.{safeName}{suffix}";
        var lastDot = typeFullName.LastIndexOf('.');
        if (lastDot <= 0)
            return $"{safeName}{suffix}";
        return $"{typeFullName.Substring(0, lastDot)}.{safeName}{suffix}";
    }

    /// <summary>
    /// Reads the cross-format [PicoIgnore] condition from a property symbol.
    /// Returns "Always", "Never", "WhenWritingNull", "WhenWritingDefault",
    /// or null when the attribute is absent.
    /// </summary>
    private static string? GetPicoIgnoreCondition(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name != "PicoIgnoreAttribute"
                || attr.AttributeClass.ContainingNamespace?.ToDisplayString() != "PicoSerDe.Core"
            )
                continue;
            foreach (var na in attr.NamedArguments)
            {
                if (na.Key == "Condition" && na.Value.Value is int v)
                    return v switch
                    {
                        1 => "Never",
                        2 => "WhenWritingNull",
                        3 => "WhenWritingDefault",
                        _ => "Always",
                    };
            }
            return "Always";
        }
        return null;
    }

    /// <summary>
    /// Single source of truth for DefaultIgnoreCondition guard eligibility:
    /// a member participates in null-based omission when it is a nullable
    /// value type or an annotated nullable reference (including collections).
    /// </summary>
    public static bool IsConditionallyOmittable(PropertyInfo p) =>
        p.IsNullable || p.IsNullableReference;

    /// <summary>
    /// Emits the always-omit null guard (used by formats without a runtime
    /// null representation on the wire path: TOML, INI, YAML). Returns true
    /// when a guard block was opened; the caller must emit the matching
    /// closing brace. The lifted '!= null' comparison is valid for both
    /// Nullable&lt;T&gt; and reference types.
    /// </summary>
    public static bool EmitNullGuardOpen(
        StringBuilder sb,
        PropertyInfo p,
        string accessor,
        string indent
    )
    {
        if (!IsConditionallyOmittable(p))
            return false;
        sb.Append(indent);
        sb.Append("if (");
        sb.Append(accessor);
        sb.AppendLine(" != null)");
        sb.Append(indent);
        sb.AppendLine("{");
        return true;
    }

    /// <summary>
    /// True for TypeKinds whose WhenWritingDefault guard compares with
    /// '!= default' instead of '!= null' (numeric-like value kinds).
    /// </summary>
    public static bool IsValueDefaultKind(string kind) =>
        kind
            is "int32"
                or "int64"
                or "int16"
                or "uint16"
                or "sbyte"
                or "byte"
                or "uint32"
                or "uint64"
                or "char"
                or "half"
                or "biginteger"
                or "int128"
                or "uint128"
                or "nint"
                or "float32"
                or "float64"
                or "boolean"
                or "decimal";

    /// <summary>
    /// True for TypeKinds backed by .NET value types (structs). Used for
    /// WhenWritingDefault guards where '!= default' is the correct check.
    /// </summary>
    public static bool IsValueTypeKind(string kind) =>
        kind
            is "int32"
                or "int64"
                or "int16"
                or "uint16"
                or "sbyte"
                or "byte"
                or "uint32"
                or "uint64"
                or "char"
                or "half"
                or "biginteger"
                or "int128"
                or "uint128"
                or "nint"
                or "datetimeoffset"
                or "float32"
                or "float64"
                or "boolean"
                or "decimal"
                or "guid"
                or "datetime"
                or "dateonly"
                or "timeonly"
                or "timespan"
                or "enum";

    /// <summary>
    /// True for members whose TypeKind is handled by dedicated emit paths
    /// (nested objects and dictionaries) and therefore skipped in flat
    /// member loops such as poly dispatch.
    /// </summary>
    public static bool IsComplexMember(PropertyInfo p) => p.TypeKind is "object" or "dict";

    /// <summary>
    /// The EXTENDED collection kinds (D-2): collections every format writes as a plain
    /// sequence, exactly like <c>list</c>/<c>array</c>, but that need their own construction
    /// on the read side (<c>new HashSet&lt;int&gt;(tmpList)</c>).
    /// <para>
    /// One list, one place: <see cref="TypeKindResolver"/> names these kinds,
    /// <see cref="IsSequenceKind"/> treats them as sequences, and
    /// <see cref="EmitCollectionFromList"/> turns the temp list back into the declared type.
    /// A generator that asks <c>kind is "hashset"</c> instead of these helpers is how the
    /// family drifted apart in the first place.
    /// </para>
    /// </summary>
    public static bool IsExtendedCollectionKind(string? kind) =>
        kind
            is "hashset"
                or "queue"
                or "stack"
                or "linkedlist"
                or "immutablearray"
                or "memory"
                or "readonlymemory";

    /// <summary>
    /// True for every TypeKind whose wire form is a SEQUENCE (<c>list</c>, <c>array</c> and
    /// the seven extended collection kinds). This is the predicate the emitters should ask
    /// before treating a member or an element as a sequence; asking <c>is "list" or "array"</c>
    /// is what silently sent <c>HashSet&lt;int&gt;</c> to the scalar fallback.
    /// </summary>
    public static bool IsSequenceKind(string? kind) =>
        kind is "list" or "array" || IsExtendedCollectionKind(kind);

    /// <summary>
    /// True when a sequence kind's VALUE can be null. <c>ImmutableArray&lt;T&gt;</c>,
    /// <c>Memory&lt;T&gt;</c> and <c>ReadOnlyMemory&lt;T&gt;</c> are structs, so a generated
    /// <c>x ?? []</c> or <c>x is null</c> on them does not compile — the yaml emitter asks
    /// this before writing either.
    /// </summary>
    public static bool IsNullableSequenceKind(string? kind) =>
        kind is "list" or "array" or "hashset" or "queue" or "stack" or "linkedlist";

    /// <summary>
    /// The expression that enumerates a sequence value on the WRITE side.
    /// <c>Memory&lt;T&gt;</c>/<c>ReadOnlyMemory&lt;T&gt;</c> are not enumerable, so they are
    /// written through <c>.Span</c>; everything else (List, array, HashSet, Queue, Stack,
    /// LinkedList, ImmutableArray) is enumerated directly.
    /// </summary>
    public static string SequenceItemsExpression(string? kind, string accessor) =>
        kind is "memory" or "readonlymemory" ? accessor + ".Span" : accessor;

    /// <summary>
    /// The <c>Count</c>/<c>Length</c> expression for a sequence value, for the writers that
    /// announce the element count up front (msgpack) or test for an empty sequence (yaml's
    /// <c>- []</c> item).
    /// </summary>
    public static string SequenceCountExpression(string? kind, string accessor) =>
        kind is "array" or "immutablearray" or "memory" or "readonlymemory"
            ? accessor + ".Length"
            : accessor + ".Count";

    /// <summary>
    /// Emits the step that turns the temp <c>List&lt;T&gt;</c> a reader filled back into the
    /// DECLARED collection type. <paramref name="assignTarget"/> receives the assignment, so
    /// the caller decides whether that is <c>obj.Member</c>, a ctor parameter or a list
    /// element.
    /// <para>
    /// The seven extended kinds are why this is shared: json had the only copy, which is how
    /// the other four generators ended up with no conversion at all (and therefore no case
    /// for the kind). One copy here means a new kind is added once.
    /// </para>
    /// </summary>
    public static void EmitCollectionFromList(
        StringBuilder sb,
        string kind,
        string listVar,
        string assignTarget,
        string elemType,
        string indent
    )
    {
        switch (kind)
        {
            case "array":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = ");
                sb.Append(listVar);
                sb.AppendLine(".ToArray();");
                return;
            case "immutablearray":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = System.Collections.Immutable.ImmutableArray.CreateRange(");
                sb.Append(listVar);
                sb.AppendLine(");");
                return;
            case "memory":
            case "readonlymemory":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = new System.");
                sb.Append(kind == "memory" ? "Memory" : "ReadOnlyMemory");
                sb.Append("<");
                sb.Append(elemType);
                sb.Append(">(");
                sb.Append(listVar);
                sb.AppendLine(".ToArray());");
                return;
            case "stack":
                // A Stack<T> enumerates top-of-stack first and its ctor pushes in the given
                // order, so the temp list has to be replayed backwards or the round trip
                // silently reverses the stack.
                sb.Append(indent);
                sb.Append("var __stk = new System.Collections.Generic.Stack<");
                sb.Append(elemType);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.Append("for (int __i = ");
                sb.Append(listVar);
                sb.AppendLine(".Count - 1; __i >= 0; __i--)");
                sb.Append(indent);
                sb.Append("    __stk.Push(");
                sb.Append(listVar);
                sb.AppendLine("[__i]);");
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.AppendLine(" = __stk;");
                return;
            case "hashset":
            case "queue":
            case "linkedlist":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = new System.Collections.Generic.");
                sb.Append(
                    kind switch
                    {
                        "hashset" => "HashSet",
                        "queue" => "Queue",
                        _ => "LinkedList",
                    }
                );
                sb.Append("<");
                sb.Append(elemType);
                sb.Append(">(");
                sb.Append(listVar);
                sb.AppendLine(");");
                return;
            default:
                // "list": the temp list IS the declared type.
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = ");
                sb.Append(listVar);
                sb.AppendLine(";");
                return;
        }
    }

    /// <summary>
    /// Discriminator used when a *concrete* polymorphic base instance is
    /// serialized (there is no matching derived case otherwise, which produced
    /// malformed output such as <c>{"$type":}</c>). Collision-safe against the
    /// declared derived discriminators; abstract bases have no instances and
    /// therefore no case.
    /// </summary>
    public static string? BaseDiscriminator(TypeInfo type)
    {
        if (type.IsValueType || type.DerivedTypes.IsDefaultOrEmpty)
            return null;
        // Abstract bases cannot be instantiated.
        if (type.IsAbstract)
            return null;
        var candidate = type.Name;
        if (type.DerivedTypes.Any(d => d.TypeDiscriminator == candidate))
        {
            candidate = (type.FullyQualifiedName ?? type.Name).Replace("global::", "");
            if (type.DerivedTypes.Any(d => d.TypeDiscriminator == candidate))
                candidate = candidate + "_" + StableHash(candidate);
        }
        return candidate;
    }

    public static string ShortName(string fullName)
    {
        var name = fullName.Replace("global::", "");
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
    }

    /// <summary>
    /// Enum.Parse/Enum.TryParse require a non-nullable <c>TEnum</c>, so a
    /// nullable enum member (<c>Level?</c>) must pass the underlying type as
    /// the generic argument — <c>Enum.TryParse&lt;Level?&gt;</c> is CS0453.
    /// The parsed value is a plain <c>Level</c>, which widens implicitly into
    /// the <c>Level?</c> member.
    /// </summary>
    public static string MapEnumReadType(string typeFullName) => typeFullName.TrimEnd('?');

    public static string ToCamelCase(string name)
    {
        if (name.Length == 0)
            return name;
        int upperCount = 0;
        while (upperCount < name.Length && char.IsUpper(name[upperCount]))
            upperCount++;
        if (upperCount <= 1)
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        int keepUpper = upperCount > 1 && upperCount < name.Length ? upperCount - 1 : upperCount;
        return name.Substring(0, keepUpper).ToLowerInvariant() + name.Substring(keepUpper);
    }

    // ── Public API (TransformType / ExtractNestedProperties) ──
    // Both delegate to the shared ExtractProperties core below.

    /// <summary>
    /// True when a nested-table member contains an OBJECT COLLECTION (<c>List&lt;Dto&gt;</c>,
    /// <c>Dto[]</c>, a dictionary of objects) — the one thing the section formats still cannot
    /// read from inside a table.
    /// <para>
    /// Why it is narrower than "nested deeper than one level" (its R2 Task 5 predecessor):
    /// a plain chain of nested tables now reads fine, because each helper dispatches its OWN
    /// child tables (<c>[Parent]</c> → <c>[Parent.L2]</c> → <c>[Parent.L2.L3]</c>). An object
    /// COLLECTION inside a table is a different shape: TOML writes it as an array of tables
    /// (<c>[[Items]]</c>) — with no ancestor qualification yet — and the child dispatch has no
    /// arm for an <c>ArrayStart</c> token, so reading it would return an EMPTY collection
    /// rather than fail. Silent loss is exactly what R2 forbids, so the TOML generator keeps
    /// throwing loudly for this member until the array-of-tables path is qualified too.
    /// </para>
    /// </summary>
    public static bool HasNestedObjectCollection(PropertyInfo p) =>
        p.TypeKind == "object"
        && p.NestedProperties.Any(np =>
            ((np.TypeKind is "list" or "array") && np.ElementTypeKind == "object")
            || (np.TypeKind == "dict" && np.ElementTypeKind == "object")
        );

    /// <summary>
    /// True for top-level targets that resolve to a scalar kind (including
    /// Nullable&lt;T&gt;); the generators only support object, collection and
    /// dictionary shapes at the top level.
    /// </summary>
    private static bool IsTopLevelScalarTarget(INamedTypeSymbol type, string formatTag)
    {
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return true;

        var (kind, _, _) = TypeKindResolver.Resolve(type, formatTag);
        return kind is not null and not ("object" or "list" or "array" or "dict" or "bytes");
    }

    /// <summary>
    /// Convert an <see cref="INamedTypeSymbol"/> into a <see cref="TypeInfo"/> for code generation.
    /// Shared by both usage-driven and attribute-driven pipelines.
    /// </summary>
    public static TypeInfo? TransformTypeSymbol(
        INamedTypeSymbol namedType,
        FormatConfig config,
        AttributeHelpers attrs,
        bool includeReadOnlyProperties = false,
        bool includeFields = false
    )
    {
        // Top-level scalar / Nullable<T> targets cannot be expressed by the
        // object emit paths: Nullable<T> produced non-compiling code
        // (CS0721/CS0722 references to the static System.Nullable class) and
        // scalar structs (Guid, DateOnly, ...) emitted an empty-object
        // serializer that silently produced "{}". Skip generation here so
        // the call site fails loudly with "no serializer registered" instead.
        if (IsTopLevelScalarTarget(namedType, config.FormatTag))
            return null;

        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>")
            ns = string.Empty;

        var useCamelCase = attrs.HasCamelCase(namedType);
        var skippedRecursive = new List<SkippedMember>();
        var skippedUnsupported = new List<SkippedMember>();
        // Extraction cannot report (it runs in the transform, which must be pure),
        // so the PICOSERDE001 records travel on the TypeInfo to GenerateAll.
        var droppedMembers = new UnsupportedTypeMemberCollector();
        var properties = ExtractProperties(
            namedType,
            config.FormatTag,
            attrs,
            includeReadOnlyProperties,
            useCamelCase,
            visiting: new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { namedType },
            includeFields: includeFields,
            skippedRecursive: skippedRecursive,
            skippedUnsupported: skippedUnsupported,
            droppedMembers: droppedMembers
        );

        return new TypeInfo(
            namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            namedType.Name,
            properties.ToImmutableArray(),
            IsRefLikeType: namedType.IsRefLikeType,
            IsValueType: namedType.IsValueType,
            IsAbstract: namedType.IsAbstract,
            SkippedRecursiveMembers: skippedRecursive.ToImmutableArray(),
            SkippedUnsupportedMembers: skippedUnsupported.ToImmutableArray(),
            UnsupportedTypeMembers: droppedMembers.ToImmutable()
        );
    }

    public static TypeInfo? TransformType(
        GeneratorSyntaxContext ctx,
        FormatConfig config,
        AttributeHelpers attrs,
        bool includeReadOnlyProperties = false
    )
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method)
            return null;

        if (
            method.ContainingType.Name != config.SerializerClassName
            || method.ContainingType.ContainingNamespace?.ToDisplayString() != config.Namespace
        )
            return null;

        if (method.TypeArguments.Length != 1)
            return null;

        var typeArg = method.TypeArguments[0];
        if (typeArg.SpecialType != SpecialType.None)
            return null;
        if (typeArg is not INamedTypeSymbol namedType)
            return null;
        if (namedType.ContainingType is not null)
            return null;

        // Ref struct: skip in shared path — format-specific Transforms handle them explicitly
        if (namedType.IsRefLikeType)
            return null;

        // Check for [PicoSerializable(IncludeFields = true)] on the type
        bool includeFields = false;
        foreach (var attr in namedType.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "PicoSerializableAttribute")
            {
                foreach (var na in attr.NamedArguments)
                    if (na.Key == "IncludeFields" && na.Value.Value is bool bf && bf)
                        includeFields = true;
            }
        }

        return TransformTypeSymbol(
            namedType,
            config,
            attrs,
            includeReadOnlyProperties,
            includeFields
        );
    }

    /// <summary>
    /// Expand <see cref="GeneratorAttributeSyntaxContext"/> into zero or more <see cref="TypeInfo"/> values.
    /// Handles both <c>[PicoSerializable]</c> (uses <see cref="GeneratorAttributeSyntaxContext.TargetSymbol"/>)
    /// and <c>[PicoSerializable(typeof(T))]</c> (extracts <c>T</c> from the attribute constructor argument).
    /// </summary>
    public static ImmutableArray<TypeInfo> ExpandAttributes(
        GeneratorAttributeSyntaxContext ctx,
        FormatConfig config,
        AttributeHelpers attrs
    )
    {
        var builder = ImmutableArray.CreateBuilder<TypeInfo>();

        foreach (var attr in ctx.Attributes)
        {
            // Extract IncludeFields from named arguments
            bool includeFields = false;
            foreach (var na in attr.NamedArguments)
                if (na.Key == "IncludeFields" && na.Value.Value is bool bf)
                    includeFields = bf;

            // [PicoSerializable(typeof(ExternalType))]
            if (
                attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Kind == TypedConstantKind.Type
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol externalType
            )
            {
                var ti = TransformTypeSymbol(
                    externalType,
                    config,
                    attrs,
                    includeFields: includeFields
                );
                if (ti.HasValue)
                    builder.Add(ti.Value);
            }
            // [PicoSerializable] — no type argument, use the target symbol itself
            else if (ctx.TargetSymbol is INamedTypeSymbol localType)
            {
                var ti = TransformTypeSymbol(
                    localType,
                    config,
                    attrs,
                    includeFields: includeFields
                );
                if (ti.HasValue)
                    builder.Add(ti.Value);
            }
        }

        return builder.ToImmutable();
    }

    public static ImmutableArray<PropertyInfo> ExtractNestedProperties(
        INamedTypeSymbol type,
        AttributeHelpers attrs,
        string formatTag
    ) =>
        ExtractNestedProperties(
            type,
            attrs,
            formatTag,
            new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { type }
        );

    private static ImmutableArray<PropertyInfo> ExtractNestedProperties(
        INamedTypeSymbol type,
        AttributeHelpers attrs,
        string formatTag,
        HashSet<INamedTypeSymbol>? visiting,
        List<SkippedMember>? skippedRecursive = null,
        List<SkippedMember>? skippedUnsupported = null,
        UnsupportedTypeMemberCollector? droppedMembers = null
    )
    {
        var useCamelCase = attrs.HasCamelCase(type);
        // Ref struct nested types: include fields (they commonly use public fields)
        var includeFields = type.IsRefLikeType;
        var properties = ExtractProperties(
            type,
            formatTag,
            attrs,
            includeReadOnlyProperties: includeFields,
            useCamelCase,
            includeFields: includeFields,
            visiting: visiting,
            skippedRecursive: skippedRecursive,
            skippedUnsupported: skippedUnsupported,
            droppedMembers: droppedMembers
        );
        return properties.ToImmutableArray();
    }

    /// <summary>
    /// Cycle-safe nested extraction: when <paramref name="type"/> is already on
    /// the extraction stack, returns an empty property set and reports WHICH cycle it
    /// closed (<paramref name="recursion"/>). The emitter then references the target
    /// type's own generated helper (built from its real top-level properties).
    /// </summary>
    /// <param name="type">The member / element type to expand.</param>
    /// <param name="root">
    /// The type that OWNS the member set this descent belongs to: the extraction root of
    /// this unit, which is not necessarily the outermost type of the whole request. The
    /// four sites that walk a type's own declared members pass that type (for a derived
    /// type's INHERITED members that is the base-chain level, which is the whole point of
    /// obligation ① in spec §5.6); the two nested-container helpers (nested list chain,
    /// nested dict value) receive it from their caller instead of re-deriving it from the
    /// inner type they happen to be looking at. Only
    /// <see cref="RecursionClassifier.Classify"/> reads it.
    /// </param>
    /// <param name="recursion">
    /// Which cycle was hit; <see cref="RecursionKind.None"/> when the descent completed.
    /// </param>
    private static ImmutableArray<PropertyInfo> ExtractNestedPropertiesGuarded(
        INamedTypeSymbol type,
        INamedTypeSymbol root,
        AttributeHelpers attrs,
        string formatTag,
        HashSet<INamedTypeSymbol>? visiting,
        out RecursionKind recursion,
        List<SkippedMember>? skippedRecursive = null,
        List<SkippedMember>? skippedUnsupported = null,
        UnsupportedTypeMemberCollector? droppedMembers = null
    )
    {
        recursion = RecursionKind.None;
        if (visiting is not null && !visiting.Add(type))
        {
            recursion = RecursionClassifier.Classify(type, root);
            return ImmutableArray<PropertyInfo>.Empty;
        }
        try
        {
            return ExtractNestedProperties(
                type,
                attrs,
                formatTag,
                visiting,
                skippedRecursive,
                skippedUnsupported,
                droppedMembers
            );
        }
        finally
        {
            visiting?.Remove(type);
        }
    }

    /// <summary>
    /// May the emitters CLOSE this cycle for this format? The ONE place the two cycle sites
    /// ask it, so the field path and the property path cannot drift apart.
    /// <para>
    /// Three conditions, all from the capability model (I-3 — no format literal here):
    /// <list type="number">
    /// <item>the <paramref name="point"/> row (RecursiveSelf for S11a, RecursiveMutual for
    ///       S11b) must be Supported for the format — the format-capability half;</item>
    /// <item>that format must have an emit branch for the POSITION the cycle was hit in
    ///       (<see cref="FormatCapabilities.CanCloseCycle"/>);</item>
    /// <item>and it must be able to construct the target it hands the cycle back to
    ///       (<see cref="FormatCapabilities.CanConstructCycleTarget"/>).</item>
    /// </list>
    /// The last two are what keep R1 W3's CS0144 out of the build: a cycle through an abstract
    /// type is Supported by the FACTS, but json/msgpack can only emit it because their inner
    /// helper dispatches polymorphically.
    /// </para>
    /// </summary>
    /// <param name="point">
    /// The decision row for the KIND of cycle that was hit — never a fixed row: asking
    /// RecursiveSelf for a mutual cycle is what made the toml row claim Support it did not
    /// have before R2 Task 5.
    /// </param>
    /// <param name="position">
    /// Where the cycle was hit. It matters because each position has its own emit branch: a
    /// member becomes a nested table/mapping, an array ELEMENT a table inside an array, a dict
    /// VALUE a table inside a dict entry. <see cref="FormatCapabilities.CanCloseCycle"/> says
    /// which of those each format's generated code actually has, so a position with no branch
    /// stays a reported drop instead of being emitted as a scalar.
    /// </param>
    /// <param name="target">The type the cycle points back at.</param>
    private static bool CycleMayBeEmitted(
        string formatTag,
        Decision point,
        CyclePosition position,
        ITypeSymbol? target
    )
    {
        if (FormatCapabilities.Decide(formatTag, point) != Effect.Supported)
            return false;

        return FormatCapabilities.CanCloseCycle(
            formatTag,
            position,
            target is not null && RecursionClassifier.IsInstantiable(target)
        );
    }

    // ── Shared core: single source of truth for property extraction ──

    /// <summary>True for System.ValueTuple&lt;...&gt; (whose members are public fields).</summary>
    private static bool IsValueTupleLike(INamedTypeSymbol type) =>
        type.Name.StartsWith("ValueTuple")
        && type.ContainingNamespace?.ToDisplayString() == "System";

    /// <summary>
    /// Extracts serializable properties from <paramref name="type"/>.
    /// Both TransformType and ExtractNestedProperties delegate here.
    /// </summary>
    /// <remarks>
    /// This overload is the entry point (the core below takes the collector as a
    /// REQUIRED 6th argument, so a call that omits it cannot bind there). Keep it
    /// that way: the ValueTuple fallback below is part of the generated code.
    /// </remarks>
    private static List<PropertyInfo> ExtractProperties(
        INamedTypeSymbol type,
        string formatTag,
        AttributeHelpers attrs,
        bool includeReadOnlyProperties,
        bool useCamelCase,
        bool includeFields = false,
        HashSet<INamedTypeSymbol>? visiting = null,
        List<SkippedMember>? skippedRecursive = null,
        List<SkippedMember>? skippedUnsupported = null,
        UnsupportedTypeMemberCollector? droppedMembers = null
    )
    {
        var result = ExtractPropertiesCore(
            type,
            formatTag,
            attrs,
            includeReadOnlyProperties,
            useCamelCase,
            droppedMembers,
            includeFields,
            visiting,
            skippedRecursive,
            skippedUnsupported
        );
        // Field-based fallback for System.ValueTuple<...>: tuples carry their data in
        // public fields and have no serializable properties — extract fields so they
        // serialize their members instead of silently emitting '{}'. Scoped to tuples
        // on purpose: regular types must keep 'fields are not serialized by default'.
        if (result.Count == 0 && !includeFields && IsValueTupleLike(type))
        {
            result = ExtractPropertiesCore(
                type,
                formatTag,
                attrs,
                includeReadOnlyProperties,
                useCamelCase,
                droppedMembers,
                includeFields: true,
                visiting: visiting,
                skippedRecursive: skippedRecursive,
                skippedUnsupported: skippedUnsupported
            );
        }
        return result;
    }

    /// <summary>
    /// Extracts serializable properties, merging the members of every inherited
    /// base type (most-base first). A derived member hides/overrides a base one
    /// of the same name: the derived value wins, and the position is kept at the
    /// base declaration slot — stable, it does not drift as derived members are
    /// added or removed.
    /// If <paramref name="droppedMembers"/> is provided, records every member this
    /// format drops because its type is unresolvable (reported as PICOSERDE001).
    /// </summary>
    private static List<PropertyInfo> ExtractPropertiesCore(
        INamedTypeSymbol type,
        string formatTag,
        AttributeHelpers attrs,
        bool includeReadOnlyProperties,
        bool useCamelCase,
        UnsupportedTypeMemberCollector? droppedMembers,
        bool includeFields = false,
        HashSet<INamedTypeSymbol>? visiting = null,
        List<SkippedMember>? skippedRecursive = null,
        List<SkippedMember>? skippedUnsupported = null
    )
    {
        // Base chain, most-base first: inherited members precede the derived
        // ones, so every format emits and reads them in the same order.
        var chain = new List<INamedTypeSymbol> { type };
        for (
            var current = type.BaseType;
            current is not null && IsMergeableBaseType(current, formatTag);
            current = current.BaseType
        )
            chain.Add(current);
        chain.Reverse();

        var merged = new List<PropertyInfo>();
        // One counter for the whole chain: a per-level counter would restart at
        // 0 and hand out duplicate auto field ids to inherited members.
        int autoKey = 0;
        foreach (var level in chain)
        {
            // A type's own members always see that type as "in progress": a
            // self-referencing member (Base? Next) must become a recursive ref
            // instead of being expanded inline (which would emit `new` on an
            // abstract base or loop forever). Transient on purpose — a member of
            // a plain base type may still be expanded normally.
            bool seeded = visiting is not null && visiting.Add(level);
            List<PropertyInfo> declared;
            try
            {
                declared = ExtractDeclaredProperties(
                    level,
                    formatTag,
                    attrs,
                    includeReadOnlyProperties,
                    useCamelCase,
                    droppedMembers,
                    ref autoKey,
                    includeFields,
                    visiting,
                    skippedRecursive,
                    skippedUnsupported
                );
            }
            finally
            {
                if (seeded)
                    visiting!.Remove(level);
            }
            // Derived (later) level wins on a name collision; the replacement is
            // in place, so the merged slot stays where the most-base declaration
            // put it (stable, independent of the derived member count).
            foreach (var declaredProp in declared)
            {
                int existing = merged.FindIndex(x => x.Name == declaredProp.Name);
                if (existing >= 0)
                    merged[existing] = declaredProp;
                else
                    merged.Add(declaredProp);
            }
        }
        return merged;
    }

    /// <summary>
    /// True when <paramref name="type"/> is a user-declared, object-shaped base
    /// type whose members belong to the serialized shape of a derived type.
    /// Framework and collection base types are excluded — their members are not
    /// part of that shape.
    /// </summary>
    private static bool IsMergeableBaseType(INamedTypeSymbol type, string formatTag)
    {
        if (type.SpecialType != SpecialType.None)
            return false;
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
            return false;
        var (kind, _, _) = TypeKindResolver.Resolve(type, formatTag);
        return kind == "object";
    }

    /// <summary>
    /// Extracts the members declared directly on <paramref name="type"/>; the
    /// inheritance walk lives in <see cref="ExtractProperties"/>.
    /// <paramref name="autoKey"/> continues across the base chain so auto-assigned
    /// field ids stay unique in the merged list.
    /// </summary>
    private static List<PropertyInfo> ExtractDeclaredProperties(
        INamedTypeSymbol type,
        string formatTag,
        AttributeHelpers attrs,
        bool includeReadOnlyProperties,
        bool useCamelCase,
        UnsupportedTypeMemberCollector? droppedMembers,
        ref int autoKey,
        bool includeFields = false,
        HashSet<INamedTypeSymbol>? visiting = null,
        List<SkippedMember>? skippedRecursive = null,
        List<SkippedMember>? skippedUnsupported = null
    )
    {
        var list = new List<PropertyInfo>();

        foreach (var member in type.GetMembers())
        {
            // Public fields (when IncludeFields is enabled)
            if (includeFields && member is IFieldSymbol field)
            {
                if (field.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (field.IsStatic)
                    continue;
                var (fk, fn, _) = TypeKindResolver.Resolve(field.Type, formatTag);
                if (fk is null)
                {
                    // D-6 site 1: a public field (IncludeFields / ref struct) of a
                    // type this format cannot resolve used to `continue` silently —
                    // the member vanished from every generated serializer.
                    // ⏸, not ⛔: an unnameable kind is our gap — a json-only scalar kind
                    // (char, Half, ...) lands here, and that is an encoding debt, not a
                    // format fact (DecisionTables.EncodingOnlyDecisions). The one
                    // exception is a collection whose element is itself a collection —
                    // see <see cref="UnresolvableDropVerdict"/>.
                    droppedMembers?.Add(
                        type.ToDisplayString(DeclaringTypeNameFormat),
                        field.Name,
                        DeclaredTypeOf(field.Type),
                        UnresolvableTypeReason,
                        SourceLocationOf(field),
                        UnresolvableDropVerdict(field.Type, formatTag)
                    );
                    continue;
                }
                bool fnNrt = false;
                if (!fn && field.Type.NullableAnnotation == NullableAnnotation.Annotated)
                {
                    fn = true;
                    fnNrt = true;
                }

                ImmutableArray<PropertyInfo> fieldNested = ImmutableArray<PropertyInfo>.Empty;
                RecursionKind fieldRecursion = RecursionKind.None;
                ITypeSymbol? fieldCycleTarget = null;
                if (fk is "object" && field.Type is INamedTypeSymbol fieldObjNts)
                {
                    fieldCycleTarget = fieldObjNts;
                    fieldNested = ExtractNestedPropertiesGuarded(
                        fieldObjNts,
                        type,
                        attrs,
                        formatTag,
                        visiting,
                        out fieldRecursion,
                        skippedRecursive,
                        skippedUnsupported,
                        droppedMembers
                    );
                }

                // "May this format hold THIS cycle?" — two halves, both answered by the
                // model (R2 Task 5):
                //   ① the KIND decides which row to ask. A self cycle (the member points back
                //      at the type that owns it, S11a) asks RecursiveSelf; a mutual one (it
                //      points at another type already on the descent stack, S11b) asks
                //      RecursiveMutual. Asking one row for both kinds was R1's shortcut, and
                //      it made the table lie: RecursiveMutual x toml said Supported while the
                //      generator dropped the member.
                //   ② a cycle is CLOSED by calling the target's helper and then constructing
                //      it, which an abstract/interface target cannot do (CS0144 — R1 W3's red
                //      build). CanConstructCycleTarget answers whether this format has a
                //      dispatch path for that (json/msgpack do, the section formats do not),
                //      so an abstract cycle stays a reported drop instead of uncompilable code.
                var fieldCyclePoint =
                    fieldRecursion == RecursionKind.Mutual
                        ? Decision.RecursiveMutual
                        : Decision.RecursiveSelf;
                if (
                    fieldRecursion != RecursionKind.None
                    && !CycleMayBeEmitted(
                        formatTag,
                        fieldCyclePoint,
                        CyclePosition.Member,
                        fieldCycleTarget
                    )
                )
                {
                    // D-6 site 1 (cycle branch): the property path below records the
                    // same drop in `skippedRecursive` (=> PICOSERDE003), but a field
                    // never reaches that list, so it is reported here as 001.
                    // The verdict is the FACTS answer for the row that was asked: ini
                    // cannot hold a cycle at all (⛔), toml/yaml can (spec §3, S11a/S11b)
                    // and simply have no path for this one (⏸ — including the abstract
                    // target a section format cannot construct).
                    droppedMembers?.Add(
                        type.ToDisplayString(DeclaringTypeNameFormat),
                        field.Name,
                        DeclaredTypeOf(field.Type),
                        RecursiveFieldReason,
                        SourceLocationOf(field),
                        FormatCapabilities.CanRepresent(formatTag, fieldCyclePoint)
                            ? DropVerdict.GeneratorGap
                            : DropVerdict.FormatCannot
                    );
                    continue;
                }

                list.Add(
                    new PropertyInfo(
                        field.Name,
                        field.Name,
                        fk,
                        field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        fn,
                        null,
                        null,
                        null,
                        null,
                        fieldNested,
                        null,
                        IsNullableReference: fnNrt,
                        TypeFullNameAnnotated: TypeKindResolver.DisplayType(field.Type),
                        IsRecursiveRef: fieldRecursion != RecursionKind.None
                    )
                );
                continue;
            }

            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (!includeReadOnlyProperties && prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
            {
                // A set-only property never reaches the generated code: there is no
                // getter, so there is no value to write. Dropped silently until now
                // (spec review W4 read R2 literally — every dropped member reports). ⚠ is
                // the THIRD class (review I-2): no format can serialize a value it cannot
                // read, so this is neither "the format cannot" (the declared type may be
                // `int`, which every format can hold) nor our debt (no generator change
                // helps) — the member has to change, and the message says exactly that.
                droppedMembers?.Add(
                    type.ToDisplayString(DeclaringTypeNameFormat),
                    prop.Name,
                    DeclaredTypeOf(prop.Type),
                    WriteOnlyPropertyReason,
                    SourceLocationOf(prop),
                    DropVerdict.MemberUnreadable
                );
                continue;
            }
            // Cross-format [PicoIgnore]: Always strips the property entirely;
            // other conditions keep it and are honored by the emit paths.
            var ignoreCondition = GetPicoIgnoreCondition(prop);
            if (ignoreCondition == "Always")
                continue;
            if (attrs.HasIgnore(prop))
                continue;

            // Optionally override kind when a converter is attached
            var converterType = attrs.GetConverterType(prop);
            var (typeKind, isNullable, _) = TypeKindResolver.Resolve(prop.Type, formatTag);
            if (typeKind is null)
            {
                // D-6 site 2: collected into a list no generator ever passed, so the
                // member disappeared with no diagnostic at all.
                // ⏸: see the field site above — "not resolvable here" is our gap, except
                // for the collection-of-collections shape (S8 x ini) whose element-position
                // cell IS a format fact (review I-1, spec §3).
                droppedMembers?.Add(
                    type.ToDisplayString(DeclaringTypeNameFormat),
                    prop.Name,
                    DeclaredTypeOf(prop.Type),
                    UnresolvableTypeReason,
                    SourceLocationOf(prop),
                    UnresolvableDropVerdict(prop.Type, formatTag)
                );
                continue;
            }
            // NRT annotation: string?, SomeClass?, etc. are not Nullable<T>
            // but have NullableAnnotation.Annotated. Mark them as nullable
            // reference types so SGs emit == null checks instead of .HasValue.
            bool isNrtNullable = false;
            if (!isNullable && prop.Type.NullableAnnotation == NullableAnnotation.Annotated)
            {
                isNullable = true;
                isNrtNullable = true;
            }
            if (converterType is not null && attrs.OverrideKindWithStringOnConverter)
                typeKind = "string";

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? elementTypeNameAnnotated = null;
            string? elementTypeFullName = null;
            bool elementIsNullableValue = false;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            ImmutableArray<PropertyInfo> nestedProperties = ImmutableArray<PropertyInfo>.Empty;
            RecursionKind recursion = RecursionKind.None;
            // Where the guarded descent ran into a cycle (member / element / dict value) and
            // which type it pointed back at. Both travel to the cycle gate: the position
            // decides whether this format HAS an emit branch for that shape, the target
            // whether its helper can construct what the cycle hands back.
            var cyclePosition = CyclePosition.Member;
            ITypeSymbol? cycleTarget = null;
            bool elementIsNrt = false;

            if (
                typeKind
                is "list"
                    or "array"
                    or "hashset"
                    or "queue"
                    or "stack"
                    or "linkedlist"
                    or "immutablearray"
                    or "memory"
                    or "readonlymemory"
            )
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, _) = TypeKindResolver.Resolve(elementType, formatTag);
                if (ek is null)
                {
                    // The element kind is not expressible in THIS format (extended
                    // collections resolve for json only, for example). Dropping it
                    // silently was the fourth and last silent loss in this extractor.
                    // The verdict asks the ELEMENT-position cell (S8), not the member one:
                    // four formats drop this as a gap, ini as a fact.
                    skippedUnsupported?.Add(
                        SkippedBy(type, prop, formatTag, Decision.ExtendedKindAsElement)
                    );
                    continue;
                }
                // Section-based formats cannot express nested lists or nullable
                // value-type elements. TOML/YAML additionally cannot express null
                // object/dict elements (they skip null *scalars*); INI has no null
                // representation at all, so every nullable element is dropped.
                // YAML used to be in the nested-list group as well; it has its own
                // block-sequence encoding for them now (F5).
                //
                // The element list carries the element of THIS level, but a nested
                // chain (List<List<int?>>, List<int?[]>) hides the nullability that
                // actually reaches the writer at its innermost level — checking only
                // the outermost wrapper let `int?` through to
                // YamlWriter.WriteSequenceItem(int?), which has no such overload, so
                // the generated file stopped compiling (F5 review I-3). Walk to the
                // innermost element and apply the same rule there: the member is
                // dropped with PICOSERDE004 instead of emitting broken code.
                var leafElement = InnermostCollectionElement(elementType, formatTag);
                var elementIsNullableValueType = IsNullableValueType(elementType);
                var elementIsAnnotated =
                    elementType.NullableAnnotation == NullableAnnotation.Annotated;
                var leafIsNullableValueType = IsNullableValueType(leafElement);
                var leafIsAnnotated =
                    leafElement.NullableAnnotation == NullableAnnotation.Annotated;
                var leafKind = TypeKindResolver.Resolve(leafElement, formatTag).Kind;
                // A nested collection chain (List<List<T>>, T[][], ...) needs a per-element
                // encoding for the inner collection. TOML has one: an inline array of inline
                // arrays (`G = [[1, 2], [3]]`), whose innermost element may also be an INLINE
                // TABLE since R2 Task 6 (`V = [[{ Name = "a" }]]`, spec §3 S4), so a
                // scalar or an inline-shaped object leaf is representable. INI has no array
                // syntax at all, so every chain dies there.
                //
                // TWO questions, and they are different kinds of question:
                //   — can the FORMAT hold this shape?  -> the table (Decision, an Effect).
                //   — does OUR inline emitter have a form for this leaf kind? -> a per-format
                //     LIMIT (DecisionTables.InlineNestedLeafKindsByFormat): the innermost element
                //     is the only thing such an emitter writes with a form of its own, and TOML
                //     writes a scalar directly or an object as an inline table and NOTHING else (a
                //     `[table]` header cannot live inside an array).
                // A leaf kind the emitter has no form for stays DROPPED and reported. That is
                // the difference between "S4 x toml is Supported" and "every S4-shaped member
                // is emittable": a dict leaf is a recorded gap (⏸), never `ToString()` on the
                // wire.
                var leafHasInlineForm =
                    FormatCapabilities.InlineNestedLeafKindSupported(formatTag, leafKind!)
                    && (
                        leafKind != "object"
                        // The INLINE-TABLE question only exists for the format whose object leaf
                        // IS one: a format with a per-kind encoder (json/msgpack/yaml) writes an
                        // object leaf with its own emitter and has no such restriction. Asking it
                        // unconditionally is what dropped a json `List<List<Recursive>>` (the leaf
                        // holds an object member, i.e. it is not "inline shaped").
                        || !FormatCapabilities.InlineNestedLeafKindsAreClosed(formatTag)
                        || IsInlineTableShaped(leafElement, formatTag)
                    );
                // The chain FORM is a format fact of its own: ini has none, so everything dies
                // there before the leaf question is even asked.
                var kindIsNested = ek is "list" or "array";
                var chainFormBlocked =
                    FormatCapabilities.Decide(formatTag, Decision.NestedScalarContainer)
                    != Effect.Supported;
                // NOTE (R2 Task 6): `NestedObjectContainer` is no longer read HERE. It used to
                // be this site's "the format can hold a scalar chain but not an object leaf"
                // gate — the last format that split the two rows that way was TOML, and S4 x
                // toml is Supported now, so every format with a chain form also holds object
                // leaves. The FACT question is still answered by that row for the SHAPE
                // (`Paths["S4"]`), and the drop below still reports against it; what remains
                // here is the EMITTER question, which no Effect can express (see
                // InlineNestedLeafKindsByFormat).
                // The null rule is scoped to the formats whose inline emitter is kind-limited:
                // it is the READ side of that same emitter that cannot express a nullable
                // collection level, so a format with no limit (json/msgpack/yaml) must not be
                // narrowed by it — `List<List<int>?>` round-trips there today.
                var nestedListUnsupported =
                    kindIsNested
                    && (
                        chainFormBlocked
                        || !leafHasInlineForm
                        || FormatCapabilities.InlineNestedLeafKindsAreClosed(formatTag)
                            && HasNullableCollectionLevel(elementType, formatTag)
                    );
                // Outer test: "this format cannot hold null at all" (toml/yaml/ini today).
                // Inner test: "this format has no nesting inside one member" (ini) — that is
                // why every ANNOTATED element dies there regardless of its kind.
                var nullValueBlocked =
                    FormatCapabilities.Decide(formatTag, Decision.NullableValueLeaf)
                    != Effect.Supported;
                var flatMemberOnly =
                    FormatCapabilities.Decide(formatTag, Decision.NestedScalarContainer)
                    != Effect.Supported;
                var nullableElementUnsupported =
                    nullValueBlocked
                    && (
                        elementIsNullableValueType
                        || leafIsNullableValueType
                        || elementIsAnnotated && (flatMemberOnly || ek is "object" or "dict")
                        || leafIsAnnotated && (flatMemberOnly || leafKind is "object" or "dict")
                    );
                if (nestedListUnsupported || nullableElementUnsupported)
                {
                    // Which rule fired decides which fact answers: a blocked chain form
                    // is a NESTING fact (ini has none; toml holds scalars but not object
                    // leaves), a blocked nullable element is the NULL fact (toml/ini have
                    // no null; yaml has one and simply is not implemented -> ⏸).
                    var elementDropPoint = nestedListUnsupported
                        ? (
                            chainFormBlocked
                                ? Decision.NestedScalarContainer
                                : Decision.NestedObjectContainer
                        )
                        : Decision.NullableValueLeaf;
                    skippedUnsupported?.Add(SkippedBy(type, prop, formatTag, elementDropPoint));
                    continue;
                }
                // Extended collection in ELEMENT position (S8). The decision comes from the
                // table, NOT from a kind list: this site used to be an unconditional
                // `ek is "hashset" or ...` test, so the cell could say Supported while the
                // member was still dropped — a table flip changed nothing at all (the
                // "false green" the R2 review called out). Asking the table makes the
                // model the single answer: a format whose cell is Supported reaches its
                // element emitter below, and one whose cell is not still reports a drop.
                if (
                    IsExtendedCollectionKind(ek)
                    && FormatCapabilities.Decide(formatTag, Decision.ExtendedKindAsElement)
                        != Effect.Supported
                )
                {
                    skippedUnsupported?.Add(
                        SkippedBy(type, prop, formatTag, Decision.ExtendedKindAsElement)
                    );
                    continue;
                }
                // INI cannot represent a nested object list (its sections are flat) —
                // the answer comes from the capability table (ObjectListInMember, ini =
                // DropMember), not from a tag test. Reported like every other
                // unrepresentable shape: this used to be a bare `continue`, so an INI DTO
                // with a List<Dto> member lost the member silently.
                if (
                    FormatCapabilities.Decide(formatTag, Decision.ObjectListInMember)
                        != Effect.Supported
                    && ek == "object"
                )
                {
                    skippedUnsupported?.Add(
                        SkippedBy(type, prop, formatTag, Decision.ObjectListInMember)
                    );
                    continue;
                }
                elementTypeKind = ek;
                elementTypeFullName = elementType.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                );
                elementTypeName = TypeKindResolver.MapTypeName(ek, elementType);
                elementTypeNameAnnotated = TypeKindResolver.MapTypeNamePreservingNullability(
                    ek,
                    elementType
                );
                elementIsNrt = elementType.NullableAnnotation == NullableAnnotation.Annotated;
                elementIsNullableValue =
                    elementType
                        is INamedTypeSymbol
                        {
                            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                        };
                // Recursively describe nested List<List<...<T>>> — any depth. The extended
                // collection kinds are sequences too, so `List<HashSet<int>>` gets the same
                // one-descriptor-per-level chain `List<List<int>>` gets: without it the
                // element emitter has no way to name the INNER element kind (int) and falls
                // back to writing the whole set with ToString().
                if (
                    IsSequenceKind(ek)
                    && elementType
                        is INamedTypeSymbol { TypeArguments.Length: 1 }
                            or IArrayTypeSymbol
                )
                {
                    nestedProperties = BuildNestedListElement(
                        elementType,
                        type,
                        formatTag,
                        attrs,
                        visiting,
                        skippedRecursive,
                        skippedUnsupported,
                        droppedMembers
                    );
                }
                else if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                {
                    cyclePosition = CyclePosition.Element;
                    cycleTarget = eNtsObj;
                    nestedProperties = ExtractNestedPropertiesGuarded(
                        eNtsObj,
                        type,
                        attrs,
                        formatTag,
                        visiting,
                        out recursion,
                        skippedRecursive,
                        skippedUnsupported,
                        droppedMembers
                    );
                }
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = TypeKindResolver.Resolve(keyType, formatTag);
                    var (vk, _, _) = TypeKindResolver.Resolve(valType, formatTag);
                    if (kk is null || vk is null)
                    {
                        // Same rule as the element path: a key/value kind that this
                        // format cannot express is reported, never dropped silently.
                        // ⏸: a kind this format cannot NAME is an encoding gap (like
                        // ScalarKindResolves, whose cells the model keeps as encoding-only)
                        // — no fact says the format cannot hold such a value.
                        skippedUnsupported?.Add(
                            SkippedBy(type, prop, formatTag, Decision.ScalarKindResolves)
                        );
                        continue;
                    }
                    // Generators only emit key parsing for the kind set the model allows
                    // (FormatCapabilities.AllowedDictKeyKinds is the ONE definition; a new
                    // kind must be added there and nowhere else) — drop dicts with other
                    // keys instead of emitting non-compiling code, and REPORT the drop in
                    // every format. Reporting only from the format with the narrowest key
                    // set (INI reads a key as raw line text) left json/msgpack/toml/yaml
                    // dropping the member silently; that was the last silent loss left in
                    // this extractor once PICOSERDE001 was wired (spec review D2). The
                    // decision still comes from the table, never from a tag literal.
                    var dictKeyKinds = FormatCapabilities.AllowedDictKeyKinds(formatTag);
                    if (!dictKeyKinds.Contains(kk))
                    {
                        // Drop — but the VERDICT comes from the table, not from the fact
                        // that a kind is missing from this format's set (review I-3): the
                        // set is what our emitters key, so a kind outside it is either a
                        // format fact (the format narrowed its key set — INI reads a key as
                        // raw line text) or our missing case (yaml/msgpack can key a complex
                        // value, json/toml can stringify a scalar). See
                        // <see cref="SkippedByDictKeyKind"/>.
                        skippedUnsupported?.Add(SkippedByDictKeyKind(type, prop, formatTag));
                        continue;
                    }
                    // A dict value that is a collection (S10). ONE cell answers this, and
                    // the site ASKS it instead of testing a kind: which kinds are sequences
                    // is a family FACT (IsSequenceKind), whether a format can hold such a
                    // value is the TABLE's answer (DictValueCollection). This used to be two
                    // unconditional kind lists — the same trap R2 Task 2 removed from the
                    // ELEMENT site (ExtendedKindAsElement), in its second copy: a cell could
                    // say Supported while the member was still dropped, and the seven extended
                    // kinds were even reported against a row that says Supported (T2 review
                    // I-2). Nested dicts have their own helper path and never reach here.
                    if (
                        IsSequenceKind(vk)
                        && FormatCapabilities.Decide(formatTag, Decision.DictValueCollection)
                            != Effect.Supported
                    )
                    {
                        // ini cannot nest inside a member (⛔, D-3); the four formats that can
                        // implement it in R2 Task 3 and their cell now says Supported.
                        skippedUnsupported?.Add(
                            SkippedBy(type, prop, formatTag, Decision.DictValueCollection)
                        );
                        continue;
                    }
                    // Same rule for dict values.
                    var valueIsNullableValue =
                        valType is INamedTypeSymbol
                        {
                            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                        };
                    var valueIsAnnotated =
                        valType.NullableAnnotation == NullableAnnotation.Annotated;
                    // Same two table answers as the list-element path above: "cannot hold
                    // null" (toml/yaml/ini) and "no nesting inside one member" (ini).
                    var dictNullBlocked =
                        FormatCapabilities.Decide(formatTag, Decision.NullableValueLeaf)
                        != Effect.Supported;
                    var dictFlatMemberOnly =
                        FormatCapabilities.Decide(formatTag, Decision.NestedScalarContainer)
                        != Effect.Supported;
                    if (
                        dictNullBlocked
                        && (
                            valueIsNullableValue
                            || valueIsAnnotated && (dictFlatMemberOnly || vk is "object" or "dict")
                            || dictFlatMemberOnly && vk is "object" or "dict"
                        )
                    )
                    {
                        // Same split as the list path: a nullable VALUE is the null fact
                        // (toml/ini have none, yaml has one and is not implemented), an
                        // object/dict value in a flat-member format is the nesting fact.
                        var dictDropPoint =
                            valueIsNullableValue || valueIsAnnotated
                                ? Decision.NullableValueLeaf
                                : Decision.NestedScalarContainer;
                        skippedUnsupported?.Add(SkippedBy(type, prop, formatTag, dictDropPoint));
                        continue;
                    }
                    keyTypeKind = kk;
                    keyTypeName = TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeFullName = valType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    );
                    elementIsNullableValue =
                        valType
                            is INamedTypeSymbol
                            {
                                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                            };
                    elementTypeName = TypeKindResolver.MapTypeName(vk, valType);
                    elementTypeNameAnnotated = TypeKindResolver.MapTypeNamePreservingNullability(
                        vk,
                        valType
                    );
                    elementIsNrt = valType.NullableAnnotation == NullableAnnotation.Annotated;
                    if (
                        IsSequenceKind(vk)
                        && valType
                            is INamedTypeSymbol { TypeArguments.Length: 1 }
                                or IArrayTypeSymbol
                    )
                    {
                        // What the value's sequence ENDS in decides whether the four formats
                        // can write it, and the answer is the same one the list-member arm
                        // gives for its own chain — same helpers, so the two positions cannot
                        // disagree:
                        //   • a NULLABLE leaf (int?, Foo?) in a format with no null: the value
                        //     writers have no overload for it (YamlWriter.WriteSequenceItem
                        //     takes no int?), so the member is reported and dropped instead of
                        //     emitting code that does not compile (F5 review I-3);
                        //   • an OBJECT/DICT leaf: TOML has no inline table inside an inline
                        //     array (NestedObjectContainer is a FACT there) and the other three
                        //     would need a per-element helper this task did not build — a
                        //     deferred drop, reported, never silent.
                        var valueLeaf = InnermostCollectionElement(valType, formatTag);
                        var valueLeafKind = TypeKindResolver.Resolve(valueLeaf, formatTag).Kind;
                        var valueLeafIsNullableValue = IsNullableValueType(valueLeaf);
                        var valueLeafIsAnnotated =
                            valueLeaf.NullableAnnotation == NullableAnnotation.Annotated;
                        var valueNullBlocked =
                            FormatCapabilities.Decide(formatTag, Decision.NullableValueLeaf)
                            != Effect.Supported;
                        if (
                            valueNullBlocked
                            && (
                                valueLeafIsNullableValue
                                || valueLeafIsAnnotated
                                    && (dictFlatMemberOnly || valueLeafKind is "object" or "dict")
                                || HasNullableCollectionLevel(valType, formatTag)
                            )
                        )
                        {
                            skippedUnsupported?.Add(
                                SkippedBy(type, prop, formatTag, Decision.NullableValueLeaf)
                            );
                            continue;
                        }
                        if (valueLeafKind is "object" or "dict" or "any")
                        {
                            skippedUnsupported?.Add(
                                SkippedBy(type, prop, formatTag, Decision.NestedObjectContainer)
                            );
                            continue;
                        }
                        // S10's second half: the dict value is a sequence, so the emitters
                        // need the same one-descriptor-per-level chain `List<List<T>>` gets
                        // (BuildNestedListElement) — without it the value emitter has no way
                        // to name the INNER element kind and falls back to ToString(). This is
                        // deliberately the same builder the member arm above uses, one level
                        // down, so a dict value and a list element of the same type describe
                        // themselves identically.
                        nestedProperties = BuildNestedListElement(
                            valType,
                            type,
                            formatTag,
                            attrs,
                            visiting,
                            skippedRecursive,
                            skippedUnsupported,
                            droppedMembers
                        );
                        if (nestedProperties.IsDefaultOrEmpty)
                        {
                            // The INNERMOST element kind is not expressible in this format
                            // (the same rule the list-member arm applies when its element
                            // kind resolves to null). Reported against the element-position
                            // cell, exactly as that arm does, so the marker keeps saying
                            // "our gap"/"format fact" instead of "supported".
                            skippedUnsupported?.Add(
                                SkippedBy(type, prop, formatTag, Decision.ExtendedKindAsElement)
                            );
                            continue;
                        }
                    }
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                    {
                        cyclePosition = CyclePosition.DictValue;
                        cycleTarget = vNtsObj;
                        nestedProperties = ExtractNestedPropertiesGuarded(
                            vNtsObj,
                            type,
                            attrs,
                            formatTag,
                            visiting,
                            out recursion,
                            skippedRecursive,
                            skippedUnsupported,
                            droppedMembers
                        );
                    }
                    else if (
                        vk is "dict"
                        && valType is INamedTypeSymbol vNtsDict
                        && vNtsDict.TypeArguments.Length == 2
                    )
                    {
                        // Nested Dictionary<K2,V2> — resolve inner dict's K/V and wrap as synthetic PropertyInfo
                        nestedProperties = BuildNestedDictElement(
                            vNtsDict,
                            type,
                            formatTag,
                            attrs,
                            visiting,
                            skippedRecursive,
                            skippedUnsupported,
                            droppedMembers
                        );
                    }
                }
                else
                {
                    // Defensive: the kind resolver answered "dict" but the declared type
                    // is not a two-argument INamedTypeSymbol, so its key/value types cannot
                    // be inspected. Unreachable today (the resolver only says "dict" for
                    // that symbol shape); reported instead of a bare `continue` so R2 has
                    // no silent exit left even if the resolver grows a new dict arm. ⏸:
                    // no fact says the format cannot hold it, so it is our gap.
                    droppedMembers?.Add(
                        type.ToDisplayString(DeclaringTypeNameFormat),
                        prop.Name,
                        DeclaredTypeOf(prop.Type),
                        UninspectableDictionaryReason,
                        SourceLocationOf(prop),
                        DropVerdict.GeneratorGap
                    );
                    continue;
                }
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                cycleTarget = objNts;
                nestedProperties = ExtractNestedPropertiesGuarded(
                    objNts,
                    type,
                    attrs,
                    formatTag,
                    visiting,
                    out recursion,
                    skippedRecursive,
                    skippedUnsupported,
                    droppedMembers
                );
            }

            // The same two-part question as the field path above: the KIND picks the row
            // (Self / Mutual), and CanConstructCycleTarget decides whether this format can
            // close a cycle through THIS target at all (an abstract target needs the
            // format's polymorphic dispatch, I-2). CycleMayBeEmitted is that pair in one
            // place so the two sites cannot answer it differently.
            var cyclePoint =
                recursion == RecursionKind.Mutual
                    ? Decision.RecursiveMutual
                    : Decision.RecursiveSelf;
            if (
                recursion != RecursionKind.None
                && !CycleMayBeEmitted(formatTag, cyclePoint, cyclePosition, cycleTarget)
            )
            {
                // The member is dropped; record it so GenerateAll surfaces a shared
                // PICOSERDE003 diagnostic (never silently). This member is NOT also recorded
                // as PICOSERDE001: one member, one diagnostic (the field path has no 003
                // channel, which is why it reports 001).
                // The verdict is the FACTS answer: ini cannot hold a cycle (⛔), toml and
                // yaml can (S11a/S11b in spec §3) and this one simply has no emit path (⏸).
                skippedRecursive?.Add(SkippedBy(type, prop, formatTag, cyclePoint));
                continue;
            }

            list.Add(
                new PropertyInfo(
                    prop.Name,
                    attrs.GetCustomName(prop)
                        ?? (useCamelCase ? ToCamelCase(prop.Name) : prop.Name),
                    typeKind,
                    prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    isNullable,
                    elementTypeKind,
                    elementTypeName,
                    keyTypeKind,
                    keyTypeName,
                    nestedProperties,
                    converterType,
                    attrs.GetDateTimeFormat(prop),
                    IntKey: (attrs.GetIntKey is not null)
                        ? (attrs.GetIntKey(prop) ?? autoKey++)
                        : null,
                    SectionName: attrs.GetSectionName?.Invoke(prop),
                    Comment: attrs.GetPropertyComment?.Invoke(prop)
                        ?? attrs.GetComment?.Invoke(prop.ContainingType),
                    IsNullableReference: isNrtNullable,
                    IsRequired: prop.IsRequired,
                    ElementIsNullableReference: elementIsNrt,
                    IgnoreCondition: ignoreCondition,
                    TypeFullNameAnnotated: TypeKindResolver.DisplayType(prop.Type),
                    ElementTypeNameAnnotated: elementTypeNameAnnotated,
                    IsRecursiveRef: recursion != RecursionKind.None,
                    ElementTypeFullName: elementTypeFullName,
                    ElementIsNullableValue: elementIsNullableValue
                )
            );
        }
        return list;
    }

    /// <summary>
    /// True when <paramref name="t"/> is <c>Nullable&lt;T&gt;</c>: the one shape whose
    /// null cannot be written by the section-based formats' scalar writers.
    /// </summary>
    private static bool IsNullableValueType(ITypeSymbol t) =>
        t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    /// <summary>
    /// Walks a <c>List&lt;List&lt;...&lt;T&gt;&gt;&gt;</c> / <c>List&lt;T[]&gt;</c> element type down to the
    /// element that is actually written per item (<c>int?</c> for both
    /// <c>List&lt;List&lt;int?&gt;&gt;</c> and <c>List&lt;int?[]&gt;</c>). The walk mirrors
    /// <see cref="BuildNestedListElement"/>: it descends while the CURRENT type is a
    /// list/array and stops at the first non-collection element, which is returned
    /// unchanged. So a non-collection input — including an object that merely carries a
    /// single generic argument (<c>List&lt;Wrapper&lt;int?&gt;&gt;</c>, <c>List&lt;Box&lt;int&gt;&gt;</c>) — is
    /// never unwrapped: its generic argument is not a sequence element. Callers can use
    /// this unconditionally.
    /// </summary>
    private static ITypeSymbol InnermostCollectionElement(ITypeSymbol elementType, string formatTag)
    {
        var current = elementType;
        // Depth guard: a pathological generic chain must not hang the generator.
        for (var guard = 0; guard < 32; guard++)
        {
            // Only an actual collection has an element that the writer emits per item;
            // descending through any other single-type-argument generic would treat an
            // object's type argument as a nested sequence element (F5 fix round 2, A).
            if (TypeKindResolver.Resolve(current, formatTag).Kind is not ("list" or "array"))
                return current;
            ITypeSymbol? inner = current switch
            {
                IArrayTypeSymbol arr => arr.ElementType,
                INamedTypeSymbol nts when nts.TypeArguments.Length == 1 => nts.TypeArguments[0],
                _ => null,
            };
            if (inner is null)
                return current;
            current = inner;
        }
        return current;
    }

    /// <summary>
    /// True when a collection level <em>inside</em> a nested chain is itself a nullable
    /// reference (<c>List&lt;List&lt;int&gt;?&gt;</c>, <c>int[]?[]</c>). TOML has no null at all,
    /// so there is nothing to write for a null inner array and nothing to skip that
    /// would not silently change the element count — such a chain is unrepresentable.
    /// The nullability of the innermost <em>scalar</em> element is a different case and
    /// is handled by the per-element skip/null rules.
    /// </summary>
    /// <summary>
    /// True when every member of <paramref name="type"/> fits the INLINE TABLE form
    /// <c>{ Name = "a", N = 1 }</c> — the one shape an object can take as an ARRAY
    /// ELEMENT (TOML S4: <c>V = [[{ Name = "a" }]]</c>).
    /// <para>
    /// This is the second half of the leaf question the capability table answers by KIND
    /// ("object" is in <c>InlineNestedLeafKindsByFormat</c>): the kind says the format has an
    /// inline-table form, this says THIS object can use it. A member that is itself an object
    /// or a dictionary needs a <c>[table]</c> header, which cannot live inside an array; a
    /// member that is a collection would be written fine but read back truncated (the
    /// inline-table read path scans a value up to the next comma); a member this format cannot
    /// even name has no emitter at all.
    /// </para>
    /// <para>
    /// Deliberately over-strict: an ignored or write-only member still counts as a member, so
    /// an object it is unsure about stays DROPPED and reported (⏸) instead of being written
    /// as text that reads back wrong. Over-strictness costs a gap; the other direction costs a
    /// silent data change.
    /// </para>
    /// </summary>
    private static bool IsInlineTableShaped(ITypeSymbol type, string formatTag)
    {
        if (type is not INamedTypeSymbol nts)
            return false;
        foreach (var member in nts.GetMembers())
        {
            if (member is not IPropertySymbol prop || prop.IsStatic || prop.IsIndexer)
                continue;
            var (kind, _, _) = TypeKindResolver.Resolve(prop.Type, formatTag);
            if (kind is null || kind is "object" or "dict" or "any")
                return false;
            if (IsSequenceKind(kind))
                return false;
        }
        return true;
    }

    private static bool HasNullableCollectionLevel(ITypeSymbol elementType, string formatTag)
    {
        var current = elementType;
        // Same depth guard as InnermostCollectionElement.
        for (var guard = 0; guard < 32; guard++)
        {
            if (TypeKindResolver.Resolve(current, formatTag).Kind is not ("list" or "array"))
                return false;
            if (current.NullableAnnotation == NullableAnnotation.Annotated)
                return true;
            ITypeSymbol? inner = current switch
            {
                IArrayTypeSymbol arr => arr.ElementType,
                INamedTypeSymbol nts when nts.TypeArguments.Length == 1 => nts.TypeArguments[0],
                _ => null,
            };
            if (inner is null)
                return false;
            current = inner;
        }
        return false;
    }

    /// <summary>
    /// Builds a recursive NestedProperties chain for List&lt;List&lt;...&lt;T&gt;&gt;&gt;.
    /// E.g. List&lt;List&lt;int&gt;&gt; → [PropertyInfo(TypeKind="list", NestedProperties=[PropertyInfo(TypeKind="int32")])].
    /// The innermost element has its actual TypeKind; each wrapper has TypeKind="list".
    /// </summary>
    private static ImmutableArray<PropertyInfo> BuildNestedListElement(
        ITypeSymbol listType,
        INamedTypeSymbol root,
        string formatTag,
        AttributeHelpers attrs,
        HashSet<INamedTypeSymbol>? visiting = null,
        List<SkippedMember>? skippedRecursive = null,
        List<SkippedMember>? skippedUnsupported = null,
        UnsupportedTypeMemberCollector? droppedMembers = null
    )
    {
        // List<T>/T[]/T[][] — extract the element type T (or the inner array).
        var innerType = listType switch
        {
            IArrayTypeSymbol arr => arr.ElementType,
            INamedTypeSymbol nts when nts.TypeArguments.Length == 1 => nts.TypeArguments[0],
            _ => null,
        };
        if (innerType is null)
            return ImmutableArray<PropertyInfo>.Empty;
        var (innerKind, _, _) = TypeKindResolver.Resolve(innerType, formatTag);
        if (innerKind is null)
            return ImmutableArray<PropertyInfo>.Empty;

        var innerTypeName = TypeKindResolver.MapTypeName(innerKind, innerType);
        var innerFullName = innerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        ImmutableArray<PropertyInfo> innerNested = ImmutableArray<PropertyInfo>.Empty;
        RecursionKind innerRecursion = RecursionKind.None;

        // If the inner type is also a list, recurse; an object element still
        // needs its members extracted so the generated inner helper is not
        // empty (and so a recursion back into the current type is detected).
        if (
            IsSequenceKind(innerKind)
            && innerType is INamedTypeSymbol { TypeArguments.Length: 1 } or IArrayTypeSymbol
        )
        {
            innerNested = BuildNestedListElement(
                innerType,
                root,
                formatTag,
                attrs,
                visiting,
                skippedRecursive,
                skippedUnsupported,
                droppedMembers
            );
        }
        else if (innerKind is "object" && innerType is INamedTypeSymbol ntsObjElem)
        {
            innerNested = ExtractNestedPropertiesGuarded(
                ntsObjElem,
                root,
                attrs,
                formatTag,
                visiting,
                out innerRecursion,
                skippedRecursive,
                skippedUnsupported,
                droppedMembers
            );
        }

        var wrapper = new PropertyInfo(
            Name: "__nested",
            JsonName: "__nested",
            TypeKind: innerKind,
            TypeFullName: innerFullName,
            IsNullable: false,
            ElementTypeKind: innerKind,
            ElementTypeName: innerTypeName,
            ElementTypeNameAnnotated: TypeKindResolver.MapTypeNamePreservingNullability(
                innerKind,
                innerType
            ),
            KeyTypeKind: null,
            KeyTypeName: null,
            NestedProperties: innerNested,
            ConverterTypeFullName: null,
            IsRecursiveRef: innerRecursion != RecursionKind.None,
            ElementTypeFullName: innerFullName,
            // The innermost element's nullability must survive the chain so
            // nullable elements (List<List<int?>>) get their null handling.
            ElementIsNullableReference: innerType.NullableAnnotation
                == NullableAnnotation.Annotated,
            ElementIsNullableValue: innerType
                is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                }
        );

        return ImmutableArray.Create(wrapper);
    }

    /// <summary>
    /// Builds a synthetic PropertyInfo chain for a nested Dictionary value type.
    /// E.g. for Dictionary&lt;string, Dictionary&lt;string, Foo&gt;&gt;,
    /// the inner dict's value resolves to: PropertyInfo(TypeKind="dict",
    ///   KeyTypeKind="string", ElementTypeKind="object", ElementTypeName="Foo",
    ///   NestedProperties=[Foo's properties]).
    /// This single-element array is stored in NestedProperties of the outer dict prop
    /// so that CollectNestedDictTypes can find and generate inner helpers from it.
    /// </summary>
    private static ImmutableArray<PropertyInfo> BuildNestedDictElement(
        INamedTypeSymbol dictType,
        INamedTypeSymbol root,
        string formatTag,
        AttributeHelpers attrs,
        HashSet<INamedTypeSymbol>? visiting = null,
        List<SkippedMember>? skippedRecursive = null,
        List<SkippedMember>? skippedUnsupported = null,
        UnsupportedTypeMemberCollector? droppedMembers = null
    )
    {
        if (dictType.TypeArguments.Length != 2)
            return ImmutableArray<PropertyInfo>.Empty;

        var innerKeyType = dictType.TypeArguments[0];
        var innerValType = dictType.TypeArguments[1];
        var (ik, _, _) = TypeKindResolver.Resolve(innerKeyType, formatTag);
        var (iv, _, _) = TypeKindResolver.Resolve(innerValType, formatTag);
        if (ik is null || iv is null)
            return ImmutableArray<PropertyInfo>.Empty;

        var innerKeyTypeName = TypeKindResolver.MapTypeName(ik, innerKeyType);
        var innerValTypeName = TypeKindResolver.MapTypeName(iv, innerValType);
        var innerValFullName = innerValType.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );
        ImmutableArray<PropertyInfo> innerNested = ImmutableArray<PropertyInfo>.Empty;
        var dictFullName = dictType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        RecursionKind dictValueRecursion = RecursionKind.None;
        if (iv is "object" && innerValType is INamedTypeSymbol ivNtsObj)
        {
            innerNested = ExtractNestedPropertiesGuarded(
                ivNtsObj,
                root,
                attrs,
                formatTag,
                visiting,
                out dictValueRecursion,
                skippedRecursive,
                skippedUnsupported,
                droppedMembers
            );
        }
        else if (
            iv is "dict"
            && innerValType is INamedTypeSymbol ivNtsDict
            && ivNtsDict.TypeArguments.Length == 2
        )
        {
            innerNested = BuildNestedDictElement(
                ivNtsDict,
                root,
                formatTag,
                attrs,
                visiting,
                skippedRecursive,
                skippedUnsupported,
                droppedMembers
            );
        }

        var wrapper = new PropertyInfo(
            Name: "__nested_dict",
            JsonName: "__nested_dict",
            TypeKind: "dict",
            TypeFullName: dictFullName,
            IsNullable: false,
            ElementTypeKind: iv,
            ElementTypeName: innerValTypeName,
            KeyTypeKind: ik,
            KeyTypeName: innerKeyTypeName,
            NestedProperties: innerNested,
            ConverterTypeFullName: null,
            IsRecursiveRef: dictValueRecursion != RecursionKind.None,
            ElementTypeFullName: innerValType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            // The inner dict's value nullability must survive the chain, exactly
            // like the nested-list wrapper above: without these two flags the
            // generated inner helper cannot tell Dictionary<string, int?> from
            // Dictionary<string, int> and coerces a JSON null into 0.
            ElementIsNullableReference: innerValType.NullableAnnotation
                == NullableAnnotation.Annotated,
            ElementIsNullableValue: innerValType
                is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                }
        );

        return ImmutableArray.Create(wrapper);
    }

    /// <summary>
    /// Collects nested Dictionary types from TypeInfo properties.
    /// For dict-valued dicts (ElementTypeKind == "dict"), the inner dict's synthetic
    /// PropertyInfo is stored in NestedProperties[0]. This method walks the type tree
    /// and collects them all, keyed by fully-qualified type name.
    /// </summary>
    public static Dictionary<string, PropertyInfo> CollectNestedDictTypes(
        TypeInfo type,
        Dictionary<string, PropertyInfo>? existing = null
    )
    {
        var result = existing ?? new Dictionary<string, PropertyInfo>();
        foreach (var prop in type.Properties)
        {
            if (
                prop.TypeKind == "dict"
                && prop.ElementTypeKind == "dict"
                && prop.NestedProperties.Length > 0
                && !string.IsNullOrEmpty(prop.NestedProperties[0].TypeFullName)
            )
            {
                AddNestedDictType(prop.NestedProperties[0], result);
            }
        }
        return result;
    }

    private static void AddNestedDictType(
        PropertyInfo dictProp,
        Dictionary<string, PropertyInfo> result
    )
    {
        var fqn = dictProp.TypeFullName!;
        if (string.IsNullOrEmpty(fqn) || result.ContainsKey(fqn))
            return;
        result[fqn] = dictProp;

        // Recurse: if this dict's value is also a dict, collect it too
        if (
            dictProp.ElementTypeKind == "dict"
            && dictProp.NestedProperties.Length > 0
            && !string.IsNullOrEmpty(dictProp.NestedProperties[0].TypeFullName)
        )
        {
            AddNestedDictType(dictProp.NestedProperties[0], result);
        }
    }

    public static void CollectNestedTypes(
        TypeInfo type,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        foreach (var prop in type.Properties)
        {
            if (prop.TypeKind == "object" && !string.IsNullOrEmpty(prop.TypeFullName))
                AddNestedType(prop.TypeFullName, prop.NestedProperties, nestedTypes);
            if (
                (prop.TypeKind == "list" || prop.TypeKind == "array")
                && prop.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(prop.ElementTypeName)
            )
                AddNestedType(prop.ElementTypeName!, prop.NestedProperties, nestedTypes);
            if (
                prop.TypeKind == "dict"
                && prop.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(prop.ElementTypeName)
            )
                AddNestedType(prop.ElementTypeName!, prop.NestedProperties, nestedTypes);
            // Nested list chains (List<List<...<TObject>>>): the innermost
            // object element needs its own helper too.
            if ((prop.TypeKind is "list" or "array") && prop.NestedProperties.Length > 0)
                AddNestedTypeFromListChain(prop.NestedProperties[0], nestedTypes);
            // Recurse into nested-dict synthetic PropertyInfo to collect object value types
            if (prop.TypeKind == "dict" && prop.NestedProperties.Length > 0)
            {
                foreach (var dnp in prop.NestedProperties)
                    AddNestedTypeFromDict(dnp, nestedTypes);
            }
        }
    }

    private static void AddNestedTypeFromListChain(
        PropertyInfo wrapper,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        var current = wrapper;
        while (true)
        {
            if (current.TypeKind is "list" or "array")
            {
                if (current.NestedProperties.Length == 0)
                    return;
                current = current.NestedProperties[0];
                continue;
            }
            if (current.TypeKind == "object" && !string.IsNullOrEmpty(current.TypeFullName))
                AddNestedType(current.TypeFullName, current.NestedProperties, nestedTypes);
            return;
        }
    }

    private static void AddNestedTypeFromDict(
        PropertyInfo dictProp,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        // If this dict's value is an object, collect it
        if (dictProp.ElementTypeKind == "object" && !string.IsNullOrEmpty(dictProp.ElementTypeName))
            AddNestedType(dictProp.ElementTypeName!, dictProp.NestedProperties, nestedTypes);
        // If this dict's value is also a dict, recurse deeper
        if (dictProp.ElementTypeKind == "dict" && dictProp.NestedProperties.Length > 0)
        {
            foreach (var dnp in dictProp.NestedProperties)
                AddNestedTypeFromDict(dnp, nestedTypes);
        }
    }

    private static void AddNestedType(
        string fullName,
        ImmutableArray<PropertyInfo> props,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        if (string.IsNullOrEmpty(fullName))
            return;
        if (nestedTypes.ContainsKey(fullName))
            return;
        nestedTypes[fullName] = props;

        foreach (var np in props)
        {
            // Recursive references have no extracted nested properties; their
            // helper is seeded from the target type's real property set instead.
            if (np.IsRecursiveRef)
                continue;
            if (np.TypeKind == "object" && !string.IsNullOrEmpty(np.TypeFullName))
                AddNestedType(np.TypeFullName, np.NestedProperties, nestedTypes);
            if (
                (np.TypeKind == "list" || np.TypeKind == "array")
                && np.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(np.ElementTypeName)
            )
                AddNestedType(np.ElementTypeName!, np.NestedProperties, nestedTypes);
            if (
                np.TypeKind == "dict"
                && np.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(np.ElementTypeName)
            )
                AddNestedType(np.ElementTypeName!, np.NestedProperties, nestedTypes);
            // Recurse into nested-dict synthetic props inside a collected object
            if (np.TypeKind == "dict" && np.NestedProperties.Length > 0)
            {
                foreach (var dnp in np.NestedProperties)
                    AddNestedTypeFromDict(dnp, nestedTypes);
            }
        }
    }

    /// <summary>
    /// Collects the target type names of every recursive-reference member found
    /// in the given property sets. GenerateAll uses this to seed those targets'
    /// helpers from their real (non-recursive) top-level property set.
    /// </summary>
    public static void CollectRecursiveRefTargets(
        IEnumerable<ImmutableArray<PropertyInfo>> propertySets,
        HashSet<string> targets
    )
    {
        foreach (var props in propertySets)
            Walk(props);

        void Walk(ImmutableArray<PropertyInfo> props)
        {
            if (props.IsDefaultOrEmpty)
                return;
            foreach (var p in props)
            {
                if (p.IsRecursiveRef)
                {
                    var target = p.TypeKind is "list" or "array" or "dict" or "hashset"
                        ? p.ElementTypeFullName
                        : p.TypeFullName;
                    if (!string.IsNullOrEmpty(target))
                        targets.Add(target!);
                }
                Walk(p.NestedProperties);
            }
        }
    }

    /// <summary>
    /// Seeds the helpers of recursive cycle targets from their real (merged)
    /// top-level property set. Prefers a polymorphic entry and, among equals,
    /// the entry with the most properties, so a recursive polymorphic type gets
    /// the same helper shape it would get as a root type.
    /// </summary>
    public static void SeedRecursiveTargets(
        IReadOnlyList<TypeInfo> validTypes,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        var targets = new HashSet<string>();
        CollectRecursiveRefTargets(
            validTypes.Select(t => t.Properties).Concat(nestedTypes.Values),
            targets
        );
        if (targets.Count == 0)
            return;

        foreach (var target in targets)
        {
            TypeInfo? best = null;
            foreach (var t in validTypes)
            {
                if (t.FullyQualifiedName != target)
                    continue;
                if (
                    best is null
                    || (
                        !t.DerivedTypes.IsDefaultOrEmpty && best.Value.DerivedTypes.IsDefaultOrEmpty
                    )
                    || (
                        t.DerivedTypes.IsDefaultOrEmpty == best.Value.DerivedTypes.IsDefaultOrEmpty
                        && t.Properties.Length > best.Value.Properties.Length
                    )
                )
                    best = t;
            }
            if (best is { } chosen)
                nestedTypes[target] = chosen.Properties;
        }
    }

    /// <summary>Detects [JsonConstructor] attribute and returns ctor param info.</summary>
    public static ImmutableArray<CtorParamInfo>? DetectConstructor(
        INamedTypeSymbol type,
        string formatTag,
        string attributeName
    )
    {
        foreach (var ctor in type.Constructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public)
                continue;
            bool hasAttr = false;
            foreach (var attr in ctor.GetAttributes())
            {
                if (attr.AttributeClass?.Name == attributeName)
                {
                    hasAttr = true;
                    break;
                }
            }
            if (!hasAttr)
                continue;

            return BuildCtorParams(ctor, formatTag);
        }

        // Records expose their data through the primary constructor: accept it
        // without requiring the format's constructor attribute (all formats,
        // not just JSON which has its own record handling).
        if (type.IsRecord)
        {
            var primary = type.Constructors.FirstOrDefault(c =>
                c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared
            );
            if (primary is not null)
                return BuildCtorParams(primary, formatTag);
        }

        return null;
    }

    /// <summary>
    /// Maps a constructor's parameters to <see cref="CtorParamInfo"/>. Returns
    /// null when a parameter type is unsupported (the caller then falls back to
    /// the parameterless constructor instead of emitting a call with a missing
    /// argument, which would fail compilation).
    /// </summary>
    private static ImmutableArray<CtorParamInfo>? BuildCtorParams(
        IMethodSymbol ctor,
        string formatTag
    )
    {
        var ctorParams = new List<CtorParamInfo>();
        foreach (var param in ctor.Parameters)
        {
            var (typeKind, _, _) = TypeKindResolver.Resolve(param.Type, formatTag);
            if (typeKind is null)
                return null;
            ctorParams.Add(
                new CtorParamInfo(
                    param.Name,
                    typeKind,
                    param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    TypeFullNameAnnotated: TypeKindResolver.DisplayType(param.Type)
                )
            );
        }
        return ctorParams.ToImmutableArray();
    }

    /// <summary>
    /// Processes [PicoDerivedType] and [PicoPolymorphic] attributes on a type.
    /// Returns TypeInfo for the base type (with DerivedTypes populated) plus
    /// TypeInfo for each derived type (so their serializers are generated).
    /// </summary>
    public static ImmutableArray<TypeInfo> ExpandPolymorphicTypes(
        GeneratorAttributeSyntaxContext ctx,
        FormatConfig config,
        AttributeHelpers attrs
    )
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol baseType)
            return ImmutableArray<TypeInfo>.Empty;

        var builder = ImmutableArray.CreateBuilder<TypeInfo>();
        var derivedList = ImmutableArray.CreateBuilder<DerivedTypeInfo>();
        var derivedTypeSymbols = new List<INamedTypeSymbol>();
        var discriminatorPropertyName = "$type";

        foreach (var attr in baseType.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "PicoPolymorphicAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoSerDe.Core"
            )
            {
                foreach (var na in attr.NamedArguments)
                    if (na.Key == "TypeDiscriminatorPropertyName" && na.Value.Value is string dpn)
                        discriminatorPropertyName = dpn;
            }

            if (
                attr.AttributeClass?.Name == "PicoDerivedTypeAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoSerDe.Core"
                && attr.ConstructorArguments.Length == 2
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol derivedType
                && attr.ConstructorArguments[1].Value is string discriminator
            )
            {
                derivedList.Add(
                    new DerivedTypeInfo(
                        derivedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        discriminator
                    )
                );
                derivedTypeSymbols.Add(derivedType);
            }
        }

        if (derivedList.Count == 0)
            return ImmutableArray<TypeInfo>.Empty;

        // Generate base type TypeInfo (with DerivedTypes populated)
        var baseInfo = TransformTypeSymbol(baseType, config, attrs);
        if (baseInfo.HasValue)
        {
            builder.Add(
                baseInfo.Value with
                {
                    DiscriminatorPropertyName = discriminatorPropertyName,
                    DerivedTypes = derivedList.ToImmutable(),
                }
            );
        }

        // Generate derived type TypeInfos (with [JsonConstructor] detection)
        foreach (var dt in derivedTypeSymbols)
        {
            // Detect [JsonConstructor] first so we include read-only properties
            var ctorParams = DetectConstructor(
                dt,
                config.FormatTag,
                config.ConstructorAttributeName
            );

            // Record primary constructor auto-detection for polymorphic derived types.
            // Records use positional construction (no parameterless ctor) and init-only
            // properties, so the poly deserializer must use the same ctor-driven path
            // as the normal deserializer instead of new() + property assignment.
            if (!ctorParams.HasValue && dt.IsRecord)
            {
                var primary = dt
                    .Constructors.Where(c =>
                        c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared
                    )
                    .FirstOrDefault();
                if (primary is not null)
                {
                    var ctorList = ImmutableArray.CreateBuilder<CtorParamInfo>();
                    bool anyUnsupported = false;
                    foreach (var param in primary.Parameters)
                    {
                        var (typeKind, _, _) = TypeKindResolver.Resolve(
                            param.Type,
                            config.FormatTag
                        );
                        if (typeKind is null)
                        {
                            anyUnsupported = true;
                            break;
                        }
                        ctorList.Add(
                            new CtorParamInfo(
                                param.Name,
                                typeKind,
                                param.Type.ToDisplayString(
                                    SymbolDisplayFormat.FullyQualifiedFormat
                                ),
                                TypeFullNameAnnotated: TypeKindResolver.DisplayType(param.Type)
                            )
                        );
                    }
                    ctorParams = anyUnsupported ? null : ctorList.ToImmutable();
                }
            }

            var hasCtor = ctorParams.HasValue;

            var ti = TransformTypeSymbol(dt, config, attrs, includeReadOnlyProperties: hasCtor);
            if (!ti.HasValue)
                continue;

            // Inherited members (poly root included) are already part of
            // ti.Value.Properties: ExtractProperties walks the base chain itself
            // and merges it most-base-first. No extra merge is needed here.
            var mergedProps = ti.Value.Properties;

            // Keep the merged list free of duplicate auto field ids (MsgPack
            // field ids). ExtractProperties already hands out unique keys via one
            // counter across the chain, so this is a positional safety net that
            // also keeps explicit [IntKey] ordering contiguous; the emitters sort
            // by IntKey, so serializer and deserializer stay in agreement.
            if (mergedProps.Any(p => p.IntKey is not null))
            {
                for (int mi = 0; mi < mergedProps.Length; mi++)
                    if (mergedProps[mi].IntKey is not null)
                        mergedProps = mergedProps.SetItem(mi, mergedProps[mi] with { IntKey = mi });
            }

            ti = ti.Value with { Properties = mergedProps };

            if (hasCtor)
                ti = ti.Value with { CtorParams = ctorParams!.Value };

            builder.Add(ti.Value);
        }

        return builder.ToImmutable();
    }

    // ── Top-level List<T> detection and transformation (shared across all format generators) ──

    /// <summary>
    /// Detect if a type is a List&lt;T&gt; from System.Collections.Generic.
    /// Use this in each format SG's Transform method to route List&lt;T&gt; to the
    /// collection code path instead of treating it as a regular object.
    /// </summary>
    public static bool IsGenericList(ITypeSymbol type) =>
        type is INamedTypeSymbol nts
        && nts.TypeArguments.Length == 1
        && nts.Name == "List"
        && nts.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic";

    /// <summary>
    /// Creates a synthetic TypeInfo for a top-level List&lt;T&gt; (e.g. List&lt;int&gt;, List&lt;SomeDto&gt;).
    /// This unifies the code path with top-level arrays — both produce collection-style output.
    /// Each format SG routes the resulting TypeInfo to its collection-specific code gen methods.
    /// </summary>
    public static TypeInfo TransformTopLevelList(
        INamedTypeSymbol listType,
        FormatConfig config,
        AttributeHelpers attrs
    )
    {
        var elementType = listType.TypeArguments[0];
        var (ek, _, _) = TypeKindResolver.Resolve(elementType, config.FormatTag);
        if (ek is null)
            return default;

        var listFqn = listType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var elemFqn = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        ImmutableArray<PropertyInfo> elemNested = default;

        if (ek is "object" && elementType is INamedTypeSymbol eNts)
        {
            elemNested = ExtractNestedProperties(eNts, attrs, config.FormatTag);
        }

        return new TypeInfo(
            FullyQualifiedName: listFqn,
            Namespace: "",
            Name: $"List_{SafeName(listFqn)}",
            Properties: ImmutableArray<PropertyInfo>.Empty,
            ArrayElementKind: ek,
            ArrayElementName: elemFqn,
            ArrayElementNestedProps: elemNested,
            IsTopLevelList: true
        );
    }
}
