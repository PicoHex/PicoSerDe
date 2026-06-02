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
        }

        // Dictionary<K,V>
        if (type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
        {
            if (
                ntsDict.Name == "Dictionary"
                && ntsDict.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            )
                return ("dict", false, null);
        }

        // Built-in special types
        string? kind = type.SpecialType switch
        {
            SpecialType.System_String => "string",
            SpecialType.System_Int32 => "int32",
            SpecialType.System_Int64 => "int64",
            SpecialType.System_Double => "float64",
            // NOTE: System.Single (C# float) maps to "float64" intentionally —
            // the generated code uses double as the universal floating-point
            // representation. Round-tripping preserves exact values for most
            // float32 inputs. If lossless float32 is needed, add a separate "float32" kind.
            SpecialType.System_Single
                => "float64",
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
                INamedTypeSymbol { Name: "DateOnly", ContainingNamespace.Name: "System" }
                    => "dateonly",
                INamedTypeSymbol { Name: "TimeOnly", ContainingNamespace.Name: "System" }
                    => "timeonly",
                INamedTypeSymbol { Name: "TimeSpan", ContainingNamespace.Name: "System" }
                    => "timespan",
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
            }
        }

        return (kind, false, null);
    }

    public static string MapTypeName(string kind, ITypeSymbol type) =>
        kind switch
        {
            "string" => "string",
            "int32" => "int",
            "int64" => "long",
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
            "object" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => "object",
        };
}
