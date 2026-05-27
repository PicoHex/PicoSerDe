using Microsoft.CodeAnalysis;

namespace PicoYaml.Gen;

internal static class TypeKindResolver
{
    public static (string? Kind, bool IsNullable, ITypeSymbol? InnerType) Resolve(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        { var (k, _, _) = Resolve(n.TypeArguments[0]); return (k, true, n.TypeArguments[0]); }
        if (type is IArrayTypeSymbol) return ("array", false, null);
        if (type is INamedTypeSymbol nl && nl.TypeArguments.Length == 1
            && nl.Name == "List" && nl.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
            return ("list", false, null);
        string? kk = type.SpecialType switch
        { SpecialType.System_String => "string", SpecialType.System_Int32 => "int32",
          SpecialType.System_Int64 => "int64", SpecialType.System_Double => "float64",
          SpecialType.System_Boolean => "boolean", _ => null };
        if (kk is null && type is INamedTypeSymbol { TypeKind: TypeKind.Enum }) kk = "enum";
        return (kk, false, null);
    }
    public static string MapTypeName(string k, ITypeSymbol t) => k switch
    { "string" => "string", "int32" => "int", "int64" => "long", "float64" => "double",
      "boolean" => "bool", "enum" => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
      _ => "object" };
}
