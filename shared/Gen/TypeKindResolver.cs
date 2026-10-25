// Unified TypeKindResolver — shared across all 5 format source generators
// Each SG project includes this file via <Compile Include>

namespace PicoSerDe.Gen;

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
            foreach (var member in ntsObj.GetMembers())
            {
                if (
                    member
                        is IPropertySymbol
                        {
                            DeclaredAccessibility: Accessibility.Public,
                            IsStatic: false,
                            IsIndexer: false
                        } ps
                    && ps.GetMethod is not null
                    && !(ps.IsReadOnly && ps.SetMethod is null)
                )
                {
                    return ("object", false, null);
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
        }

        return (kind, false, null);
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
        var name = kind
            is "list"
                or "array"
                or "dict"
                or "hashset"
                or "queue"
                or "stack"
                or "linkedlist"
                or "immutablearray"
                or "memory"
                or "readonlymemory"
                or "enum"
                or "object"
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
            "hashset" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "queue" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "stack" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "linkedlist" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "immutablearray" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "memory" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "readonlymemory" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
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
            "list" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "array" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "dict" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "object" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => "object",
        };
}
