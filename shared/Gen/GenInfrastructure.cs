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
    ImmutableArray<PropertyInfo> Properties,
    ImmutableArray<CtorParamInfo> CtorParams = default,
    string? TypeTag = null
)
{
    public bool Equals(TypeInfo other) =>
        FullyQualifiedName == other.FullyQualifiedName
        && Namespace == other.Namespace
        && Name == other.Name
        && Properties.SequenceEqual(other.Properties)
        && CtorParams.SequenceEqual(other.CtorParams)
        && TypeTag == other.TypeTag;

    public override int GetHashCode()
    {
        var hash = FullyQualifiedName.GetHashCode();
        hash = (hash * 397) ^ Namespace.GetHashCode();
        hash = (hash * 397) ^ Name.GetHashCode();
        foreach (var p in Properties)
            hash = (hash * 397) ^ p.GetHashCode();
        foreach (var cp in CtorParams)
            hash = (hash * 397) ^ cp.GetHashCode();
        return hash;
    }
}

/// <summary>Constructor parameter info for [JsonConstructor] support.</summary>
internal readonly record struct CtorParamInfo(
    string Name,
    string TypeKind,
    string TypeFullName,
    string? DateTimeFormat = null
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
    bool IsNullableReference = false
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
    private static readonly DiagnosticDescriptor UnsupportedTypeWarning = new(
        id: "PICOSERDE001",
        title: "Unsupported type skipped during source generation",
        messageFormat: "Type '{0}' on property '{1}' is not supported by the serializer — the property will be ignored",
        category: "PicoSerDe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

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

        return methodName is "Serialize" or "SerializeToUtf8Bytes" or "Deserialize";
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

    // ── Public API (TransformType / ExtractNestedProperties) ──
    // Both delegate to the shared ExtractProperties core below.

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

        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>")
            ns = "";

        var useCamelCase = attrs.HasCamelCase(namedType);
        var properties = ExtractProperties(
            namedType,
            config.FormatTag,
            attrs,
            includeReadOnlyProperties,
            useCamelCase
        );

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
        var useCamelCase = attrs.HasCamelCase(type);
        var properties = ExtractProperties(type, formatTag, attrs, false, useCamelCase);
        return properties.ToImmutableArray();
    }

    // ── Shared core: single source of truth for property extraction ──

    /// <summary>
    /// Extracts serializable properties from <paramref name="type"/>.
    /// Both TransformType and ExtractNestedProperties delegate here.
    /// </summary>
    private static List<PropertyInfo> ExtractProperties(
        INamedTypeSymbol type,
        string formatTag,
        AttributeHelpers attrs,
        bool includeReadOnlyProperties,
        bool useCamelCase
    )
    {
        return ExtractProperties(
            type,
            formatTag,
            attrs,
            includeReadOnlyProperties,
            useCamelCase,
            null
        );
    }

    /// <summary>
    /// Extracts serializable properties. If <paramref name="diagnostics"/> is provided,
    /// emits warnings for skipped members (unsupported types, etc.).
    /// </summary>
    private static List<PropertyInfo> ExtractProperties(
        INamedTypeSymbol type,
        string formatTag,
        AttributeHelpers attrs,
        bool includeReadOnlyProperties,
        bool useCamelCase,
        List<Diagnostic>? diagnostics
    )
    {
        var list = new List<PropertyInfo>();
        int autoKey = 0;

        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (!includeReadOnlyProperties && prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
                continue;
            if (attrs.HasIgnore(prop))
                continue;

            // Optionally override kind when a converter is attached
            var converterType = attrs.GetConverterType(prop);
            var (typeKind, isNullable, _) = TypeKindResolver.Resolve(prop.Type, formatTag);
            if (typeKind is null)
            {
                diagnostics?.Add(
                    Diagnostic.Create(
                        UnsupportedTypeWarning,
                        prop.Locations.FirstOrDefault(),
                        prop.Type.ToDisplayString(),
                        prop.Name
                    )
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
                // Recursively describe nested List<List<...<T>>> — any depth
                if (
                    (ek is "list" or "array")
                    && elementType is INamedTypeSymbol ntsNested
                    && ntsNested.TypeArguments.Length == 1
                )
                {
                    nestedProperties = BuildNestedListElement(ntsNested, formatTag, attrs);
                }
                else if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                {
                    nestedProperties = ExtractNestedProperties(eNtsObj, attrs, formatTag);
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
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = TypeKindResolver.MapTypeName(vk, valType);
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                        nestedProperties = ExtractNestedProperties(vNtsObj, attrs, formatTag);
                }
                else
                {
                    continue;
                }
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                nestedProperties = ExtractNestedProperties(objNts, attrs, formatTag);
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
                    IsNullableReference: isNrtNullable
                )
            );
        }
        return list;
    }

    /// <summary>
    /// Builds a recursive NestedProperties chain for List&lt;List&lt;...&lt;T&gt;&gt;&gt;.
    /// E.g. List&lt;List&lt;int&gt;&gt; → [PropertyInfo(TypeKind="list", NestedProperties=[PropertyInfo(TypeKind="int32")])].
    /// The innermost element has its actual TypeKind; each wrapper has TypeKind="list".
    /// </summary>
    private static ImmutableArray<PropertyInfo> BuildNestedListElement(
        INamedTypeSymbol listType,
        string formatTag,
        AttributeHelpers attrs
    )
    {
        // listType is a List<T> or similar — extract T
        if (listType.TypeArguments.Length != 1)
            return ImmutableArray<PropertyInfo>.Empty;

        var innerType = listType.TypeArguments[0];
        var (innerKind, _, _) = TypeKindResolver.Resolve(innerType, formatTag);
        if (innerKind is null)
            return ImmutableArray<PropertyInfo>.Empty;

        var innerTypeName = TypeKindResolver.MapTypeName(innerKind, innerType);
        var innerFullName = innerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        ImmutableArray<PropertyInfo> innerNested = ImmutableArray<PropertyInfo>.Empty;

        // If the inner type is also a list, recurse
        if (
            (innerKind is "list" or "array")
            && innerType is INamedTypeSymbol ntsInner
            && ntsInner.TypeArguments.Length == 1
        )
        {
            innerNested = BuildNestedListElement(ntsInner, formatTag, attrs);
        }

        var wrapper = new PropertyInfo(
            Name: "__nested",
            JsonName: "__nested",
            TypeKind: innerKind,
            TypeFullName: innerFullName,
            IsNullable: false,
            ElementTypeKind: innerKind,
            ElementTypeName: innerTypeName,
            KeyTypeKind: null,
            KeyTypeName: null,
            NestedProperties: innerNested,
            ConverterTypeFullName: null
        );

        return ImmutableArray.Create(wrapper);
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

    /// <summary>Detects [JsonConstructor] attribute and returns ctor param info.</summary>
    public static ImmutableArray<CtorParamInfo>? DetectJsonConstructor(
        INamedTypeSymbol type,
        string formatTag
    )
    {
        foreach (var ctor in type.Constructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public)
                continue;
            bool hasAttr = false;
            foreach (var attr in ctor.GetAttributes())
            {
                if (attr.AttributeClass?.Name == "JsonConstructorAttribute")
                {
                    hasAttr = true;
                    break;
                }
            }
            if (!hasAttr)
                continue;

            var ctorParams = new List<CtorParamInfo>();
            foreach (var param in ctor.Parameters)
            {
                var (typeKind, _, _) = TypeKindResolver.Resolve(param.Type, formatTag);
                if (typeKind is null)
                    continue;
                ctorParams.Add(
                    new CtorParamInfo(
                        param.Name,
                        typeKind,
                        param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    )
                );
            }
            return ctorParams.ToImmutableArray();
        }
        return null;
    }
}
