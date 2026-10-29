namespace PicoJetson.Gen;

using Microsoft.CodeAnalysis.CSharp;
using AnonFieldInfo = PicoSerDe.Gen.AnonFieldInfo;
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

    private static readonly DiagnosticDescriptor AnonRequiresCSharp12 = new(
        id: "PICOJETSON003",
        title: "Anonymous types require C# 12+",
        messageFormat: "Anonymous type serialization requires C# 12 or later",
        category: "PicoJetson.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor AnonRequiresUnsafe = new(
        id: "PICOJETSON004",
        title: "Requires AllowUnsafeBlocks",
        messageFormat: "Anonymous type serialization requires AllowUnsafeBlocks to be enabled",
        category: "PicoJetson.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
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

    private static readonly PicoSerDe.Gen.AnonFormatConfig AnonCfg = new(
        HasNullLiteral: true,
        EmbedsKeyInValue: false,
        ObjectStartMethod: "WriteStartObject",
        ObjectEndMethod: "WriteEndObject",
        ObjectStartNeedsCount: false,
        HasIndentedMaxDepth: true,
        KeyIsEncodedString: false,
        HasNamingPolicy: true,
        HasOptionsParam: true,
        FacadeTakesOptions: true
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

        // Pipeline-Anon: anonymous type serialization via interceptors
        var anonDriven = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: (n, _) => IsCandidate(n),
                transform: (ctx, _) =>
                {
                    // Anonymous-type usage is detected regardless of capabilities; the
                    // output callback reports the diagnostic and skips emission (review P3-1).
                    if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol m)
                        return null;
                    if (m.TypeArguments.Length != 1)
                        return null;
                    if (m.TypeArguments[0] is not INamedTypeSymbol nt || !nt.IsAnonymousType)
                        return null;
                    if (
                        m.ContainingType.Name != Config.SerializerClassName
                        || m.ContainingType.ContainingNamespace?.ToDisplayString()
                            != Config.Namespace
                    )
                        return null;
                    return PicoSerDe.Gen.AnonTypeHandler.BuildAnonTypeInfo(nt, ctx, Config, Attrs);
                }
            )
            .Where(a => a is not null);

        // Capability flags for the anonymous-type diagnostic: reported
        // only when anonymous serialization is actually used (review P3-1).
        var anonCaps = context.CompilationProvider.Select(
            static (c, _) =>
            {
                var comp = (CSharpCompilation)c;
                return (
                    LangOk: comp.LanguageVersion >= LanguageVersion.CSharp12,
                    Unsafe: comp.Options.AllowUnsafe
                );
            }
        );

        var anonOut = anonDriven.Collect().Combine(asmName).Combine(anonCaps);
        context.RegisterSourceOutput(
            anonOut,
            (spc, pair) =>
            {
                var anonTypes = pair.Left.Left;
                var __asmName = pair.Left.Right;
                var caps = pair.Right;
                if (anonTypes.Length > 0 && !caps.LangOk)
                    spc.ReportDiagnostic(Diagnostic.Create(AnonRequiresCSharp12, null));
                else if (anonTypes.Length > 0 && !caps.Unsafe)
                    spc.ReportDiagnostic(Diagnostic.Create(AnonRequiresUnsafe, null));
                if (anonTypes.Length == 0 || !caps.LangOk || !caps.Unsafe)
                    return;
                PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = $"__PicoSerDe_{__asmName}";
                foreach (var ai in anonTypes)
                {
                    if (ai is not { } info)
                        continue;
                    PicoSerDe.Gen.AnonTypeHandler.GenerateInterceptorClass(
                        spc,
                        info,
                        Config,
                        AnonCfg,
                        (f, vv, wv) => EmitJsonValue(f, vv, wv)
                    );
                }
            }
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
        {
            // The generated deserializer constructs the type with new() when
            // there is no constructor path. Skip types that cannot be
            // constructed that way instead of emitting code that fails
            // compilation.
            bool hasParameterless = false;
            foreach (var c in namedType.Constructors)
                if (c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0)
                {
                    hasParameterless = true;
                    break;
                }
            if (!hasParameterless)
                return null;
            return ti;
        }

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
                bool anyUnsupported = false;
                foreach (var param in primary.Parameters)
                {
                    var (typeKind, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(
                        param.Type,
                        Config.FormatTag
                    );
                    if (typeKind is null)
                    {
                        anyUnsupported = true;
                        break;
                    }
                    list.Add(
                        new CtorParamInfo(
                            param.Name,
                            typeKind,
                            param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                            TypeFullNameAnnotated: PicoSerDe.Gen.TypeKindResolver.DisplayType(
                                param.Type
                            )
                        )
                    );
                }
                ctorParams = anyUnsupported ? null : list.ToArray();
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
            Name: $"Array_{PicoSerDe.Gen.GenInfrastructure.UniqueName(arrFqn)}",
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
        // Collect nested types (silently skip malformed/empty TypeInfos —
        // e.g. unresolvable element kinds — instead of NRE-ing on the
        // default ImmutableArray in CollectNestedTypes).
        var validTypes = types
            .Where(t =>
                !string.IsNullOrEmpty(t.FullyQualifiedName) && !string.IsNullOrEmpty(t.Name)
            )
            .ToArray();
        foreach (var type in validTypes)
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
                // Key must match CollectNestedTypes' convention (raw FQN with
                // global::) so a type used both as a DTO property and as a
                // top-level array element shares one helper instead of producing
                // a duplicate hintName.
                var elemFqn = type.ArrayElementName!;
                if (!nestedTypes.ContainsKey(elemFqn))
                    nestedTypes[elemFqn] = type.ArrayElementNestedProps;
            }
        }

        // Recursive references: seed the cycle targets' helpers from their real
        // (merged) property sets, and surface per-format skips as PICOSERDE003.
        PicoSerDe.Gen.GenInfrastructure.SeedRecursiveTargets(validTypes, nestedTypes);
        PicoSerDe.Gen.GenInfrastructure.ReportSkippedRecursiveMembers(
            spc,
            validTypes,
            Config.FormatTag
        );
        // PICOSERDE004: a member the format cannot express was dropped at extraction
        // time. YAML/TOML/INI always reported it; JSON and MsgPack collected the same
        // skip list but never surfaced it, so their drops were silent.
        PicoSerDe.Gen.GenInfrastructure.ReportUnsupportedMembers(spc, validTypes, Config.FormatTag);
        // PICOSERDE001: a member whose type is unresolvable for this format is
        // recorded during extraction (it cannot be reported there — a transform must
        // stay pure), so the drop becomes visible here, once per member (D-6).
        PicoSerDe.Gen.GenInfrastructure.ReportCollectedDiagnostics(
            spc,
            PicoSerDe.Gen.GenInfrastructure.CollectUnsupportedTypeMemberDiagnostics(
                validTypes,
                Config.FormatTag
            )
        );

        // Collect nested Dictionary types (e.g. Dictionary<string, Dictionary<string, Foo>>)
        var nestedDictTypes = new Dictionary<string, PropertyInfo>();
        foreach (var type in validTypes)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedDictTypes(type, nestedDictTypes);

        // Generate inner helpers for shared nested types
        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var safeName = PicoSerDe.Gen.GenInfrastructure.UniqueName(cleanName);
            var hintName = $"{safeName}_JsonInner.g.cs";
            if (!hintNames.Add(hintName))
            {
                spc.ReportDiagnostic(
                    Diagnostic.Create(
                        HintNameTrace,
                        null,
                        $"DUPLICATE inner hintName: '{hintName}' (fullName='{fullName}')"
                    )
                );
                // Never call AddSource twice — that throws and would drop every
                // generated source in the compilation.
                continue;
            }
            var polyTarget = validTypes.FirstOrDefault(t =>
                t.FullyQualifiedName == fullName && !t.DerivedTypes.IsDefaultOrEmpty
            );
            spc.AddSource(
                hintName,
                SourceText.From(
                    polyTarget.FullyQualifiedName is not null
                        ? GeneratePolyInnerHelper(cleanName, safeName, polyTarget)
                        : GenerateInnerHelper(cleanName, safeName, props),
                    Encoding.UTF8
                )
            );
        }

        // Emit a shared helper for Dictionary<string, object?> ("any"-valued dicts)
        // when any type in this compilation uses that pattern.
        // Must check types, nestedTypes (inner helpers), and nestedDictTypes —
        // a property like ContentBlock.Arguments is only discovered via nested type traversal.
        bool hasAnyValue =
            validTypes.Any(t => t.Properties.Any(p => p.ElementTypeKind == "any"))
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
            var safeName = PicoSerDe.Gen.GenInfrastructure.UniqueName(cleanName);
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
                // Prefer the entry with richer property info: the polymorphic
                // pipeline merges inherited base-class properties into derived
                // types, while other pipelines may only see the type's own
                // (possibly empty) member list.
                else if (type.Properties.Length > existing.Properties.Length)
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
            var safeFq = PicoSerDe.Gen.GenInfrastructure.UniqueName(type.FullyQualifiedName ?? "");
            var mainHintName = $"{safeFq}_JsonSerializer.g.cs";

            var source = GenerateTypeCode(type, typeMap);
            if (!hintNames.Add(mainHintName))
            {
                spc.ReportDiagnostic(
                    Diagnostic.Create(
                        HintNameTrace,
                        null,
                        $"DUPLICATE main hintName: '{mainHintName}' (fq='{type.FullyQualifiedName}')"
                    )
                );
                continue;
            }
            spc.AddSource(mainHintName, SourceText.From(source, Encoding.UTF8));
        }
    }

    /// <summary>
    /// Inner helper for a recursive reference whose target is polymorphic: the
    /// nested value must go through the discriminator-aware serializer and
    /// deserializer, otherwise it would be read as an empty base instance.
    /// </summary>
    private static string GeneratePolyInnerHelper(
        string fullName,
        string shortName,
        TypeInfo polyType
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System; using System.Buffers; using System.Text;");
        sb.AppendLine("using PicoSerDe.Core; using PicoJetson;");

        var prefix = PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix;
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
        sb.AppendLine();
        var poly = PolyClassPrefix(polyType);
        sb.Append("internal static class ");
        sb.Append(shortName);
        sb.AppendLine("JsonInner");
        sb.AppendLine("{");
        sb.Append("    internal static void Serialize(ref JsonWriter jw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        sb.Append("        ");
        sb.Append(poly);
        sb.AppendLine("JsonPolySer.Serialize(jw.Buffer, value, jw.Options);");
        // PolySerial writes the value straight into the buffer, bypassing
        // BeforeWriteValue: tell the caller's writer the value is complete.
        sb.AppendLine("        jw.NotifyRawValueWritten();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.Append("    internal static ");
        sb.Append(fullName);
        sb.AppendLine(" Deserialize(ref JsonReader reader)");
        sb.Append("        => ");
        sb.Append(poly);
        sb.AppendLine("JsonPolyDes.DeserializeCore(ref reader, reader.Options);");
        sb.AppendLine("}");
        return HoistColdThrows(sb.ToString());
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
            sb.Append("        var obj = ");
            sb.Append(ObjectConstructor(fullName));
            sb.AppendLine(";");
        }
        sb.AppendLine(
            "        if (!reader.IsResumed && reader.TokenType != TokenType.ObjectStart) throw __PicoJsonCold.Err(\"Expected a JSON object at offset \", reader.BytesConsumed, \" but found \" + reader.TokenType + \".\");"
        );
        foreach (var rp in props.Where(p => p.IsRequired))
        {
            sb.Append("        bool __seen_");
            sb.Append(rp.Name);
            sb.AppendLine(" = false;");
        }
        sb.AppendLine("        " + CiLocal);
        sb.AppendLine(
            "        while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            var __n = reader.GetStringRaw();");
        // Streaming: an incomplete value must abort the helper so the caller
        // can rewind to the property snapshot. Span mode keeps the old strict
        // error path (NeedsMoreData is false there).
        sb.AppendLine("            if (!reader.Read() && reader.NeedsMoreData) break;");
        EmitNameDispatch(
            sb,
            props,
            "__n",
            "            ",
            (np, bodyIndent) =>
            {
                EmitDeserializeProperty(sb, np, "obj", bodyIndent);
                if (np.IsRequired)
                {
                    sb.Append(bodyIndent);
                    sb.Append("__seen_");
                    sb.Append(np.Name);
                    sb.AppendLine(" = true;");
                }
            },
            caseInsensitiveVar: "__ci"
        );
        if (props.Length > 0)
        {
            sb.AppendLine(
                "            else if (reader.Options?.UnmappedMemberHandling == PicoJetson.JsonUnmappedMemberHandling.Disallow)"
            );
            sb.AppendLine(
                "                throw __PicoJsonCold.Err(\"Unexpected property '\" + Encoding.UTF8.GetString(__n) + \"' at offset \", reader.BytesConsumed);"
            );
            sb.AppendLine("            else reader.TrySkip();");
        }
        else
            sb.AppendLine("            reader.TrySkip();");
        sb.AppendLine("        }");
        foreach (var rp in props.Where(p => p.IsRequired))
        {
            sb.Append("        if (!__seen_");
            sb.Append(rp.Name);
            sb.Append(") throw new System.FormatException(\"Missing required property '");
            sb.Append(EscapeCSharpString(rp.JsonName));
            sb.AppendLine("'.\");");
        }
        sb.AppendLine("        return obj;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return HoistColdThrows(sb.ToString());
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
        sb.Append("        var obj = ");
        sb.Append(ObjectConstructor(fullName));
        sb.AppendLine(";");
        sb.AppendLine("        if (reader.TokenType == TokenType.ObjectStart)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("            {");
        EmitDictInnerKeyRead(sb, dictProp, "                ");
        sb.AppendLine("                if (!reader.Read() && reader.NeedsMoreData) break;");
        EmitDictInnerValueDeserialize(sb, dictProp, "obj", "                ");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return obj;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return HoistColdThrows(sb.ToString());
    }

    private static void EmitDictInnerKey(StringBuilder sb, PropertyInfo dp, string indent)
    {
        // The key goes out through the writer's char overload, which encodes into a stack
        // buffer below 256 bytes: Encoding.UTF8.GetBytes allocated a byte[] for every key of
        // every dictionary (64 B/op for the two-entry benchmark model) to hand the writer
        // bytes it then copies into its own span anyway.
        sb.Append(indent);
        if (dp.KeyTypeKind == "string")
            sb.AppendLine("jw.WritePropertyName(__kvp.Key);");
        else
            sb.AppendLine("jw.WritePropertyName(__kvp.Key.ToString());");
    }

    private static void EmitDictInnerValueSerialize(
        StringBuilder sb,
        PropertyInfo dp,
        string indent
    )
    {
        // Nullable values are emitted with the same two-step shape as
        // EmitSerializeElement: JSON null for the empty case, the unwrapped
        // (non-nullable) value for the typed core emission.
        if (dp.ElementIsNullableValue)
        {
            sb.Append(indent);
            sb.AppendLine("if (__kvp.Value == null)");
            sb.Append(indent);
            sb.AppendLine("    jw.WriteNull();");
            sb.Append(indent);
            sb.AppendLine("else");
            sb.Append(indent);
            sb.AppendLine("{");
            EmitDictInnerValueSerializeCore(sb, dp, "__kvp.Value.Value", indent + "    ");
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }
        EmitDictInnerValueSerializeCore(sb, dp, "__kvp.Value", indent);
    }

    private static void EmitDictInnerValueSerializeCore(
        StringBuilder sb,
        PropertyInfo dp,
        string valueExpr,
        string indent
    )
    {
        switch (dp.ElementTypeKind)
        {
            case "string":
                if (dp.ElementIsNullableReference)
                {
                    sb.Append(indent);
                    sb.AppendLine("if (" + valueExpr + " != null)");
                    sb.Append(indent);
                    sb.AppendLine("    jw.WriteString(Encoding.UTF8.GetBytes(" + valueExpr + "));");
                    sb.Append(indent);
                    sb.AppendLine("else");
                    sb.Append(indent);
                    sb.AppendLine("    jw.WriteNull();");
                }
                else
                {
                    sb.Append(indent);
                    sb.AppendLine("jw.WriteString(Encoding.UTF8.GetBytes(" + valueExpr + "));");
                }
                break;
            case "int32":
            case "int64":
            case "int16":
            case "uint16":
            case "sbyte":
            case "byte":
            case "uint32":
            case "uint64":
            case "float32":
            case "float64":
                sb.Append(indent);
                sb.AppendLine("jw.WriteNumber(" + valueExpr + ");");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("jw.WriteBoolean(" + valueExpr + ");");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine(
                    "jw.WriteString(Encoding.UTF8.GetBytes(" + valueExpr + ".ToString(\"O\")));"
                );
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine(
                    "jw.WriteString(Encoding.UTF8.GetBytes(" + valueExpr + ".ToString()));"
                );
                break;
            case "decimal":
            case "enum":
                sb.Append(indent);
                sb.AppendLine("jw.WriteNumber(" + valueExpr + ");");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    dp.ElementTypeName!
                );
                sb.Append(indent);
                sb.Append("if (" + valueExpr + " == null) jw.WriteNull(); else ");
                sb.Append(sn);
                sb.AppendLine(".Serialize(ref jw, " + valueExpr + ");");
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
                sb.AppendLine(".Serialize(ref jw, " + valueExpr + ");");
                break;
            }
            case "any":
                EmitAnyValueSerialize(sb, valueExpr, indent);
                break;
            default:
                if (
                    dp.ElementTypeKind is not null
                    && EmitExtendedScalarWrite(sb, dp.ElementTypeKind, valueExpr, indent)
                )
                    break;
                sb.Append(indent);
                sb.AppendLine(
                    "jw.WriteString(Encoding.UTF8.GetBytes(" + valueExpr + ".ToString()));"
                );
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

            case "int16":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("short.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "uint16":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("ushort.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "sbyte":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("sbyte.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "byte":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("byte.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("uint.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("ulong.TryParse(__rawBytes, out var __dictKey);");
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
                sb.Append(PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(dp.KeyTypeName!));
                sb.AppendLine(">(__strValue, out var __dictKey);");
                break;
            default:
                sb.Append(indent);
                sb.AppendLine("var __dictKey = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
        }
    }

    /// <summary>
    /// Emits the value read of a nested-dictionary helper. Nullable value-type
    /// elements get a null check first, like <see cref="EmitDeserializeElementAssign"/>.
    /// </summary>
    private static void EmitDictInnerValueDeserialize(
        StringBuilder sb,
        PropertyInfo dp,
        string dictVar,
        string indent
    )
    {
        if (dp.ElementIsNullableValue)
        {
            sb.Append(indent);
            sb.AppendLine("if (reader.TokenType == TokenType.Null)");
            sb.Append(indent);
            sb.Append(dictVar);
            sb.AppendLine("[__dictKey] = null;");
            sb.Append(indent);
            sb.AppendLine("else");
            sb.Append(indent);
            sb.AppendLine("{");
            EmitDictInnerValueDeserializeCore(sb, dp, dictVar, indent + "    ");
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }
        EmitDictInnerValueDeserializeCore(sb, dp, dictVar, indent);
    }

    private static void EmitDictInnerValueDeserializeCore(
        StringBuilder sb,
        PropertyInfo dp,
        string dictVar,
        string indent
    )
    {
        switch (dp.ElementTypeKind)
        {
            case "string":
                // Aligned with EmitDeserializeElementAssignCore: a JSON null for a
                // string value must stay null, not the string "null".
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = null!;");
                sb.Append(indent);
                sb.AppendLine("else");
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

            case "int16":
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
                sb.AppendLine("[__dictKey] = checked((short)__ev);");
                break;
            case "uint16":
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
                sb.AppendLine("[__dictKey] = checked((ushort)__ev);");
                break;
            case "sbyte":
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
                sb.AppendLine("[__dictKey] = checked((sbyte)__ev);");
                break;
            case "byte":
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
                sb.AppendLine("[__dictKey] = checked((byte)__ev);");
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __ev)) {");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetUInt64(out var __ulev);");
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((long)__ulev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.AppendLine("[__dictKey] = checked((uint)__ev);");
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetUInt64(out var __ev);");
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
                sb.Append(PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(dp.ElementTypeName!));
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
                if (
                    dp.ElementTypeKind is not null
                    && ExtendedScalarReadExpr(dp.ElementTypeKind) is string __sx1
                )
                {
                    sb.Append(indent);
                    sb.Append(dictVar);
                    sb.AppendLine("[__dictKey] = " + __sx1 + ";");
                    break;
                }
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
        sb.AppendLine("            jw.WriteNumber(__ul);");
        sb.AppendLine("        else if (value is int __i)");
        sb.AppendLine("            jw.WriteNumber(__i);");
        sb.AppendLine("        else if (value is uint __ui)");
        sb.AppendLine("            jw.WriteNumber(__ui);");
        sb.AppendLine("        else if (value is short __sh)");
        sb.AppendLine("            jw.WriteNumber(__sh);");
        sb.AppendLine("        else if (value is byte __by)");
        sb.AppendLine("            jw.WriteNumber(__by);");
        sb.AppendLine("        else if (value is ushort __ush)");
        sb.AppendLine("            jw.WriteNumber(__ush);");
        sb.AppendLine("        else if (value is sbyte __sby)");
        sb.AppendLine("            jw.WriteNumber(__sby);");
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
        // Char overload: it encodes into a stack buffer below 256 bytes, so a dictionary key
        // costs no byte[] (the byte overload the generator used to pick allocated one per key).
        sb.AppendLine("                jw.WritePropertyName(__kvp.Key);");
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
        sb.AppendLine("        if (reader.TokenType == TokenType.UInt64)");
        sb.AppendLine(
            "            throw new System.FormatException($\"Unsigned 64-bit integer at offset {reader.BytesConsumed} cannot be converted to Int64 in object-typed values\");"
        );
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
        sb.AppendLine("                if (!reader.Read() && reader.NeedsMoreData) break;");
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
        return HoistColdThrows(sb.ToString());
    }

    /// <summary>
    /// The hoisted option read (json-gen3 #4): one evaluation per deserializer call instead of one per
    /// property. <c>?? true</c> is load-bearing — a reader with no options object is case-INSENSITIVE,
    /// exactly like the inline expression it replaces.
    /// </summary>
    private const string CiLocal =
        "bool __ci = reader.Options?.PropertyNameCaseInsensitive ?? true;";

    /// <summary>Name of the generated file-local cold-path helper. Double-underscored so it can
    /// never collide with a user type, and <c>file</c>-scoped so it can never leak out of the file.</summary>
    private const string ColdHelperClass = "__PicoJsonCold";

    /// <summary>
    /// One inline throw site: <c>throw new System.FormatException($"&lt;prefix&gt;{reader.BytesConsumed}&lt;suffix&gt;")</c>
    /// where neither prefix nor suffix contains another interpolation hole (a site that interpolates
    /// the token type or a runtime name is written by hand — see the emit sites — because moving it
    /// here would mean re-typing the message, and re-typing is exactly how message text drifts).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ColdThrowSite = new(
        "throw new System\\.FormatException\\(\\$\"(?<prefix>(?:[^\"\\\\{\\r\\n]|\\\\.)*)"
            + "\\{reader\\.BytesConsumed\\}"
            + "(?<suffix>(?:[^\"\\\\{\\r\\n]|\\\\.)*)\"\\);",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    /// <summary>
    /// Moves every constant-prefix/suffix throw message out of the deserializer's method body and into
    /// one <c>[MethodImpl(NoInlining)]</c> helper:
    /// <para>
    /// <c>throw new FormatException($"…{reader.BytesConsumed}…")</c> becomes
    /// <c>throw __PicoJsonCold.Err("…", reader.BytesConsumed, "…")</c>.
    /// </para>
    /// <para>
    /// <b>Why:</b> an interpolated string puts its <c>DefaultInterpolatedStringHandler</c> (and the
    /// stack buffer behind it) in the CALLER's frame. A 30-property deserializer carries 30 of them,
    /// so the document-sized method is compiled with room for machinery that runs only on the error
    /// path. The message itself is byte-identical: the prefix and the suffix are lifted VERBATIM from
    /// the emitted literal, the only unescaping applied is <c>{{</c>/<c>}}</c> → <c>{</c>/<c>}</c>
    /// (which is what the interpolation did with them), and the offset is rendered by the helper with
    /// the same <c>long.ToString()</c> the interpolation used.
    /// </para>
    /// <para>
    /// The helper class is appended once per file, and only when something in that file calls it.
    /// </para>
    /// </summary>
    private static string HoistColdThrows(string text)
    {
        var rewritten = ColdThrowSite.Replace(
            text,
            m =>
            {
                var prefix = UnescapeInterpolationBraces(m.Groups["prefix"].Value);
                var suffix = UnescapeInterpolationBraces(m.Groups["suffix"].Value);
                return suffix.Length == 0
                    ? $"throw {ColdHelperClass}.Err(\"{prefix}\", reader.BytesConsumed);"
                    : $"throw {ColdHelperClass}.Err(\"{prefix}\", reader.BytesConsumed, \"{suffix}\");";
            }
        );
        if (!rewritten.Contains(ColdHelperClass + ".Err(", StringComparison.Ordinal))
            return rewritten;

        var sb = new StringBuilder(rewritten.Length + 512);
        sb.Append(rewritten);
        EmitColdHelper(sb);
        return sb.ToString();
    }

    /// <summary>
    /// The escaping an interpolated literal applies to text that must be emitted literally: a doubled
    /// brace is one brace. Everything else (<c>\"</c>, <c>\\</c>, <c>\n</c>) is already a valid escape
    /// in the non-interpolated literal the message moves into, so it is left exactly as it is.
    /// </summary>
    private static string UnescapeInterpolationBraces(string text) =>
        text.Replace("{{", "{").Replace("}}", "}");

    /// <summary>
    /// The cold helper emitted into every generated file that throws. Both overloads are
    /// <c>NoInlining</c>: inlining them would rebuild the stack frame this change removes.
    /// <c>offset.ToString()</c> is culture-identical to the interpolation it replaces (both go through
    /// <c>long.ToString(null, CurrentCulture)</c>).
    /// </summary>
    private static void EmitColdHelper(StringBuilder sb)
    {
        sb.Append("file static class ");
        sb.Append(ColdHelperClass);
        sb.AppendLine();
        sb.AppendLine("{");
        sb.AppendLine(
            "    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]"
        );
        sb.AppendLine(
            "    internal static System.FormatException Err(string prefix, long offset) =>"
        );
        sb.AppendLine("        new System.FormatException(prefix + offset.ToString());");
        sb.AppendLine();
        sb.AppendLine(
            "    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]"
        );
        sb.AppendLine(
            "    internal static System.FormatException Err(string prefix, long offset, string suffix) =>"
        );
        sb.AppendLine("        new System.FormatException(prefix + offset.ToString() + suffix);");
        sb.AppendLine("}");
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
            // Skip streaming for value types (shared StreamingFunc<T> delegate
            // cannot express Nullable<T> parameters — struct DTOs keep the
            // direct deserialization path) and for types with [JsonConstructor].
            var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;
            if (type.Properties.Length > 0 && !type.IsValueType)
                EmitStreamingDeserializer(sb, type);
        }

        return HoistColdThrows(sb.ToString());
    }

    // ── Serializer emission ──

    private static void EmitSerializer(StringBuilder sb, TypeInfo type, bool isRefLikeType = false)
    {
        // Single static-class form (both ref structs and regular types): the
        // top-level method carries an explicit options parameter and is
        // registered as a method group — no ISerializer wrapper class.
        sb.Append("    file static class ");
        sb.Append(type.Name);
        sb.AppendLine("JsonSer");
        sb.AppendLine("    {");
        EmitPropertyNameConstants(sb, type.Properties, "        ");
        sb.Append("        public static void Serialize(IBufferWriter<byte> writer, ");
        sb.Append(type.Name);
        sb.AppendLine(" value, global::PicoSerDe.Core.SerOptions? options)");
        sb.AppendLine("        {");
        sb.Append("            var __opts = (global::PicoJetson.JsonOptions?)options;\n");
        sb.AppendLine(
            "            var jw = new JsonWriter(writer, indented: __opts?.Indented ?? false, maxDepth: __opts?.MaxDepth ?? 63, options: __opts);"
        );
        sb.AppendLine("            jw.WriteStartObject();");
        if (type.Properties.Length > 0)
            sb.AppendLine("            var __policy = jw.Options?.PropertyNamingPolicy;");

        foreach (var prop in type.Properties)
        {
            // DefaultIgnoreCondition: wrap property in conditional check
            bool checkNull = EmitIgnoreConditionOpen(
                sb,
                prop,
                "value." + prop.Name,
                "            "
            );
            EmitPropertyNameWrite(sb, prop, "            ");
            EmitSerializeProperty(sb, prop, $"value.{prop.Name}", "            ");
            if (checkNull)
                sb.AppendLine("            }");
        }

        sb.AppendLine("            jw.WriteEndObject();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    /// <summary>
    /// Emits one <c>private static ReadOnlySpan&lt;byte&gt; PropName_X =&gt; "X"u8;</c> per
    /// property: the compiled UTF-8 form of the JSON name, so a serializer without a
    /// naming policy never transcodes the name at run time (P0-2). The span is exactly
    /// what <c>JsonWriter.WritePropertyName(ReadOnlySpan&lt;byte&gt;)</c> already takes, so no
    /// new runtime member is needed — that overload still runs the escape scan and stays
    /// the single exit for every name, escapable bytes or not.
    /// </summary>
    private static void EmitPropertyNameConstants(
        StringBuilder sb,
        ImmutableArray<PropertyInfo> props,
        string indent
    )
    {
        if (props.IsDefaultOrEmpty)
            return;
        foreach (var prop in props)
        {
            sb.Append(indent);
            sb.Append("private static global::System.ReadOnlySpan<byte> PropName_");
            sb.Append(prop.Name);
            sb.Append(" => \"");
            sb.Append(EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\"u8;");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the property-name write for one property. Without a naming policy the compiled
    /// UTF-8 constant goes straight to <c>WritePropertyName(ReadOnlySpan&lt;byte&gt;)</c> — no
    /// <c>GetMaxByteCount</c>/<c>GetBytes</c> per property, no per-call stack copy. With a
    /// policy (CamelCase, a custom <c>JsonNamingPolicy</c>, …) the name is not a compile-time
    /// constant, so the pre-P0-2 <c>ConvertName</c> call is kept verbatim, including its
    /// <c>?? name</c> fallback for a policy that returns null.
    /// </summary>
    private static void EmitPropertyNameWrite(StringBuilder sb, PropertyInfo prop, string indent)
    {
        var jsonName = EscapeCSharpString(prop.JsonName);
        sb.Append(indent);
        sb.AppendLine("if (__policy is null)");
        sb.Append(indent);
        sb.Append("    jw.WritePropertyName(PropName_");
        sb.Append(prop.Name);
        sb.AppendLine(");");
        sb.Append(indent);
        sb.AppendLine("else");
        sb.Append(indent);
        sb.Append("    jw.WritePropertyName(__policy.ConvertName(\"");
        sb.Append(jsonName);
        sb.AppendLine("\") ?? \"" + jsonName + "\");");
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
                return EmitDefaultGuard(sb, prop, accessor, indent);
        }

        if (IsNullableValueType(prop))
        {
            // Nullable<T>: omitted when null (WhenWritingNull) or when null
            // or holding default(T) (WhenWritingDefault).
            sb.Append(indent);
            sb.AppendLine(
                "if (jw.Options?.DefaultIgnoreCondition == PicoJetson.JsonIgnoreCondition.WhenWritingNull"
            );
            sb.Append(indent);
            sb.Append("    ? ");
            sb.Append(accessor);
            sb.AppendLine(".HasValue");
            sb.Append(indent);
            sb.AppendLine(
                "    : jw.Options?.DefaultIgnoreCondition == PicoJetson.JsonIgnoreCondition.WhenWritingDefault"
            );
            sb.Append(indent);
            sb.Append("    ? (");
            sb.Append(accessor);
            sb.Append(".HasValue && ");
            sb.Append(accessor);
            sb.AppendLine(".Value != default)");
            sb.Append(indent);
            sb.AppendLine("    : true)");
            sb.Append(indent);
            sb.AppendLine("{");
            return true;
        }

        if (PicoSerDe.Gen.GenInfrastructure.IsValueTypeKind(prop.TypeKind))
        {
            // Non-nullable value type: only WhenWritingDefault can omit it.
            sb.Append(indent);
            sb.AppendLine(
                "if (jw.Options?.DefaultIgnoreCondition != PicoJetson.JsonIgnoreCondition.WhenWritingDefault"
            );
            sb.Append(indent);
            sb.Append("    || ");
            sb.Append(accessor);
            sb.AppendLine(" != default)");
            sb.Append(indent);
            sb.AppendLine("{");
            return true;
        }

        if (prop.IsNullableReference)
        {
            sb.Append(indent);
            sb.AppendLine(
                "if (jw.Options?.DefaultIgnoreCondition == PicoJetson.JsonIgnoreCondition.WhenWritingNull"
            );
            sb.Append(indent);
            sb.Append("    ? ");
            sb.Append(accessor);
            sb.AppendLine(" != null");
            sb.Append(indent);
            sb.AppendLine(
                "    : jw.Options?.DefaultIgnoreCondition == PicoJetson.JsonIgnoreCondition.WhenWritingDefault"
            );
            sb.Append(indent);
            sb.Append("    ? ");
            sb.Append(accessor);
            sb.AppendLine(" != null");
            sb.Append(indent);
            sb.AppendLine("    : true)");
            sb.Append(indent);
            sb.AppendLine("{");
            return true;
        }

        return false;
    }

    /// <summary>True for Nullable&lt;T&gt; properties (value types wrapped in Nullable).</summary>
    private static bool IsNullableValueType(PropertyInfo p) =>
        p.IsNullable && !p.IsNullableReference;

    /// <summary>Emits the per-property WhenWritingDefault guard.</summary>
    private static bool EmitDefaultGuard(
        StringBuilder sb,
        PropertyInfo prop,
        string accessor,
        string indent
    )
    {
        if (IsNullableValueType(prop))
        {
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(accessor);
            sb.Append(".HasValue && ");
            sb.Append(accessor);
            sb.AppendLine(".Value != default)");
        }
        else if (PicoSerDe.Gen.GenInfrastructure.IsValueTypeKind(prop.TypeKind))
        {
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(accessor);
            sb.AppendLine(" != default)");
        }
        else if (PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(prop))
        {
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(accessor);
            sb.AppendLine(" != null)");
        }
        else
        {
            return false;
        }
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
            // The converter only sees the raw buffer: declare the value written so
            // the pending property name is cleared on the caller's writer.
            sb.Append(indent);
            sb.AppendLine("jw.NotifyRawValueWritten();");
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
                sb.Append("jw.WriteString(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            case "int32":
            case "int64":
            case "int16":
            case "uint16":
            case "sbyte":
            case "byte":
            case "uint32":
            case "uint64":
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
                // A custom [DateTimeFormat] keeps the ToString path verbatim: the attribute
                // accepts any pattern, and a long one could outgrow the helper's stack buffer.
                // The default ("O") goes through TypeIo.WriteFormattable, which formats the
                // ISO text straight into UTF-8 instead of allocating it first.
                sb.Append(indent);
                if (prop.DateTimeFormat is not null)
                {
                    sb.Append("jw.WriteString(");
                    sb.Append(effectiveAccessor);
                    sb.Append(".ToString(\"");
                    sb.Append(prop.DateTimeFormat);
                    sb.AppendLine("\"));");
                }
                else
                    EmitFormattableWrite(sb, effectiveAccessor, "O");
                break;
            case "dateonly":
                EmitFormattableWrite(sb, effectiveAccessor, "O", indent);
                break;
            case "timeonly":
                EmitFormattableWrite(sb, effectiveAccessor, "O", indent);
                break;
            case "timespan":
                EmitFormattableWrite(sb, effectiveAccessor, "c", indent);
                break;
            case "guid":
                EmitFormattableWrite(sb, effectiveAccessor, "D", indent);
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
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
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
            case "memory":
            case "readonlymemory":
                sb.Append(indent);
                sb.AppendLine("jw.WriteStartArray();");
                sb.Append(indent);
                sb.Append("foreach (var __item in ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".Span)");
                sb.Append(indent);
                sb.AppendLine("{");
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
                // Char overload (see EmitDictInnerKey): no byte[] per key, and the writer
                // encodes into its own stack buffer below 256 bytes.
                sb.Append("    jw.WritePropertyName(__kvp.Key");
                if (prop.KeyTypeKind == "string")
                    sb.AppendLine(");");
                else
                    sb.AppendLine(".ToString());");
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
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    global::PicoJetson.JsonSerializer.SerializeCustom<");
                sb.Append(ct);
                sb.Append(">(jw.Buffer, ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(", jw.Options);");
                // SerializeCustom<T> only gets the raw buffer, so the value never
                // passes through BeforeWriteValue: declare it written here.
                sb.Append(indent);
                sb.AppendLine("    jw.NotifyRawValueWritten();");
                sb.Append(indent);
                sb.AppendLine("}");
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
            default:
                if (EmitExtendedScalarWrite(sb, prop.TypeKind, effectiveAccessor, indent))
                    break;
                break;
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

    /// <summary>
    /// Emits one collection element. Nullable elements get a null check first:
    /// nullable value types are unwrapped with <c>.Value</c> for the core
    /// emission, nullable reference elements write JSON null.
    /// </summary>
    private static void EmitSerializeElement(
        StringBuilder sb,
        PropertyInfo prop,
        string itemVar,
        string indent,
        int depth = 0
    )
    {
        if (prop.ElementIsNullableValue)
        {
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(itemVar);
            sb.AppendLine(" == null)");
            sb.Append(indent);
            sb.AppendLine("    jw.WriteNull();");
            sb.Append(indent);
            sb.AppendLine("else {");
            EmitSerializeElementCore(sb, prop, itemVar + ".Value", indent + "    ", depth);
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }
        if (
            prop.ElementTypeKind is "object" or "dict"
            || (prop.ElementIsNullableReference && prop.ElementTypeKind is not "string")
        )
        {
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(itemVar);
            sb.AppendLine(" == null)");
            sb.Append(indent);
            sb.AppendLine("    jw.WriteNull();");
            sb.Append(indent);
            sb.AppendLine("else {");
            EmitSerializeElementCore(sb, prop, itemVar, indent + "    ", depth);
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }
        EmitSerializeElementCore(sb, prop, itemVar, indent, depth);
    }

    private static void EmitSerializeElementCore(
        StringBuilder sb,
        PropertyInfo prop,
        string itemVar,
        string indent,
        int depth = 0
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
                    sb.Append("    jw.WriteString(");
                    sb.Append(itemVar);
                    sb.AppendLine(");");
                    sb.Append(indent);
                    sb.AppendLine("else");
                    sb.Append(indent);
                    sb.AppendLine("    jw.WriteNull();");
                }
                else
                {
                    sb.Append(indent);
                    sb.Append("jw.WriteString(");
                    sb.Append(itemVar);
                    sb.AppendLine(");");
                }
                break;
            case "int32":
            case "int64":
            case "int16":
            case "uint16":
            case "sbyte":
            case "byte":
            case "uint32":
            case "uint64":
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
            case "list":
            case "array":
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
            case "memory":
            case "readonlymemory":
            {
                // A SEQUENCE value in a non-member position: S8's element (a collection as
                // the element of another collection) and S10's dict VALUE (a collection as
                // the value of a dictionary) are the same shape here — the wire form is the
                // nested array `List<List<int>>` writes, and the extractor's descriptor chain
                // (NestedProperties[0]) says what that inner array's items are, which is the
                // part a kind-only test could never recover. Asking IsSequenceKind's family
                // (not one kind) is why `list`/`array` belong in this group: a dict value of
                // type List<int> used to fall through to the `default:` arm and be written as
                // the item's ToString().
                // The loop variable is per-depth: two nesting levels of this arm sit in the
                // same scope chain, so reusing one name is CS0136.
                var setItem = depth == 0 ? "__setItem" : "__setItem" + depth.ToString();
                sb.Append(indent);
                sb.AppendLine("jw.WriteStartArray();");
                sb.Append(indent);
                sb.Append("foreach (var ");
                sb.Append(setItem);
                sb.Append(" in ");
                sb.Append(
                    PicoSerDe.Gen.GenInfrastructure.SequenceItemsExpression(
                        prop.ElementTypeKind,
                        itemVar
                    )
                );
                sb.AppendLine(")");
                sb.Append(indent);
                sb.AppendLine("{");
                if (prop.NestedProperties.Length > 0)
                    EmitSerializeElement(
                        sb,
                        prop.NestedProperties[0],
                        setItem,
                        indent + "    ",
                        depth + 1
                    );
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("jw.WriteEndArray();");
                break;
            }
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
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    global::PicoJetson.JsonSerializer.SerializeCustom<");
                sb.Append(ct);
                sb.Append(">(jw.Buffer, ");
                sb.Append(itemVar);
                sb.AppendLine(");");
                // Same as the property path: the raw buffer write must be declared.
                sb.Append(indent);
                sb.AppendLine("    jw.NotifyRawValueWritten();");
                sb.Append(indent);
                sb.AppendLine("}");
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
                if (EmitExtendedScalarWrite(sb, prop.ElementTypeKind!, itemVar, indent))
                    break;
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

    /// <summary>
    /// Emits a raw throw statement for an invalid property value. The property
    /// name is escaped for safe embedding in the generated C# interpolated
    /// string literal.
    /// </summary>
    private static void EmitThrowInvalidValue(
        StringBuilder sb,
        string indent,
        PropertyInfo prop,
        string expected
    )
    {
        var name = EscapeCSharpString(prop.JsonName).Replace("{", "{{").Replace("}", "}}");
        sb.Append(indent);
        sb.Append("throw new System.FormatException($\"Expected ");
        sb.Append(expected);
        sb.Append(" value for property \\\"");
        sb.Append(name);
        sb.AppendLine("\\\" at offset {reader.BytesConsumed}\");");
    }

    /// <summary>Emits a token-kind check that throws when the current token is not <paramref name="tokenType"/>.</summary>
    private static void EmitTokenCheck(
        StringBuilder sb,
        string indent,
        PropertyInfo prop,
        string tokenType
    )
    {
        sb.Append(indent);
        sb.Append("if (reader.TokenType != TokenType.");
        sb.Append(tokenType);
        sb.AppendLine(")");
        sb.Append(indent);
        sb.AppendLine("{");
        EmitThrowInvalidValue(sb, indent + "    ", prop, tokenType);
        sb.Append(indent);
        sb.AppendLine("}");
    }

    /// <summary>
    /// Emits a token-kind check accepting String or numeric tokens. Used for
    /// decimal and enum kinds, which are serialized as JSON numbers but also
    /// accept string forms on input.
    /// </summary>
    private static void EmitStringOrNumberCheck(StringBuilder sb, string indent, PropertyInfo prop)
    {
        sb.Append(indent);
        sb.AppendLine(
            "if (reader.TokenType is not (TokenType.String or TokenType.Int32 or TokenType.Int64 or TokenType.Float32 or TokenType.Float64))"
        );
        sb.Append(indent);
        sb.AppendLine("{");
        EmitThrowInvalidValue(sb, indent + "    ", prop, "a string or number");
        sb.Append(indent);
        sb.AppendLine("}");
    }

    // ── Scalar reads without a string round trip ────────────────────────────────────────
    //
    // JsonWriter puts a DateTime on the wire as its ISO-8601 'O' form ("T.fffffffZ" for Utc,
    // "T.fffffff" for Unspecified, an offset for Local) and an enum as its number. Reading them
    // back through Encoding.UTF8.GetString + DateTime.TryParse/Enum.TryParse therefore paid for a
    // string allocation and a full culture-aware parse on every document. Both helpers below answer
    // the shape we actually write with Utf8Parser in one span parse and keep the *reference* parser
    // — the same overloads, on a char span, same options, same culture — for everything else. The
    // equivalence is not assumed: each guard says exactly which inputs the fast arm may answer, and
    // the fallback is the old code path with the allocation taken out.

    /// <summary>
    /// Char-span read buffer for the scalar fallbacks: at most this many <b>bytes</b> are transcoded
    /// onto the stack. <c>charCount &lt;= byteCount</c> for UTF-8 (1–4 bytes encode one char, and an
    /// invalid byte decodes to one U+FFFD), so 64 bytes never need more than 64 chars. Longer
    /// payloads keep the old string path — that is a pathological DateTime, not a document we write.
    /// </summary>
    private const int ScalarCharStackBuffer = 64;

    /// <summary>
    /// Emits the <c>DateTime</c> read for a value already in <c>__rawBytes</c>: the fast arm, the
    /// char-span fallback and the failure throw.
    /// <para>
    /// <b>The fast arm's guard is two lengths, and both are load-bearing.</b> <c>'O'</c> is 27 chars
    /// without a zone designator and 28 with a trailing <c>Z</c>; only those two shapes are answered
    /// exactly like <c>DateTime.TryParse(…, RoundtripKind)</c>. An explicit <c>±HH:mm</c> offset
    /// (33 chars) is <i>not</i>: <c>Utf8Parser</c> clamps at <c>DateTime.Min/MaxValue</c> where the
    /// reference parser fails outright, so an offset payload must fall through. The
    /// <c>Used == Length</c> check then makes the shape exact rather than approximate.
    /// </para>
    /// </summary>
    /// <summary>
    /// Emits a scalar string write that formats straight into UTF-8 — no intermediate
    /// <c>string</c>. Equal to <c>value.ToString(format)</c> followed by
    /// <c>jw.WriteString(string)</c>, byte for byte (test: TypeIoFormattingTests).
    /// </summary>
    private static void EmitFormattableWrite(
        StringBuilder sb,
        string accessor,
        string format,
        string indent = ""
    )
    {
        sb.Append(indent);
        sb.Append("global::PicoJetson.TypeIo.WriteFormattable(ref jw, ");
        sb.Append(accessor);
        sb.Append(", \"");
        sb.Append(format);
        sb.AppendLine("\");");
    }

    private static void EmitDateTimeReadNoString(
        StringBuilder sb,
        PropertyInfo prop,
        string indent,
        string valueVar,
        string nameSuffix,
        string expected
    )
    {
        sb.Append(indent);
        sb.Append("System.DateTime ");
        sb.Append(valueVar);
        sb.AppendLine(";");
        sb.Append(indent);
        sb.Append("bool __dtOk");
        sb.Append(nameSuffix);
        sb.AppendLine(";");
        sb.Append(indent);
        sb.AppendLine(
            "if ((__rawBytes.Length == 27 || (__rawBytes.Length == 28 && __rawBytes[27] == (byte)'Z'))"
        );
        sb.Append(indent);
        sb.Append("    && System.Buffers.Text.Utf8Parser.TryParse(__rawBytes, out ");
        sb.Append(valueVar);
        sb.Append(", out var __dtUsed");
        sb.Append(nameSuffix);
        sb.AppendLine(", 'O')");
        sb.Append(indent);
        sb.Append("    && __dtUsed");
        sb.Append(nameSuffix);
        sb.AppendLine(" == __rawBytes.Length)");
        sb.Append(indent);
        sb.Append("    __dtOk");
        sb.Append(nameSuffix);
        sb.AppendLine(" = true;");
        sb.Append(indent);
        sb.Append("else if (__rawBytes.Length <= ");
        sb.Append(ScalarCharStackBuffer);
        sb.AppendLine(")");
        sb.Append(indent);
        sb.AppendLine("{");
        sb.Append(indent);
        sb.Append("    System.Span<char> __dtChars");
        sb.Append(nameSuffix);
        sb.Append(" = stackalloc char[");
        sb.Append(ScalarCharStackBuffer);
        sb.AppendLine("];");
        sb.Append(indent);
        sb.Append("    var __dtLen");
        sb.Append(nameSuffix);
        sb.Append(" = Encoding.UTF8.GetChars(__rawBytes, __dtChars");
        sb.Append(nameSuffix);
        sb.AppendLine(");");
        sb.Append(indent);
        sb.Append("    __dtOk");
        sb.Append(nameSuffix);
        sb.Append(" = System.DateTime.TryParse(__dtChars");
        sb.Append(nameSuffix);
        sb.Append("[..__dtLen");
        sb.Append(nameSuffix);
        sb.AppendLine(
            "], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out "
                + valueVar
                + ");"
        );
        sb.Append(indent);
        sb.AppendLine("}");
        sb.Append(indent);
        sb.AppendLine("else");
        sb.Append(indent);
        sb.Append("    __dtOk");
        sb.Append(nameSuffix);
        sb.AppendLine(
            " = System.DateTime.TryParse(Encoding.UTF8.GetString(__rawBytes), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out "
                + valueVar
                + ");"
        );
        sb.Append(indent);
        sb.Append("if (!__dtOk");
        sb.Append(nameSuffix);
        sb.AppendLine(")");
        EmitThrowInvalidValue(sb, indent + "    ", prop, expected);
    }

    /// <summary>
    /// Emits the <c>enum</c> read for a value already in <c>__rawBytes</c>: numeric fast arm,
    /// <c>Enum.TryParse</c> fallback, failure throw.
    /// <para>
    /// <b>The numeric arm's guard is the enum's own width, expressed without knowing it.</b> The
    /// parsed number is cast to the member's enum type and cast back; if anything was lost the
    /// reference parser would have rejected the value too, so the read declines and the fallback
    /// answers. A leading <c>'-'</c> always declines: for a <c>ulong</c>-backed enum the round trip
    /// through <c>long</c> would otherwise turn <c>-1</c> into <c>18446744073709551615</c>, which is
    /// exactly the value <c>Enum.TryParse</c> refuses.
    /// </para>
    /// <para>
    /// The fallback is <c>Enum.TryParse&lt;T&gt;(ReadOnlySpan&lt;char&gt;)</c>, the same parser the
    /// string overload runs — so a legacy string payload (<c>"Friday"</c>), a padded number
    /// (<c>" 5"</c>), a combined name (<c>"Friday,Tuesday"</c>) and an unknown name all answer what
    /// they answered before.
    /// </para>
    /// </summary>
    private static void EmitEnumReadNoString(
        StringBuilder sb,
        PropertyInfo prop,
        string indent,
        string enumType,
        string valueVar,
        string nameSuffix
    )
    {
        sb.Append(indent);
        sb.Append(enumType);
        sb.Append(' ');
        sb.Append(valueVar);
        sb.AppendLine(" = default;");
        sb.Append(indent);
        sb.Append("bool __enumOk");
        sb.Append(nameSuffix);
        sb.AppendLine(";");
        sb.Append(indent);
        sb.AppendLine("if (__rawBytes.Length > 0 && __rawBytes[0] != (byte)'-'");
        sb.Append(indent);
        // Explicit `long`: the TryParse<byte>/<sbyte>/<bool>/… overloads make `out var` ambiguous.
        sb.Append(
            "    && System.Buffers.Text.Utf8Parser.TryParse(__rawBytes, out long __enumNumber, out var __enumUsed"
        );
        sb.Append(nameSuffix);
        sb.AppendLine(")");
        sb.Append(indent);
        sb.Append("    && __enumUsed");
        sb.Append(nameSuffix);
        sb.AppendLine(" == __rawBytes.Length)");
        sb.Append(indent);
        sb.AppendLine("{");
        sb.Append(indent);
        sb.Append("    var __enumCast");
        sb.Append(nameSuffix);
        sb.Append(" = unchecked((");
        sb.Append(enumType);
        sb.Append(")__enumNumber)");
        sb.AppendLine(";");
        sb.Append(indent);
        sb.Append("    __enumOk");
        sb.Append(nameSuffix);
        sb.Append(" = unchecked((long)__enumCast");
        sb.Append(nameSuffix);
        sb.AppendLine(") == __enumNumber;");
        sb.Append(indent);
        sb.Append("    ");
        sb.Append(valueVar);
        sb.Append(" = __enumCast");
        sb.Append(nameSuffix);
        sb.AppendLine(";");
        sb.Append(indent);
        sb.AppendLine("}");
        sb.Append(indent);
        sb.AppendLine("else");
        sb.Append(indent);
        sb.Append("    __enumOk");
        sb.Append(nameSuffix);
        sb.AppendLine(" = false;");
        sb.Append(indent);
        sb.Append("if (!__enumOk");
        sb.Append(nameSuffix);
        sb.AppendLine(")");
        sb.Append(indent);
        sb.AppendLine("{");
        sb.Append(indent);
        sb.Append("    if (__rawBytes.Length <= ");
        sb.Append(ScalarCharStackBuffer);
        sb.AppendLine(")");
        sb.Append(indent);
        sb.AppendLine("    {");
        sb.Append(indent);
        sb.Append("        System.Span<char> __enumChars");
        sb.Append(nameSuffix);
        sb.Append(" = stackalloc char[");
        sb.Append(ScalarCharStackBuffer);
        sb.AppendLine("];");
        sb.Append(indent);
        sb.Append("        var __enumLen");
        sb.Append(nameSuffix);
        sb.Append(" = Encoding.UTF8.GetChars(__rawBytes, __enumChars");
        sb.Append(nameSuffix);
        sb.AppendLine(");");
        sb.Append(indent);
        sb.Append("        __enumOk");
        sb.Append(nameSuffix);
        sb.Append(" = System.Enum.TryParse<");
        sb.Append(enumType);
        sb.Append(">(__enumChars");
        sb.Append(nameSuffix);
        sb.Append("[..__enumLen");
        sb.Append(nameSuffix);
        sb.Append("], out ");
        sb.Append(valueVar);
        sb.AppendLine(");");
        sb.Append(indent);
        sb.AppendLine("    }");
        sb.Append(indent);
        sb.AppendLine("    else");
        sb.Append(indent);
        sb.Append("        __enumOk");
        sb.Append(nameSuffix);
        sb.Append(" = System.Enum.TryParse<");
        sb.Append(enumType);
        sb.Append(">(Encoding.UTF8.GetString(__rawBytes), out ");
        sb.Append(valueVar);
        sb.AppendLine(");");
        sb.Append(indent);
        sb.AppendLine("}");
        sb.Append(indent);
        sb.Append("if (!__enumOk");
        sb.Append(nameSuffix);
        sb.AppendLine(")");
        EmitThrowInvalidValue(sb, indent + "    ", prop, "an enum value");
    }

    // ── Deserializer emission ──

    private static void EmitDeserializer(StringBuilder sb, TypeInfo type)
    {
        var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;

        sb.Append("    file static class ");
        sb.Append(type.Name);
        sb.AppendLine("JsonDeserializer");
        sb.AppendLine("    {");
        sb.Append("        public static ");
        sb.Append(type.Name);
        sb.AppendLine(
            " Deserialize(ReadOnlySpan<byte> data, global::PicoSerDe.Core.SerOptions? options)"
        );
        sb.AppendLine("        {");
        sb.Append("            var __opts = (global::PicoJetson.JsonOptions?)options;\n");
        sb.AppendLine(
            "            var reader = new JsonReader(data, maxDepth: __opts?.MaxDepth ?? 256, options: __opts);"
        );
        sb.AppendLine("            try");
        sb.AppendLine("            {");

        if (type.Properties.Length > 0 && !type.IsValueType)
        {
            // ── Thin wrapper over the streaming delegate ──
            // The single property dispatch chain lives in {Type}Streaming.
            // DeserializeStreaming. Deserialize and DeserializeStreaming share
            // it, so behavior (UnmappedMemberHandling, top-level null, ...)
            // can never drift between the two paths again.
            sb.Append("                ");
            sb.Append(type.Name);
            sb.AppendLine("? result;");
            sb.Append("                var __status = ");
            sb.Append(type.Name);
            sb.AppendLine("Streaming.DeserializeStreaming(ref reader, null, out result);");
            sb.AppendLine("                if (__status == ReadStatus.EndOfInput)");
            sb.AppendLine(
                "                    throw __PicoJsonCold.Err(\"Expected a JSON object at offset \", reader.BytesConsumed, \" but found \" + reader.TokenType + \".\");"
            );
            sb.AppendLine("                if (__status == ReadStatus.NeedMoreData)");
            sb.AppendLine(
                "                    throw new System.FormatException(\"Unexpected end of stream while parsing.\");"
            );
            sb.AppendLine(
                "                if (reader.Read()) throw new System.FormatException($\"Unexpected data after the document at offset {reader.BytesConsumed}\");"
            );
            sb.AppendLine("                return result!;");
        }
        else
        {
            if (hasCtor)
            {
                // Declare temp variables for constructor parameters
                for (int ci = 0; ci < type.CtorParams.Length; ci++)
                {
                    var cp = type.CtorParams[ci];
                    // Use TypeFullName directly — MapTypeName with null type NREs
                    // for complex kinds (object, enum, list, dict).
                    var typeName = cp.TypeFullNameAnnotated ?? cp.TypeFullName;
                    var defaultVal = cp.TypeKind switch
                    {
                        "string" => "\"\"",
                        "int32"
                        or "int64"
                        or "int16"
                        or "uint16"
                        or "sbyte"
                        or "byte"
                        or "uint32"
                        or "uint64"
                        or "float64" => "0",
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
            if (!type.IsValueType)
                sb.AppendLine("            if (reader.TokenType == TokenType.Null) return null!;");
            sb.AppendLine(
                "            if (reader.TokenType != TokenType.ObjectStart) throw __PicoJsonCold.Err(\"Expected a JSON object at offset \", reader.BytesConsumed, \" but found \" + reader.TokenType + \".\");"
            );
            if (!hasCtor)
                foreach (var rp in type.Properties.Where(p => p.IsRequired))
                {
                    sb.Append("            bool __seen_");
                    sb.Append(rp.Name);
                    sb.AppendLine(" = false;");
                }
            sb.AppendLine("            " + CiLocal);
            sb.AppendLine(
                "            while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
            );
            sb.AppendLine("            {");
            sb.AppendLine("                var propNameSpan = reader.GetStringRaw();");
            sb.AppendLine("                reader.Read();");
            sb.AppendLine();

            EmitNameDispatch(
                sb,
                type.Properties,
                "propNameSpan",
                "                ",
                (prop, bodyIndent) =>
                {
                    if (hasCtor)
                    {
                        // Map JSON property to constructor parameter by name
                        EmitDeserializeCtorParam(sb, prop, type, bodyIndent);
                    }
                    else
                    {
                        EmitDeserializeProperty(sb, prop, "obj", bodyIndent);
                        if (prop.IsRequired)
                        {
                            sb.Append(bodyIndent);
                            sb.Append("__seen_");
                            sb.Append(prop.Name);
                            sb.AppendLine(" = true;");
                        }
                    }
                },
                caseInsensitiveVar: "__ci"
            );

            if (type.Properties.Length > 0)
            {
                sb.AppendLine(
                    "                else if (reader.Options?.UnmappedMemberHandling == PicoJetson.JsonUnmappedMemberHandling.Disallow)"
                );
                sb.AppendLine(
                    "                    throw __PicoJsonCold.Err(\"Unexpected property '\" + Encoding.UTF8.GetString(propNameSpan) + \"' at offset \", reader.BytesConsumed);"
                );
                sb.AppendLine("                else reader.TrySkip();");
            }
            else
                sb.AppendLine("                reader.TrySkip();");

            sb.AppendLine("            }");

            if (!hasCtor)
                foreach (var rp in type.Properties.Where(p => p.IsRequired))
                {
                    sb.Append("            if (!__seen_");
                    sb.Append(rp.Name);
                    sb.Append(") throw new System.FormatException(\"Missing required property '");
                    sb.Append(EscapeCSharpString(rp.JsonName));
                    sb.AppendLine("'.\");");
                }
            sb.AppendLine(
                "            if (reader.Read()) throw new System.FormatException($\"Unexpected data after the document at offset {reader.BytesConsumed}\");"
            );

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
        } // end else (value types / 0-prop types keep the direct path — no streaming delegate)
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
        string indent,
        string namePrefix = ""
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
        var target = $"__cp_{namePrefix}{matchIdx}";

        // Nullable ctor params must accept JSON null — the writer emits
        // "Prop":null for null-valued optional parameters, and persisted
        // event streams may contain them. Mirror EmitDeserializeProperty:
        // wrap the kind switch in a null-token guard.
        var guardIndent = indent;
        var wrapNullGuard = prop.IsNullable && prop.TypeKind != "object";
        if (wrapNullGuard)
        {
            sb.Append(guardIndent);
            sb.AppendLine("if (reader.TokenType == TokenType.Null)");
            sb.Append(guardIndent);
            sb.Append("    ");
            sb.Append(target);
            sb.AppendLine(" = null!;");
            sb.Append(guardIndent);
            sb.AppendLine("else");
            sb.Append(guardIndent);
            sb.AppendLine("{");
            indent = indent + "    ";
        }

        switch (cp.TypeKind)
        {
            case "string":
                EmitTokenCheck(sb, indent, prop, "String");
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
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
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
                sb.AppendLine("if (!reader.TryGetInt64(out var __v))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a 64-bit integer");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;

            case "int16":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __v))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __v = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = checked((short)__v);");
                break;
            case "uint16":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __v))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __v = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = checked((ushort)__v);");
                break;
            case "sbyte":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __v))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __v = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = checked((sbyte)__v);");
                break;
            case "byte":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __v))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __v = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = checked((byte)__v);");
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __v))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetUInt64(out var __ulv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __v = checked((long)__ulv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = checked((uint)__v);");
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetUInt64(out var __v))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a 64-bit unsigned integer");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __v))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a floating-point number");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = (float)__v;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __v))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a floating-point number");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetBool(out var __v))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a boolean");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __v;");
                break;
            case "enum":
                EmitStringOrNumberCheck(sb, indent, prop);
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                EmitEnumReadNoString(
                    sb,
                    prop,
                    indent,
                    PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(cp.TypeFullName),
                    "__ev",
                    "_" + prop.Name + "_ctor"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __ev;");
                break;
            case "datetime":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                EmitDateTimeReadNoString(
                    sb,
                    prop,
                    indent,
                    "__dt",
                    "_" + prop.Name + "_ctor",
                    "a DateTime"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __dt;");
                break;
            case "guid":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!System.Guid.TryParse(__rawBytes, out var __g))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a Guid");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __g;");
                break;
            case "decimal":
                EmitStringOrNumberCheck(sb, indent, prop);
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "if (!decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __dec))"
                );
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a decimal number");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __dec;");
                break;
            case "dateonly":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("if (!System.DateOnly.TryParse(__strValue, out var __dov))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a DateOnly");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __dov;");
                break;
            case "timeonly":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("if (!System.TimeOnly.TryParse(__strValue, out var __tov))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a TimeOnly");
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = __tov;");
                break;
            case "timespan":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("if (!System.TimeSpan.TryParse(__strValue, out var __tsv))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a TimeSpan");
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
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
            case "memory":
            case "readonlymemory":
            {
                var elemType = prop.ElementTypeNameAnnotated ?? prop.ElementTypeName ?? "object";
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
                EmitCollectionElementLoop(sb, prop, listVar, indent + "    ", 0);
                sb.Append(indent);
                sb.AppendLine("}");
                if (cp.TypeKind != "list")
                {
                    EmitCollectionConversion(
                        sb,
                        cp.TypeKind,
                        listVar,
                        target,
                        prop.ElementTypeNameAnnotated ?? prop.ElementTypeName ?? "object",
                        indent
                    );
                }
                else
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.Append(" = ");
                    sb.Append(listVar);
                    sb.AppendLine(";");
                }
                break;
            }
            case "dict":
            {
                var keyType = prop.KeyTypeName ?? "string";
                var valType = prop.ElementTypeNameAnnotated ?? prop.ElementTypeName ?? "object";
                var dictVar = $"__dict_{cp.Name}";
                sb.Append(indent);
                sb.Append(cp.TypeFullNameAnnotated ?? cp.TypeFullName);
                sb.Append(" ");
                sb.Append(dictVar);
                sb.AppendLine(" = new();");
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
                if (ExtendedScalarReadExpr(cp.TypeKind) is string __sx2)
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = " + __sx2 + ";");
                    break;
                }
                sb.Append(indent);
                sb.Append(target);
                sb.AppendLine(" = default!; // unsupported ctor param type: " + cp.TypeKind);
                break;
        }

        if (wrapNullGuard)
        {
            sb.Append(guardIndent);
            sb.AppendLine("}");
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
                EmitTokenCheck(sb, indent, prop, "String");
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
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
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
                sb.AppendLine("if (!reader.TryGetInt64(out var __longValue))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a 64-bit integer");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __longValue;");
                break;

            case "int16":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __intValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __intValue = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = checked((short)__intValue);");
                break;
            case "uint16":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __intValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __intValue = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = checked((ushort)__intValue);");
                break;
            case "sbyte":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __intValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __intValue = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = checked((sbyte)__intValue);");
                break;
            case "byte":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __intValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __intValue = checked((int)__lv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = checked((byte)__intValue);");
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __intValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetUInt64(out var __ulv))");
                EmitThrowInvalidValue(sb, indent + "        ", prop, "an integer");
                sb.Append(indent);
                sb.AppendLine("    __intValue = checked((long)__ulv);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = checked((uint)__intValue);");
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetUInt64(out var __ulongValue))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a 64-bit unsigned integer");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __ulongValue;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __doubleValue))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a floating-point number");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __doubleValue;");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __floatValue))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a floating-point number");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = (float)__floatValue;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetBool(out var __boolValue))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a boolean");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __boolValue;");
                break;
            case "datetime":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                if (prop.DateTimeFormat is null)
                {
                    EmitDateTimeReadNoString(
                        sb,
                        prop,
                        indent,
                        "__dt_" + prop.Name,
                        "_" + prop.Name,
                        "a DateTime"
                    );
                }
                else
                {
                    // A declared format is the caller asking for a different shape, so the
                    // round-trip fast path does not apply: keep TryParseExact, string and all.
                    sb.Append(indent);
                    sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                    sb.Append(indent);
                    sb.Append("if (!System.DateTime.TryParseExact(__strValue, \"");
                    sb.Append(prop.DateTimeFormat);
                    sb.Append(
                        "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt_"
                    );
                    sb.Append(prop.Name);
                    sb.AppendLine("))");
                    EmitThrowInvalidValue(sb, indent + "    ", prop, "a DateTime");
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
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!System.Guid.TryParse(__rawBytes, out var __guidValue))");
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a Guid");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __guidValue;");
                break;
            case "decimal":
                EmitStringOrNumberCheck(sb, indent, prop);
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "if (!decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue))"
                );
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a decimal number");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __decimalValue;");
                break;
            case "dateonly":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "if (!System.DateOnly.TryParse(__strValue, out var __dateOnlyValue))"
                );
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a DateOnly");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dateOnlyValue;");
                break;
            case "timeonly":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "if (!System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue))"
                );
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a TimeOnly");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __timeOnlyValue;");
                break;
            case "timespan":
                EmitTokenCheck(sb, indent, prop, "String");
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "if (!System.TimeSpan.TryParse(__strValue, out var __timeSpanValue))"
                );
                EmitThrowInvalidValue(sb, indent + "    ", prop, "a TimeSpan");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __timeSpanValue;");
                break;
            case "enum":
                EmitStringOrNumberCheck(sb, indent, prop);
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                EmitEnumReadNoString(
                    sb,
                    prop,
                    indent,
                    PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(prop.TypeFullName),
                    "__enumValue",
                    "_" + prop.Name
                );
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __enumValue;");
                break;
            case "list":
            case "array":
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
            case "memory":
            case "readonlymemory":
                if (prop.TypeKind == "list")
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.Append(".");
                    sb.Append(prop.Name);
                    sb.Append(" = new System.Collections.Generic.List<");
                    sb.Append(prop.ElementTypeNameAnnotated ?? prop.ElementTypeName);
                    sb.AppendLine(">();");
                }
                else
                {
                    sb.Append(indent);
                    sb.Append("var __list_");
                    sb.Append(prop.Name);
                    sb.Append(" = new System.Collections.Generic.List<");
                    sb.Append(prop.ElementTypeNameAnnotated ?? prop.ElementTypeName);
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
                        0,
                        prop.ElementTypeKind == "array"
                    );
                }
                else
                {
                    // One emitter for every element kind: the loop shape lives in
                    // EmitCollectionElementLoop, including the E-3 fused read for scalar elements.
                    // The former int32/int64/boolean branch was byte-identical to this one.
                    EmitCollectionElementLoop(sb, prop, listAcc, indent + "    ", nestLevel);
                }

                sb.Append(indent);
                sb.AppendLine("}");
                if (prop.TypeKind != "list")
                {
                    EmitCollectionConversion(
                        sb,
                        prop.TypeKind,
                        $"__list_{prop.Name}",
                        $"{target}.{prop.Name}",
                        prop.ElementTypeNameAnnotated ?? prop.ElementTypeName ?? "object",
                        indent
                    );
                }
                break;
            case "dict":
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                // Target-typed new (not `new {TypeFullName}()`) — TypeFullName drops
                // nullable annotations, so an explicit `Dictionary<string, object>`
                // would trigger CS8619 against a Dictionary<string, object?> property.
                sb.AppendLine(" ??= new();");
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
            default:
                if (ExtendedScalarReadExpr(prop.TypeKind) is string __sx3)
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.Append(".");
                    sb.Append(prop.Name);
                    sb.AppendLine(" = " + __sx3 + ";");
                }
                break;
        }
    }

    /// <summary>
    /// Emits the property-name dispatch of a deserializer. <paramref name="nameVar"/> is
    /// the already-read property name span; <paramref name="emitBody"/> writes one
    /// property's read at the indent it is handed.
    /// <para>
    /// Shape (P0-1): a length-bucketed <b>exact-match</b> fast path over the compiled
    /// UTF-8 names resolves a match index, so binding one name costs a <c>switch</c> on
    /// the length plus one <c>SequenceEqual</c> per same-length candidate — instead of the
    /// old chain of case-insensitive byte loops, which is O(n²) comparisons per object.
    /// When nothing matched exactly <b>and</b> the reader is case-insensitive (the
    /// default) the pre-P0-1 chain runs as the fallback; that fallback is what still binds
    /// <c>{"name":…}</c> to <c>Name</c>. With <c>PropertyNameCaseInsensitive = false</c>
    /// the fallback never runs — exactly like the old chain, which then matched no
    /// differently-cased name either.
    /// </para>
    /// <para>
    /// The fallback is reached only when no compiled name matched exactly, which would let
    /// an exact match outrank an EARLIER case-insensitive one. For a type whose JSON names
    /// differ only by case the fast path is therefore not emitted at all and the old chain
    /// stands verbatim (same declaration-order precedence). That relation is
    /// <c>OrdinalIgnoreCase</c>, a superset of the ASCII-only fold
    /// <c>TextHelpers.Eq</c> applies, so the guard can only be too strict, never too loose.
    /// </para>
    /// </summary>
    private static void EmitNameDispatch(
        StringBuilder sb,
        IReadOnlyList<PropertyInfo> props,
        string nameVar,
        string indent,
        Action<PropertyInfo, string> emitBody,
        string indexVar = "__mi",
        string? caseInsensitiveVar = null
    )
    {
        if (props.Count == 0)
            return;

        if (HasCaseInsensitiveNameCollision(props))
        {
            EmitNameDispatchLegacy(sb, props, nameVar, indent, emitBody);
            return;
        }

        var inner = indent + "    ";
        var inner2 = indent + "        ";

        // 1. Exact-match fast path: bucketed by UTF-8 name length, so the span compare only ever
        //    runs against names that can possibly be equal.
        sb.Append(indent);
        sb.Append("int ");
        sb.Append(indexVar);
        sb.AppendLine(" = -1;");
        EmitLengthBucketSwitch(sb, NameCandidates(props), nameVar, indexVar, indent);

        // 2. Fallback: the pre-P0-1 chain, now reached only by a name that differs from a
        //    compiled one by case, and only while case-insensitivity is on.
        // The option is a reader-wide setting, so a caller that owns a property loop evaluates it
        // once above that loop and hands the local in (json-gen3 #4); every other caller keeps the
        // inline expression, which is the same value.
        sb.Append(indent);
        sb.Append("if (");
        sb.Append(indexVar);
        sb.Append(" < 0 && ");
        sb.Append(
            caseInsensitiveVar is null
                ? "(reader.Options?.PropertyNameCaseInsensitive ?? true)"
                : caseInsensitiveVar
        );
        sb.AppendLine(")");
        sb.Append(indent);
        sb.AppendLine("{");
        for (var i = 0; i < props.Count; i++)
        {
            sb.Append(inner);
            sb.Append(i == 0 ? "if (" : "else if (");
            sb.Append("TextHelpers.Eq(");
            sb.Append(nameVar);
            sb.Append(", \"");
            sb.Append(EscapeCSharpString(props[i].JsonName));
            sb.AppendLine("\"u8))");
            sb.Append(inner);
            sb.Append("    ");
            sb.Append(indexVar);
            sb.Append(" = ");
            sb.Append(i);
            sb.AppendLine(";");
        }
        sb.Append(indent);
        sb.AppendLine("}");

        // 3. One body per property, dispatched on the resolved index.
        for (var i = 0; i < props.Count; i++)
        {
            sb.Append(indent);
            sb.Append(i == 0 ? "if (" : "else if (");
            sb.Append(indexVar);
            sb.Append(" == ");
            sb.Append(i);
            sb.AppendLine(")");
            sb.Append(indent);
            sb.AppendLine("{");
            emitBody(props[i], inner);
            sb.Append(indent);
            sb.AppendLine("}");
        }
    }

    /// <summary>
    /// The pre-P0-1 dispatch: one case-insensitive <c>TextHelpers.Eq</c> per property,
    /// chained in declaration order. Kept for the types <see cref="EmitNameDispatch"/>
    /// cannot fast-path (case-only name collisions).
    /// </summary>
    private static void EmitNameDispatchLegacy(
        StringBuilder sb,
        IReadOnlyList<PropertyInfo> props,
        string nameVar,
        string indent,
        Action<PropertyInfo, string> emitBody
    )
    {
        for (var i = 0; i < props.Count; i++)
        {
            sb.Append(indent);
            sb.Append(i == 0 ? "if" : "else if");
            sb.Append(" (TextHelpers.Eq(");
            sb.Append(nameVar);
            sb.Append(", \"");
            sb.Append(EscapeCSharpString(props[i].JsonName));
            sb.AppendLine("\"u8, !(reader.Options?.PropertyNameCaseInsensitive ?? true)))");
            sb.Append(indent);
            sb.AppendLine("{");
            emitBody(props[i], indent + "    ");
            sb.Append(indent);
            sb.AppendLine("}");
        }
    }

    /// <summary>
    /// True when two compiled names can match the same document name through the
    /// case-insensitive relation <c>TextHelpers.Eq</c> uses (ASCII a-z folding).
    /// <c>OrdinalIgnoreCase</c> is a superset of that fold, so this guard is conservative:
    /// it can only disable the fast path, never widen a match.
    /// </summary>
    private static bool HasCaseInsensitiveNameCollision(IReadOnlyList<PropertyInfo> props)
    {
        for (var i = 0; i < props.Count; i++)
        for (var j = i + 1; j < props.Count; j++)
            if (
                string.Equals(
                    props[i].JsonName,
                    props[j].JsonName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return true;
        return false;
    }

    /// <summary>
    /// One entry per compiled property: its declaration index (the value the dispatch assigns), the
    /// property, and the UTF-8 <b>byte</b> length of its JSON name. Byte length, not char count: the
    /// dispatch indexes the span, and a non-ASCII name is longer in bytes than in chars.
    /// </summary>
    private static List<(int Index, PropertyInfo Prop, int Length)> NameCandidates(
        IReadOnlyList<PropertyInfo> props
    )
    {
        var list = new List<(int Index, PropertyInfo Prop, int Length)>(props.Count);
        for (var i = 0; i < props.Count; i++)
            list.Add((i, props[i], Encoding.UTF8.GetByteCount(props[i].JsonName)));
        return list;
    }

    /// <summary>
    /// Emits the per-length switch for the names a three-byte key cannot resolve (one and two
    /// bytes), shaped exactly as it was before the packed key existed: a bucket per length, one more
    /// level on the first byte when a bucket holds several names, then a <c>SequenceEqual</c> chain.
    /// </summary>
    private static void EmitLengthBucketSwitch(
        StringBuilder sb,
        List<(int Index, PropertyInfo Prop, int Length)> candidates,
        string nameVar,
        string indexVar,
        string indent
    )
    {
        var caseIndent = indent + "    ";
        var chainIndent = indent + "        ";
        sb.Append(indent);
        sb.Append("switch (");
        sb.Append(nameVar);
        sb.AppendLine(".Length)");
        sb.Append(indent);
        sb.AppendLine("{");
        foreach (var bucket in LengthBuckets(candidates))
        {
            sb.Append(caseIndent);
            sb.Append("case ");
            sb.Append(bucket.Key);
            sb.AppendLine(":");

            // Several names of the same length are the common case, so a plain chain inside the
            // bucket would stay O(k). One more level \u2014 the first byte \u2014 makes each candidate set
            // smaller, and for one-character names (A\u2026J) the first byte IS the whole name.
            var byFirstByte =
                bucket.Key == 0
                    ? null
                    : bucket.Value.GroupBy(c => FirstUtf8Byte(c.Prop.JsonName)).ToList();
            if (byFirstByte is null || byFirstByte.Count == 1)
            {
                EmitExactChain(sb, ExactChainEntries(bucket.Value), nameVar, indexVar, chainIndent);
            }
            else
            {
                sb.Append(chainIndent);
                sb.Append("switch (");
                sb.Append(nameVar);
                sb.AppendLine("[0])");
                sb.Append(chainIndent);
                sb.AppendLine("{");
                foreach (var group in byFirstByte)
                {
                    sb.Append(chainIndent);
                    sb.Append("    case ");
                    sb.Append(FirstByteLiteral(group.Key));
                    sb.AppendLine(":");
                    EmitExactChain(
                        sb,
                        ExactChainEntries(group.ToList()),
                        nameVar,
                        indexVar,
                        chainIndent + "        "
                    );
                    sb.Append(chainIndent);
                    sb.AppendLine("        break;");
                }
                sb.Append(chainIndent);
                sb.AppendLine("}");
            }

            sb.Append(chainIndent);
            sb.AppendLine("break;");
        }
        sb.Append(indent);
        sb.AppendLine("}");
    }

    /// <summary>
    /// Groups candidates by UTF-8 name length \u2014 ascending by length, declaration-ordered inside a
    /// bucket, i.e. a deterministic order so the generated file and the checked-in baseline stay
    /// stable.
    /// </summary>
    private static IEnumerable<
        KeyValuePair<int, List<(int Index, PropertyInfo Prop, int Length)>>
    > LengthBuckets(List<(int Index, PropertyInfo Prop, int Length)> candidates)
    {
        var buckets = new Dictionary<int, List<(int Index, PropertyInfo Prop, int Length)>>();
        foreach (var candidate in candidates)
        {
            if (!buckets.TryGetValue(candidate.Length, out var bucket))
                buckets[candidate.Length] = bucket =
                    new List<(int Index, PropertyInfo Prop, int Length)>();
            bucket.Add(candidate);
        }
        foreach (var length in buckets.Keys.OrderBy(k => k))
            yield return new KeyValuePair<int, List<(int Index, PropertyInfo Prop, int Length)>>(
                length,
                buckets[length]
            );
    }

    private static IEnumerable<(int Index, PropertyInfo Prop)> ExactChainEntries(
        List<(int Index, PropertyInfo Prop, int Length)> candidates
    ) => candidates.Select(c => (c.Index, c.Prop));

    /// <summary>
    /// Emits one exact-match chain over <paramref name="candidates"/>: the first test is an
    /// <c>if</c>, the rest are <c>else if</c>, each assigning the match index.
    /// </summary>
    private static void EmitExactChain(
        StringBuilder sb,
        IEnumerable<(int Index, PropertyInfo Prop)> candidates,
        string nameVar,
        string indexVar,
        string indent
    )
    {
        var first = true;
        foreach (var (index, prop) in candidates)
        {
            sb.Append(indent);
            sb.Append(first ? "if (" : "else if (");
            sb.Append(nameVar);
            sb.Append(".SequenceEqual(\"");
            sb.Append(EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\"u8))");
            sb.Append(indent);
            sb.Append("    ");
            sb.Append(indexVar);
            sb.Append(" = ");
            sb.Append(index);
            sb.AppendLine(";");
            first = false;
        }
    }

    /// <summary>
    /// The first UTF-8 byte of a compiled name — the byte the generated
    /// <c>switch (propNameSpan[0])</c> actually tests. Deriving it from the string's
    /// first CHAR would disagree for a non-ASCII name (U+00FC is the byte 0xFC, while its
    /// UTF-8 lead byte is 0xC3), and the exact fast path would silently never match it.
    /// </summary>
    private static byte FirstUtf8Byte(string name) => Encoding.UTF8.GetBytes(name)[0];

    /// <summary>
    /// A <c>switch</c> label for one first-byte bucket: <c>'F'</c> when the byte is
    /// printable ASCII (it reads as the property name it comes from), hex otherwise.
    /// </summary>
    private static string FirstByteLiteral(byte b) =>
        b is >= 0x20 and <= 0x7E && b != (byte)'\'' && b != (byte)'\\'
            ? $"(byte)'{(char)b}'"
            : $"0x{b:X2}";

    /// <summary>
    /// Emits the dispatch described by <see cref="EmitNameDispatch"/> for a nested object
    /// deserializer body that assigns straight into <paramref name="target"/>.
    /// </summary>
    private static void EmitNestedDeserialize(
        StringBuilder sb,
        ImmutableArray<PropertyInfo> props,
        string target,
        string indent,
        int nestLevel = 0,
        string propVarName = "__nestedPropName"
    )
    {
        EmitNameDispatch(
            sb,
            props,
            propVarName,
            indent,
            (np, bodyIndent) => EmitDeserializeProperty(sb, np, target, bodyIndent, nestLevel)
        );
    }

    /// <summary>Emits value write for a nested list inner element (simplified — emits jw.WriteNumber).</summary>
    private static void EmitNestedListElement(StringBuilder sb, string indent, string itemVar)
    {
        sb.Append(indent);
        sb.Append("jw.WriteNumber(");
        sb.Append(itemVar);
        sb.AppendLine(");");
    }

    /// <summary>
    /// The element kinds T15 reads with <b>one fused call</b> instead of "<c>Read()</c>, then
    /// <c>TryGetX()</c>" (spec §3.4.1 rule <b>E-3</b>). Only <c>int32</c> qualifies: spec §3.3 marks
    /// <c>TryReadInt32</c> as JSON's single override and leaves the other three fused reads as one
    /// forwarding line each, so emitting those would rewrite every golden file for no gain.
    /// </summary>
    static bool IsFusedElementKind(string? kind) => kind == "int32";

    /// <summary>
    /// Emits one collection read loop: rule <b>E-2</b> (spec §3.4.1) — JSON carries no element count
    /// (spec §3.3: the reader finds the closing bracket by scanning bytes and counts nothing), so there
    /// is no counted loop (<b>E-1</b>) to emit — with rule <b>E-3</b> applied to the element read where
    /// the element is one JSON fuses (<see cref="IsFusedElementKind"/>).
    /// <para>
    /// <b>Everything else keeps the reference shape verbatim</b>, which is what holds the golden diff to
    /// the <c>int</c> element sites: a non-fused element still reads
    /// <c>while (reader.Read() &amp;&amp; reader.TokenType != TokenType.ArrayEnd)</c> and then
    /// <c>TryGetX()</c>.
    /// </para>
    /// <para>
    /// <b>The call is made on the concrete reader type.</b> <c>reader</c> is the generated
    /// <see cref="JsonReader"/> local, never a token-reader interface: spec §3.4.3 bans both interface
    /// names from the emitted text, and <c>JsonFusedElementEmissionTests</c> greps this file for them —
    /// so they may not be written here either, not even in prose.
    /// </para>
    /// </summary>
    static void EmitCollectionElementLoop(
        StringBuilder sb,
        PropertyInfo element,
        string addTarget,
        string ind,
        int nestLevel
    )
    {
        if (IsFusedElementKind(element.ElementTypeKind))
        {
            EmitFusedInt32ElementLoop(
                sb,
                addTarget,
                "__elementValue",
                element.ElementIsNullableValue,
                ind
            );
            return;
        }

        sb.Append(ind);
        sb.AppendLine("while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)");
        sb.Append(ind);
        sb.AppendLine("{");
        EmitDeserializeElementAdd(sb, element, addTarget, ind + "    ", nestLevel);
        sb.Append(ind);
        sb.AppendLine("}");
    }

    /// <summary>
    /// Rule <b>E-3</b> for an <c>int</c> element (spec §3.4.1): <c>reader.TryReadInt32(out v)</c> advances
    /// the reader <b>and</b> decodes the token in one pass, replacing the reference pair whose
    /// <c>Read()</c> classifies the same bytes (int, then long, then ulong) before <c>TryGetInt32()</c>
    /// parses them once more.
    /// <para>
    /// <b>Why the failure path is longer than "swap the two calls".</b> JSON has no count (spec §3.3), so
    /// the loop cannot assume an element is there, and <c>false</c> means one of three things, each with a
    /// different answer in the loop being replaced:
    /// </para>
    /// <list type="number">
    /// <item><description><b>The end token</b> (<c>TokenType.ArrayEnd</c>) — that is exactly what the
    /// reference <c>while</c> condition tested, so break.</description></item>
    /// <item><description><b>A chunk boundary</b> (<c>NeedsMoreData</c>, which only <c>Read()</c> sets and
    /// which a token it produced always clears) — the reference loop's <c>reader.Read()</c> was false and
    /// broke out, so this loop breaks out identically and the caller keeps its rewind-and-retry.</description></item>
    /// <item><description><b>The end of the data</b> — no token at all. <c>Read()</c> returns the pooled
    /// buffer and clears the value span when it fails at the end of a final block, so the token still
    /// current is the <i>previous element</i>: breaking is what the reference loop did, while consuming it
    /// again would append the last element twice. It is recognised by an empty
    /// <c>ValueSpan</c> <b>plus</b> a type an int loop can leave behind (<c>Int32</c>/<c>Int64</c>/
    /// <c>ArrayStart</c>): a fresh <c>Int32</c> can never reach this branch (the fused read answers
    /// <c>true</c> for every literal <c>Read()</c> would call <c>Int32</c>), a fresh <c>Int64</c> carries
    /// its literal, and a fresh <c>ArrayStart</c> carries the superseded span.</description></item>
    /// </list>
    /// <para>
    /// <b>Three is a conservative test, not a proof.</b> It can only misfire on a malformed payload whose
    /// collection is preceded by an <i>empty</i> JSON property name (the superseded span of a fresh
    /// <c>ArrayStart</c> is then empty too), where this loop exits instead of throwing; every well-formed
    /// document and every malformed one without that shape takes the reference answer. The alternative —
    /// asking the reader whether its last read produced a token — would be a new member, and spec §2.2 N2
    /// keeps <c>Read()</c>'s observable surface closed.
    /// </para>
    /// <para>
    /// <b>Everything else keeps the reference body.</b> A JSON <c>null</c> element is stored when the
    /// declared element is nullable and is otherwise the same format error; a number that needs
    /// <c>long</c> is widened and narrowed with <c>checked</c> (the reference never truncates); any other
    /// token type throws the identical message at the identical offset.
    /// </para>
    /// </summary>
    static void EmitFusedInt32ElementLoop(
        StringBuilder sb,
        string addTarget,
        string valueVar,
        bool nullableElement,
        string ind
    )
    {
        var body = ind + "    ";
        var cold = ind + "        ";

        sb.Append(ind);
        sb.AppendLine("while (true)");
        sb.Append(ind);
        sb.AppendLine("{");
        sb.Append(body);
        sb.Append("if (reader.TryReadInt32(out var ");
        sb.Append(valueVar);
        sb.AppendLine("))");
        sb.Append(body);
        sb.AppendLine("{");
        sb.Append(cold);
        sb.Append(addTarget);
        sb.Append('.');
        sb.AppendLine("Add(" + valueVar + ");");
        sb.Append(cold);
        sb.AppendLine("continue;");
        sb.Append(body);
        sb.AppendLine("}");
        sb.Append(body);
        sb.AppendLine("// rule E-3 (spec §3.4.1): the three ways the fused read can answer false");
        sb.Append(body);
        sb.AppendLine("if (reader.TokenType == TokenType.ArrayEnd) break;");
        sb.Append(body);
        sb.AppendLine("if (reader.NeedsMoreData) break;");
        sb.Append(body);
        sb.AppendLine("if (reader.ValueSpan.IsEmpty");
        sb.Append(body);
        sb.AppendLine(
            "    && reader.TokenType is TokenType.Int32 or TokenType.Int64 or TokenType.ArrayStart) break;"
        );
        if (nullableElement)
        {
            sb.Append(body);
            sb.AppendLine("if (reader.TokenType == TokenType.Null)");
            sb.Append(body);
            sb.AppendLine("{");
            sb.Append(cold);
            sb.Append(addTarget);
            sb.AppendLine(".Add(null!);");
            sb.Append(cold);
            sb.AppendLine("continue;");
            sb.Append(body);
            sb.AppendLine("}");
        }
        sb.Append(body);
        sb.AppendLine("if (!reader.TryGetInt64(out var __lev))");
        sb.Append(cold);
        sb.AppendLine(
            "throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
        );
        sb.Append(body);
        sb.Append(addTarget);
        sb.AppendLine(".Add(checked((int)__lev));");
        sb.Append(ind);
        sb.AppendLine("}");
    }

    /// <summary>
    /// Reads one collection element. Nullable elements accept a JSON null
    /// element (stored as null) before the typed read runs.
    /// </summary>
    private static void EmitDeserializeElementAdd(
        StringBuilder sb,
        PropertyInfo prop,
        string listVar,
        string indent,
        int nestLevel
    )
    {
        if (
            prop.ElementIsNullableValue
            || prop.ElementTypeKind is "object" or "dict"
            || (prop.ElementIsNullableReference && prop.ElementTypeKind != "string")
        )
        {
            sb.Append(indent);
            sb.AppendLine("if (reader.TokenType == TokenType.Null)");
            sb.Append(indent);
            sb.Append(listVar);
            sb.AppendLine(".Add(null!);");
            sb.Append(indent);
            sb.AppendLine("else {");
            EmitDeserializeElementAddCore(sb, prop, listVar, indent + "    ", nestLevel);
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }
        EmitDeserializeElementAddCore(sb, prop, listVar, indent, nestLevel);
    }

    private static void EmitDeserializeElementAddCore(
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
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(listVar);
                sb.AppendLine(".Add(null!);");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (reader.TokenType != TokenType.String)");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected a string element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(listVar);
                sb.AppendLine(".Add(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                sb.Append(indent);
                sb.AppendLine("}");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
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
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;

            case "int16":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(checked((short)__elementValue));");
                break;
            case "uint16":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(checked((ushort)__elementValue));");
                break;
            case "sbyte":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(checked((sbyte)__elementValue));");
                break;
            case "byte":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(checked((byte)__elementValue));");
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetUInt64(out var __ulev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((long)__ulev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(checked((uint)__elementValue));");
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit unsigned integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetUInt64(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit unsigned integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add((float)__elementValue);");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a boolean element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetBool(out var __elementValue))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a boolean element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType != TokenType.String)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a DateTime element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "if (!System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dateTimeValue))"
                );
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid DateTime element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__dateTimeValue);");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType != TokenType.String)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a Guid element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!System.Guid.TryParse(__rawBytes, out var __guidValue))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid Guid element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__guidValue);");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine(
                    "if (reader.TokenType is not (TokenType.String or TokenType.Int32 or TokenType.Int64 or TokenType.Float32 or TokenType.Float64))"
                );
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a decimal element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "if (!decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue))"
                );
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid decimal element at offset {reader.BytesConsumed}\");"
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
                sb.Append(PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(prop.ElementTypeName!));
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
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
            case "memory":
            case "readonlymemory":
            {
                // S8 on the read side: the item is an array, the DECLARED element is a
                // collection — so read into a temp list and let the shared conversion
                // (GenInfrastructure.EmitCollectionFromList) build the real type. A
                // hard-coded `new HashSet<...>` per generator is exactly the duplication
                // that left four generators with no case at all.
                var inner = prop.NestedProperties.Length > 0 ? prop.NestedProperties[0] : prop;
                var elemType = inner.ElementTypeNameAnnotated ?? inner.ElementTypeName ?? "object";
                var setTmp = $"__setTmp{nestLevel}";
                var setVal = $"__setVal{nestLevel}";
                sb.Append(indent);
                sb.Append("var ");
                sb.Append(setTmp);
                sb.Append(" = new System.Collections.Generic.List<");
                sb.Append(elemType);
                sb.AppendLine(">(8);");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ArrayStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                EmitCollectionElementLoop(sb, inner, setTmp, indent + "    ", nestLevel + 1);
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(prop.ElementTypeNameAnnotated ?? prop.ElementTypeName ?? "object");
                sb.Append(' ');
                sb.Append(setVal);
                sb.AppendLine(" = default!;");
                PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
                    sb,
                    prop.ElementTypeKind!,
                    setTmp,
                    setVal,
                    elemType,
                    indent
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.Append(".Add(");
                sb.Append(setVal);
                sb.AppendLine(");");
                break;
            }
            default:
                if (ExtendedScalarReadExpr(prop.ElementTypeKind!) is string __sx4)
                {
                    sb.Append(indent);
                    sb.Append(listVar);
                    sb.AppendLine(".Add(" + __sx4 + ");");
                    break;
                }
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
                sb.AppendLine(
                    "if (!System.DateOnly.TryParse(__strValue, out var __dateOnlyValue))"
                );
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid DateOnly element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__dateOnlyValue);");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine(
                    "if (!System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue))"
                );
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid TimeOnly element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__timeOnlyValue);");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine(
                    "if (!System.TimeSpan.TryParse(__strValue, out var __timeSpanValue))"
                );
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid TimeSpan element at offset {reader.BytesConsumed}\");"
                );
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
                sb.AppendLine("if (!int.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid integer dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!long.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid 64-bit dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;

            case "int16":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!short.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid 16-bit dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "uint16":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!ushort.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid 16-bit dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "sbyte":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!sbyte.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid 8-bit dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "byte":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!byte.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid 8-bit dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!uint.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid 32-bit dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!ulong.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid 64-bit dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("if (!System.Guid.TryParse(__rawBytes, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid Guid dictionary key at offset {reader.BytesConsumed}\");"
                );
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("if (!System.Enum.TryParse<");
                sb.Append(PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(prop.KeyTypeName!));
                sb.AppendLine(">(__strValue, out var __dictKey))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Invalid enum dictionary key at offset {reader.BytesConsumed}\");"
                );
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

    /// <summary>
    /// Emits one dictionary value. Nullable value-type elements (Dictionary&lt;string,int?&gt;)
    /// get a null check first: the value readers below are all Try*-based, so a JSON
    /// null used to be stored as 0 / Guid.Empty / DateTime.MinValue instead of null.
    /// </summary>
    private static void EmitDeserializeElementAssign(
        StringBuilder sb,
        PropertyInfo prop,
        string dictVar,
        string keyVar,
        string indent,
        int nestLevel
    )
    {
        if (prop.ElementIsNullableValue)
        {
            sb.Append(indent);
            sb.AppendLine("if (reader.TokenType == TokenType.Null)");
            sb.Append(indent);
            sb.Append(dictVar);
            sb.Append("[");
            sb.Append(keyVar);
            sb.AppendLine("] = null;");
            sb.Append(indent);
            sb.AppendLine("else");
            sb.Append(indent);
            sb.AppendLine("{");
            EmitDeserializeElementAssignCore(sb, prop, dictVar, keyVar, indent + "    ", nestLevel);
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }
        EmitDeserializeElementAssignCore(sb, prop, dictVar, keyVar, indent, nestLevel);
    }

    private static void EmitDeserializeElementAssignCore(
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
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = null!;");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                sb.Append(indent);
                sb.AppendLine("}");
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

            case "int16":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue)) {");
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
                sb.AppendLine("] = checked((short)__elementValue);");
                break;
            case "uint16":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue)) {");
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
                sb.AppendLine("] = checked((ushort)__elementValue);");
                break;
            case "sbyte":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue)) {");
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
                sb.AppendLine("] = checked((sbyte)__elementValue);");
                break;
            case "byte":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __elementValue)) {");
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
                sb.AppendLine("] = checked((byte)__elementValue);");
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __elementValue)) {");
                sb.Append(indent);
                sb.AppendLine("    reader.TryGetUInt64(out var __ulev);");
                sb.Append(indent);
                sb.AppendLine("    __elementValue = checked((long)__ulev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = checked((uint)__elementValue);");
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetUInt64(out var __elementValue);");
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
                sb.Append(PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(prop.ElementTypeName!));
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
            case "list":
            case "array":
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
            case "memory":
            case "readonlymemory":
            {
                // S10: the dict VALUE is a collection. Read it into a temp list of the
                // value's ELEMENT type (NestedProperties[0] describes that element, one level
                // below the value) and let the shared EmitCollectionFromList turn it back
                // into the declared kind — the same pair the member arm above uses, so a
                // HashSet value and a List value cannot drift apart.
                var elemDesc = prop.NestedProperties[0];
                var elemType =
                    elemDesc.ElementTypeNameAnnotated ?? elemDesc.ElementTypeName ?? "object";
                var tmpVar = "__dictList_" + prop.Name;
                sb.Append(indent);
                sb.Append("var ");
                sb.Append(tmpVar);
                sb.Append(" = new System.Collections.Generic.List<");
                sb.Append(elemType);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ArrayStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                if (PicoSerDe.Gen.GenInfrastructure.IsSequenceKind(elemDesc.ElementTypeKind))
                {
                    // The value's element is itself a collection: reuse the nested-list reader
                    // (it declares one List per item and adds it to the accumulator).
                    EmitNestedListDeserialize(
                        sb,
                        elemDesc.NestedProperties[0],
                        tmpVar,
                        prop.Name,
                        indent + "    ",
                        nestLevel,
                        elemDesc.ElementTypeKind == "array"
                    );
                }
                else
                {
                    EmitCollectionElementLoop(sb, elemDesc, tmpVar, indent + "    ", nestLevel);
                }
                sb.Append(indent);
                sb.AppendLine("}");
                PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
                    sb,
                    prop.ElementTypeKind!,
                    tmpVar,
                    dictVar + "[" + keyVar + "]",
                    elemType,
                    indent
                );
                break;
            }
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
                if (ExtendedScalarReadExpr(prop.ElementTypeKind!) is string __sx5)
                {
                    sb.Append(indent);
                    sb.Append(dictVar);
                    sb.Append("[");
                    sb.Append(keyVar);
                    sb.AppendLine("] = " + __sx5 + ";");
                    break;
                }
                sb.Append(indent);
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = null;");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                sb.Append(indent);
                sb.AppendLine("}");
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
            "    internal static ReadStatus DeserializeStreaming(ref JsonReader reader, "
                + type.Name
                + "? partial, out "
                + type.Name
                + "? result)"
        );
        sb.AppendLine("    {");
        sb.Append("        result = partial ?? ");
        if (!hasCtor)
        {
            var reqProps2 = type.Properties.Where(p => p.IsRequired).ToArray();
            if (reqProps2.Length > 0)
            {
                sb.Append("new ");
                sb.Append(type.Name);
                sb.AppendLine(" {");
                foreach (var rp in reqProps2)
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
                sb.Append("new ");
                sb.Append(type.Name);
                sb.AppendLine("();");
            }
        }
        else
        {
            sb.AppendLine("default;");
        }
        sb.AppendLine();

        // Constructor-param temps and required-property flags must survive
        // chunk resumes, so they live in the reader's StreamState (exported
        // across chunks) instead of per-call locals.
        var streamReqProps = type.Properties.Where(p => p.IsRequired).ToArray();
        bool needsPersistedState = hasCtor || streamReqProps.Length > 0;
        if (needsPersistedState)
        {
            sb.Append("        var __st = (");
            sb.Append(type.Name);
            sb.Append("StreamingState)(reader.StreamState ??= new ");
            sb.Append(type.Name);
            sb.AppendLine("StreamingState());");
            if (hasCtor)
            {
                for (int ci = 0; ci < type.CtorParams.Length; ci++)
                {
                    sb.Append("        ref var __cp_");
                    sb.Append(ci);
                    sb.Append(" = ref __st.__cp_");
                    sb.Append(ci);
                    sb.AppendLine(";");
                }
            }
            if (!hasCtor)
                foreach (var rp in streamReqProps)
                {
                    sb.Append("        ref bool __seen_");
                    sb.Append(rp.Name);
                    sb.Append(" = ref __st.__seen_");
                    sb.Append(rp.Name);
                    sb.AppendLine(";");
                }
        }
        sb.AppendLine();
        sb.AppendLine("        // ReadStart runs on the first invocation only.");
        sb.AppendLine("        if (!reader.IsResumed)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : reader.TokenType != TokenType.None ? ReadStatus.Success : ReadStatus.EndOfInput;"
        );
        if (!type.IsValueType)
            sb.AppendLine(
                "            if (reader.TokenType == TokenType.Null) { result = null; return ReadStatus.Success; }"
            );
        sb.AppendLine(
            "            if (reader.TokenType != TokenType.ObjectStart) throw __PicoJsonCold.Err(\"Expected a JSON object at offset \", reader.BytesConsumed, \" but found \" + reader.TokenType + \".\");"
        );
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        " + CiLocal);
        sb.AppendLine("        while (true)");
        sb.AppendLine("        {");
        // Snapshot before the property name: any incomplete inner read
        // rewinds here so the whole property is re-read on resume.
        sb.AppendLine("            long __snap = reader.BytesConsumed;");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
        );
        sb.AppendLine("            if (reader.TokenType != TokenType.PropertyName) break;");
        sb.AppendLine("            var propNameSpan = reader.GetStringRaw();");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
        );
        EmitNameDispatch(
            sb,
            type.Properties,
            "propNameSpan",
            "            ",
            (prop, bodyIndent) =>
            {
                if (hasCtor)
                    EmitDeserializeCtorParam(sb, prop, type, bodyIndent);
                else
                {
                    EmitDeserializeProperty(sb, prop, "result", bodyIndent);
                    if (prop.IsRequired)
                    {
                        sb.Append(bodyIndent);
                        sb.Append("__seen_");
                        sb.Append(prop.Name);
                        sb.AppendLine(" = true;");
                    }
                }
            },
            caseInsensitiveVar: "__ci"
        );
        if (type.Properties.Length > 0)
        {
            sb.AppendLine(
                "            else if (reader.Options?.UnmappedMemberHandling == PicoJetson.JsonUnmappedMemberHandling.Disallow)"
            );
            sb.AppendLine(
                "                throw __PicoJsonCold.Err(\"Unexpected property '\" + Encoding.UTF8.GetString(propNameSpan) + \"' at offset \", reader.BytesConsumed);"
            );
            sb.AppendLine("            else reader.TrySkip();");
        }
        // Any incomplete inner read (collection element, nested object, skipped
        // member) rewinds to the property snapshot and asks for more data.
        sb.AppendLine(
            "            if (reader.NeedsMoreData) { reader.RewindTo(__snap); return ReadStatus.NeedMoreData; }"
        );
        sb.AppendLine("        }");
        if (hasCtor)
        {
            sb.Append("        result = partial ?? new ");
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
        if (!hasCtor)
            foreach (var rp in type.Properties.Where(p => p.IsRequired))
            {
                sb.Append("        if (!__seen_");
                sb.Append(rp.Name);
                sb.Append(") throw new System.FormatException(\"Missing required property '");
                sb.Append(EscapeCSharpString(rp.JsonName));
                sb.AppendLine("'.\");");
            }
        sb.AppendLine("        return ReadStatus.Success;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        // Per-type streaming state holder: ctor args + required flags survive
        // chunk boundaries via JsonReader.StreamState.
        if (needsPersistedState)
        {
            sb.Append("file sealed class ");
            sb.Append(type.Name);
            sb.AppendLine("StreamingState");
            sb.AppendLine("{");
            if (hasCtor)
                for (int ci = 0; ci < type.CtorParams.Length; ci++)
                {
                    var cp = type.CtorParams[ci];
                    // Use TypeFullName directly — MapTypeName with null type NREs
                    // for complex kinds (object, enum, list, dict).
                    var typeName = cp.TypeFullNameAnnotated ?? cp.TypeFullName;
                    var defaultVal = cp.TypeKind switch
                    {
                        "string" => "\"\"",
                        "int32"
                        or "int64"
                        or "int16"
                        or "uint16"
                        or "sbyte"
                        or "byte"
                        or "uint32"
                        or "uint64"
                        or "float64" => "0",
                        "boolean" => "false",
                        _ => "default!",
                    };
                    sb.Append("    public ");
                    sb.Append(typeName);
                    sb.Append(" __cp_");
                    sb.Append(ci);
                    sb.Append(" = ");
                    sb.Append(defaultVal);
                    sb.AppendLine(";");
                }
            if (!hasCtor)
                foreach (var rp in streamReqProps)
                {
                    sb.Append("    public bool __seen_");
                    sb.Append(rp.Name);
                    sb.AppendLine(";");
                }
            sb.AppendLine("}");
        }
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
        var isPoly = !type.DerivedTypes.IsDefaultOrEmpty;
        sb.Append("            ");
        sb.Append(isPoly ? PolyClassPrefix(type) + "JsonPolySer" : type.Name + "JsonSer");
        sb.Append(isPoly ? ".Serialize, " : ".Serialize, ");
        sb.Append(isPoly ? PolyClassPrefix(type) + "JsonPolyDes" : type.Name + "JsonDeserializer");
        sb.AppendLine(".Deserialize);");
        // Streaming: emitted for regular (class only), array, and poly types.
        // (EmitPolyStreamingDeserializer fills the original gap.)
        if (
            (type.Properties.Length > 0 && !type.IsValueType)
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

    /// <summary>
    /// Cross-file class prefix for a polymorphic type's generated serializer and
    /// deserializer. They are <c>internal</c> (not <c>file</c>) so the recursion
    /// helper emitted for the same type can route nested values through the
    /// discriminator dispatch instead of building an empty base instance.
    /// </summary>
    private static string PolyClassPrefix(TypeInfo type) =>
        PicoSerDe.Gen.GenInfrastructure.UniqueName(type.FullyQualifiedName ?? type.Name);

    private static void EmitPolySerializer(
        StringBuilder sb,
        TypeInfo type,
        Dictionary<string, TypeInfo> derivedLookup
    )
    {
        var dpn = type.DiscriminatorPropertyName ?? "$type";
        // Concrete bases get a synthesized discriminator so a base-typed
        // instance serializes as a valid object (and round-trips).
        var __baseDisc = PicoSerDe.Gen.GenInfrastructure.BaseDiscriminator(type);
        var __cases = __baseDisc is null
            ? type.DerivedTypes
            : type.DerivedTypes.Add(
                new PicoSerDe.Gen.DerivedTypeInfo(type.FullyQualifiedName!, __baseDisc)
            );

        sb.Append("    internal static class ");
        sb.Append(PolyClassPrefix(type));
        sb.AppendLine("JsonPolySer");
        sb.AppendLine("    {");
        sb.Append("        public static void Serialize(IBufferWriter<byte> writer, ");
        sb.Append(type.Name);
        sb.AppendLine(" value, global::PicoSerDe.Core.SerOptions? options)");
        sb.AppendLine("        {");
        sb.Append("            var __opts = (global::PicoJetson.JsonOptions?)options;\n");
        sb.AppendLine(
            "            var jw = new JsonWriter(writer, indented: __opts?.Indented ?? false, maxDepth: __opts?.MaxDepth ?? 63, options: __opts);"
        );
        if (!type.IsValueType)
        {
            sb.AppendLine("            if (value is null)");
            sb.AppendLine("            {");
            sb.AppendLine("                jw.WriteNull();");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            jw.WriteStartObject();");
        sb.Append("            jw.WritePropertyName(Encoding.UTF8.GetBytes(\"");
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("\"));");

        sb.AppendLine("            switch (value)");
        sb.AppendLine("            {");

        foreach (var dt in __cases)
        {
            var dtProps = derivedLookup.TryGetValue(dt.FullyQualifiedName, out var dti)
                ? dti.Properties
                : ImmutableArray<PropertyInfo>.Empty;

            // Fully-qualified pattern: avoids CS collisions when two derived
            // types share the same short name in different namespaces.
            sb.Append("                case ");
            sb.Append(dt.FullyQualifiedName);
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

        // Unmapped runtime type: no [PicoDerivedType] case matches the value's
        // concrete type. Without this default the discriminator was written and
        // the switch fell through, emitting a truncated `{"$type":}` document
        // that silently loses the object (review C-03). The message is built at
        // generation time from the known base type name — GetType() would be
        // reflection, which the generator avoids everywhere (AOT/trimming).
        sb.AppendLine("                default:");
        sb.Append(
            "                    throw new System.FormatException(\"Unregistered derived type for polymorphic base '"
        );
        sb.Append(EscapeCSharpString(type.FullyQualifiedName ?? type.Name));
        sb.AppendLine("'; add [PicoDerivedType] for the concrete type.\");");

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
        sb.Append("    internal static class ");
        sb.Append(PolyClassPrefix(type));
        sb.AppendLine("JsonPolyDes");
        sb.AppendLine("    {");
        sb.Append("        public static ");
        sb.Append(type.Name);
        sb.AppendLine(
            " Deserialize(ReadOnlySpan<byte> data, global::PicoSerDe.Core.SerOptions? options)"
        );
        sb.AppendLine("        {");
        sb.Append("            var __opts = (global::PicoJetson.JsonOptions?)options;\n");
        sb.AppendLine(
            "            var reader = new JsonReader(data, maxDepth: __opts?.MaxDepth ?? 256, options: __opts);"
        );
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                reader.Read(); // {");
        sb.AppendLine("                if (reader.TokenType == TokenType.Null) return null!;");
        sb.AppendLine("                return DeserializeCore(ref reader, options);");
        sb.AppendLine("            }");
        sb.AppendLine("            finally");
        sb.AppendLine("            {");
        sb.AppendLine("                reader.Dispose();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.Append("        internal static ");
        sb.Append(type.Name);
        sb.AppendLine(
            " DeserializeCore(ref JsonReader reader, global::PicoSerDe.Core.SerOptions? options)"
        );
        sb.AppendLine("        {");
        var dpn = type.DiscriminatorPropertyName ?? "$type";
        // Concrete bases get a synthesized discriminator so a base-typed
        // instance serializes as a valid object (and round-trips).
        var __baseDisc = PicoSerDe.Gen.GenInfrastructure.BaseDiscriminator(type);
        var __cases = __baseDisc is null
            ? type.DerivedTypes
            : type.DerivedTypes.Add(
                new PicoSerDe.Gen.DerivedTypeInfo(type.FullyQualifiedName!, __baseDisc)
            );

        // Convention: the caller positioned the reader at the object start.
        if (!type.IsValueType)
            sb.AppendLine("            if (reader.TokenType == TokenType.Null) return null!;");
        sb.AppendLine(
            "            if (!reader.Read() && reader.NeedsMoreData) return null!; // discriminator property name"
        );
        // Fast path first (exact bytes), the case-insensitive Eq stays as the fallback:
        // discriminators are matched case-insensitively (Eq's third argument is
        // `caseSensitive: false`), so the exact compare can skip the byte-fold loop but
        // must never be the only test.
        sb.Append("            if (!(reader.GetStringRaw().SequenceEqual(\"");
        sb.Append(EscapeCSharpString(dpn));
        sb.Append("\"u8) || TextHelpers.Eq(reader.GetStringRaw(), \"");
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("\"u8, false)))");
        sb.Append(
            "                throw new System.FormatException(\"Expected discriminator property '"
        );
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("' but found different property name\");");
        sb.AppendLine(
            "            if (!reader.Read() && reader.NeedsMoreData) return null!; // discriminator value"
        );
        sb.AppendLine("            var __disc = reader.GetStringRaw();");

        for (int i = 0; i < __cases.Length; i++)
        {
            var dt = __cases[i];
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
            sb.Append(" (__disc.SequenceEqual(\"");
            sb.Append(EscapeCSharpString(dt.TypeDiscriminator));
            sb.Append("\"u8) || TextHelpers.Eq(__disc, \"");
            sb.Append(EscapeCSharpString(dt.TypeDiscriminator));
            sb.AppendLine("\"u8, false))");
            sb.AppendLine("            {");

            // Emit derived type deserialization inline (fully-qualified type
            // name to avoid short-name collisions across namespaces).
            var dtName = dt.FullyQualifiedName;
            if (hasCtor)
            {
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    var cp = dti.CtorParams[ci];
                    // Use TypeFullName directly — MapTypeName with null type NREs
                    // for complex kinds (object, enum, list, dict).
                    var tn = cp.TypeFullNameAnnotated ?? cp.TypeFullName;
                    var dv = cp.TypeKind switch
                    {
                        "string" => "\"\"",
                        "int32"
                        or "int64"
                        or "int16"
                        or "uint16"
                        or "sbyte"
                        or "byte"
                        or "uint32"
                        or "uint64"
                        or "float64" => "0",
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
                sb.Append("                var obj = ");
                sb.Append(ObjectConstructor(dtName));
                sb.AppendLine(";");
            }

            sb.AppendLine(
                "                while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
            );
            sb.AppendLine("                {");
            sb.AppendLine("                    var __n = reader.GetStringRaw();");
            sb.AppendLine("                    if (!reader.Read() && reader.NeedsMoreData) break;");
            EmitNameDispatch(
                sb,
                dtProps,
                "__n",
                "                    ",
                (prop, bodyIndent) =>
                {
                    if (hasCtor)
                        EmitDeserializeCtorParam(sb, prop, dti, bodyIndent);
                    else
                        EmitDeserializeProperty(sb, prop, "obj", bodyIndent);
                }
            );
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
        sb.Append("    internal static ReadStatus DeserializeStreaming(ref JsonReader reader, ");
        sb.Append(type.Name);
        sb.Append("? partial, out ");
        sb.Append(type.Name);
        sb.AppendLine("? result)");
        sb.AppendLine("    {");
        sb.Append("        result = partial;");
        sb.AppendLine();

        var dpn = type.DiscriminatorPropertyName ?? "$type";
        // Concrete bases get a synthesized discriminator so a base-typed
        // instance serializes as a valid object (and round-trips).
        var __baseDisc = PicoSerDe.Gen.GenInfrastructure.BaseDiscriminator(type);
        var __cases = __baseDisc is null
            ? type.DerivedTypes
            : type.DerivedTypes.Add(
                new PicoSerDe.Gen.DerivedTypeInfo(type.FullyQualifiedName!, __baseDisc)
            );

        // Streaming state: the discriminator and every branch's ctor temps must
        // survive chunk resumes, so they live in the reader's StreamState. A poly
        // base never gets the regular `{Type}StreamingState` (only
        // EmitStreamingDeserializer emits that one), hence its own holder —
        // reusing the regular name would collide (CS0101) if both ever existed.
        sb.Append("        var __st = (");
        sb.Append(type.Name);
        sb.Append("PolyStreamingState)(reader.StreamState ??= new ");
        sb.Append(type.Name);
        sb.AppendLine("PolyStreamingState());");
        for (int i = 0; i < __cases.Length; i++)
        {
            var sti = derivedLookup.TryGetValue(__cases[i].FullyQualifiedName, out var stFound)
                ? stFound
                : default(TypeInfo);
            if (sti.CtorParams.IsDefaultOrEmpty)
                continue;
            for (int ci = 0; ci < sti.CtorParams.Length; ci++)
            {
                sb.Append("        ref var __cp_");
                sb.Append(i);
                sb.Append('_');
                sb.Append(ci);
                sb.Append(" = ref __st.__cp_");
                sb.Append(i);
                sb.Append('_');
                sb.Append(ci);
                sb.AppendLine(";");
            }
        }
        sb.AppendLine();
        sb.AppendLine("        string? __disc = __st.Discriminator;");
        sb.AppendLine();

        // ObjectStart read runs on the first invocation only.
        sb.AppendLine("        if (!reader.IsResumed)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : reader.TokenType != TokenType.None ? ReadStatus.Success : ReadStatus.EndOfInput;"
        );
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        // Discriminator read: skipped once __disc is set (resume after");
        sb.AppendLine("        // the discriminator, or re-run after a rewind).");
        sb.AppendLine("        if (__disc is null)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
        );
        sb.AppendLine(
            "            if (reader.TokenType != TokenType.PropertyName) return ReadStatus.Success;"
        );
        // Fast path first (exact bytes), the case-insensitive Eq stays as the fallback.
        sb.Append("            if (!(reader.GetStringRaw().SequenceEqual(\"");
        sb.Append(EscapeCSharpString(dpn));
        sb.Append("\"u8) || TextHelpers.Eq(reader.GetStringRaw(), \"");
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("\"u8, false)))");
        sb.Append(
            "                throw new System.FormatException(\"Expected discriminator property '"
        );
        sb.Append(EscapeCSharpString(dpn));
        sb.AppendLine("' but found different property name\");");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
        );
        sb.AppendLine(
            "            __disc = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());"
        );
        sb.AppendLine("            __st.Discriminator = __disc;");
        sb.AppendLine("        }");
        sb.AppendLine();

        for (int i = 0; i < __cases.Length; i++)
        {
            var dt = __cases[i];
            var keyword = i == 0 ? "if" : "else if";
            var dti = derivedLookup.TryGetValue(dt.FullyQualifiedName, out var found)
                ? found
                : default(TypeInfo);
            var dtProps = dti.Properties.IsDefault
                ? ImmutableArray<PropertyInfo>.Empty
                : dti.Properties;
            var hasCtor = !dti.CtorParams.IsDefaultOrEmpty && dti.CtorParams.Length > 0;
            var dtName = dt.FullyQualifiedName;

            sb.Append("        ");
            sb.Append(keyword);
            sb.Append(" (__disc == \"");
            sb.Append(EscapeCSharpString(dt.TypeDiscriminator));
            sb.Append("\" || TextHelpers.Eq(System.Text.Encoding.UTF8.GetBytes(__disc), \"");
            sb.Append(EscapeCSharpString(dt.TypeDiscriminator));
            sb.AppendLine("\"u8, false))");
            sb.AppendLine("        {");

            // Initialize: guarded creation for non-ctor types (resume keeps the object);
            // ctor temps are hoisted to method scope with branch-unique names so they
            // persist across resumes.
            if (!hasCtor)
            {
                sb.Append("            if (result is null) result = new ");
                sb.Append(dtName);
                sb.AppendLine("();");
            }

            // Ctor temps are `ref` aliases of __st.__cp_{branch}_{param} declared at
            // method scope, so a chunk resume no longer resets them (review C-06).

            // Read remaining properties using streaming loop
            sb.AppendLine("            " + CiLocal);
            sb.AppendLine("            while (true)");
            sb.AppendLine("            {");
            sb.AppendLine(
                "                long __snap = reader.BytesConsumed; // per member: an incomplete read rewinds here"
            );
            sb.AppendLine(
                "                if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
            );
            sb.AppendLine("                if (reader.TokenType != TokenType.PropertyName) break;");
            sb.AppendLine("                var propNameSpan = reader.GetStringRaw();");
            sb.AppendLine(
                "                if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
            );

            EmitNameDispatch(
                sb,
                dtProps,
                "propNameSpan",
                "                ",
                (prop, bodyIndent) =>
                {
                    if (hasCtor)
                        EmitDeserializeCtorParam(sb, prop, dti, bodyIndent, $"{i}_");
                    else
                        EmitDeserializeProperty(sb, prop, $"(({dtName})result!)", bodyIndent);
                },
                caseInsensitiveVar: "__ci"
            );

            if (dtProps.Length > 0)
                sb.AppendLine("                else reader.TrySkip();");
            sb.AppendLine(
                "                if (reader.NeedsMoreData) { reader.RewindTo(__snap); return ReadStatus.NeedMoreData; }"
            );
            sb.AppendLine("            }");

            // Construct result
            if (hasCtor)
            {
                sb.Append("            if (result is null) result = new ");
                sb.Append(dtName);
                sb.Append("(");
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    if (ci > 0)
                        sb.Append(", ");
                    sb.Append("__cp_");
                    sb.Append(i);
                    sb.Append("_");
                    sb.Append(ci);
                }
                sb.AppendLine(");");
            }

            sb.AppendLine("            return ReadStatus.Success;");
            sb.AppendLine("        }");
        }

        sb.AppendLine();
        sb.AppendLine(
            "        throw new System.FormatException($\"Unknown type discriminator: {__disc}\");"
        );
        sb.AppendLine("    }");
        sb.AppendLine("}");

        // Per-poly-type streaming state holder: the discriminator plus every
        // branch's ctor temps survive chunk boundaries via JsonReader.StreamState.
        // (Poly bases never get the regular `{Type}StreamingState`.)
        sb.Append("file sealed class ");
        sb.Append(type.Name);
        sb.AppendLine("PolyStreamingState");
        sb.AppendLine("{");
        sb.AppendLine("    public string? Discriminator;");
        for (int i = 0; i < __cases.Length; i++)
        {
            var sti = derivedLookup.TryGetValue(__cases[i].FullyQualifiedName, out var stFound)
                ? stFound
                : default(TypeInfo);
            if (sti.CtorParams.IsDefaultOrEmpty)
                continue;
            for (int ci = 0; ci < sti.CtorParams.Length; ci++)
            {
                var cp = sti.CtorParams[ci];
                // Use TypeFullName directly — MapTypeName with null type NREs
                // for complex kinds (object, enum, list, dict).
                var tn = cp.TypeFullNameAnnotated ?? cp.TypeFullName;
                var dv = cp.TypeKind switch
                {
                    "string" => "\"\"",
                    "int32"
                    or "int64"
                    or "int16"
                    or "uint16"
                    or "sbyte"
                    or "byte"
                    or "uint32"
                    or "uint64"
                    or "float64" => "0",
                    "boolean" => "false",
                    _ => "default!",
                };
                sb.Append("    public ");
                sb.Append(tn);
                sb.Append(" __cp_");
                sb.Append(i);
                sb.Append('_');
                sb.Append(ci);
                sb.Append(" = ");
                sb.Append(dv);
                sb.AppendLine(";");
            }
        }
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
        string indent,
        int nestLevel = 0
    )
    {
        // Unique loop variable per level: reusing one name triggers CS0136 for
        // List<List<List<T>>>.
        var loopVar = nestLevel == 0 ? "__inner" : $"__inner_{nestLevel}";
        sb.Append(indent);
        sb.AppendLine("jw.WriteStartArray();");
        sb.Append(indent);
        sb.Append("foreach (var ");
        sb.Append(loopVar);
        sb.Append(" in ");
        sb.Append(itemVar);
        sb.AppendLine(")");
        sb.Append(indent);
        sb.AppendLine("{");

        if (IsNestedList(prop))
        {
            // Another level of nesting
            EmitNestedListSerialize(
                sb,
                prop.NestedProperties[0],
                loopVar,
                indent + "    ",
                nestLevel + 1
            );
        }
        else
        {
            // Base case: emit element directly
            EmitSerializeElement(sb, prop, loopVar, indent + "    ");
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
        int nestLevel,
        bool parentElementIsArray = false
    )
    {
        // For nested lists, use ElementTypeName (e.g. "System.Collections.Generic.List<int>");
        // for primitives, resolve from TypeKind (e.g. "int32" → "int").
        // The innermost element keeps its nullability annotation (int? → "int?"),
        // so the nested list declaration matches the property type.
        var innerTypeName =
            prop.TypeKind is "list" or "array"
                ? (prop.ElementTypeNameAnnotated ?? prop.ElementTypeName ?? "object")
            : prop.TypeKind == "object"
                ? (
                    prop.ElementTypeNameAnnotated
                    ?? prop.ElementTypeName
                    ?? prop.TypeFullName
                    ?? "object"
                )
            : (
                prop.ElementTypeNameAnnotated
                ?? prop.ElementTypeName
                ?? ResolveCSharpTypeName(prop.TypeKind)
            );
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
                nestLevel + 1,
                prop.ElementTypeKind == "array"
            );
        }
        else
        {
            EmitCollectionElementLoop(sb, prop, innerVar, indent + "        ", nestLevel);
        }

        sb.Append(indent);
        sb.AppendLine("    }");
        sb.Append(indent);
        sb.Append("    ");
        sb.Append(parentListAcc);
        sb.Append(".Add(");
        sb.Append(parentElementIsArray ? innerVar + ".ToArray()" : innerVar);
        sb.AppendLine(");");
        sb.Append(indent);
        sb.AppendLine("}");
    }

    /// <summary>
    /// ValueTuple types cannot be constructed with 'new T()' or object initializers
    /// (CS8181) — they use 'default(T)' plus member assignment instead.
    /// </summary>
    private static string ObjectConstructor(string typeName) =>
        typeName.Contains("ValueTuple") || typeName.StartsWith("(")
            ? $"default({typeName})"
            : $"new {typeName}()";

    /// <summary>Emits the temp-List to concrete-collection conversion for array-like kinds.</summary>
    private static void EmitCollectionConversion(
        StringBuilder sb,
        string kind,
        string listVar,
        string assignTarget,
        string elemType,
        string indent
    )
    {
        switch (kind)
        {
            case "array":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = ");
                sb.Append(listVar);
                sb.AppendLine(".ToArray();");
                break;
            case "hashset":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = new System.Collections.Generic.HashSet<");
                sb.Append(elemType);
                sb.Append(">(");
                sb.Append(listVar);
                sb.AppendLine(");");
                break;
            case "queue":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = new System.Collections.Generic.Queue<");
                sb.Append(elemType);
                sb.Append(">(");
                sb.Append(listVar);
                sb.AppendLine(");");
                break;
            case "linkedlist":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = new System.Collections.Generic.LinkedList<");
                sb.Append(elemType);
                sb.Append(">(");
                sb.Append(listVar);
                sb.AppendLine(");");
                break;
            case "stack":
                sb.Append(indent);
                sb.Append("var __stk = new System.Collections.Generic.Stack<");
                sb.Append(elemType);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.Append("for (int __i = ");
                sb.Append(listVar);
                sb.AppendLine(".Count - 1; __i >= 0; __i--)");
                sb.Append(indent);
                sb.Append("    __stk.Push(");
                sb.Append(listVar);
                sb.AppendLine("[__i]);");
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.AppendLine(" = __stk;");
                break;
            case "immutablearray":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = System.Collections.Immutable.ImmutableArray.CreateRange(");
                sb.Append(listVar);
                sb.AppendLine(");");
                break;
            case "memory":
            case "readonlymemory":
                sb.Append(indent);
                sb.Append(assignTarget);
                sb.Append(" = new System.");
                sb.Append(kind == "memory" ? "Memory" : "ReadOnlyMemory");
                sb.Append("<");
                sb.Append(elemType);
                sb.Append(">(");
                sb.Append(listVar);
                sb.AppendLine(".ToArray());");
                break;
        }
    }

    /// <summary>Emits a write statement for the extended scalar family. False when the kind is not one.</summary>
    private static bool EmitExtendedScalarWrite(
        StringBuilder sb,
        string kind,
        string accessor,
        string indent
    )
    {
        var method = kind switch
        {
            "datetimeoffset" => "WriteDateTimeOffset",
            "char" => "WriteChar",
            "uri" => "WriteUri",
            "version" => "WriteVersion",
            "half" => "WriteHalf",
            "biginteger" => "WriteBigInteger",
            "int128" => "WriteInt128",
            "uint128" => "WriteUInt128",
            "nint" => "WriteNInt",
            _ => null,
        };
        if (method is null)
            return false;
        sb.Append(indent);
        sb.Append("global::PicoJetson.TypeIo.");
        sb.Append(method);
        sb.Append("(ref jw, ");
        sb.Append(accessor);
        sb.AppendLine(");");
        return true;
    }

    /// <summary>Read expression for the extended scalar family, or null when the kind is not one.</summary>
    private static string? ExtendedScalarReadExpr(string kind) =>
        kind switch
        {
            "datetimeoffset" => "global::PicoJetson.TypeIo.ReadDateTimeOffset(ref reader)",
            "char" => "global::PicoJetson.TypeIo.ReadChar(ref reader)",
            "uri" => "global::PicoJetson.TypeIo.ReadUri(ref reader)",
            "version" => "global::PicoJetson.TypeIo.ReadVersion(ref reader)",
            "half" => "global::PicoJetson.TypeIo.ReadHalf(ref reader)",
            "biginteger" => "global::PicoJetson.TypeIo.ReadBigInteger(ref reader)",
            "int128" => "global::PicoJetson.TypeIo.ReadInt128(ref reader)",
            "uint128" => "global::PicoJetson.TypeIo.ReadUInt128(ref reader)",
            "nint" => "global::PicoJetson.TypeIo.ReadNInt(ref reader)",
            _ => null,
        };

    /// <summary>Maps a TypeKind string to its C# type name for code generation.</summary>
    private static string ResolveCSharpTypeName(string typeKind) =>
        typeKind switch
        {
            "string" => "string",
            "int32" => "int",
            "int64" => "long",
            "char" => "char",
            "datetimeoffset" => "System.DateTimeOffset",
            "uri" => "System.Uri",
            "version" => "System.Version",
            "half" => "System.Half",
            "biginteger" => "System.Numerics.BigInteger",
            "int128" => "System.Int128",
            "uint128" => "System.UInt128",
            "nint" => "nint",
            "int16" => "short",
            "uint16" => "ushort",
            "sbyte" => "sbyte",
            "byte" => "byte",
            "uint32" => "uint",
            "uint64" => "ulong",
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

        sb.Append("file static class ");
        sb.Append(type.Name);
        sb.AppendLine("JsonSer");
        sb.AppendLine("    {");
        sb.Append("        public static void Serialize(IBufferWriter<byte> writer, ");
        sb.Append(arrTypeName);
        sb.AppendLine(" value, global::PicoSerDe.Core.SerOptions? options)");
        sb.AppendLine("        {");
        sb.Append("            var __opts = (global::PicoJetson.JsonOptions?)options;\n");
        sb.AppendLine(
            "            var jw = new JsonWriter(writer, indented: __opts?.Indented ?? false, maxDepth: __opts?.MaxDepth ?? 63, options: __opts);"
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
                sb.AppendLine("jw.WriteString(__item);");
                break;
            case "int32":
            case "int64":
            case "int16":
            case "uint16":
            case "sbyte":
            case "byte":
            case "uint32":
            case "uint64":
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
                if (EmitExtendedScalarWrite(sb, elemKind, "__item", indent))
                    break;
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

        sb.Append("    file static class ");
        sb.Append(type.Name);
        sb.AppendLine("JsonDeserializer");
        sb.AppendLine("    {");
        sb.Append("        public static ");
        sb.Append(arrTypeName);
        sb.AppendLine(
            " Deserialize(ReadOnlySpan<byte> data, global::PicoSerDe.Core.SerOptions? options)"
        );
        sb.AppendLine("        {");
        sb.Append("            var __opts = (global::PicoJetson.JsonOptions?)options;\n");
        sb.AppendLine(
            "            var reader = new JsonReader(data, maxDepth: __opts?.MaxDepth ?? 256, options: __opts);"
        );
        sb.Append("            var __list = new System.Collections.Generic.List<");
        sb.Append(elemCsType);
        sb.AppendLine(">();");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                if (!reader.Read())");
        sb.AppendLine(
            "                    throw new System.FormatException(\"Empty JSON input.\");"
        );
        sb.AppendLine("                if (reader.TokenType == TokenType.Null)");
        if (isList)
            sb.AppendLine("                    return null!;");
        else
            sb.AppendLine("                    return null!;");
        sb.AppendLine(
            "                if (!reader.IsResumed && reader.TokenType != TokenType.ArrayStart) throw __PicoJsonCold.Err(\"Expected a JSON array at offset \", reader.BytesConsumed, \" but found \" + reader.TokenType + \".\");"
        );
        if (IsFusedElementKind(elemKind))
        {
            // The top-level array entry point has its own element body (Engine's `__ev`), so it gets
            // the fused loop directly instead of going through EmitDeserializeElementAdd.
            EmitFusedInt32ElementLoop(
                sb,
                "__list",
                "__ev",
                nullableElement: false,
                "                "
            );
        }
        else
        {
            sb.AppendLine(
                "                while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
            );
            sb.AppendLine("                {");
            EmitArrayElementDeserialize(sb, elemKind, elemTypeName, "                    ");
            sb.AppendLine("                }");
        }
        sb.AppendLine(
            "                if (reader.Read()) throw new System.FormatException($\"Unexpected data after the document at offset {reader.BytesConsumed}\");"
        );
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
            "char" => "char",
            "datetimeoffset" => "System.DateTimeOffset",
            "uri" => "System.Uri",
            "version" => "System.Version",
            "half" => "System.Half",
            "biginteger" => "System.Numerics.BigInteger",
            "int128" => "System.Int128",
            "uint128" => "System.UInt128",
            "nint" => "nint",
            "int16" => "short",
            "uint16" => "ushort",
            "sbyte" => "sbyte",
            "byte" => "byte",
            "uint32" => "uint",
            "uint64" => "ulong",
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
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine("    __list.Add(null!);");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (reader.TokenType != TokenType.String)");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected a string element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine(
                    "    __list.Add(System.Text.Encoding.UTF8.GetString(reader.GetStringRaw()));"
                );
                sb.Append(indent);
                sb.AppendLine("}");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __ev))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __ev))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;

            case "int16":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __ev))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("__list.Add(checked((short)__ev));");
                break;
            case "uint16":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __ev))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("__list.Add(checked((ushort)__ev));");
                break;
            case "sbyte":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __ev))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("__list.Add(checked((sbyte)__ev));");
                break;
            case "byte":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt32(out var __ev))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetInt64(out var __lev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((int)__lev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("__list.Add(checked((byte)__ev));");
                break;
            case "uint32":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetInt64(out var __ev))");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (!reader.TryGetUInt64(out var __ulev))");
                sb.Append(indent);
                sb.AppendLine(
                    "        throw new System.FormatException($\"Expected an integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("    __ev = checked((long)__ulev);");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("__list.Add(checked((uint)__ev));");
                break;
            case "uint64":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit unsigned integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetUInt64(out var __ev))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a 64-bit unsigned integer element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "float32":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __ev))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("__list.Add((float)__ev);");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetFloat64(out var __ev))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a floating-point element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("__list.Add(__ev);");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a boolean element at offset {reader.BytesConsumed}\");"
                );
                sb.Append(indent);
                sb.AppendLine("if (!reader.TryGetBool(out var __ev))");
                sb.Append(indent);
                sb.AppendLine(
                    "    throw new System.FormatException($\"Expected a boolean element at offset {reader.BytesConsumed}\");"
                );
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
                sb.Append(PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(elemTypeName));
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
                if (ExtendedScalarReadExpr(elemKind) is string __sx6)
                {
                    sb.Append(indent);
                    sb.AppendLine("__list.Add(" + __sx6 + ");");
                    break;
                }
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
        sb.Append("    internal static ReadStatus DeserializeStreaming(ref JsonReader reader, ");
        sb.Append(arrTypeName);
        sb.Append("? partial, out ");
        sb.Append(arrTypeName);
        sb.AppendLine("? result)");
        sb.AppendLine("    {");
        sb.Append("        var __list = reader.StreamState as System.Collections.Generic.List<");
        sb.Append(elemCsType);
        sb.AppendLine("> ?? new System.Collections.Generic.List<");
        sb.Append(elemCsType);
        sb.AppendLine(">();");
        sb.Append("        reader.StreamState = __list;");
        sb.AppendLine("        result = default;");
        sb.AppendLine();
        sb.AppendLine("        // ArrayStart read runs on the first invocation only.");
        sb.AppendLine("        if (!reader.IsResumed)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : reader.TokenType != TokenType.None ? ReadStatus.Success : ReadStatus.EndOfInput;"
        );
        sb.AppendLine(
            "            if (reader.TokenType != TokenType.ArrayStart) throw __PicoJsonCold.Err(\"Expected a JSON array at offset \", reader.BytesConsumed, \" but found \" + reader.TokenType + \".\");"
        );
        sb.AppendLine(
            "            if (reader.TokenType == TokenType.Null) return ReadStatus.Success;"
        );
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        while (true)");
        sb.AppendLine("        {");
        sb.AppendLine("            long __snap = reader.BytesConsumed;");
        sb.AppendLine(
            "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : throw new System.FormatException($\"Unexpected end of JSON array at offset {reader.BytesConsumed}\");"
        );
        sb.AppendLine("            if (reader.TokenType == TokenType.ArrayEnd) break;");
        sb.AppendLine("            int __before = __list.Count;");
        EmitArrayElementDeserialize(sb, elemKind, elemTypeName, "            ");
        // The element read may bail on a chunk boundary (nested helper or
        // collection element). Discard the partial element, rewind to its
        // start and ask for more data — the resume re-reads it from scratch.
        sb.AppendLine("            if (reader.NeedsMoreData)");
        sb.AppendLine("            {");
        sb.AppendLine(
            "                if (__list.Count > __before) __list.RemoveRange(__before, __list.Count - __before);"
        );
        sb.AppendLine("                reader.RewindTo(__snap);");
        sb.AppendLine("                return ReadStatus.NeedMoreData;");
        sb.AppendLine("            }");
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

    private static string EmitJsonValue(AnonFieldInfo f, string vv, string wv) =>
        f.TypeKind switch
        {
            "string" => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}));",
            "int32"
            or "int64"
            or "int16"
            or "uint16"
            or "sbyte"
            or "byte"
            or "uint32"
            or "uint64"
            or "float32"
            or "float64"
            or "decimal" => $"{wv}.WriteNumber({vv});",
            "boolean" => $"{wv}.WriteBoolean({vv});",
            "datetime" => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}.ToString(\"O\")));",
            "char" => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}.ToString()));",
            "dateonly" or "timeonly" or "timespan" or "guid" =>
                $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}.ToString()));",
            _ => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}?.ToString() ?? \"\"));",
        };

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
