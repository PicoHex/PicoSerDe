// Unified TypeKindResolver — shared across all 5 format source generators
// Each SG project includes this file via <Compile Include>

namespace PicoSerDe.Gen;

/// <summary>
/// Which gate of the empty-object arm said no — or that none did
/// (<see cref="None"/>: the type's shape IS the empty object of this format).
/// <para>
/// The gates split into two classes, and <c>GenInfrastructure</c> turns the split into the
/// diagnostic marker: the three that make the type IMPOSSIBLE TO INSTANTIATE are terminal (no
/// generator change can write a shape whose instance cannot be created), while "our writer has
/// no empty form" and "the base contributes members" are our own reopenable gaps. Naming the
/// gate, rather than answering a bool, is what keeps the verdict and the decision in step
/// (AGENTS.md rule 3: a debt must not be called terminal, and a terminal fact must not be
/// written up as a debt to wait for).
/// </para>
/// </summary>
internal enum EmptyObjectBlock
{
    /// <summary>No gate fired: this type is the empty object of this format.</summary>
    None,

    /// <summary>The type DECLARES public instance data — it is not a zero-member type.</summary>
    NotZeroMember,

    /// <summary>
    /// ⏸ our writer has no form for an empty object in this format
    /// (<c>FormatCapabilities.EmptyObjectSupported</c>). Reopenable: the FORMAT can hold it.
    /// No format answers false today (yaml was the last one, closed when
    /// <c>YamlWriter.WriteEndMapping</c> learned the empty-mapping flow form); the gate stays
    /// because the next format starts here.
    /// </summary>
    FormatHasNoEmptyForm,

    /// <summary>⛔ abstract: <c>new T()</c> is CS0144 in every format.</summary>
    Abstract,

    /// <summary>⛔ a static class is not a value at all: <c>new T()</c> is CS0712.</summary>
    StaticClass,

    /// <summary>
    /// ⏸ the base class is not <c>object</c>/<c>ValueType</c>, so it still carries members —
    /// a separate extraction question, not a fact about this type.
    /// </summary>
    BaseContributesMembers,

    /// <summary>⛔ no accessible parameterless constructor: <c>new T()</c> is CS7036.</summary>
    NotConstructible,
}

internal static class TypeKindResolver
{
    /// <param name="format">
    /// Format hint: "json" | "ini" | "msgpack" | "toml" | "yaml".
    /// Used for format-specific type mapping (e.g., byte[] → "bytes" in MsgPack).
    /// <para>
    /// Every format-dependent branch below asks the capability model
    /// (<c>FormatCapabilities.Decide</c>), which is keyed by exactly
    /// <c>DecisionTables.FormatTags</c> and throws <c>ArgumentOutOfRangeException</c> for
    /// any other string — so a caller must pass a real tag. The <c>""</c> default is a
    /// signature left-over with no caller today (the old literal comparisons merely treated
    /// it as "not json/msgpack"), and it is not a supported argument any more.
    /// </para>
    /// </param>
    public static (string? Kind, bool IsNullable, ITypeSymbol? InnerType) Resolve(
        ITypeSymbol type,
        string format = ""
    )
    {
        // Nullable<T>
        if (
            type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } ntsNullable
        )
        {
            var inner = ntsNullable.TypeArguments[0];
            var (innerKind, _, _) = Resolve(inner, format);
            return (innerKind, true, inner);
        }

        // T[]
        if (type is IArrayTypeSymbol arr)
        {
            // MsgPack-specific: byte[] → "bytes". Encoding, not capability: msgpack can
            // carry the same bytes as a binary value or as an array of integers, so there
            // is no table cell to consult here (Task 4's guard keys off the marker below).
            if (
                format == "msgpack" // encoding, not capability
                && arr.ElementType.SpecialType == SpecialType.System_Byte
            )
                return ("bytes", false, null);
            return ("array", false, null);
        }

        // List<T>, IList<T>, ICollection<T>, IEnumerable<T>, IReadOnlyList<T>, IReadOnlyCollection<T>
        if (type is INamedTypeSymbol ntsList && ntsList.TypeArguments.Length == 1)
        {
            if (
                ntsList.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IList_T
                || ntsList.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_ICollection_T
                || ntsList.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IEnumerable_T
                || (
                    ntsList.Name is "IReadOnlyList" or "IReadOnlyCollection" or "IEnumerable"
                    && ntsList.ContainingNamespace?.ToDisplayString()
                        == "System.Collections.Generic"
                )
                || (
                    ntsList.Name == "List"
                    && ntsList.ContainingNamespace?.ToDisplayString()
                        == "System.Collections.Generic"
                )
            )
            {
                var elementType = ntsList.TypeArguments[0];
                var (ek, _, _) = Resolve(elementType, format);
                if (ek is null)
                    return (null, false, null);
                return ("list", false, null);
            }

            // Extended collections: only the formats whose table cell says Supported
            // resolve the kind (D-1: json today; the other four drop the member).
            // This arm also serves the ELEMENT position (the `Resolve(elementType, …)`
            // recursion above): both positions read DropMember in the four non-json
            // formats, so one decision point answers for both.
            if (
                FormatCapabilities.Decide(format, Decision.ExtendedKindResolves) == Effect.Supported
            )
            {
                var ns = ntsList.ContainingNamespace?.ToDisplayString();
                var extKind = (ns, ntsList.Name) switch
                {
                    ("System.Collections.Generic", "HashSet") => "hashset",
                    ("System.Collections.Generic", "ISet") => "hashset",
                    ("System.Collections.Generic", "Queue") => "queue",
                    ("System.Collections.Generic", "Stack") => "stack",
                    ("System.Collections.Generic", "LinkedList") => "linkedlist",
                    ("System.Collections.Immutable", "ImmutableArray") => "immutablearray",
                    ("System", "Memory") => "memory",
                    ("System", "ReadOnlyMemory") => "readonlymemory",
                    _ => null,
                };
                if (extKind is not null)
                {
                    var elementType = ntsList.TypeArguments[0];
                    var (ek, _, _) = Resolve(elementType, format);
                    if (ek is null)
                        return (null, false, null);
                    return (extKind, false, null);
                }
            }
        }

        // Dictionary<K,V> and extended dictionary types (JSON only)
        if (type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
        {
            var ns = ntsDict.ContainingNamespace?.ToDisplayString();
            if (
                ns == "System.Collections.Generic"
                && ntsDict.Name is "Dictionary" or "SortedDictionary"
            )
                return ("dict", false, null);
            if (
                FormatCapabilities.Decide(format, Decision.ConcurrentDictResolves)
                    == Effect.Supported
                && ns == "System.Collections.Concurrent"
                && ntsDict.Name == "ConcurrentDictionary"
            )
                return ("dict", false, null);
        }

        // Kinds only the table's "resolves" column lets this format name (D-1: json
        // today). Asked ONCE: the nine arms below share this one answer.
        var formatResolvesScalarKinds =
            FormatCapabilities.Decide(format, Decision.ScalarKindResolves) == Effect.Supported;

        // Built-in special types
        string? kind = type.SpecialType switch
        {
            SpecialType.System_Object => "any",
            SpecialType.System_String => "string",
            SpecialType.System_Int32 => "int32",
            SpecialType.System_Int64 => "int64",
            SpecialType.System_Int16 => "int16",
            SpecialType.System_UInt16 => "uint16",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Byte => "byte",
            SpecialType.System_UInt32 => "uint32",
            SpecialType.System_UInt64 => "uint64",
            SpecialType.System_Char when formatResolvesScalarKinds => "char",
            SpecialType.System_Double => "float64",
            // NOTE: System.Single (C# float) maps to "float64" intentionally —
            // the generated code uses double as the universal floating-point
            // representation. Round-tripping preserves exact values for most
            // float32 inputs. If lossless float32 is needed, add a separate "float32" kind.
            SpecialType.System_Single => "float32",
            SpecialType.System_Boolean => "boolean",
            SpecialType.System_DateTime => "datetime",
            SpecialType.System_Decimal => "decimal",
            _ => null,
        };

        if (kind is null)
        {
            kind = type switch
            {
                INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
                INamedTypeSymbol { Name: "Guid", ContainingNamespace.Name: "System" } => "guid",
                INamedTypeSymbol { Name: "DateOnly", ContainingNamespace.Name: "System" } =>
                    "dateonly",
                INamedTypeSymbol { Name: "TimeOnly", ContainingNamespace.Name: "System" } =>
                    "timeonly",
                INamedTypeSymbol { Name: "TimeSpan", ContainingNamespace.Name: "System" } =>
                    "timespan",
                INamedTypeSymbol { Name: "DateTimeOffset", ContainingNamespace.Name: "System" }
                    when formatResolvesScalarKinds => "datetimeoffset",
                INamedTypeSymbol { Name: "Uri", ContainingNamespace.Name: "System" }
                    when formatResolvesScalarKinds => "uri",
                INamedTypeSymbol { Name: "Version", ContainingNamespace.Name: "System" }
                    when formatResolvesScalarKinds => "version",
                INamedTypeSymbol { Name: "Half", ContainingNamespace.Name: "System" }
                    when formatResolvesScalarKinds => "half",
                INamedTypeSymbol { Name: "BigInteger", ContainingNamespace.Name: "Numerics" }
                    when formatResolvesScalarKinds => "biginteger",
                INamedTypeSymbol { Name: "Int128", ContainingNamespace.Name: "System" }
                    when formatResolvesScalarKinds => "int128",
                INamedTypeSymbol { Name: "UInt128", ContainingNamespace.Name: "System" }
                    when formatResolvesScalarKinds => "uint128",
                INamedTypeSymbol { Name: "IntPtr", ContainingNamespace.Name: "System" }
                    when formatResolvesScalarKinds => "nint",
                _ => null,
            };
        }

        // Nested complex types
        if (
            kind is null
            && type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } ntsObj
        )
        {
            // Does the type declare a member we can SERIALIZE? That is a different question
            // from "does it declare public instance data at all", which is the empty-object
            // arm's precondition (<see cref="DeclaresPublicInstanceData"/>).
            foreach (var member in ntsObj.GetMembers())
            {
                if (
                    member is IPropertySymbol
                    {
                        DeclaredAccessibility: Accessibility.Public,
                        IsStatic: false
                    } ps
                )
                {
                    if (
                        !ps.IsIndexer
                        && ps.GetMethod is not null
                        && !(ps.IsReadOnly && ps.SetMethod is null)
                    )
                    {
                        return ("object", false, null);
                    }
                    continue;
                }
                // Also accept public instance fields (ref structs commonly use fields)
                if (
                    member is IFieldSymbol
                    {
                        DeclaredAccessibility: Accessibility.Public,
                        IsStatic: false
                    }
                )
                {
                    return ("object", false, null);
                }
            }

            // BU-4: a class/struct that declares NO public instance member is not
            // "unresolvable" — its serialized shape is the EMPTY OBJECT, and every format
            // whose capability table says so has a form for it (`{}`, an empty map,
            // `[Name]`). Without this arm the whole MEMBER disappeared with PICOSERDE001 ⏸
            // ("this format can express it, we have not implemented it"), so the document
            // read `{"SessionCapabilities":{}}` where the user's model said
            // `{"SessionCapabilities":{"List":{}}}`.
            //
            // The five gates are NOT inlined here: they live in <see cref="EmptyObjectBlockOf"/>,
            // because the DROP VERDICT is decided from the same list (N-1/N-4). Three of the
            // gates are terminal (abstract / static class / no accessible parameterless
            // constructor — CS0144 / CS0712 / CS7036 in every format, so no generator change
            // can help) and two are our own reopenable gaps (the format's writer has no empty
            // form; the base class still carries members). A verdict that re-derived those
            // gates would be a second copy of this decision.
            if (EmptyObjectBlockOf(ntsObj, format) == EmptyObjectBlock.None)
                return ("object", false, null);
        }

        return (kind, false, null);
    }

    /// <summary>
    /// Does this type declare any PUBLIC INSTANCE data at all — a property (get-only and
    /// indexers included) or a field?
    /// <para>
    /// The empty-object arm answers only for a type that declares NONE, and this is a narrower
    /// claim than the member loop's "is there a member we can SERIALIZE": a get-only property is
    /// a member we decline to serialize, and a type made only of those
    /// (<c>KeyValuePair&lt;,&gt;</c>, <c>Tuple&lt;,&gt;</c>) is a real shape, not an empty one —
    /// writing <c>{}</c> for it would silently drop data the loud PICOSERDE001 drop keeps
    /// visible.
    /// </para>
    /// </summary>
    private static bool DeclaresPublicInstanceData(INamedTypeSymbol type)
    {
        // No LINQ here on purpose: this file is linked into the netstandard2.0 generator
        // projects with no `using` list of its own (see the head of the file).
        foreach (var member in type.GetMembers())
        {
            if (
                member is IPropertySymbol
                {
                    DeclaredAccessibility: Accessibility.Public,
                    IsStatic: false
                }
            )
                return true;
            if (
                member is IFieldSymbol
                {
                    DeclaredAccessibility: Accessibility.Public,
                    IsStatic: false
                }
            )
                return true;
        }

        return false;
    }

    /// <summary>
    /// The gates of the empty-object arm, in ONE place, answering WHICH gate said no
    /// (<see cref="EmptyObjectBlock.None"/> means this type IS the empty object of this format).
    /// <para>
    /// Extracted so that the DROP VERDICT is decided by the same code that made the decision
    /// (N-1/N-4). The gates are not one class of fact:
    /// </para>
    /// <list type="number">
    /// <item>a type that DECLARES public instance data is not zero-member at all — the
    ///       precondition of the empty object, not a gate on it;</item>
    /// <item>the FORMAT must have the form — our writer's gap (no format today),
    ///       reopenable ⇒ <see cref="EmptyObjectBlock.FormatHasNoEmptyForm"/>;</item>
    /// <item>abstract: the reader's <c>new T()</c> is CS0144 ⇒ terminal;</item>
    /// <item>static class: not a value at all, <c>new T()</c> is CS0712 ⇒ terminal;</item>
    /// <item>a base class other than <c>object</c>/<c>ValueType</c> still carries members — a
    ///       separate EXTRACTION question, reopenable ⇒
    ///       <see cref="EmptyObjectBlock.BaseContributesMembers"/>. Writing <c>{}</c> for it
    ///       would silently lose <c>Message</c> on <c>Opaque : Exception</c>;</item>
    /// <item>no accessible parameterless constructor: <c>new T()</c> is CS7036 ⇒ terminal.</item>
    /// </list>
    /// <para>
    /// The order is the order the gates used to be written in, so a type that fails several of
    /// them reports the first one a reader can act on. ③④⑥ are what
    /// <c>GenInfrastructure</c> turns into the terminal ⛔ wording
    /// (<c>DropVerdict.TypeUnconstructible</c>); ②⑤ keep the ⏸ debt wording.
    /// </para>
    /// </summary>
    internal static EmptyObjectBlock EmptyObjectBlockOf(ITypeSymbol type, string format)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } nts)
            return EmptyObjectBlock.NotZeroMember;
        if (DeclaresPublicInstanceData(nts))
            return EmptyObjectBlock.NotZeroMember;
        if (!FormatCapabilities.EmptyObjectSupported(format))
            return EmptyObjectBlock.FormatHasNoEmptyForm;
        if (nts.IsAbstract)
            return EmptyObjectBlock.Abstract;
        if (nts.IsStatic)
            return EmptyObjectBlock.StaticClass;
        if (
            nts.BaseType is { } baseType
            && baseType.SpecialType != SpecialType.System_Object
            && baseType.SpecialType != SpecialType.System_ValueType
        )
            return EmptyObjectBlock.BaseContributesMembers;
        if (!HasParameterlessConstructor(nts))
            return EmptyObjectBlock.NotConstructible;

        return EmptyObjectBlock.None;
    }

    /// <summary>
    /// Can the generated `new T()` create this type — is there an ACCESSIBLE parameterless
    /// constructor, implicit or declared?
    /// <para>
    /// The empty-object arm hands the emitters an ordinary object shape, and every one of them
    /// creates the instance by calling <c>new &lt;Type&gt;()</c> on the read path. A type whose
    /// only constructors take parameters has no such call, so it keeps the loud drop instead of
    /// emitting code that does not compile (invariant I-2). A struct always answers yes:
    /// <c>new S()</c> is legal even when every declared constructor takes parameters.
    /// </para>
    /// <para>
    /// N-4: <c>internal</c> counts too, because the generated code lands in the CONSUMER
    /// assembly, so a parameterless <c>internal</c> constructor of a type declared THERE is
    /// <c>new T()</c>-callable from it. The exception is a type that comes from METADATA only (a
    /// referenced package): its <c>internal</c> constructor is not reachable from the assembly we
    /// generate into, so only <c>public</c> counts there. "Declared in this compilation" is the
    /// right oracle for that, and a generator sees exactly one compilation, so a symbol with
    /// source declarations IS a type the generated code is compiled alongside. <c>protected</c>,
    /// <c>protected internal</c> and <c>private protected</c> all need a derived type or the
    /// declaring class — generated code is neither, so none of them counts.
    /// </para>
    /// </summary>
    private static bool HasParameterlessConstructor(INamedTypeSymbol type)
    {
        if (type.IsValueType)
            return true;

        bool declaredInThisCompilation = !type.DeclaringSyntaxReferences.IsEmpty;

        // No LINQ here on purpose: this file is linked into the netstandard2.0 generator
        // projects with no `using` list of its own (see the head of the file).
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.Parameters.Length != 0)
                continue;
            if (ctor.DeclaredAccessibility == Accessibility.Public)
                return true;
            if (declaredInThisCompilation && ctor.DeclaredAccessibility == Accessibility.Internal)
                return true;
        }

        return false;
    }

    /// <summary>Display format that keeps reference-type nullable annotations ("?").</summary>
    public static readonly SymbolDisplayFormat FullyQualifiedWithNullability =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    /// <summary>
    /// Declaration-position type text: keeps "?" annotations. Use ONLY where generated
    /// code declares or assigns a value — never for identifiers, cache keys, new T()
    /// or typeof(T), where annotations are invalid. Identity positions keep using
    /// <see cref="MapTypeName"/>.
    /// </summary>
    public static string MapTypeNamePreservingNullability(string kind, ITypeSymbol type)
    {
        // Symbol-derived kinds use the annotation-preserving display so nested
        // annotations survive (List<List<string?>> must not collapse to
        // List<List<string>>), not just the top-level '?'.
        //
        // The collection half of this question is "is it a sequence kind", which the
        // collection-kind table answers (CollectionKinds.IsSequence) — spelling the nine kinds
        // here meant a new kind had to be remembered here too. `dict`/`enum`/`object` are NOT
        // sequence kinds and are symbol-derived for their own reason, so they stay: this list is
        // "kind whose declared type is the symbol's own type", not "collection kind".
        var name =
            CollectionKinds.IsSequence(kind) || kind is "dict" or "enum" or "object"
                ? DisplayType(type)
                : MapTypeName(kind, type);
        // Nullable value types (int?, Guid?, ...) and annotated reference types
        // keep their '?' so collection declarations stay exact.
        if (
            (
                (type.NullableAnnotation == NullableAnnotation.Annotated && type.IsReferenceType)
                || type
                    is INamedTypeSymbol
                    {
                        OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                    }
            ) && !name.EndsWith("?", System.StringComparison.Ordinal)
        )
            return name + "?";
        return name;
    }

    /// <summary>Annotation-preserving fully-qualified display for declaration positions.</summary>
    public static string DisplayType(ITypeSymbol type) =>
        type.ToDisplayString(FullyQualifiedWithNullability);

    public static string MapTypeName(string kind, ITypeSymbol type) =>
        kind switch
        {
            "any" => "object",
            "string" => "string",
            "int32" => "int",
            "int64" => "long",
            "int16" => "short",
            "uint16" => "ushort",
            "sbyte" => "sbyte",
            "byte" => "byte",
            "uint32" => "uint",
            "uint64" => "ulong",
            "char" => "char",
            "datetimeoffset" => "System.DateTimeOffset",
            "uri" => "System.Uri",
            "version" => "System.Version",
            "half" => "System.Half",
            "biginteger" => "System.Numerics.BigInteger",
            "int128" => "System.Int128",
            "uint128" => "System.UInt128",
            "nint" => "nint",
            "float32" => "float",
            "float64" => "double",
            "boolean" => "bool",
            "datetime" => "System.DateTime",
            "dateonly" => "System.DateOnly",
            "timeonly" => "System.TimeOnly",
            "timespan" => "System.TimeSpan",
            "guid" => "System.Guid",
            "decimal" => "decimal",
            "bytes" => "byte[]",
            "enum" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "dict" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "object" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            // Every sequence kind is displayed as its own declared type — one arm, asked of the
            // collection-kind table, in place of the nine identical arms that had to be extended
            // by hand whenever a kind was added. (A kind missing here used to fall to
            // `_ => "object"` and silently declare the wrong type.)
            _ when CollectionKinds.IsSequence(kind) => type.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            _ => "object",
        };
}
