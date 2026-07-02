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
    string? DiscriminatorPropertyName = null,
    ImmutableArray<DerivedTypeInfo> DerivedTypes = default,
    // Non-null when this TypeInfo represents a top-level array type (e.g. string[]).
    // The element kind drives code gen (EmitArraySerializer / EmitArrayDeserializer).
    string? ArrayElementKind = null,
    string? ArrayElementName = null,
    ImmutableArray<PropertyInfo> ArrayElementNestedProps = default
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
        && DiscriminatorPropertyName == other.DiscriminatorPropertyName
        && DerivedTypes.SequenceEqual(other.DerivedTypes);

    public override int GetHashCode()
    {
        var hash = FullyQualifiedName.GetHashCode();
        hash = (hash * 397) ^ Namespace.GetHashCode();
        hash = (hash * 397) ^ Name.GetHashCode();
        foreach (var p in Properties)
            hash = (hash * 397) ^ p.GetHashCode();
        foreach (var cp in CtorParams)
            hash = (hash * 397) ^ cp.GetHashCode();
        hash = (hash * 397) ^ (DiscriminatorPropertyName?.GetHashCode() ?? 0);
        foreach (var dt in DerivedTypes)
            hash = (hash * 397) ^ dt.GetHashCode();
        return hash;
    }
}

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
    bool IsNullableReference = false,
    bool IsRequired = false
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
        return fullName
            .Replace("global::", "")
            .Replace('.', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace(',', '_')
            .Replace(' ', '_')
            .Replace('[', '_')
            .Replace(']', '_');
    }

    /// <summary>Returns the fully qualified inner helper class name (e.g. "global::Ns.Sub_TypeJsonInner").</summary>
    public static string InnerClassName(string suffix, string typeFullName)
    {
        // For generic type names (containing '<'), don't try to extract a
        // namespace — the class is emitted at global scope.
        if (typeFullName.Contains('<'))
            return $"global::{SafeName(typeFullName)}{suffix}";

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
        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>")
            ns = "";

        var useCamelCase = attrs.HasCamelCase(namedType);
        var properties = ExtractProperties(
            namedType,
            config.FormatTag,
            attrs,
            includeReadOnlyProperties,
            useCamelCase,
            includeFields: includeFields
        );

        return new TypeInfo(
            namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            namedType.Name,
            properties.ToImmutableArray(),
            IsRefLikeType: namedType.IsRefLikeType
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
            includeFields: includeFields
        );
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
        bool useCamelCase,
        bool includeFields = false
    )
    {
        return ExtractProperties(
            type,
            formatTag,
            attrs,
            includeReadOnlyProperties,
            useCamelCase,
            null,
            includeFields
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
        List<Diagnostic>? diagnostics,
        bool includeFields = false
    )
    {
        var list = new List<PropertyInfo>();
        int autoKey = 0;

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
                    continue;
                bool fnNrt = false;
                if (!fn && field.Type.NullableAnnotation == NullableAnnotation.Annotated)
                {
                    fn = true;
                    fnNrt = true;
                }

                ImmutableArray<PropertyInfo> fieldNested = ImmutableArray<PropertyInfo>.Empty;
                if (fk is "object" && field.Type is INamedTypeSymbol fieldObjNts)
                {
                    fieldNested = ExtractNestedProperties(fieldObjNts, attrs, formatTag);
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
                        IsNullableReference: fnNrt
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
                    {
                        nestedProperties = ExtractNestedProperties(vNtsObj, attrs, formatTag);
                    }
                    else if (
                        vk is "dict"
                        && valType is INamedTypeSymbol vNtsDict
                        && vNtsDict.TypeArguments.Length == 2
                    )
                    {
                        // Nested Dictionary<K2,V2> — resolve inner dict's K/V and wrap as synthetic PropertyInfo
                        nestedProperties = BuildNestedDictElement(vNtsDict, formatTag, attrs);
                    }
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
                    IsNullableReference: isNrtNullable,
                    IsRequired: prop.IsRequired
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
        string formatTag,
        AttributeHelpers attrs
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

        if (iv is "object" && innerValType is INamedTypeSymbol ivNtsObj)
        {
            innerNested = ExtractNestedProperties(ivNtsObj, attrs, formatTag);
        }
        else if (
            iv is "dict"
            && innerValType is INamedTypeSymbol ivNtsDict
            && ivNtsDict.TypeArguments.Length == 2
        )
        {
            innerNested = BuildNestedDictElement(ivNtsDict, formatTag, attrs);
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
            ConverterTypeFullName: null
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
            // Recurse into nested-dict synthetic PropertyInfo to collect object value types
            if (prop.TypeKind == "dict" && prop.NestedProperties.Length > 0)
            {
                foreach (var dnp in prop.NestedProperties)
                    AddNestedTypeFromDict(dnp, nestedTypes);
            }
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
            var hasCtor = ctorParams.HasValue;

            var ti = TransformTypeSymbol(dt, config, attrs, includeReadOnlyProperties: hasCtor);
            if (!ti.HasValue)
                continue;

            if (hasCtor)
                ti = ti.Value with { CtorParams = ctorParams.Value };

            builder.Add(ti.Value);
        }

        return builder.ToImmutable();
    }
}
