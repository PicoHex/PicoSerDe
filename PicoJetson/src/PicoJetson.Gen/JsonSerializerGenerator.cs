namespace PicoJetson.Gen;

using CtorParamInfo = PicoSerDe.Gen.CtorParamInfo;
using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class JsonSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "JsonSerializer",
        "PicoJetson",
        "json",
        "JsonConstructorAttribute"
    );

    private static readonly DiagnosticDescriptor HintNameTrace = new(
        id: "PICOJETSON001",
        title: "AddSource hintName trace",
        messageFormat: "AddSource hintName: '{0}'",
        category: "PicoJetson.Gen",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor EmptyTypeName = new(
        id: "PICOJETSON002",
        title: "Empty type name detected",
        messageFormat: "TypeInfo has empty/null Name (FullyQualifiedName='{0}'). Skipping to prevent invalid hintName.",
        category: "PicoJetson.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        HasJsonCamelCase,
        GetJsonPropertyName,
        HasJsonIgnore,
        GetJsonConverterType,
        GetDateTimeFormat
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline A: usage-driven (existing)
        var usageDriven = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidate(node),
                transform: static (ctx, _) => Transform(ctx)
            )
            .Where(static t => t is not null)
            .Select(static (t, _) => t!.Value);

        // Pipeline B: attribute-driven — discover types via [PicoSerializable]
        var attrDriven = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoSerDe.Core.PicoSerializableAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandAttributes(ctx, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline D: shorthand attribute [GenerateSerializer(typeof(T))]
        var shorthandAttr = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoSerDe.Core.GenerateSerializerAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandAttributes(ctx, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline C: format-specific attribute — discover types via [PicoJsonSerializable]
        var formatAttr = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoJetson.PicoJsonSerializableAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandAttributes(ctx, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline E: polymorphic — discover types via [PicoDerivedType]
        var polyPipeline = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoSerDe.Core.PicoDerivedTypeAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandPolymorphicTypes(ctx, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline F: assembly name for namespace isolation of generated helpers
        var asmName = context.CompilationProvider.Select(
            static (c, _) =>
                (c.AssemblyName ?? "unknown").Replace('.', '_').Replace('-', '_').Replace(' ', '_')
        );

        // Merge all pipelines into one output
        var all = usageDriven
            .Collect()
            .Combine(attrDriven.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(formatAttr.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(shorthandAttr.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(polyPipeline.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(asmName);

        context.RegisterSourceOutput(
            all,
            static (spc, pair) =>
            {
                PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = $"__PicoSerDe_{pair.Right}";
                GenerateAll(spc, pair.Left);
            }
        );
    }

    // ── Candidate detection ──

    private static bool IsCandidate(SyntaxNode node) =>
        PicoSerDe.Gen.GenInfrastructure.IsCandidate(node);

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method)
            return null;
        if (method.TypeArguments.Length != 1)
            return null;

        var typeArg = method.TypeArguments[0];

        // ── Top-level array types (e.g. DiscoveredModel[], string[], Message[]) ──
        if (typeArg is IArrayTypeSymbol arrType)
            return TransformArray(arrType);

        // ── Top-level List<T> (e.g. List<int>, List<ProviderTemplate>) ──
        if (
            typeArg is INamedTypeSymbol ntsCheck
            && PicoSerDe.Gen.GenInfrastructure.IsGenericList(ntsCheck)
        )
            return PicoSerDe.Gen.GenInfrastructure.TransformTopLevelList(ntsCheck, Config, Attrs);

        // ── Named type (class/struct) ──
        if (typeArg is not INamedTypeSymbol namedType)
            return null;
        if (namedType.IsAnonymousType)
            return null; // handled by anonDriven pipeline

        // Detect [JsonConstructor] first to know if we should include read-only props
        bool hasCtor = false;

        // Ref struct: handle with includes, skip constructor detection
        if (namedType.IsRefLikeType)
        {
            return PicoSerDe.Gen.GenInfrastructure.TransformTypeSymbol(
                namedType,
                Config,
                Attrs,
                includeReadOnlyProperties: true,
                includeFields: true
            );
        }

        // Record primary constructor auto-detection (like [JsonConstructor] for records)
        if (namedType.IsRecord && !hasCtor)
        {
            var ctors = namedType
                .Constructors.Where(c => c.DeclaredAccessibility == Accessibility.Public)
                .ToArray();
            if (ctors.Length == 1 && !ctors[0].IsImplicitlyDeclared)
                hasCtor = true;
        }

        foreach (var ctor in namedType.Constructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public)
                continue;
            foreach (var attr in ctor.GetAttributes())
            {
                if (attr.AttributeClass?.Name == "JsonConstructorAttribute")
                {
                    hasCtor = true;
                    break;
                }
            }
            if (hasCtor)
                break;
        }

        var info = PicoSerDe.Gen.GenInfrastructure.TransformType(
            ctx,
            Config,
            Attrs,
            includeReadOnlyProperties: hasCtor
        );
        if (info is not { } ti)
            return null;

        if (!hasCtor)
            return ti;

        // Check for [JsonConstructor] on the target type
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method2)
            return ti;
        if (method2.TypeArguments.Length != 1)
            return ti;
        var typeArg2 = method2.TypeArguments[0];
        if (typeArg2 is not INamedTypeSymbol namedType2)
            return ti;

        // For records: extract primary constructor params without requiring [JsonConstructor]
        CtorParamInfo[]? ctorParams = null;
        if (namedType2.IsRecord)
        {
            var primary = namedType2
                .Constructors.Where(c =>
                    c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared
                )
                .FirstOrDefault();
            if (primary is not null)
            {
                var list = new List<CtorParamInfo>();
                foreach (var param in primary.Parameters)
                {
                    var (typeKind, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(
                        param.Type,
                        Config.FormatTag
                    );
                    if (typeKind is not null)
                        list.Add(
                            new CtorParamInfo(
                                param.Name,
                                typeKind,
                                param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            )
                        );
                }
                ctorParams = list.ToArray();
            }
        }

        if (ctorParams is null)
        {
            var cp = PicoSerDe.Gen.GenInfrastructure.DetectConstructor(
                namedType2,
                Config.FormatTag,
                "JsonConstructorAttribute"
            );
            if (cp is not { } existing)
                return ti;
            ctorParams = new CtorParamInfo[existing.Length];
            for (int i = 0; i < existing.Length; i++)
                ctorParams[i] = existing[i];
        }

        return ti with
        {
            CtorParams = ImmutableArray.Create(ctorParams),
        };
    }

    /// <summary>
    /// Creates a synthetic TypeInfo for a top-level array type (e.g. string[], DiscoveredModel[]).
    /// The SG will emit array-specific serializer/deserializer code.
    /// </summary>
    private static TypeInfo TransformArray(IArrayTypeSymbol arrType)
    {
        var elementType = arrType.ElementType;
        var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(elementType, Config.FormatTag);
        if (ek is null)
            return new TypeInfo(); // will be skipped by caller check on Name being empty

        var arrFqn = arrType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var elemFqn = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        ImmutableArray<PropertyInfo> elemNested = default;

        if (ek is "object" && elementType is INamedTypeSymbol eNts)
        {
            // Extract nested properties so GenerateAll can emit inner helpers
            elemNested = PicoSerDe.Gen.GenInfrastructure.ExtractNestedProperties(
                eNts,
                Attrs,
                Config.FormatTag
            );
        }

        return new TypeInfo(
            FullyQualifiedName: arrFqn,
            Namespace: "",
            Name: $"Array_{PicoSerDe.Gen.GenInfrastructure.SafeName(arrFqn)}",
            Properties: ImmutableArray<PropertyInfo>.Empty,
            ArrayElementKind: ek,
            ArrayElementName: elemFqn,
            ArrayElementNestedProps: elemNested
        );
    }

    // ── Attribute helpers ──

    private static string? GetJsonPropertyName(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonPropertyNameAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJetson"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string name
            )
                return name;
        }
        return null;
    }

    private static bool HasJsonIgnore(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonIgnoreAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJetson"
            )
                return true;
        }
        return false;
    }

    private static string? GetJsonConverterType(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonConverterAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJetson"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol converterType
            )
                return converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        return null;
    }

    private static string? GetDateTimeFormat(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "DateTimeFormatAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJetson"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string format
            )
                return format;
        }
        return null;
    }

    private static bool HasJsonCamelCase(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonCamelCaseAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJetson"
            )
                return true;
        }
        return false;
    }

    // ── Source generation ──

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var hintNames = new HashSet<string>();

        // Collect all unique nested object types (M×N dedup: emit once, reference from parents)
        var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
        foreach (var type in types)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedTypes(type, nestedTypes);

        // Also collect array element types (e.g. DiscoveredModel for DiscoveredModel[])
        foreach (var type in types)
        {
            if (
                type.ArrayElementKind is "object"
                && !string.IsNullOrEmpty(type.ArrayElementName)
                && !type.ArrayElementNestedProps.IsDefaultOrEmpty
            )
            {
                var elemFqn = type.ArrayElementName!.Replace("global::", "");
                if (!nestedTypes.ContainsKey(elemFqn))
                    nestedTypes[elemFqn] = type.ArrayElementNestedProps;
            }
        }

        // Collect nested Dictionary types (e.g. Dictionary<string, Dictionary<string, Foo>>)
        var nestedDictTypes = new Dictionary<string, PropertyInfo>();
        foreach (var type in types)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedDictTypes(type, nestedDictTypes);

        // Generate inner helpers for shared nested types
        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var safeName = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanName);
            var hintName = $"{safeName}_JsonInner.g.cs";
            if (!hintNames.Add(hintName))
                spc.ReportDiagnostic(
                    Diagnostic.Create(
                        HintNameTrace,
                        null,
                        $"DUPLICATE inner hintName: '{hintName}' (fullName='{fullName}')"
                    )
                );
            spc.AddSource(
                hintName,
                SourceText.From(GenerateInnerHelper(cleanName, safeName, props), Encoding.UTF8)
            );
        }

        // Emit a shared helper for Dictionary<string, object?> ("any"-valued dicts)
        // when any type in this compilation uses that pattern.
        // Must check types, nestedTypes (inner helpers), and nestedDictTypes —
        // a property like ContentBlock.Arguments is only discovered via nested type traversal.
        bool hasAnyValue =
            types.Any(t => t.Properties.Any(p => p.ElementTypeKind == "any"))
            || nestedTypes.Values.Any(props => props.Any(p => p.ElementTypeKind == "any"))
            || nestedDictTypes.Values.Any(dp => dp.ElementTypeKind == "any");
        if (hasAnyValue)
        {
            var helperHint = "__PicoAnyDictHelper.g.cs";
            if (hintNames.Add(helperHint))
                spc.AddSource(helperHint, SourceText.From(GenerateAnyDictHelper(), Encoding.UTF8));
        }

        // Generate inner helpers for nested Dictionary types
        foreach (var kv in nestedDictTypes)
        {
            var fullName = kv.Key;
            var dictProp = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var safeName = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanName);
            var hintName = $"{safeName}_JsonDictInner.g.cs";
            if (hintNames.Add(hintName))
                spc.AddSource(
                    hintName,
                    SourceText.From(
                        GenerateDictInnerHelper(cleanName, safeName, dictProp),
                        Encoding.UTF8
                    )
                );
        }

        // Generate main type files — merge duplicate FQNs, preferring poly entries
        var typeMap = new Dictionary<string, TypeInfo>();
        foreach (var type in types)
        {
            if (string.IsNullOrEmpty(type.FullyQualifiedName))
                continue;
            if (typeMap.TryGetValue(type.FullyQualifiedName, out var existing))
            {
                // Merge: if new entry has DerivedTypes but existing doesn't, use new
                if (!type.DerivedTypes.IsDefaultOrEmpty && existing.DerivedTypes.IsDefaultOrEmpty)
                    typeMap[type.FullyQualifiedName] = type;
                // If existing has DerivedTypes, keep it (first poly wins)
            }
            else
            {
                typeMap[type.FullyQualifiedName] = type;
            }
        }

        foreach (var kv in typeMap)
        {
            var type = kv.Value;

            // Guard: skip types with empty/null Name
            if (string.IsNullOrEmpty(type.Name))
            {
                spc.ReportDiagnostic(
                    Diagnostic.Create(EmptyTypeName, null, type.FullyQualifiedName ?? "(null)")
                );
                continue;
            }

            // Always use FQN-based hintName — zero collision risk (same strategy as Inner Helpers)
            var safeFq = PicoSerDe.Gen.GenInfrastructure.SafeName(type.FullyQualifiedName ?? "");
            var mainHintName = $"{safeFq}_JsonSerializer.g.cs";

            var source = GenerateTypeCode(type, typeMap);
            spc.AddSource(mainHintName, SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string GenerateInnerHelper(
        string fullName,
        string shortName,
        ImmutableArray<PropertyInfo> props
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System; using System.Buffers; using System.Text;");
        sb.AppendLine("using PicoSerDe.Core; using PicoJetson;");

        // Assembly-prefixed namespace for cross-project isolation + using to bring
        // original types into scope inside the prefixed namespace.
        var prefix = PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix;
        if (prefix is not null)
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(prefix);
            sb.AppendLine(";");
            // Bring original namespace into scope so type references resolve.
            var lastDot = fullName.LastIndexOf('.');
            if (lastDot > 0)
            {
                sb.Append("using ");
                sb.Append(fullName.Substring(0, lastDot));
                sb.AppendLine(";");
            }
        }
        else
        {
            // No prefix — use original namespace.
            var lastDot = fullName.LastIndexOf('.');
            if (lastDot > 0)
            {
                sb.AppendLine();
                sb.Append("namespace ");
                sb.Append(fullName.Substring(0, lastDot));
                sb.AppendLine(";");
            }
        }
        sb.AppendLine();
        sb.Append("internal static class ");
        sb.Append(shortName);
        sb.AppendLine("JsonInner");
        sb.AppendLine("{");

        // Serialize helper
        sb.Append("    internal static void Serialize(ref JsonWriter jw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        jw.WriteStartObject();");
        foreach (var prop in props)
        {
            // DefaultIgnoreCondition: same guard as the top-level emit path
            bool checkNull = EmitIgnoreConditionOpen(sb, prop, "value." + prop.Name, "        ");
            sb.Append("        jw.WritePropertyName(\"");
            sb.Append(EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\"u8);");
            EmitSerializeProperty(sb, prop, "value." + prop.Name, "        ");
            if (checkNull)
                sb.AppendLine("        }");
        }
        sb.AppendLine("        jw.WriteEndObject();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Deserialize helper
        sb.Append("    internal static ");
        sb.Append(fullName);
        sb.AppendLine(" Deserialize(ref JsonReader reader)");
        sb.AppendLine("    {");
        var reqInner = props.Where(p => p.IsRequired).ToArray();
        if (reqInner.Length > 0)
        {
            sb.Append("        var obj = new ");
            sb.Append(fullName);
            sb.AppendLine(" {");
            foreach (var rp in reqInner)
            {
                sb.Append("            ");
                sb.Append(rp.Name);
                sb.Append(" = ");
                switch (rp.TypeKind)
                {
                    case "string":
                        sb.Append("\"\"");
                        break;
                    default:
                        sb.Append("default");
                        break;
                }
                sb.AppendLine(",");
            }
            sb.Append("        };");
        }
        else
        {
            sb.Append("        var obj = new ");
            sb.Append(fullName);
            sb.AppendLine("();");
        }
        sb.AppendLine(
            "        while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            var __n = reader.GetStringRaw();");
        sb.AppendLine("            reader.Read();");
        for (int i = 0; i < props.Length; i++)
        {
            var np = props[i];
            var kw = i == 0 ? "if" : "else if";
            sb.Append("            ");
            sb.Append(kw);
            sb.Append(" (TextHelpers.Eq(__n, \"");
            sb.Append(EscapeCSharpString(np.JsonName));
            sb.AppendLine(
                "\"u8, !(global::PicoJetson.JsonOptions.Current?.PropertyNameCaseInsensitive ?? true)))"
            );
            sb.AppendLine("            {");
            EmitDeserializeProperty(sb, np, "obj", "                ");
            sb.AppendLine("            }");
        }
        if (props.Length > 0)
            sb.AppendLine("            else reader.TrySkip();");
        else
            sb.AppendLine("            reader.TrySkip();");
        sb.AppendLine("        }");
        sb.AppendLine("        return obj;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Generates an inner helper for a nested Dictionary type (used when a dict's
    /// value type is itself a Dictionary). The helper provides Serialize/Deserialize
    /// methods that the parent dict's element-level code calls.
    /// </summary>
    private static string GenerateDictInnerHelper(
        string fullName,
        string shortName,
        PropertyInfo dictProp
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System; using System.Buffers; using System.Text;");
        sb.AppendLine("using PicoSerDe.Core; using PicoJetson;");

        // Assembly-prefixed namespace (flat) + using original namespace.
        var prefix = PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix;
        if (!fullName.Contains('<'))
        {
            if (prefix is not null)
            {
                sb.AppendLine();
                sb.Append("namespace ");
                sb.Append(prefix);
                sb.AppendLine(";");
                var lastDot = fullName.LastIndexOf('.');
                if (lastDot > 0)
                {
                    sb.Append("using ");
                    sb.Append(fullName.Substring(0, lastDot));
                    sb.AppendLine(";");
                }
            }
            else
            {
                var lastDot = fullName.LastIndexOf('.');
                if (lastDot > 0)
                {
                    sb.AppendLine();
                    sb.Append("namespace ");
                    sb.Append(fullName.Substring(0, lastDot));
                    sb.AppendLine(";");
                }
            }
        }
        sb.AppendLine();
        sb.Append("internal static class ");
        sb.Append(shortName);
        sb.AppendLine("JsonDictInner");
        sb.AppendLine("{");

        // ── Serialize ──
        sb.Append("    internal static void Serialize(ref JsonWriter jw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        jw.WriteStartObject();");
        sb.AppendLine("        foreach (var __kvp in value)");
        sb.AppendLine("        {");
        EmitDictInnerKey(sb, dictProp, "            ");
        EmitDictInnerValueSerialize(sb, dictProp, "            ");
        sb.AppendLine("        }");
        sb.AppendLine("        jw.WriteEndObject();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── Deserialize ──
        sb.Append("    internal static ");
        sb.Append(fullName);
        sb.AppendLine(" Deserialize(ref JsonReader reader)");
        sb.AppendLine("    {");
        sb.Append("        var obj = new ");
        sb.Append(fullName);
        sb.AppendLine("();");
        sb.AppendLine("        if (reader.TokenType == TokenType.ObjectStart)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("            {");
        EmitDictInnerKeyRead(sb, dictProp, "                ");
        sb.AppendLine("                reader.Read();");
        EmitDictInnerValueDeserialize(sb, dictProp, "obj", "                ");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return obj;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitDictInnerKey(StringBuilder sb, PropertyInfo dp, string indent)
    {
        if (dp.KeyTypeKind == "string")
        {
            sb.Append(indent);
            sb.AppendLine("jw.WritePropertyName(Encoding.UTF8.GetBytes(__kvp.Key));");
        }
        else
        {
            sb.Append(indent);
            sb.AppendLine("jw.WritePropertyName(Encoding.UTF8.GetBytes(__kvp.Key.ToString()));");
        }
    }

    private static void EmitDictInnerValueSerialize(
        StringBuilder sb,
        PropertyInfo dp,
        string indent
    )
    {
        switch (dp.ElementTypeKind)
        {
            case "string":
                if (dp.ElementIsNullableReference)
                {
                    sb.Append(indent);
                    sb.AppendLine("if (__kvp.Value != null)");
                    sb.Append(indent);
                    sb.AppendLine("    jw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value));");
                    sb.Append(indent);
                    sb.AppendLine("else");
                    sb.Append(indent);
                    sb.AppendLine("    jw.WriteNull();");
                }
                else
                {
                    sb.Append(indent);
                    sb.AppendLine("jw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value));");
                }
                break;
            case "int32":
            case "int64":
            case "float32":
            case "float64":
                sb.Append(indent);
                sb.AppendLine("jw.WriteNumber(__kvp.Value);");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("jw.WriteBoolean(__kvp.Value);");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine(
                    "jw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString(\"O\")));"
                );
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));");
                break;
            case "decimal":
            case "enum":
                sb.Append(indent);
                sb.AppendLine("jw.WriteNumber(__kvp.Value);");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    dp.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append("if (__kvp.Value == null) jw.WriteNull(); else ");
                sb.Append(sn);
                sb.AppendLine(".Serialize(ref jw, __kvp.Value);");
                break;
            }
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonDictInner",
                    dp.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append(sn);
                sb.AppendLine(".Serialize(ref jw, __kvp.Value);");
                break;
            }
            case "any":
                EmitAnyValueSerialize(sb, "__kvp.Value", indent);
                break;
            default:
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));");
                break;
        }
    }

    private static void EmitDictInnerKeyRead(StringBuilder sb, PropertyInfo dp, string indent)
    {
        switch (dp.KeyTypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.AppendLine("var __dictKey = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("int.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("long.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(dp.KeyTypeName);
                sb.AppendLine(">(__strValue, out var __dictKey);");
                break;
            default:
                sb.Append(indent);
                sb.AppendLine("var __dictKey = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
        }
    }

    private static void EmitDictInnerValueDeserialize(
        StringBuilder sb,
        PropertyInfo dp,
        string dictVar,
        string indent
    )
    {
        switch (dp.ElementTypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __ev)) {");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetInt64(out var __lev);");
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __ev);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __ev);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = (float)__ev;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __ev);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __ev);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __ev);"
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __ev);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __ev);"
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(dp.ElementTypeName);
                sb.AppendLine(">(__strValue, out var __ev);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = __ev;");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    dp.ElementTypeName!
                );
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = null!;");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[__dictKey] = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonDictInner",
                    dp.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[__dictKey] = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
            case "any":
                EmitAnyValueDeserialize(sb, $"{dictVar}[__dictKey]", indent);
                break;
            default:
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
        }
    }

    /// <summary>
    /// Emits a shared helper for Dictionary&lt;string, object?&gt; values.
    /// Generated once per compilation when any type uses &quot;any&quot;-valued dicts.
    /// The helper handles full runtime dispatch including nested dict/list recursively.
    /// </summary>
    private static string GenerateAnyDictHelper()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System; using System.Buffers; using System.Text;");
        sb.AppendLine("using PicoSerDe.Core; using PicoJetson;");
        var prefix = PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix ?? "__PicoSerDe";
        sb.AppendLine();
        sb.Append("namespace ");
        sb.Append(prefix);
        sb.AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("internal static class __PicoAnyDictHelper");
        sb.AppendLine("{");

        // ── SerializeAnyValue ──
        sb.AppendLine("    internal static void SerializeValue(ref JsonWriter jw, object? value)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (value == null)");
        sb.AppendLine("            jw.WriteNull();");
        sb.AppendLine("        else if (value is string __s)");
        sb.AppendLine("            jw.WriteString(Encoding.UTF8.GetBytes(__s));");
        sb.AppendLine("        else if (value is long __l)");
        sb.AppendLine("            jw.WriteNumber(__l);");
        sb.AppendLine("        else if (value is ulong __ul)");
        sb.AppendLine("            jw.WriteNumber((decimal)__ul);");
        sb.AppendLine("        else if (value is int __i)");
        sb.AppendLine("            jw.WriteNumber(__i);");
        sb.AppendLine("        else if (value is uint __ui)");
        sb.AppendLine("            jw.WriteNumber(__ui);");
        sb.AppendLine("        else if (value is short __sh)");
        sb.AppendLine("            jw.WriteNumber(__sh);");
        sb.AppendLine("        else if (value is byte __by)");
        sb.AppendLine("            jw.WriteNumber(__by);");
        sb.AppendLine("        else if (value is double __d)");
        sb.AppendLine("            jw.WriteNumber(__d);");
        sb.AppendLine("        else if (value is float __f)");
        sb.AppendLine("            jw.WriteNumber(__f);");
        sb.AppendLine("        else if (value is decimal __m)");
        sb.AppendLine("            jw.WriteNumber(__m);");
        sb.AppendLine("        else if (value is bool __b)");
        sb.AppendLine("            jw.WriteBoolean(__b);");
        sb.AppendLine(
            "        else if (value is System.Collections.Generic.Dictionary<string, object?> __nd)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            jw.WriteStartObject();");
        sb.AppendLine("            foreach (var __kvp in __nd)");
        sb.AppendLine("            {");
        sb.AppendLine("                jw.WritePropertyName(Encoding.UTF8.GetBytes(__kvp.Key));");
        sb.AppendLine("                SerializeValue(ref jw, __kvp.Value);");
        sb.AppendLine("            }");
        sb.AppendLine("            jw.WriteEndObject();");
        sb.AppendLine("        }");
        sb.AppendLine("        else if (value is System.Collections.Generic.List<object?> __nl)");
        sb.AppendLine("        {");
        sb.AppendLine("            jw.WriteStartArray();");
        sb.AppendLine("            foreach (var __item in __nl)");
        sb.AppendLine("                SerializeValue(ref jw, __item);");
        sb.AppendLine("            jw.WriteEndArray();");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("            jw.WriteString(Encoding.UTF8.GetBytes(value.ToString()!));");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── DeserializeAnyValue ──
        sb.AppendLine("    internal static object? DeserializeValue(ref JsonReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (reader.TokenType == TokenType.Null)");
        sb.AppendLine("            return null;");
        sb.AppendLine("        if (reader.TokenType == TokenType.String)");
        sb.AppendLine("            return Encoding.UTF8.GetString(reader.GetStringRaw());");
        sb.AppendLine(
            "        if (reader.TokenType == TokenType.Int32 || reader.TokenType == TokenType.Int64)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            reader.TryGetInt64(out var __l);");
        sb.AppendLine("            return __l;");
        sb.AppendLine("        }");
        sb.AppendLine(
            "        if (reader.TokenType == TokenType.Float64 || reader.TokenType == TokenType.Float32)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            reader.TryGetFloat64(out var __d);");
        sb.AppendLine("            return __d;");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.TokenType == TokenType.Bool)");
        sb.AppendLine("        {");
        sb.AppendLine("            reader.TryGetBool(out var __b);");
        sb.AppendLine("            return __b;");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.TokenType == TokenType.ObjectStart)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            var __nd = new System.Collections.Generic.Dictionary<string, object?>();"
        );
        sb.AppendLine(
            "            while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("            {");
        sb.AppendLine("                var __nk = Encoding.UTF8.GetString(reader.GetStringRaw());");
        sb.AppendLine("                reader.Read();");
        sb.AppendLine("                __nd[__nk] = DeserializeValue(ref reader);");
        sb.AppendLine("            }");
        sb.AppendLine("            return __nd;");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.TokenType == TokenType.ArrayStart)");
        sb.AppendLine("        {");
        sb.AppendLine("            var __nl = new System.Collections.Generic.List<object?>();");
        sb.AppendLine(
            "            while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
        );
        sb.AppendLine("                __nl.Add(DeserializeValue(ref reader));");
        sb.AppendLine("            return __nl;");
        sb.AppendLine("        }");
        sb.AppendLine("        return Encoding.UTF8.GetString(reader.GetStringRaw());");
        sb.AppendLine("    }");

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTypeCode(
        TypeInfo type,
        Dictionary<string, TypeInfo> derivedLookup
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System; using System.Buffers; using System.Text;");
        sb.AppendLine("using PicoSerDe.Core; using PicoJetson;");

        if (!string.IsNullOrEmpty(type.Namespace))
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(type.Namespace);
            sb.AppendLine(";");
        }

        sb.AppendLine();

        if (type.IsRefLikeType)
        {
            // Ref struct: static method serializer only (can't implement ISerializer<T>).
            EmitSerializer(sb, type, isRefLikeType: true);
            sb.AppendLine();
            EmitRefStructRegistration(sb, type);
        }
        else if (type.IsTopLevelList || type.ArrayElementKind is not null)
        {
            // Top-level array (T[]) or list (List<T>) type
            EmitArraySerializer(sb, type);
            sb.AppendLine();
            EmitArrayDeserializer(sb, type);
            sb.AppendLine();
            EmitRegistration(sb, type);
            sb.AppendLine();
            EmitArrayStreamingDeserializer(sb, type);
        }
        else if (!type.DerivedTypes.IsDefaultOrEmpty)
        {
            // Polymorphic base type: serializer with type switch + discriminator dispatch deserializer
            EmitPolySerializer(sb, type, derivedLookup);
            sb.AppendLine();
            EmitPolyDeserializer(sb, type, derivedLookup);
            sb.AppendLine();
            EmitRegistration(sb, type);
            sb.AppendLine();
            EmitPolyStreamingDeserializer(sb, type, derivedLookup);
        }
        else
        {
            // Regular type: static method serializer + interface deserializer
            EmitSerializer(sb, type);
            sb.AppendLine();
            EmitDeserializer(sb, type);
            sb.AppendLine();
            EmitRegistration(sb, type);
            sb.AppendLine();
            // Skip streaming for types with [JsonConstructor] (immutable types)
            var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;
            if (type.Properties.Length > 0)
                EmitStreamingDeserializer(sb, type);
        }

        return sb.ToString();
    }

    // ── Serializer emission ──

    private static void EmitSerializer(StringBuilder sb, TypeInfo type, bool isRefLikeType = false)
    {
        if (isRefLikeType)
        {
            sb.Append("    file static class ");
            sb.Append(type.Name);
            sb.AppendLine("JsonSer");
            sb.AppendLine("    {");
            sb.Append("        public static void Serialize(IBufferWriter<byte> writer, ");
        }
        else
        {
            sb.Append("    file readonly struct ");
            sb.Append(type.Name);
            sb.AppendLine("JsonSer : ISerializer<");
            sb.Append(type.Name);
            sb.AppendLine(">");
            sb.AppendLine("    {");
            sb.Append("        public void Serialize(IBufferWriter<byte> writer, ");
        }
        sb.Append(type.Name);
        sb.AppendLine(" value)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            var jw = new JsonWriter(writer, indented: PicoJetson.JsonOptions.Current?.Indented ?? false, maxDepth: PicoJetson.JsonOptions.Current?.MaxDepth ?? 63);"
        );
        sb.AppendLine("            jw.WriteStartObject();");

        foreach (var prop in type.Properties)
        {
            // DefaultIgnoreCondition: wrap property in conditional check
            bool checkNull = EmitIgnoreConditionOpen(
                sb,
                prop,
                "value." + prop.Name,
                "            "
            );
            sb.Append("            var __name_");
            sb.Append(prop.Name);
            sb.Append(" = PicoJetson.JsonOptions.Current?.PropertyNamingPolicy?.ConvertName(\"");
            sb.Append(EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\") ?? \"" + EscapeCSharpString(prop.JsonName) + "\";");
            sb.Append("            jw.WritePropertyName(__name_");
            sb.Append(prop.Name);
            sb.AppendLine(");");
            EmitSerializeProperty(sb, prop, $"value.{prop.Name}", "            ");
            if (checkNull)
                sb.AppendLine("            }");
        }

        sb.AppendLine("            jw.WriteEndObject();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    /// <summary>
    /// Emits the DefaultIgnoreCondition guard for a property when it is nullable
    /// (including explicitly nullable collections such as <c>T[]?</c>).
    /// Returns true when a guard block was opened; the caller must emit the
    /// matching closing brace.
    /// </summary>
    private static bool EmitIgnoreConditionOpen(
        StringBuilder sb,
        PropertyInfo prop,
        string accessor,
        string indent
    )
    {
        // Per-property [PicoIgnore] condition overrides the global option
        // (STJ semantics): Never exempts entirely; WhenWritingNull/Default
        // emit an unconditional value check.
        switch (prop.IgnoreCondition)
        {
            case "Never":
                return false;
            case "WhenWritingNull":
                return PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                    sb,
                    prop,
                    accessor,
                    indent
                );
            case "WhenWritingDefault":
                if (!PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(prop))
                    return false;
                sb.Append(indent);
                sb.Append("if (");
                sb.Append(accessor);
                if (PicoSerDe.Gen.GenInfrastructure.IsValueDefaultKind(prop.TypeKind))
                    sb.AppendLine(" != default)");
                else
                    sb.AppendLine(" != null)");
                sb.Append(indent);
                sb.AppendLine("{");
                return true;
        }
        if (!(PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(prop)))
            return false;
        sb.Append(indent);
        sb.AppendLine(
            "if (PicoJetson.JsonOptions.Current?.DefaultIgnoreCondition == PicoJetson.JsonIgnoreCondition.WhenWritingNull"
        );
        sb.Append(indent);
        sb.AppendLine("    ? " + accessor + " != null");
        sb.Append(indent);
        sb.AppendLine(
            "    : PicoJetson.JsonOptions.Current?.DefaultIgnoreCondition == PicoJetson.JsonIgnoreCondition.WhenWritingDefault"
        );
        sb.Append(indent);
        if (PicoSerDe.Gen.GenInfrastructure.IsValueDefaultKind(prop.TypeKind))
            sb.AppendLine("    ? " + accessor + " != default");
        else
            sb.AppendLine("    ? " + accessor + " != null");
        sb.Append(indent);
        sb.AppendLine("    : true)");
        sb.Append(indent);
        sb.AppendLine("{");
        return true;
    }

    private static void EmitSerializeProperty(
        StringBuilder sb,
        PropertyInfo prop,
        string accessor,
        string indent
    )
    {
        // Custom converter
        if (prop.ConverterTypeFullName is not null)
        {
            sb.Append(indent);
            sb.Append("var __conv = new ");
            sb.Append(prop.ConverterTypeFullName);
            sb.AppendLine("();");
            sb.Append(indent);
            sb.Append("__conv.Write(jw.Buffer, ");
            sb.Append(accessor);
            sb.AppendLine(");");
            return;
        }

        // Nullable wrapping
        var effectiveAccessor = accessor;
        if (prop.IsNullable && !prop.IsNullableReference)
        {
            // Value-type Nullable<T>: x.HasValue / x.Value
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(accessor);
            sb.AppendLine(".HasValue)");
            sb.Append(indent);
            sb.AppendLine("{");
            effectiveAccessor = $"{accessor}.Value";
            indent += "    ";
        }
        else if (prop.IsNullable && prop.IsNullableReference)
        {
            // Reference-type NRT: x != null
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(accessor);
            sb.AppendLine(" != null)");
            sb.Append(indent);
            sb.AppendLine("{");
            indent += "    ";
        }

        switch (prop.TypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(effectiveAccessor);
                sb.AppendLine("));");
                break;
            case "int32":
            case "int64":
            case "float32":
            case "float64":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            case "boolean":
                sb.Append(indent);
                sb.Append("jw.WriteBoolean(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            case "datetime":
                sb.Append(indent);
                sb.Append("var __iso_");
                sb.Append(prop.Name);
                sb.Append(" = ");
                sb.Append(effectiveAccessor);
                if (prop.DateTimeFormat is not null)
                {
                    sb.Append(".ToString(\"");
                    sb.Append(prop.DateTimeFormat);
                    sb.AppendLine("\");");
                }
                else
                    sb.AppendLine(".ToString(\"O\");");
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(__iso_");
                sb.Append(prop.Name);
                sb.AppendLine("));");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.Append("var __d = ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString(\"O\");");
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(__d);");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.Append("var __t = ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString(\"O\");");
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(__t);");
                break;
            case "timespan":
                sb.Append(indent);
                sb.Append("var __ts = ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString();");
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(__ts);");
                break;
            case "guid":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString()));");
                break;
            case "enum":
                sb.Append(indent);
                sb.Append("jw.WriteNumber((int)");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            case "decimal":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            case "list":
            case "array":
                sb.Append(indent);
                sb.AppendLine("jw.WriteStartArray();");
                sb.Append(indent);
                sb.Append("foreach (var __item in ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(")");
                sb.Append(indent);
                sb.AppendLine("{");
                // Check for nested list: NestedProperties contains wrapper(s)
                if (prop.NestedProperties.Length > 0 && IsNestedList(prop))
                {
                    EmitNestedListSerialize(
                        sb,
                        prop.NestedProperties[0],
                        "__item",
                        indent + "    "
                    );
                }
                else
                {
                    EmitSerializeElement(sb, prop, "__item", indent + "    ");
                }
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("jw.WriteEndArray();");
                break;
            case "dict":
                sb.Append(indent);
                sb.AppendLine("jw.WriteStartObject();");
                sb.Append(indent);
                sb.Append("foreach (var __kvp in ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(")");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    jw.WritePropertyName(Encoding.UTF8.GetBytes(__kvp.Key");
                if (prop.KeyTypeKind == "string")
                    sb.AppendLine("));");
                else
                    sb.AppendLine(".ToString()));");
                EmitSerializeElement(sb, prop, "__kvp.Value", indent + "    ");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("jw.WriteEndObject();");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    prop.TypeFullName!
                );
                var ct = prop.TypeFullName!.TrimEnd('?');
                sb.Append(indent);
                if (PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(prop))
                {
                    sb.Append("if (");
                    sb.Append(effectiveAccessor);
                    sb.AppendLine(" == null) jw.WriteNull();");
                    sb.Append(indent);
                    sb.Append("else ");
                }
                // RegisterCustom<T> overrides the SG inner helper for nested values
                sb.Append("if (global::PicoJetson.JsonSerializer.HasCustomSerializer<");
                sb.Append(ct);
                sb.AppendLine(">())");
                sb.Append(indent);
                sb.Append("    global::PicoJetson.JsonSerializer.SerializeCustom<");
                sb.Append(ct);
                sb.Append(">(jw.Buffer, ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(sn);
                sb.Append(".Serialize(ref jw, ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            }
        }

        // Close nullable block
        if (prop.IsNullable)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent);
            sb.AppendLine("}");
            sb.Append(indent);
            sb.AppendLine("else");
            sb.Append(indent);
            sb.AppendLine("    jw.WriteNull();");
        }
    }

    private static void EmitSerializeElement(
        StringBuilder sb,
        PropertyInfo prop,
        string itemVar,
        string indent
    )
    {
        switch (prop.ElementTypeKind!)
        {
            case "string":
                if (prop.ElementIsNullableReference)
                {
                    sb.Append(indent);
                    sb.Append("if (");
                    sb.Append(itemVar);
                    sb.AppendLine(" != null)");
                    sb.Append(indent);
                    sb.Append("    jw.WriteString(Encoding.UTF8.GetBytes(");
                    sb.Append(itemVar);
                    sb.AppendLine("));");
                    sb.Append(indent);
                    sb.AppendLine("else");
                    sb.Append(indent);
                    sb.AppendLine("    jw.WriteNull();");
                }
                else
                {
                    sb.Append(indent);
                    sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                    sb.Append(itemVar);
                    sb.AppendLine("));");
                }
                break;
            case "int32":
            case "int64":
            case "float32":
            case "float64":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            case "boolean":
                sb.Append(indent);
                sb.Append("jw.WriteBoolean(");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            case "datetime":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "timespan":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString()));");
                break;
            case "guid":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString()));");
                break;
            case "enum":
                sb.Append(indent);
                sb.Append("jw.WriteNumber((int)");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            case "decimal":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonDictInner",
                    prop.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append(sn);
                sb.Append(".Serialize(ref jw, ");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            }
            case "any":
                EmitAnyValueSerialize(sb, itemVar, indent);
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    prop.ElementTypeName!
                );
                var ct = prop.ElementTypeName!.TrimEnd('?');
                sb.Append(indent);
                sb.Append("if (global::PicoJetson.JsonSerializer.HasCustomSerializer<");
                sb.Append(ct);
                sb.AppendLine(">())");
                sb.Append(indent);
                sb.Append("    global::PicoJetson.JsonSerializer.SerializeCustom<");
                sb.Append(ct);
                sb.Append(">(jw.Buffer, ");
                sb.Append(itemVar);
                sb.AppendLine(");");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(sn);
                sb.Append(".Serialize(ref jw, ");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            }
            default:
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString()));");
                break;
        }
    }

    // ── Any-value helpers (runtime type dispatch for Dictionary<string, object?>) ──

    /// <summary>
    /// Namespace-qualified name of the generated any-dict helper. Must match the
    /// namespace emitted for the __PicoAnyDictHelper source file, so call sites
    /// resolve regardless of which namespace the calling file lives in.
    /// </summary>
    private static string AnyDictHelperName() =>
        (PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix ?? "__PicoSerDe") + ".__PicoAnyDictHelper";

    private static void EmitAnyValueSerialize(StringBuilder sb, string valueExpr, string indent)
    {
        sb.Append(indent);
        sb.Append(AnyDictHelperName());
        sb.Append(".SerializeValue(ref jw, ");
        sb.Append(valueExpr);
        sb.AppendLine(");");
    }

    private static void EmitAnyValueDeserialize(
        StringBuilder sb,
        string assignTarget,
        string indent
    )
    {
        sb.Append(indent);
        sb.Append(assignTarget);
        sb.Append(" = ");
        sb.Append(AnyDictHelperName());
        sb.AppendLine(".DeserializeValue(ref reader);");
    }

    // ── Deserializer emission ──

    private static void EmitDeserializer(StringBuilder sb, TypeInfo type)
    {
        var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;

        sb.Append("    file readonly struct ");
        sb.Append(type.Name);
        sb.Append("JsonDeserializer : IDeserializer<");
        sb.Append(type.Name);
        sb.AppendLine(">");
        sb.AppendLine("    {");
        sb.Append("        public ");
        sb.Append(type.Name);
        sb.AppendLine(" Deserialize(ReadOnlySpan<byte> data)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            var reader = new JsonReader(data, maxDepth: PicoJetson.JsonOptions.Current?.MaxDepth ?? 256);"
        );
        sb.AppendLine("            try");
        sb.AppendLine("            {");

        if (hasCtor)
        {
            // Declare temp variables for constructor parameters
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
            {
                var cp = type.CtorParams[ci];
                // Use TypeFullName directly — MapTypeName with null type NREs
                // for complex kinds (object, enum, list, dict).
                var typeName = cp.TypeFullName;
                var defaultVal = cp.TypeKind switch
                {
                    "string" => "\"\"",
                    "int32" or "int64" or "float64" => "0",
                    "boolean" => "false",
                    _ => "default!",
                };
                sb.Append("            ");
                sb.Append(typeName);
                sb.Append(" __cp_");
                sb.Append(ci);
                sb.Append(" = ");
                sb.Append(defaultVal);
                sb.AppendLine(";");
            }
        }
        else
        {
            var reqProps = type.Properties.Where(p => p.IsRequired).ToArray();
            if (reqProps.Length > 0)
            {
                sb.Append("            var obj = new ");
                sb.Append(type.Name);
                sb.AppendLine(" {");
                foreach (var rp in reqProps)
                {
                    sb.Append("                ");
                    sb.Append(rp.Name);
                    sb.Append(" = ");
                    switch (rp.TypeKind)
                    {
                        case "string":
                            sb.Append("\"\"");
                            break;
                        default:
                            sb.Append("default");
                            break;
                    }
                    sb.AppendLine(",");
                }
                sb.Append("            };");
            }
            else
            {
                sb.Append("            var obj = new ");
                sb.Append(type.Name);
                sb.AppendLine("();");
            }
        }

        sb.AppendLine("            reader.Read();");
        sb.AppendLine(
            "            while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("            {");
        sb.AppendLine("                var propNameSpan = reader.GetStringRaw();");
        sb.AppendLine("                reader.Read();");
        sb.AppendLine();

        for (var i = 0; i < type.Properties.Length; i++)
        {
            var prop = type.Properties[i];
            var keyword = i == 0 ? "if" : "else if";
            sb.Append("                ");
            sb.Append(keyword);
            sb.Append(" (TextHelpers.Eq(propNameSpan, \"");
            sb.Append(EscapeCSharpString(prop.JsonName));
            sb.AppendLine(
                "\"u8, !(global::PicoJetson.JsonOptions.Current?.PropertyNameCaseInsensitive ?? true)))"
            );
            sb.AppendLine("                {");

            if (hasCtor)
            {
                // Map JSON property to constructor parameter by name
                EmitDeserializeCtorParam(sb, prop, type, "                    ");
            }
            else
            {
                EmitDeserializeProperty(sb, prop, "obj", "                    ");
            }

            sb.AppendLine("                }");
        }

        if (type.Properties.Length > 0)
        {
            sb.AppendLine(
                "                else if (PicoJetson.JsonOptions.Current?.UnmappedMemberHandling == PicoJetson.JsonUnmappedMemberHandling.Disallow)"
            );
            sb.AppendLine(
                "                    throw new System.FormatException($\"Unexpected property '{Encoding.UTF8.GetString(propNameSpan)}' at offset {reader.BytesConsumed}\");"
            );
            sb.AppendLine("                else reader.TrySkip();");
        }
        else
            sb.AppendLine("                reader.TrySkip();");

        sb.AppendLine("            }");

        if (hasCtor)
        {
            sb.Append("            return new ");
            sb.Append(type.Name);
            sb.Append("(");
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
            {
                if (ci > 0)
                    sb.Append(", ");
                sb.Append("__cp_");
                sb.Append(ci);
            }
            sb.AppendLine(");");
        }
        else
        {
            sb.AppendLine("            return obj;");
        }

        sb.AppendLine("            }");
        sb.AppendLine("            finally");
        sb.AppendLine("            {");
        sb.AppendLine("                reader.Dispose();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    /// <summary>Emit assignment to a constructor parameter temp variable.</summary>
    private static void EmitDeserializeCtorParam(
        StringBuilder sb,
        PropertyInfo prop,
        TypeInfo type,
        string indent
    )
    {
        // Find matching constructor parameter by case-insensitive name
        int matchIdx = -1;
        for (int ci = 0; ci < type.CtorParams.Length; ci++)
        {
            if (
                string.Equals(
                    type.CtorParams[ci].Name,
                    prop.Name,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                matchIdx = ci;
                break;
            }
        }
        if (matchIdx < 0)
        {
            // No matching ctor param — skip this property
            sb.Append(indent);
            sb.AppendLine("reader.TrySkip();");
            return;
        }

        var cp = type.CtorParams[matchIdx];
        var target = $"__cp_{matchIdx}";

        switch (cp.TypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __v))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetInt64(out var __lv);");
                sb.Append(indent);
                sb.AppendLine("    __v = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = (float)__v;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine(
                    "var __rawStr = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(cp.TypeFullName);
                sb.AppendLine(">(__rawStr, out var __ev);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __ev;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __dt;");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __g);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __g;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __dec);"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __dec;");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__strValue, out var __dov);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __dov;");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__strValue, out var __tov);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __tov;");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__strValue, out var __tsv);");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __tsv;");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    cp.TypeFullName
                );
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(target);
                sb.AppendLine(" = default!;");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(target);
                sb.Append(" = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
            case "list":
            case "array":
            {
                var elemType = prop.ElementTypeName ?? "object";
                var listVar = $"__list_{cp.Name}";
                sb.Append(indent);
                sb.Append("var ");
                sb.Append(listVar);
                sb.Append(" = new System.Collections.Generic.List<");
                sb.Append(elemType);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ArrayStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine(
                    "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
                );
                sb.Append(indent);
                sb.AppendLine("    {");
                EmitDeserializeElementAdd(sb, prop, listVar, indent + "        ", 0);
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(" = ");
                if (cp.TypeKind == "array")
                {
                    sb.Append(listVar);
                    sb.AppendLine(".ToArray();");
                }
                else
                {
                    sb.Append(listVar);
                    sb.AppendLine(";");
                }
                break;
            }
            case "dict":
            {
                var keyType = prop.KeyTypeName ?? "string";
                var valType = prop.ElementTypeName ?? "object";
                var dictVar = $"__dict_{cp.Name}";
                sb.Append(indent);
                sb.Append("var ");
                sb.Append(dictVar);
                sb.Append(" = new System.Collections.Generic.Dictionary<");
                sb.Append(keyType);
                sb.Append(", ");
                sb.Append(valType);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ObjectStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine(
                    "    while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
                );
                sb.Append(indent);
                sb.AppendLine("    {");
                EmitDeserializeDictKey(sb, prop, dictVar, indent + "        ");
                sb.Append(indent);
                sb.AppendLine("        reader.Read();");
                sb.Append(indent);
                sb.AppendLine("        if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("            ");
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = default!;");
                sb.Append(indent);
                sb.AppendLine("        else");
                sb.Append(indent);
                sb.AppendLine("        {");
                EmitDeserializeElementAssign(
                    sb,
                    prop,
                    dictVar,
                    "__dictKey",
                    indent + "            ",
                    0
                );
                sb.Append(indent);
                sb.AppendLine("        }");
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(" = ");
                sb.Append(dictVar);
                sb.AppendLine(";");
                break;
            }
            default:
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = default!; // unsupported ctor param type: " + cp.TypeKind);
                break;
        }
    }

    private static void EmitDeserializeProperty(
        StringBuilder sb,
        PropertyInfo prop,
        string target,
        string indent,
        int nestLevel = 0
    )
    {
        // Custom converter
        if (prop.ConverterTypeFullName is not null)
        {
            sb.Append(indent);
            sb.Append("var __conv = new ");
            sb.Append(prop.ConverterTypeFullName);
            sb.AppendLine("();");
            sb.Append(indent);
            sb.Append(target);
            sb.Append(".");
            sb.Append(prop.Name);
            sb.AppendLine(" = __conv.Read(ref reader);");
            return;
        }

        // Nullable
        if (prop.IsNullable && prop.TypeKind != "object")
        {
            sb.Append(indent);
            sb.AppendLine("if (reader.TokenType == TokenType.Null)");
            sb.Append(indent);
            sb.Append("    ");
            sb.Append(target);
            sb.Append(".");
            sb.Append(prop.Name);
            sb.AppendLine(" = null;");
            sb.Append(indent);
            sb.AppendLine("else");
            sb.Append(indent);
            sb.AppendLine("{");
            EmitDeserializeValue(sb, prop, target, indent + "    ", nestLevel);
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }

        EmitDeserializeValue(sb, prop, target, indent, nestLevel);
    }

    private static void EmitDeserializeValue(
        StringBuilder sb,
        PropertyInfo prop,
        string target,
        string indent,
        int nestLevel
    )
    {
        switch (prop.TypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __intValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetInt64(out var __lv);");
                sb.Append(indent);
                sb.AppendLine("    __intValue = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __intValue;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __longValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __longValue;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __doubleValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __doubleValue;");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __floatValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = (float)__floatValue;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __boolValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __boolValue;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                if (prop.DateTimeFormat is not null)
                {
                    sb.Append("System.DateTime.TryParseExact(__strValue, \"");
                    sb.Append(prop.DateTimeFormat);
                    sb.Append(
                        "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt_"
                    );
                    sb.Append(prop.Name);
                    sb.AppendLine(");");
                }
                else
                {
                    sb.Append(
                        "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt_"
                    );
                    sb.Append(prop.Name);
                    sb.AppendLine(");");
                }
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.Append(" = __dt_");
                sb.Append(prop.Name);
                sb.AppendLine(";");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __guidValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __guidValue;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue);"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __decimalValue;");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__strValue, out var __dateOnlyValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dateOnlyValue;");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __timeOnlyValue;");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__strValue, out var __timeSpanValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __timeSpanValue;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.TypeFullName);
                sb.AppendLine(">(__strValue, out var __enumValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __enumValue;");
                break;
            case "list":
            case "array":
                if (prop.TypeKind == "list")
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.Append(".");
                    sb.Append(prop.Name);
                    sb.Append(" = new System.Collections.Generic.List<");
                    sb.Append(prop.ElementTypeName);
                    sb.AppendLine(">();");
                }
                else
                {
                    sb.Append(indent);
                    sb.Append("var __list_");
                    sb.Append(prop.Name);
                    sb.Append(" = new System.Collections.Generic.List<");
                    sb.Append(prop.ElementTypeName);
                    sb.AppendLine(">();");
                }
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ArrayStart)");
                sb.Append(indent);
                sb.AppendLine("{");

                var listAcc =
                    prop.TypeKind == "list" ? $"{target}.{prop.Name}" : $"__list_{prop.Name}";

                // Handle nested List<List<...<T>>> recursively
                if (IsNestedList(prop))
                {
                    EmitNestedListDeserialize(
                        sb,
                        prop.NestedProperties[0],
                        listAcc,
                        prop.Name,
                        indent + "    ",
                        0
                    );
                }
                else if (
                    prop.ElementTypeKind == "int32"
                    || prop.ElementTypeKind == "int64"
                    || prop.ElementTypeKind == "boolean"
                )
                {
                    sb.Append(indent);
                    sb.AppendLine(
                        "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
                    );
                    sb.Append(indent);
                    sb.AppendLine("    {");
                    EmitDeserializeElementAdd(sb, prop, listAcc, indent + "        ", nestLevel);
                    sb.Append(indent);
                    sb.AppendLine("    }");
                }
                else
                {
                    sb.Append(indent);
                    sb.AppendLine(
                        "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
                    );
                    sb.Append(indent);
                    sb.AppendLine("    {");
                    EmitDeserializeElementAdd(sb, prop, listAcc, indent + "        ", nestLevel);
                    sb.Append(indent);
                    sb.AppendLine("    }");
                }

                sb.Append(indent);
                sb.AppendLine("}");
                if (prop.TypeKind == "array")
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.Append(".");
                    sb.Append(prop.Name);
                    sb.Append(" = __list_");
                    sb.Append(prop.Name);
                    sb.AppendLine(".ToArray();");
                }
                break;
            case "dict":
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" ??= new System.Collections.Generic.Dictionary<");
                sb.Append(prop.KeyTypeName);
                sb.Append(", ");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ObjectStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine(
                    "    while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
                );
                sb.Append(indent);
                sb.AppendLine("    {");
                var dictAcc = $"{target}.{prop.Name}";
                EmitDeserializeDictKey(sb, prop, dictAcc, indent + "        ");
                sb.Append(indent);
                sb.AppendLine("        reader.Read();");
                EmitDeserializeElementAssign(
                    sb,
                    prop,
                    dictAcc,
                    "__dictKey",
                    indent + "        ",
                    nestLevel
                );
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    prop.TypeFullName!
                );
                if (prop.IsNullable)
                {
                    sb.Append(indent);
                    sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                    sb.Append(indent);
                    sb.Append("    ");
                    sb.Append(target);
                    sb.Append(".");
                    sb.Append(prop.Name);
                    sb.AppendLine(" = null;");
                    sb.Append(indent);
                    sb.AppendLine("else");
                    sb.Append(indent);
                    sb.Append("    ");
                }
                else
                {
                    // Non-nullable: skip null tokens, keep default value
                    sb.Append(indent);
                    sb.AppendLine("if (reader.TokenType != TokenType.Null)");
                    sb.Append(indent);
                    sb.Append("    ");
                }
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.Append(" = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
        }
    }

    private static void EmitNestedDeserialize(
        StringBuilder sb,
        ImmutableArray<PropertyInfo> props,
        string target,
        string indent,
        int nestLevel = 0,
        string propVarName = "__nestedPropName"
    )
    {
        for (var i = 0; i < props.Length; i++)
        {
            var np = props[i];
            var keyword = i == 0 ? "if" : "else if";
            sb.Append(indent);
            sb.Append(keyword);
            sb.Append(" (TextHelpers.Eq(");
            sb.Append(propVarName);
            sb.Append(", \"");
            sb.Append(EscapeCSharpString(np.JsonName));
            sb.AppendLine(
                "\"u8, !(global::PicoJetson.JsonOptions.Current?.PropertyNameCaseInsensitive ?? true)))"
            );
            sb.Append(indent);
            sb.AppendLine("{");
            EmitDeserializeProperty(sb, np, target, indent + "    ", nestLevel);
            sb.Append(indent);
            sb.AppendLine("}");
        }
    }

    /// <summary>Emits value write for a nested list inner element (simplified — emits jw.WriteNumber).</summary>
    private static void EmitNestedListElement(StringBuilder sb, string indent, string itemVar)
    {
        sb.Append(indent);
        sb.Append("jw.WriteNumber(");
        sb.Append(itemVar);
        sb.AppendLine(");");
    }

    private static void EmitDeserializeElementAdd(
        StringBuilder sb,
        PropertyInfo prop,
        string listVar,
        string indent,
        int nestLevel
    )
    {
        switch (prop.ElementTypeKind!)
        {
            case "string":
                if (prop.ElementIsNullableReference)
                {
                    sb.Append(indent);
                    sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                    sb.Append(indent);
                    sb.Append("    ");
                    sb.Append(listVar);
                    sb.AppendLine(".Add(null!);");
                    sb.Append(indent);
                    sb.AppendLine("else");
                    sb.Append(indent);
                    sb.Append("    ");
                    sb.Append(listVar);
                    sb.AppendLine(".Add(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                }
                else
                {
                    sb.Append(indent);
                    sb.Append(listVar);
                    sb.AppendLine(".Add(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                }
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetInt64(out var __lev);");
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add((float)__elementValue);");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dateTimeValue);"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__dateTimeValue);");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __guidValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__guidValue);");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue);"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__decimalValue);");
                break;
            case "dateonly":
            case "timeonly":
            case "timespan":
                EmitDeserializeElementAddTemporal(sb, prop.ElementTypeKind, listVar, indent);
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">(__strValue, out var __enumValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__enumValue);");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonDictInner",
                    prop.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.Append(".Add(");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader));");
                break;
            }
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    prop.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.Append(".Add(");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader));");
                break;
            }
            default:
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
        }
    }

    private static void EmitDeserializeElementAddTemporal(
        StringBuilder sb,
        string? kind,
        string listVar,
        string indent
    )
    {
        if (kind is null)
            return;
        sb.Append(indent);
        sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
        sb.Append(indent);
        sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
        switch (kind)
        {
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__strValue, out var __dateOnlyValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__dateOnlyValue);");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__timeOnlyValue);");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__strValue, out var __timeSpanValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__timeSpanValue);");
                break;
        }
    }

    private static void EmitDeserializeDictKey(
        StringBuilder sb,
        PropertyInfo prop,
        string dictVar,
        string indent
    )
    {
        switch (prop.KeyTypeKind!)
        {
            case "string":
                sb.Append(indent);
                sb.Append(
                    "var __dictKey = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                sb.AppendLine();
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("int.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("long.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.KeyTypeName);
                sb.AppendLine(">(__strValue, out var __dictKey);");
                break;
            default:
                sb.Append(indent);
                sb.Append(
                    "var __dictKey = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                sb.AppendLine();
                break;
        }
    }

    private static void EmitDeserializeElementAssign(
        StringBuilder sb,
        PropertyInfo prop,
        string dictVar,
        string keyVar,
        string indent,
        int nestLevel
    )
    {
        switch (prop.ElementTypeKind!)
        {
            case "string":
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetInt64(out var __lev);");
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = (float)__elementValue;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dateTimeValue);"
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __dateTimeValue;");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __guidValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __guidValue;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue);"
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __decimalValue;");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__strValue, out var __dateOnlyValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __dateOnlyValue;");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __timeOnlyValue;");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__strValue, out var __timeSpanValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __timeSpanValue;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">(__strValue, out var __enumValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __enumValue;");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonDictInner",
                    prop.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.Append("] = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
            case "any":
                EmitAnyValueDeserialize(sb, $"{dictVar}[{keyVar}]", indent);
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    prop.ElementTypeName!
                );
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = default!;");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.Append("] = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
            default:
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
        }
    }

    // ── Registration emission ──

    private static void EmitStreamingDeserializer(StringBuilder sb, TypeInfo type)
    {
        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"global::{type.Namespace}.{type.Name}";
        var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;
        sb.Append("file static class ");
        sb.Append(type.Name);
        sb.AppendLine("Streaming");
        sb.AppendLine("{");
        sb.AppendLine(
            "    internal static ReadStatus DeserializeStreaming(ref JsonReader reader, out "
                + type.Name
                + "? result)"
        );
        sb.AppendLine("    {");
        sb.Append("        result = default;");
        sb.AppendLine();

        if (hasCtor)
        {
            // Declare temp variables for constructor parameters
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
            {
                var cp = type.CtorParams[ci];
                // Use TypeFullName directly — MapTypeName with null type NREs
                // for complex kinds (object, enum, list, dict).
                var typeName = cp.TypeFullName;
                var defaultVal = cp.TypeKind switch
                {
                    "string" => "\"\"",
                    "int32" or "int64" or "float64" => "0",
                    "boolean" => "false",
                    _ => "default!",
                };
                sb.Append("        ");
                sb.Append(typeName);
                sb.Append(" __cp_");
                sb.Append(ci);
                sb.Append(" = ");
                sb.Append(defaultVal);
                sb.AppendLine(";");
            }
        }
        else
        {
            var reqProps = type.Properties.Where(p => p.IsRequired).ToArray();
            if (reqProps.Length > 0)
            {
                sb.Append("        result = new ");
                sb.Append(type.Name);
                sb.AppendLine(" {");
                foreach (var rp in reqProps)
                {
                    sb.Append("            ");
                    sb.Append(rp.Name);
                    sb.Append(" = ");
                    switch (rp.TypeKind)
                    {
                        case "string":
                            sb.Append("\"\"");
                            break;
                        default:
                            sb.Append("default");
                            break;
                    }
                    sb.AppendLine(",");
                }
                sb.Append("        };");
            }
            else
            {
                sb.Append("        result = new ");
                sb.Append(type.Name);
                sb.AppendLine("();");
            }
        }
        sb.AppendLine();
        sb.AppendLine("        // ReadStart");
        sb.AppendLine(
            "        if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : reader.TokenType != TokenType.None ? ReadStatus.Success : ReadStatus.EndOfInput;"
        );
        sb.AppendLine();
        sb.AppendLine("        while (true)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
        );
        sb.AppendLine("            if (reader.TokenType != TokenType.PropertyName) break;");
        sb.AppendLine("            var propNameSpan = reader.GetStringRaw();");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
        );
        for (var i = 0; i < type.Properties.Length; i++)
        {
            var prop = type.Properties[i];
            var keyword = i == 0 ? "if" : "else if";
            sb.Append("            ");
            sb.Append(keyword);
            sb.Append(" (TextHelpers.Eq(propNameSpan, \"");
            sb.Append(EscapeCSharpString(prop.JsonName));
            sb.AppendLine(
                "\"u8, !(global::PicoJetson.JsonOptions.Current?.PropertyNameCaseInsensitive ?? true)))"
            );
            sb.AppendLine("            {");
            if (hasCtor)
                EmitDeserializeCtorParam(sb, prop, type, "                ");
            else
                EmitDeserializeProperty(sb, prop, "result", "                ");
            sb.AppendLine("            }");
        }
        if (type.Properties.Length > 0)
        {
            sb.AppendLine("            else reader.TrySkip();");
        }
        sb.AppendLine("        }");
        if (hasCtor)
        {
            sb.Append("        result = new ");
            sb.Append(type.Name);
            sb.Append("(");
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
            {
                if (ci > 0)
                    sb.Append(", ");
                sb.Append("__cp_");
                sb.Append(ci);
            }
            sb.AppendLine(");");
        }
        sb.AppendLine("        return ReadStatus.Success;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static void EmitRegistration(StringBuilder sb, TypeInfo type)
    {
        // For array types, use the actual array FQN (e.g. "string[]") as the
        // generic type parameter. For regular types, reconstruct from namespace+name.
        var typeRef =
            type.ArrayElementKind is not null ? type.FullyQualifiedName
            : string.IsNullOrEmpty(type.Namespace) ? type.Name
            : $"global::{type.Namespace}.{type.Name}";
        var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;
        sb.Append("file static class ");
        sb.Append(type.Name);
        sb.AppendLine("SerDeRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.Append("        global::PicoJetson.JsonSerializer.Register<");
        sb.Append(typeRef);
        sb.AppendLine(">(");
        sb.Append("            new ");
        sb.Append(type.Name);
        sb.Append("JsonSer(), new ");
        sb.Append(type.Name);
        sb.AppendLine("JsonDeserializer());");
        // Streaming: emitted for regular, array, and now poly types.
        // (EmitPolyStreamingDeserializer fills the original gap.)
        if (
            type.Properties.Length > 0
            || type.ArrayElementKind is not null
            || !type.DerivedTypes.IsDefaultOrEmpty
        )
        {
            sb.AppendLine("        global::PicoJetson.JsonSerializer.RegisterStreaming<");
            sb.Append(typeRef);
            sb.Append(">(");
            sb.Append(type.Name);
            sb.AppendLine("Streaming.DeserializeStreaming);");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    /// <summary>Ref struct registration — serializer only (delegate, ref structs cannot implement interfaces).</summary>
    private static void EmitRefStructRegistration(StringBuilder sb, TypeInfo type)
    {
        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"global::{type.Namespace}.{type.Name}";
        sb.Append("file static class ");
        sb.Append(type.Name);
        sb.AppendLine("SerDeRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.Append("        global::PicoJetson.JsonSerializer.Register<");
        sb.Append(typeRef);
        sb.AppendLine(">(");
        sb.Append("            ");
        sb.Append(type.Name);
        sb.AppendLine("JsonSer.Serialize);");
        sb.AppendLine("        // No deserializer — ref structs cannot be deserialized");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    // ── Polymorphic helpers ──

    private static void EmitPolySerializer(
        StringBuilder sb,
        TypeInfo type,
        Dictionary<string, TypeInfo> derivedLookup
    )
    {
        var dpn = type.DiscriminatorPropertyName ?? "$type";

        sb.Append("    file readonly struct ");
        sb.Append(type.Name);
        sb.AppendLine("JsonSer : ISerializer<");
        sb.Append(type.Name);
        sb.AppendLine(">");
        sb.AppendLine("    {");
        sb.Append("        public void Serialize(IBufferWriter<byte> writer, ");
        sb.Append(type.Name);
        sb.AppendLine(" value)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            var jw = new JsonWriter(writer, indented: PicoJetson.JsonOptions.Current?.Indented ?? false, maxDepth: PicoJetson.JsonOptions.Current?.MaxDepth ?? 63);"
        );
        sb.AppendLine("            jw.WriteStartObject();");
        sb.Append("            jw.WritePropertyName(Encoding.UTF8.GetBytes(\"");
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("\"));");

        sb.AppendLine("            switch (value)");
        sb.AppendLine("            {");

        foreach (var dt in type.DerivedTypes)
        {
            var dtShort = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            var dtProps = derivedLookup.TryGetValue(dt.FullyQualifiedName, out var dti)
                ? dti.Properties
                : ImmutableArray<PropertyInfo>.Empty;

            sb.Append("                case ");
            sb.Append(dtShort);
            sb.AppendLine(" __v:");
            sb.Append("                    jw.WriteString(Encoding.UTF8.GetBytes(\"");
            sb.Append(EscapeCSharpString(dt.TypeDiscriminator));
            sb.AppendLine("\"));");
            foreach (var prop in dtProps)
            {
                bool checkNull = EmitIgnoreConditionOpen(
                    sb,
                    prop,
                    "__v." + prop.Name,
                    "                    "
                );
                sb.Append("                    jw.WritePropertyName(\"");
                sb.Append(EscapeCSharpString(prop.JsonName));
                sb.AppendLine("\"u8);");
                EmitSerializeProperty(sb, prop, "__v." + prop.Name, "                    ");
                if (checkNull)
                    sb.AppendLine("                    }");
            }
            sb.AppendLine("                    break;");
        }

        sb.AppendLine("            }");
        sb.AppendLine("            jw.WriteEndObject();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void EmitPolyDeserializer(
        StringBuilder sb,
        TypeInfo type,
        Dictionary<string, TypeInfo> derivedLookup
    )
    {
        sb.Append("    file readonly struct ");
        sb.Append(type.Name);
        sb.Append("JsonDeserializer : IDeserializer<");
        sb.Append(type.Name);
        sb.AppendLine(">");
        sb.AppendLine("    {");
        sb.Append("        public ");
        sb.Append(type.Name);
        sb.AppendLine(" Deserialize(ReadOnlySpan<byte> data)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            var reader = new JsonReader(data, maxDepth: PicoJetson.JsonOptions.Current?.MaxDepth ?? 256);"
        );
        var dpn = type.DiscriminatorPropertyName ?? "$type";

        sb.AppendLine("            reader.Read(); // {");
        sb.AppendLine("            reader.Read(); // discriminator property name");
        sb.Append("            if (!TextHelpers.Eq(reader.GetStringRaw(), \"");
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("\"u8, false))");
        sb.Append(
            "                throw new System.FormatException(\"Expected discriminator property '"
        );
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("' but found different property name\");");
        sb.AppendLine("            reader.Read(); // discriminator value");
        sb.AppendLine("            var __disc = reader.GetStringRaw();");

        for (int i = 0; i < type.DerivedTypes.Length; i++)
        {
            var dt = type.DerivedTypes[i];
            var keyword = i == 0 ? "if" : "else if";
            var dti = derivedLookup.TryGetValue(dt.FullyQualifiedName, out var found)
                ? found
                : default(TypeInfo);
            var dtProps = dti.Properties.IsDefault
                ? ImmutableArray<PropertyInfo>.Empty
                : dti.Properties;
            var hasCtor = !dti.CtorParams.IsDefaultOrEmpty && dti.CtorParams.Length > 0;

            sb.Append("            ");
            sb.Append(keyword);
            sb.Append(" (TextHelpers.Eq(__disc, \"");
            sb.Append(EscapeCSharpString(dt.TypeDiscriminator));
            sb.AppendLine("\"u8, false))");
            sb.AppendLine("            {");

            // Emit derived type deserialization inline
            var dtName = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            if (hasCtor)
            {
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    var cp = dti.CtorParams[ci];
                    // Use TypeFullName directly — MapTypeName with null type NREs
                    // for complex kinds (object, enum, list, dict).
                    var tn = cp.TypeFullName;
                    var dv = cp.TypeKind switch
                    {
                        "string" => "\"\"",
                        "int32" or "int64" or "float64" => "0",
                        "boolean" => "false",
                        _ => "default!",
                    };
                    sb.Append("                ");
                    sb.Append(tn);
                    sb.Append(" __cp_");
                    sb.Append(ci);
                    sb.Append(" = ");
                    sb.Append(dv);
                    sb.AppendLine(";");
                }
            }
            else
            {
                sb.Append("                var obj = new ");
                sb.Append(dtName);
                sb.AppendLine("();");
            }

            sb.AppendLine(
                "                while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
            );
            sb.AppendLine("                {");
            sb.AppendLine("                    var __n = reader.GetStringRaw();");
            sb.AppendLine("                    reader.Read();");
            for (int pi = 0; pi < dtProps.Length; pi++)
            {
                var prop = dtProps[pi];
                var kw2 = pi == 0 ? "if" : "else if";
                sb.Append("                    ");
                sb.Append(kw2);
                sb.Append(" (TextHelpers.Eq(__n, \"");
                sb.Append(EscapeCSharpString(prop.JsonName));
                sb.AppendLine(
                    "\"u8, !(global::PicoJetson.JsonOptions.Current?.PropertyNameCaseInsensitive ?? true)))"
                );
                sb.AppendLine("                    {");
                if (hasCtor)
                    EmitDeserializeCtorParam(sb, prop, dti, "                        ");
                else
                    EmitDeserializeProperty(sb, prop, "obj", "                        ");
                sb.AppendLine("                    }");
            }
            if (dtProps.Length > 0)
                sb.AppendLine("                    else reader.TrySkip();");
            sb.AppendLine("                }");

            if (hasCtor)
            {
                sb.Append("                return new ");
                sb.Append(dtName);
                sb.Append("(");
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    if (ci > 0)
                        sb.Append(", ");
                    sb.Append("__cp_");
                    sb.Append(ci);
                }
                sb.AppendLine(");");
            }
            else
            {
                sb.AppendLine("                return obj;");
            }

            sb.AppendLine("            }");
        }

        sb.AppendLine();
        sb.AppendLine(
            "            throw new System.FormatException($\"Unknown type discriminator: {System.Text.Encoding.UTF8.GetString(__disc)}\");"
        );
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    // ── Polymorphic streaming deserializer ──

    private static void EmitPolyStreamingDeserializer(
        StringBuilder sb,
        TypeInfo type,
        Dictionary<string, TypeInfo> derivedLookup
    )
    {
        sb.Append("file static class ");
        sb.Append(type.Name);
        sb.AppendLine("Streaming");
        sb.AppendLine("{");
        sb.Append(
            "    internal static ReadStatus DeserializeStreaming(ref JsonReader reader, out "
        );
        sb.Append(type.Name);
        sb.AppendLine("? result)");
        sb.AppendLine("    {");
        sb.Append("        result = default;");
        sb.AppendLine();

        var dpn = type.DiscriminatorPropertyName ?? "$type";

        // ReadStart — consume opening brace
        sb.AppendLine("        // ReadStart");
        sb.AppendLine(
            "        if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : reader.TokenType != TokenType.None ? ReadStatus.Success : ReadStatus.EndOfInput;"
        );
        sb.AppendLine();

        // Read discriminator property name
        sb.AppendLine(
            "        if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
        );
        sb.AppendLine(
            "        if (reader.TokenType != TokenType.PropertyName) return ReadStatus.Success;"
        );
        sb.Append("        if (!TextHelpers.Eq(reader.GetStringRaw(), \"");
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("\"u8, false))");
        sb.Append(
            "            throw new System.FormatException(\"Expected discriminator property '"
        );
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("' but found different property name\");");

        // Read discriminator value
        sb.AppendLine(
            "        if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
        );
        sb.AppendLine("        var __disc = reader.GetStringRaw();");
        sb.AppendLine();

        for (int i = 0; i < type.DerivedTypes.Length; i++)
        {
            var dt = type.DerivedTypes[i];
            var keyword = i == 0 ? "if" : "else if";
            var dti = derivedLookup.TryGetValue(dt.FullyQualifiedName, out var found)
                ? found
                : default(TypeInfo);
            var dtProps = dti.Properties.IsDefault
                ? ImmutableArray<PropertyInfo>.Empty
                : dti.Properties;
            var hasCtor = !dti.CtorParams.IsDefaultOrEmpty && dti.CtorParams.Length > 0;
            var dtName = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);

            sb.Append("        ");
            sb.Append(keyword);
            sb.Append(" (TextHelpers.Eq(__disc, \"");
            sb.Append(EscapeCSharpString(dt.TypeDiscriminator));
            sb.AppendLine("\"u8, false))");
            sb.AppendLine("        {");

            // Initialize: parameterless for non-ctor types; only ctor temps for [JsonConstructor]
            if (!hasCtor)
            {
                sb.Append("            var __polyObj = new ");
                sb.Append(dtName);
                sb.AppendLine("();");
            }

            if (hasCtor)
            {
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    var cp = dti.CtorParams[ci];
                    // Use TypeFullName directly — MapTypeName with null type NREs
                    // for complex kinds (object, enum, list, dict).
                    var tn = cp.TypeFullName;
                    var dv = cp.TypeKind switch
                    {
                        "string" => "\"\"",
                        "int32" or "int64" or "float64" => "0",
                        "boolean" => "false",
                        _ => "default!",
                    };
                    sb.Append("            ");
                    sb.Append(tn);
                    sb.Append(" __cp_");
                    sb.Append(ci);
                    sb.Append(" = ");
                    sb.Append(dv);
                    sb.AppendLine(";");
                }
            }

            // Read remaining properties using streaming loop
            sb.AppendLine("            while (true)");
            sb.AppendLine("            {");
            sb.AppendLine(
                "                if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
            );
            sb.AppendLine("                if (reader.TokenType != TokenType.PropertyName) break;");
            sb.AppendLine("                var propNameSpan = reader.GetStringRaw();");
            sb.AppendLine(
                "                if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
            );

            for (int pi = 0; pi < dtProps.Length; pi++)
            {
                var prop = dtProps[pi];
                var kw2 = pi == 0 ? "if" : "else if";
                sb.Append("                ");
                sb.Append(kw2);
                sb.Append(" (TextHelpers.Eq(propNameSpan, \"");
                sb.Append(EscapeCSharpString(prop.JsonName));
                sb.AppendLine(
                    "\"u8, !(global::PicoJetson.JsonOptions.Current?.PropertyNameCaseInsensitive ?? true)))"
                );
                sb.AppendLine("                {");
                if (hasCtor)
                    EmitDeserializeCtorParam(sb, prop, dti, "                    ");
                else
                    EmitDeserializeProperty(sb, prop, "__polyObj", "                    ");
                sb.AppendLine("                }");
            }

            if (dtProps.Length > 0)
                sb.AppendLine("                else reader.TrySkip();");
            sb.AppendLine("            }");

            // Construct result
            if (hasCtor)
            {
                sb.Append("            var __polyObj = new ");
                sb.Append(dtName);
                sb.Append("(");
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    if (ci > 0)
                        sb.Append(", ");
                    sb.Append("__cp_");
                    sb.Append(ci);
                }
                sb.AppendLine(");");
                sb.AppendLine("            result = __polyObj;");
            }
            else
            {
                sb.AppendLine("            result = __polyObj;");
            }

            sb.AppendLine("            return ReadStatus.Success;");
            sb.AppendLine("        }");
        }

        sb.AppendLine();
        sb.AppendLine(
            "        throw new System.FormatException($\"Unknown type discriminator: {System.Text.Encoding.UTF8.GetString(__disc)}\");"
        );
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    // ── Recursive nested-list helpers ──

    /// <summary>Returns true if the property's element type is itself a list/array (nested collection).</summary>
    private static bool IsNestedList(PropertyInfo prop) =>
        (prop.ElementTypeKind is "list" or "array") && prop.NestedProperties.Length > 0;

    /// <summary>
    /// Recursively emits WriteStartArray / foreach / WriteEndArray for nested lists.
    /// Writes one level per call; recurses into NestedProperties for deeper levels.
    /// The innermost element is emitted via EmitSerializeElement.
    /// </summary>
    private static void EmitNestedListSerialize(
        StringBuilder sb,
        PropertyInfo prop,
        string itemVar,
        string indent
    )
    {
        sb.Append(indent);
        sb.AppendLine("jw.WriteStartArray();");
        sb.Append(indent);
        sb.Append("foreach (var __inner in ");
        sb.Append(itemVar);
        sb.AppendLine(")");
        sb.Append(indent);
        sb.AppendLine("{");

        if (IsNestedList(prop))
        {
            // Another level of nesting
            EmitNestedListSerialize(sb, prop.NestedProperties[0], "__inner", indent + "    ");
        }
        else
        {
            // Base case: emit element directly
            EmitSerializeElement(sb, prop, "__inner", indent + "    ");
        }

        sb.Append(indent);
        sb.AppendLine("}");
        sb.Append(indent);
        sb.AppendLine("jw.WriteEndArray();");
    }

    // ── Recursive nested-list deserialize helper ──

    /// <summary>
    /// Recursively emits deserialization code for List&lt;List&lt;...&lt;T&gt;&gt;&gt;.
    /// Each call handles one nesting level: reads ArrayStart, loops over elements,
    /// and recurses via NestedProperties for deeper levels.
    /// </summary>
    private static void EmitNestedListDeserialize(
        StringBuilder sb,
        PropertyInfo prop,
        string parentListAcc,
        string propName,
        string indent,
        int nestLevel
    )
    {
        // For nested lists, use ElementTypeName (e.g. "System.Collections.Generic.List<int>");
        // for primitives, resolve from TypeKind (e.g. "int32" → "int").
        var innerTypeName = prop.TypeKind is "list" or "array"
            ? (prop.ElementTypeName ?? "object")
            : ResolveCSharpTypeName(prop.TypeKind);
        var innerVar = nestLevel == 0 ? $"__inner_{propName}" : $"__inner_{propName}_{nestLevel}";

        sb.Append(indent);
        sb.AppendLine("while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)");
        sb.Append(indent);
        sb.AppendLine("{");
        sb.Append(indent);
        sb.Append("    var ");
        sb.Append(innerVar);
        sb.Append(" = new System.Collections.Generic.List<");
        sb.Append(innerTypeName);
        sb.AppendLine(">(8);");
        sb.Append(indent);
        sb.AppendLine("    if (reader.TokenType == TokenType.ArrayStart)");
        sb.Append(indent);
        sb.AppendLine("    {");

        if (IsNestedList(prop))
        {
            // Another level of nesting
            EmitNestedListDeserialize(
                sb,
                prop.NestedProperties[0],
                innerVar,
                propName,
                indent + "        ",
                nestLevel + 1
            );
        }
        else
        {
            sb.Append(indent);
            sb.AppendLine(
                "        while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
            );
            sb.Append(indent);
            sb.AppendLine("        {");
            EmitDeserializeElementAdd(sb, prop, innerVar, indent + "            ", nestLevel);
            sb.Append(indent);
            sb.AppendLine("        }");
        }

        sb.Append(indent);
        sb.AppendLine("    }");
        sb.Append(indent);
        sb.Append("    ");
        sb.Append(parentListAcc);
        sb.Append(".Add(");
        sb.Append(innerVar);
        sb.AppendLine(");");
        sb.Append(indent);
        sb.AppendLine("}");
    }

    /// <summary>Maps a TypeKind string to its C# type name for code generation.</summary>
    private static string ResolveCSharpTypeName(string typeKind) =>
        typeKind switch
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
            "list" => "object",
            "array" => "object",
            _ => "object",
        };

    // ── Array type code generation ──

    private static void EmitArraySerializer(StringBuilder sb, TypeInfo type)
    {
        var arrTypeName = type.FullyQualifiedName;
        var elemKind = type.ArrayElementKind!;
        var elemTypeName = type.ArrayElementName!;

        sb.Append("file readonly struct ");
        sb.Append(type.Name);
        sb.AppendLine("JsonSer : ISerializer<");
        sb.Append(arrTypeName);
        sb.AppendLine(">");
        sb.AppendLine("    {");
        sb.Append("        public void Serialize(IBufferWriter<byte> writer, ");
        sb.Append(arrTypeName);
        sb.AppendLine(" value)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            var jw = new JsonWriter(writer, indented: PicoJetson.JsonOptions.Current?.Indented ?? false, maxDepth: PicoJetson.JsonOptions.Current?.MaxDepth ?? 63);"
        );
        sb.AppendLine("            jw.WriteStartArray();");
        sb.AppendLine("            foreach (var __item in value)");
        sb.AppendLine("            {");
        EmitArrayElementSerialize(sb, elemKind, elemTypeName, "                ");
        sb.AppendLine("            }");
        sb.AppendLine("            jw.WriteEndArray();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void EmitArrayElementSerialize(
        StringBuilder sb,
        string elemKind,
        string elemTypeName,
        string indent
    )
    {
        switch (elemKind)
        {
            case "string":
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(System.Text.Encoding.UTF8.GetBytes(__item));");
                break;
            case "int32":
            case "int64":
            case "float32":
            case "float64":
                sb.Append(indent);
                sb.AppendLine("jw.WriteNumber(__item);");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("jw.WriteBoolean(__item);");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine(
                    "jw.WriteString(System.Text.Encoding.UTF8.GetBytes(__item.ToString(\"O\")));"
                );
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine(
                    "jw.WriteString(System.Text.Encoding.UTF8.GetBytes(__item.ToString()));"
                );
                break;
            case "decimal":
            case "enum":
                sb.Append(indent);
                sb.AppendLine("jw.WriteNumber(__item);");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("JsonInner", elemTypeName);
                sb.Append(indent);
                sb.Append(sn);
                sb.AppendLine(".Serialize(ref jw, __item);");
                break;
            }
            default:
                sb.Append(indent);
                sb.AppendLine(
                    "jw.WriteString(System.Text.Encoding.UTF8.GetBytes(__item.ToString()));"
                );
                break;
        }
    }

    private static void EmitArrayDeserializer(StringBuilder sb, TypeInfo type)
    {
        var arrTypeName = type.FullyQualifiedName;
        var elemKind = type.ArrayElementKind!;
        var elemTypeName = type.ArrayElementName!;
        var elemCsType = ElementCSharpTypeName(elemKind, elemTypeName);
        var isList = type.IsTopLevelList;

        sb.Append("    file readonly struct ");
        sb.Append(type.Name);
        sb.Append("JsonDeserializer : IDeserializer<");
        sb.Append(arrTypeName);
        sb.AppendLine(">");
        sb.AppendLine("    {");
        sb.Append("        public ");
        sb.Append(arrTypeName);
        sb.AppendLine(" Deserialize(ReadOnlySpan<byte> data)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            var reader = new JsonReader(data, maxDepth: PicoJetson.JsonOptions.Current?.MaxDepth ?? 256);"
        );
        sb.Append("            var __list = new System.Collections.Generic.List<");
        sb.Append(elemCsType);
        sb.AppendLine(">();");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine(
            "                if (!reader.Read() || reader.TokenType != TokenType.ArrayStart)"
        );
        if (isList)
        {
            sb.AppendLine("                    return __list;");
        }
        else
        {
            sb.Append("                    return Array.Empty<");
            sb.Append(elemCsType);
            sb.AppendLine(">();");
        }
        sb.AppendLine(
            "                while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
        );
        sb.AppendLine("                {");
        EmitArrayElementDeserialize(sb, elemKind, elemTypeName, "                    ");
        sb.AppendLine("                }");
        if (isList)
        {
            sb.AppendLine("                return __list;");
        }
        else
        {
            sb.AppendLine("                return __list.ToArray();");
        }
        sb.AppendLine("            }");
        sb.AppendLine("            finally");
        sb.AppendLine("            {");
        sb.AppendLine("                reader.Dispose();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static string ElementCSharpTypeName(string elemKind, string elemTypeName) =>
        elemKind switch
        {
            "string" => "string",
            "int32" => "int",
            "int64" => "long",
            "float32" => "float",
            "float64" => "double",
            "boolean" => "bool",
            "datetime" => "System.DateTime",
            "dateonly" => "System.DateOnly",
            "timeonly" => "System.TimeOnly",
            "timespan" => "System.TimeSpan",
            "guid" => "System.Guid",
            "decimal" => "decimal",
            "object" => elemTypeName,
            "enum" => elemTypeName,
            _ => "object",
        };

    private static void EmitArrayElementDeserialize(
        StringBuilder sb,
        string elemKind,
        string elemTypeName,
        string indent
    )
    {
        switch (elemKind)
        {
            case "string":
                sb.Append(indent);
                sb.AppendLine(
                    "__list.Add(System.Text.Encoding.UTF8.GetString(reader.GetStringRaw()));"
                );
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __ev))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetInt64(out var __lev);");
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __ev);");
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __ev);");
                sb.Append(indent);
                sb.AppendLine("__list.Add((float)__ev);");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __ev);");
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __ev);");
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __ev);"
                );
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __ev);");
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __ev);"
                );
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(elemTypeName);
                sb.AppendLine(">(__strValue, out var __ev);");
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("JsonInner", elemTypeName);
                sb.Append(indent);
                sb.Append("__list.Add(");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader));");
                break;
            }
            default:
                sb.Append(indent);
                sb.AppendLine(
                    "__list.Add(System.Text.Encoding.UTF8.GetString(reader.GetStringRaw()));"
                );
                break;
        }
    }

    private static void EmitArrayStreamingDeserializer(StringBuilder sb, TypeInfo type)
    {
        var arrTypeName = type.FullyQualifiedName;
        var elemKind = type.ArrayElementKind!;
        var elemTypeName = type.ArrayElementName!;
        var elemCsType = ElementCSharpTypeName(elemKind, elemTypeName);
        var isList = type.IsTopLevelList;

        sb.Append("file static class ");
        sb.Append(type.Name);
        sb.AppendLine("Streaming");
        sb.AppendLine("{");
        sb.Append(
            "    internal static ReadStatus DeserializeStreaming(ref JsonReader reader, out "
        );
        sb.Append(arrTypeName);
        sb.AppendLine("? result)");
        sb.AppendLine("    {");
        sb.Append("        var __list = new System.Collections.Generic.List<");
        sb.Append(elemCsType);
        sb.AppendLine(">();");
        sb.AppendLine("        result = default;");
        sb.AppendLine();
        sb.AppendLine(
            "        if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : reader.TokenType != TokenType.None ? ReadStatus.Success : ReadStatus.EndOfInput;"
        );
        sb.AppendLine();
        sb.AppendLine("        while (true)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
        );
        sb.AppendLine("            if (reader.TokenType == TokenType.ArrayEnd) break;");
        EmitArrayElementDeserialize(sb, elemKind, elemTypeName, "            ");
        sb.AppendLine("        }");
        if (isList)
        {
            sb.AppendLine("        result = __list;");
        }
        else
        {
            sb.AppendLine("        result = __list.ToArray();");
        }
        sb.AppendLine("        return ReadStatus.Success;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    /// <summary>
    /// Escapes \\ and \" for safe embedding in generated C# string literals.
    /// Also escapes \n, \r, \t, and < 0x20 chars to keep generated C# compilable.
    /// </summary>
    private static string EscapeCSharpString(string s) =>
        s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
}
