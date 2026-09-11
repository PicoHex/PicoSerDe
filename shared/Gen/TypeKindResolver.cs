// Unified TypeKindResolver — shared across all 5 format source generators
// Each SG project includes this file via <Compile Include>

namespace PicoSerDe.Gen;

internal static class TypeKindResolver
{
    /// <param name="format">
    /// Format hint: "json" | "ini" | "msgpack" | "toml" | "yaml".
    /// Used for format-specific type mapping (e.g., byte[] → "bytes" in MsgPack).
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
            // MsgPack-specific: byte[] → "bytes"
            if (format == "msgpack" && arr.ElementType.SpecialType == SpecialType.System_Byte)
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

            // Extended collections (JSON only for now — other format generators do
            // not yet emit these kinds): serialize as arrays, constructed via the
            // concrete type on read.
            if (format == "json")
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
                format == "json"
                && ns == "System.Collections.Concurrent"
                && ntsDict.Name == "ConcurrentDictionary"
            )
                return ("dict", false, null);
        }

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
            SpecialType.System_Char when format == "json" => "char",
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
                    when format == "json" => "datetimeoffset",
                INamedTypeSymbol { Name: "Uri", ContainingNamespace.Name: "System" }
                    when format == "json" => "uri",
                INamedTypeSymbol { Name: "Version", ContainingNamespace.Name: "System" }
                    when format == "json" => "version",
                INamedTypeSymbol { Name: "Half", ContainingNamespace.Name: "System" }
                    when format == "json" => "half",
                INamedTypeSymbol { Name: "BigInteger", ContainingNamespace.Name: "Numerics" }
                    when format == "json" => "biginteger",
                INamedTypeSymbol { Name: "Int128", ContainingNamespace.Name: "System" }
                    when format == "json" => "int128",
                INamedTypeSymbol { Name: "UInt128", ContainingNamespace.Name: "System" }
                    when format == "json" => "uint128",
                INamedTypeSymbol { Name: "IntPtr", ContainingNamespace.Name: "System" }
                    when format == "json" => "nint",
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
        var name = MapTypeName(kind, type);
        if (
            type.NullableAnnotation == NullableAnnotation.Annotated
            && type.IsReferenceType
            && !name.EndsWith("?", System.StringComparison.Ordinal)
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
