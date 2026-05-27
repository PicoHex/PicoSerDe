using Microsoft.CodeAnalysis;

namespace PicoJson.Gen;

internal static class TypeKindResolver
{
    public static (string? Kind, bool IsNullable, ITypeSymbol? InnerType) Resolve(ITypeSymbol type)
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
            var (innerKind, _, _) = Resolve(inner);
            return (innerKind, true, inner);
        }

        // T[]
        if (type is IArrayTypeSymbol)
            return ("array", false, null);

        // List<T>
        if (type is INamedTypeSymbol ntsList && ntsList.TypeArguments.Length == 1)
        {
            if (
                ntsList.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IList_T
                || ntsList.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_ICollection_T
                || ntsList.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IEnumerable_T
            )
            {
                var elementType = ntsList.TypeArguments[0];
                var (ek, _, _) = Resolve(elementType);
                if (ek is null)
                    return (null, false, null);
                return ("list", false, null);
            }

            // Check by name for List<T> (Roslyn doesn't have a SpecialType for List<T>)
            if (
                ntsList.Name == "List"
                && ntsList.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            )
                return ("list", false, null);
        }

        // IReadOnlyList<T>, IReadOnlyCollection<T>, IEnumerable<T> → list
        if (type is INamedTypeSymbol ntsRoList && ntsRoList.TypeArguments.Length == 1)
        {
            var roName = ntsRoList.Name;
            var roNs = ntsRoList.ContainingNamespace?.ToDisplayString() ?? "";
            if (
                (roName is "IReadOnlyList" or "IReadOnlyCollection" or "IEnumerable")
                && roNs == "System.Collections.Generic"
            )
            {
                var elementType = ntsRoList.TypeArguments[0];
                var (ek, _, _) = Resolve(elementType);
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
            SpecialType.System_Single => "float64",
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
            "enum" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "object" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => "object",
        };
}
