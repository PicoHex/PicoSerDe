namespace PicoIni.Gen;

internal static class TypeKindResolver
{
    public static (string? Kind, bool IsNullable, ITypeSymbol? InnerType) Resolve(ITypeSymbol type)
    {
        bool isNullable = false;
        ITypeSymbol innerType = type;

        if (type is INamedTypeSymbol { IsGenericType: true } nts
            && nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            isNullable = true;
            innerType = nts.TypeArguments[0];
            type = innerType;
        }

        if (type.SpecialType == SpecialType.System_String) return ("string", isNullable, innerType);
        if (type.SpecialType == SpecialType.System_Int32) return ("int32", isNullable, innerType);
        if (type.SpecialType == SpecialType.System_Int64) return ("int64", isNullable, innerType);
        if (type.SpecialType == SpecialType.System_Double) return ("float64", isNullable, innerType);
        if (type.SpecialType == SpecialType.System_Boolean) return ("boolean", isNullable, innerType);
        if (type.SpecialType == SpecialType.System_Decimal) return ("decimal", isNullable, innerType);

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullName == "System.DateTime") return ("datetime", isNullable, innerType);
        if (fullName == "System.DateOnly") return ("dateonly", isNullable, innerType);
        if (fullName == "System.TimeOnly") return ("timeonly", isNullable, innerType);
        if (fullName == "System.TimeSpan") return ("timespan", isNullable, innerType);
        if (fullName == "System.Guid") return ("guid", isNullable, innerType);

        if (type.TypeKind == TypeKind.Enum) return ("enum", isNullable, innerType);

        if (type is IArrayTypeSymbol) return ("array", isNullable, innerType);

        if (type is INamedTypeSymbol named)
        {
            if (named.TypeArguments.Length == 1)
            {
                var typeName = named.ConstructedFrom.ToDisplayString();
                if (typeName.StartsWith("System.Collections.Generic.List<")
                    || typeName.StartsWith("System.Collections.Generic.IList<")
                    || typeName.StartsWith("System.Collections.Generic.ICollection<")
                    || typeName.StartsWith("System.Collections.Generic.IEnumerable<")
                    || typeName.StartsWith("System.Collections.Generic.IReadOnlyList<")
                    || typeName.StartsWith("System.Collections.Generic.IReadOnlyCollection<"))
                    return ("list", isNullable, innerType);
            }
            if (named.TypeArguments.Length == 2)
            {
                var typeName = named.ConstructedFrom.ToDisplayString();
                if (typeName.StartsWith("System.Collections.Generic.Dictionary<"))
                    return ("dict", isNullable, innerType);
            }
        }

        if (type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct)
            return ("object", isNullable, innerType);

        return (null, false, null);
    }

    public static string MapTypeName(string kind, ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
