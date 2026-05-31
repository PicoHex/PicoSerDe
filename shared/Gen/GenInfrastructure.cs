// Shared source generator infrastructure for PicoSerDe
// Included via <Compile Include> in all 5 format SG projects.

namespace PicoSerDe.Gen;

/// <summary>Format context passed from each SG to shared infrastructure.</summary>
public readonly record struct FormatConfig(
    string SerializerClassName,
    string Namespace,
    string FormatTag // "json" | "ini" | "msgpack" | "toml" | "yaml"
);

/// <summary>Shared type descriptor used by all 5 format SGs.</summary>
internal readonly record struct TypeInfo(
    string FullyQualifiedName,
    string Namespace,
    string Name,
    ImmutableArray<PropertyInfo> Properties
)
{
    public bool Equals(TypeInfo other) =>
        FullyQualifiedName == other.FullyQualifiedName
        && Namespace == other.Namespace
        && Name == other.Name
        && Properties.SequenceEqual(other.Properties);

    public override int GetHashCode()
    {
        var hash = FullyQualifiedName.GetHashCode();
        hash = (hash * 397) ^ Namespace.GetHashCode();
        hash = (hash * 397) ^ Name.GetHashCode();
        foreach (var p in Properties)
            hash = (hash * 397) ^ p.GetHashCode();
        return hash;
    }
}

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
    string? DateTimeFormat = null
);

/// <summary>Attribute detection helpers — each SG provides its own attribute class names.</summary>
public readonly record struct AttributeHelpers(
    Func<ITypeSymbol, bool> HasCamelCase,
    Func<IPropertySymbol, string?> GetCustomName,
    Func<IPropertySymbol, bool> HasIgnore,
    Func<IPropertySymbol, string?> GetConverterType,
    Func<IPropertySymbol, string?> GetDateTimeFormat
);

internal static class GenInfrastructure
{
    public static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: var expr })
            return false;

        var name = expr switch
        {
            MemberAccessExpressionSyntax { Name: var n } => n,
            MemberBindingExpressionSyntax { Name: var n } => n,
            _ => null
        };

        var methodName = name switch
        {
            GenericNameSyntax gn => gn.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => null
        };

        return methodName is "Serialize" or "SerializeToUtf8Bytes" or "Deserialize";
    }

    /// <summary>Converts a fully qualified type name to a safe identifier (replaces . and :: with _).</summary>
    public static string SafeName(string fullName)
    {
        return fullName.Replace("global::", "").Replace('.', '_');
    }

    /// <summary>Returns the fully qualified inner helper class name (e.g. "global::Ns.Sub_TypeJsonInner").</summary>
    public static string InnerClassName(string suffix, string typeFullName)
    {
        var lastDot = typeFullName.LastIndexOf('.');
        var safeName = SafeName(typeFullName);
        if (lastDot <= 0)
            return $"{safeName}{suffix}";
        var ns = typeFullName.Substring(0, lastDot);
        return $"{ns}.{safeName}{suffix}";
    }

    public static string ShortName(string fullName)
    {
        var name = fullName.Replace("global::", "");
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
    }

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

    public static TypeInfo? TransformType(
        GeneratorSyntaxContext ctx,
        FormatConfig config,
        AttributeHelpers attrs
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

        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>")
            ns = "";

        var useCamelCase = attrs.HasCamelCase(namedType);
        var properties = new List<PropertyInfo>();

        foreach (var member in namedType.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
                continue;
            if (attrs.HasIgnore(prop))
                continue;

            var (typeKind, isNullable, innerTypeSymbol) = TypeKindResolver.Resolve(
                prop.Type,
                config.FormatTag
            );
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            ImmutableArray<PropertyInfo> nestedProperties = ImmutableArray<PropertyInfo>.Empty;

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, _) = TypeKindResolver.Resolve(elementType, config.FormatTag);
                if (ek is null)
                    continue;
                elementTypeKind = ek;
                elementTypeName = TypeKindResolver.MapTypeName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj, attrs, config.FormatTag);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = TypeKindResolver.Resolve(keyType, config.FormatTag);
                    var (vk, _, _) = TypeKindResolver.Resolve(valType, config.FormatTag);
                    if (kk is null || vk is null)
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = TypeKindResolver.MapTypeName(vk, valType);
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                        nestedProperties = ExtractNestedProperties(
                            vNtsObj,
                            attrs,
                            config.FormatTag
                        );
                }
                else
                    continue;
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                nestedProperties = ExtractNestedProperties(objNts, attrs, config.FormatTag);
            }

            properties.Add(
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
                    attrs.GetConverterType(prop),
                    attrs.GetDateTimeFormat(prop)
                )
            );
        }

        return new TypeInfo(
            namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            namedType.Name,
            properties.ToImmutableArray()
        );
    }

    public static ImmutableArray<PropertyInfo> ExtractNestedProperties(
        INamedTypeSymbol type,
        AttributeHelpers attrs,
        string formatTag
    )
    {
        var list = new List<PropertyInfo>();
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
                continue;
            if (attrs.HasIgnore(prop))
                continue;

            var (typeKind, isNullable, _) = TypeKindResolver.Resolve(prop.Type, formatTag);
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            ImmutableArray<PropertyInfo> nestedProperties = ImmutableArray<PropertyInfo>.Empty;

            if (typeKind is "list" or "array")
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
                    continue;
                elementTypeKind = ek;
                elementTypeName = TypeKindResolver.MapTypeName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj, attrs, formatTag);
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
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = TypeKindResolver.MapTypeName(vk, valType);
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                        nestedProperties = ExtractNestedProperties(vNtsObj, attrs, formatTag);
                }
                else
                    continue;
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                nestedProperties = ExtractNestedProperties(objNts, attrs, formatTag);
            }

            list.Add(
                new PropertyInfo(
                    prop.Name,
                    attrs.GetCustomName(prop) ?? prop.Name,
                    typeKind,
                    prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    isNullable,
                    elementTypeKind,
                    elementTypeName,
                    keyTypeKind,
                    keyTypeName,
                    nestedProperties,
                    attrs.GetConverterType(prop),
                    attrs.GetDateTimeFormat(prop)
                )
            );
        }
        return list.ToImmutableArray();
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
        }
    }

    private static void AddNestedType(
        string fullName,
        ImmutableArray<PropertyInfo> props,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        if (nestedTypes.ContainsKey(fullName))
            return;
        nestedTypes[fullName] = props;

        foreach (var np in props)
        {
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
        }
    }
}
