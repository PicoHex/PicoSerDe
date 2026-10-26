namespace PicoYaml.Gen;

using Microsoft.CodeAnalysis.CSharp;
using AnonFieldInfo = PicoSerDe.Gen.AnonFieldInfo;
using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class YamlSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "YamlSerializer",
        "PicoYaml",
        "yaml",
        "YamlConstructorAttribute"
    );

    private static readonly DiagnosticDescriptor AnonRequiresCSharp12 = new(
        id: "PICOYAML003",
        title: "Anonymous types require C# 12+",
        messageFormat: "Anonymous type serialization requires C# 12 or later",
        category: "PicoYaml.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor AnonRequiresUnsafe = new(
        id: "PICOYAML004",
        title: "Requires AllowUnsafeBlocks",
        messageFormat: "Anonymous type serialization requires AllowUnsafeBlocks to be enabled",
        category: "PicoYaml.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        HasYamlCamelCase,
        GetYamlKey,
        HasYamlIgnore,
        GetYamlConverter,
        GetYamlDateTimeFormat,
        OverrideKindWithStringOnConverter: true
    );

    private static readonly PicoSerDe.Gen.AnonFormatConfig AnonCfg = new(
        HasNullLiteral: false,
        EmbedsKeyInValue: false,
        ObjectStartMethod: "WriteStartMapping",
        ObjectEndMethod: "WriteEndMapping",
        ObjectStartNeedsCount: false,
        HasIndentedMaxDepth: false,
        KeyIsEncodedString: false,
        HasNamingPolicy: false,
        HasOptionsParam: false,
        FacadeTakesOptions: true,
        HasIndented: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        // Pipeline A: usage-driven (existing)
        var usageD = ctx
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (n, _) => IsC(n),
                transform: static (c, _) => Tf(c)
            )
            .Where(static t => t.HasValue)
            .Select(static (t, _) => t!.Value);

        // Pipeline B: attribute-driven — discover types via [PicoSerializable]
        var attrD = ctx
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoSerDe.Core.PicoSerializableAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (c, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandAttributes(c, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline D: shorthand attribute [GenerateSerializer(typeof(T))]
        var shortD = ctx
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoSerDe.Core.GenerateSerializerAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (c, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandAttributes(c, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline C: format-specific attribute — discover types via [PicoYamlSerializable]
        var formatD = ctx
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoYaml.PicoYamlSerializableAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (c, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandAttributes(c, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline E: polymorphic — discover types via [PicoDerivedType]
        var polyD = ctx
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoSerDe.Core.PicoDerivedTypeAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (c, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandPolymorphicTypes(c, Config, Attrs)
            )
            .SelectMany(static (types, _) => types);

        // Pipeline F: assembly name for namespace isolation of generated helpers
        var asmName = ctx.CompilationProvider.Select(
            static (c, _) =>
                (c.AssemblyName ?? "unknown").Replace('.', '_').Replace('-', '_').Replace(' ', '_')
        );

        // Pipeline-Anon: anonymous type serialization via interceptors
        var anonDriven = ctx
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: (n, _) => IsC(n),
                transform: (cx, _) =>
                {
                    // Anonymous-type usage is detected regardless of capabilities; the
                    // output callback reports the diagnostic and skips emission (review P3-1).
                    if (cx.SemanticModel.GetSymbolInfo(cx.Node).Symbol is not IMethodSymbol m)
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
                    return PicoSerDe.Gen.AnonTypeHandler.BuildAnonTypeInfo(nt, cx, Config, Attrs);
                }
            )
            .Where(a => a is not null);

        // Capability flags for the anonymous-type diagnostic: reported
        // only when anonymous serialization is actually used (review P3-1).
        var anonCaps = ctx.CompilationProvider.Select(
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
        ctx.RegisterSourceOutput(
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
                        (f, vv, wv) => EmitYamlValue(f, vv, wv)
                    );
                }
            }
        );

        // Merge all pipelines into one output
        var all = usageD
            .Collect()
            .Combine(attrD.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(formatD.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(shortD.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(polyD.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

        ctx.RegisterSourceOutput(
            all,
            static (spc, types) =>
            {
                PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = null;
                GenerateAll(spc, types);
            }
        );
    }

    private static bool IsC(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    private static TypeInfo? Tf(GeneratorSyntaxContext ctx)
    {
        bool hasCtor = false;
        INamedTypeSymbol? namedType = null;
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol m
            && m.TypeArguments.Length == 1
        )
        {
            // ── Top-level List<T> (e.g. List<int>, List<SomeDto>) ──
            if (
                m.TypeArguments[0] is INamedTypeSymbol ntsList
                && PicoSerDe.Gen.GenInfrastructure.IsGenericList(ntsList)
            )
                return PicoSerDe.Gen.GenInfrastructure.TransformTopLevelList(
                    ntsList,
                    Config,
                    Attrs
                );

            if (m.TypeArguments[0] is not INamedTypeSymbol nt)
                return null;
            if (nt.IsAnonymousType)
                return null; // handled by anonDriven pipeline
            namedType = nt;
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
            if (namedType.IsRecord)
            {
                var ctors = namedType
                    .Constructors.Where(c => c.DeclaredAccessibility == Accessibility.Public)
                    .ToArray();
                if (ctors.Length == 1 && !ctors[0].IsImplicitlyDeclared)
                    hasCtor = true;
            }
            if (!hasCtor)
            {
                foreach (var ctor in namedType.Constructors)
                {
                    if (ctor.DeclaredAccessibility != Accessibility.Public)
                        continue;
                    foreach (var attr in ctor.GetAttributes())
                    {
                        if (attr.AttributeClass?.Name == "YamlConstructorAttribute")
                        {
                            hasCtor = true;
                            break;
                        }
                    }
                    if (hasCtor)
                        break;
                }
            }
        }

        var info = PicoSerDe.Gen.GenInfrastructure.TransformType(
            ctx,
            Config,
            Attrs,
            includeReadOnlyProperties: hasCtor
        );
        if (info is not { } ti)
            return null;

        if (hasCtor)
        {
            if (namedType is null)
                return ti;
            var cp = PicoSerDe.Gen.GenInfrastructure.DetectConstructor(
                namedType,
                Config.FormatTag,
                "YamlConstructorAttribute"
            );
            if (cp is { } ctorParams)
                ti = ti with { CtorParams = ctorParams };
            return ti;
        }

        // Detect [YamlTag] on the target type
        if (namedType is null)
            return ti;

        foreach (var attr in namedType.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "YamlTagAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoYaml"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string tag
            )
            {
                return ti with { TypeTag = tag };
            }
        }

        return ti;
    }

    // ── Attribute helpers ──

    private static string? GetYamlKey(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "YamlKeyAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoYaml"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string key
            )
                return key;
        }
        return null;
    }

    private static bool HasYamlIgnore(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "YamlIgnoreAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoYaml"
            )
                return true;
        }
        return false;
    }

    private static string? GetYamlConverter(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "YamlConverterAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoYaml"
                && attr.ConstructorArguments.Length >= 1
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol nts
            )
                return nts.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        return null;
    }

    private static bool HasYamlCamelCase(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "YamlCamelCaseAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoYaml"
            )
                return true;
        }
        return false;
    }

    private static string? GetYamlDateTimeFormat(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "YamlDateTimeFormatAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoYaml"
                && attr.ConstructorArguments.Length >= 1
                && attr.ConstructorArguments[0].Value is string fmt
            )
                return fmt;
        }
        return null;
    }

    // ── Source generation ──

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> ts)
    {
        var seen = new HashSet<string>();
        var hintNames = new HashSet<string>();

        var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
        var validTypes = ts.Where(t =>
                !string.IsNullOrEmpty(t.FullyQualifiedName) && !string.IsNullOrEmpty(t.Name)
            )
            .ToArray();
        foreach (var t in validTypes)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedTypes(t, nestedTypes);

        // Also collect element types from top-level arrays/lists (e.g. YamlAddress for List<YamlAddress>)
        foreach (var t in ts)
        {
            if (
                t.ArrayElementKind is "object"
                && !string.IsNullOrEmpty(t.ArrayElementName)
                && !t.ArrayElementNestedProps.IsDefaultOrEmpty
            )
            {
                // Raw FQN keeps this key identical to CollectNestedTypes' so a
                // type used both as a DTO property and as a top-level array
                // element shares one helper.
                var elemFqn = t.ArrayElementName!;
                if (!nestedTypes.ContainsKey(elemFqn))
                    nestedTypes[elemFqn] = t.ArrayElementNestedProps;
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

        // Collect nested Dictionary types
        var nestedDictTypes = new Dictionary<string, PropertyInfo>();
        foreach (var t in validTypes)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedDictTypes(t, nestedDictTypes);

        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var sn = PicoSerDe.Gen.GenInfrastructure.UniqueName(cleanName);
            var hintName = $"{sn}_YamlInner.g.cs";
            if (!hintNames.Add(hintName))
                continue;
            spc.AddSource(
                hintName,
                SourceText.From(GenerateInnerHelper(cleanName, sn, props), Encoding.UTF8)
            );
        }

        // Generate inner helpers for nested Dictionary types
        foreach (var kv in nestedDictTypes)
        {
            var fullName = kv.Key;
            var dictProp = kv.Value;
            var sn = PicoSerDe.Gen.GenInfrastructure.UniqueName(fullName);
            var hintName = $"{sn}_YamlDictInner.g.cs";
            if (hintNames.Add(hintName))
                spc.AddSource(
                    hintName,
                    SourceText.From(GenerateDictInnerHelper(fullName, sn, dictProp), Encoding.UTF8)
                );
        }

        var typeMap = new Dictionary<string, TypeInfo>();
        foreach (var t in ts)
        {
            if (string.IsNullOrEmpty(t.FullyQualifiedName))
                continue;
            if (typeMap.TryGetValue(t.FullyQualifiedName, out var existing))
            {
                if (!t.DerivedTypes.IsDefaultOrEmpty && existing.DerivedTypes.IsDefaultOrEmpty)
                    typeMap[t.FullyQualifiedName] = t;
            }
            else
                typeMap[t.FullyQualifiedName] = t;
        }

        foreach (var kv in typeMap)
        {
            var t = kv.Value;
            if (string.IsNullOrEmpty(t.Name))
                continue;
            var safeFq = PicoSerDe.Gen.GenInfrastructure.UniqueName(t.FullyQualifiedName ?? "");
            var hintName = $"{safeFq}_Yaml.g.cs";
            string code;
            if (t.IsRefLikeType)
                code = GenRefStruct(t);
            else if (t.IsTopLevelList || t.ArrayElementKind is not null)
                code = GenList(t);
            else if (!t.DerivedTypes.IsDefaultOrEmpty)
                code = GenPoly(t, typeMap);
            else
                code = Gen(t);
            if (!hintNames.Add(hintName))
            {
                PicoSerDe.Gen.GenInfrastructure.ReportHintNameCollision(
                    spc,
                    hintName,
                    t.FullyQualifiedName
                );
                continue;
            }
            spc.AddSource(hintName, SourceText.From(code, Encoding.UTF8));
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
        sb.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Globalization;"
        );
        sb.AppendLine("using PicoSerDe.Core; using PicoYaml;");

        // A generic type name (it contains '<') makes InnerClassName emit the helper at
        // GLOBAL scope, so this file must not declare a namespace — declaring the type's
        // own namespace here (as for plain names) put the class out of reach of every
        // `global::<name>YamlInner` reference and the caller failed with CS0400. The
        // signature below uses the fully qualified name, which resolves from global scope.
        var lastDot = fullName.LastIndexOf('.');
        if (!fullName.Contains('<') && lastDot > 0)
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(fullName.Substring(0, lastDot));
            sb.AppendLine(";");
        }
        sb.AppendLine();
        sb.Append("internal static class ");
        sb.Append(shortName);
        sb.AppendLine("YamlInner");
        sb.AppendLine("{");

        sb.Append("    internal static void Serialize(YamlWriter yw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        yw.WriteStartMapping();");
        EmitInnerSerializeProps(sb, props);
        sb.AppendLine("        yw.WriteEndMapping();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.Append("    internal static void SerializeBlock(YamlWriter yw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        EmitInnerSerializeProps(sb, props);
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.Append("    internal static ");
        sb.Append(fullName);
        sb.AppendLine(" Deserialize(ref YamlReader r)");
        sb.AppendLine("    {");
        sb.Append("        var obj = new ");
        sb.Append(fullName);
        sb.AppendLine("();");
        sb.AppendLine("        if (r.Read() && r.TokenType == TokenType.ObjectStart)");
        sb.AppendLine("        {");
        // EmitDeserialize leaves the reader ON the next member token, so the loop
        // must inspect the current token instead of reading at the top. It exits on
        // this mapping's own ObjectEnd, leaving that token for the caller — reading
        // past it would consume the enclosing mapping's next key. Sharing this one
        // emitter is what gives nested members the same element types as top-level
        // ones (C-22), and the token check keeps a list member from ending the loop
        // early, which used to silently drop every following member.
        sb.AppendLine("            while (true)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (r.TokenType is TokenType.ObjectEnd or TokenType.None)");
        sb.AppendLine("                    break;");
        sb.AppendLine(
            "                if (r.TokenType != TokenType.PropertyName) { if (!r.Read()) break; continue; }"
        );
        sb.AppendLine("                var k = r.KeySpan;");
        for (int i = 0; i < props.Length; i++)
        {
            var np = props[i];
            var kw = i == 0 ? "if" : "else if";
            sb.Append("                ");
            sb.Append(kw);
            sb.Append(" (TextHelpers.Eq(k, \"");
            sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(np.JsonName));
            sb.AppendLine("\"u8))");
            sb.AppendLine("                {");
            EmitDeserialize(sb, np, "obj", "                    ");
            sb.AppendLine("                }");
        }
        if (props.Length == 0)
            sb.AppendLine(
                "                if (!r.Read()) break; // unknown member: skip its value"
            );
        else
        {
            sb.AppendLine("                else");
            sb.AppendLine(
                "                { if (!r.Read()) break; } // unknown member: skip its value"
            );
        }
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return obj;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateDictInnerHelper(
        string fullName,
        string shortName,
        PropertyInfo dp
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System; using System.Buffers; using System.Text;");
        sb.AppendLine("using PicoSerDe.Core; using PicoYaml;");

        var lastDot = fullName.LastIndexOf('.');
        if (!fullName.Contains('<') && lastDot > 0)
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(fullName.Substring(0, lastDot));
            sb.AppendLine(";");
        }
        sb.AppendLine();
        sb.Append("internal static class ");
        sb.Append(shortName);
        sb.AppendLine("YamlDictInner");
        sb.AppendLine("{");

        // Serialize
        sb.Append("    internal static void Serialize(YamlWriter yw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        yw.WriteStartMapping();");
        sb.AppendLine("        foreach (var __kvp in value)");
        sb.AppendLine("        {");
        sb.AppendLine("            yw.WritePropertyName(__kvp.Key);");
        switch (dp.ElementTypeKind)
        {
            case "string":
                sb.AppendLine("            yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value));");
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
                sb.AppendLine(
                    "            yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));"
                );
                break;
            case "boolean":
                sb.AppendLine(
                    "            yw.WriteString(__kvp.Value ? \"true\"u8 : \"false\"u8);"
                );
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    dp.ElementTypeName!
                );
                sb.Append("            ");
                sb.Append(sn);
                sb.AppendLine(".Serialize(yw, __kvp.Value);");
                break;
            }
            case "any":
                sb.AppendLine("            if (__kvp.Value == null) yw.WriteString(\"null\"u8);");
                sb.AppendLine(
                    "            else yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()!));"
                );
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlDictInner",
                    dp.ElementTypeName!
                );
                sb.Append("            ");
                sb.Append(sn);
                sb.AppendLine(".Serialize(yw, __kvp.Value);");
                break;
            }
            default:
                sb.AppendLine(
                    "            yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));"
                );
                break;
        }
        sb.AppendLine("        }");
        sb.AppendLine("        yw.WriteEndMapping();");
        sb.AppendLine("    }");

        // Deserialize
        sb.Append("    internal static ");
        sb.Append(fullName);
        sb.AppendLine(" Deserialize(ref YamlReader reader)");
        sb.AppendLine("    {");
        sb.Append("        var obj = new ");
        sb.Append(fullName);
        sb.AppendLine("();");
        sb.AppendLine("        if (reader.Read() && reader.TokenType == TokenType.ObjectStart)");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("            {");
        sb.AppendLine("                var __dk = Encoding.UTF8.GetString(reader.KeySpan);");
        switch (dp.ElementTypeKind)
        {
            case "string":
                sb.AppendLine(
                    "                obj[__dk] = Encoding.UTF8.GetString(reader.ValueSpan);"
                );
                break;
            case "int32":
                sb.AppendLine(
                    "                if (!reader.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\"); obj[__dk] = __dv;"
                );
                break;
            case "int64":
                sb.AppendLine(
                    "                obj[__dk] = long.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "int16":
                sb.AppendLine(
                    "                obj[__dk] = short.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "uint16":
                sb.AppendLine(
                    "                obj[__dk] = ushort.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "sbyte":
                sb.AppendLine(
                    "                obj[__dk] = sbyte.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "byte":
                sb.AppendLine(
                    "                obj[__dk] = byte.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "uint32":
                sb.AppendLine(
                    "                obj[__dk] = uint.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "uint64":
                sb.AppendLine(
                    "                obj[__dk] = ulong.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "float32":
                sb.AppendLine(
                    "                obj[__dk] = float.Parse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                );
                break;
            case "float64":
                sb.AppendLine(
                    "                obj[__dk] = double.Parse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                );
                break;
            case "boolean":
                sb.AppendLine(
                    "                obj[__dk] = reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'t';"
                );
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    dp.ElementTypeName!
                );
                sb.Append("                obj[__dk] = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
            case "any":
                sb.AppendLine(
                    "                obj[__dk] = Encoding.UTF8.GetString(reader.ValueSpan);"
                );
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlDictInner",
                    dp.ElementTypeName!
                );
                sb.Append("                obj[__dk] = ");
                sb.Append(sn);
                sb.AppendLine(".Deserialize(ref reader);");
                break;
            }
            default:
                sb.AppendLine(
                    "                obj[__dk] = Encoding.UTF8.GetString(reader.ValueSpan);"
                );
                break;
        }
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return obj;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Emits the property loop shared by YamlInner Serialize/SerializeBlock.
    /// Nullable properties (including nullable collections) are wrapped in a
    /// DefaultIgnoreCondition guard for parity with the top-level emit path, and a
    /// nullable VALUE type member is written through <c>.Value</c> exactly like a
    /// top-level DTO member — null is omitted, the underlying value is written
    /// otherwise.
    /// </summary>
    private static void EmitInnerSerializeProps(
        StringBuilder sb,
        ImmutableArray<PropertyInfo> props
    )
    {
        // A reference-typed nullable is already null-guarded, so it keeps the plain
        // accessor; a nullable value type must be unwrapped for the writers.
        const string baseAccessor = "value.";
        foreach (var prop in props)
        {
            bool guard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                sb,
                prop,
                baseAccessor + prop.Name,
                "        "
            );
            sb.Append("        yw.WritePropertyName(\"");
            sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\"u8);");
            // B (fix round 2): the inner helper used to pass `int?` / `MyEnum?` straight
            // to WriteNumber(int) / GetBytes(ToString()): `T?` has no implicit conversion
            // to `T`, so every object element carrying a nullable value member failed
            // with CS1503 (and a nullable enum with CS8604) on main as well. `.Value` is
            // only used inside the `!= null` guard emitted above, which is the same null
            // handling a top-level YAML DTO member gets.
            var accessor =
                prop.IsNullable && !prop.IsNullableReference
                    ? baseAccessor + prop.Name + ".Value"
                    : baseAccessor + prop.Name;
            EmitSerializeInline(sb, prop, accessor, "        ");
            if (guard)
                sb.AppendLine("        }");
        }
    }

    private static void EmitSerializeInline(
        StringBuilder s,
        PropertyInfo p,
        string accessor,
        string ind
    )
    {
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(ind);
            s.Append("{ var __tmp = new ArrayBufferWriter<byte>(); var __cnv = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(ind);
            s.Append("  __cnv.Write(__tmp, ");
            s.Append(accessor);
            s.AppendLine(");");
            s.Append(ind);
            s.AppendLine("  yw.WriteString(__tmp.WrittenSpan); }");
            return;
        }
        switch (p.TypeKind)
        {
            case "string":
                if (p.IsNullableReference)
                {
                    s.Append(ind);
                    s.Append("if (");
                    s.Append(accessor);
                    s.AppendLine(" != null)");
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(accessor);
                    s.AppendLine("));");
                    s.Append(ind);
                    s.AppendLine("else");
                    s.Append(ind);
                    s.AppendLine("    yw.WriteString(\"null\"u8);");
                }
                else
                {
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(accessor);
                    s.AppendLine("));");
                }
                break;
            case "int32":
            case "int64":
                s.Append(ind);
                s.Append("yw.WriteNumber(");
                s.Append(accessor);
                s.AppendLine(");");
                break;
            case "int16":
            case "uint16":
            case "sbyte":
            case "byte":
            case "uint32":
                s.Append(ind);
                s.Append("yw.WriteInt64(");
                s.Append(accessor);
                s.AppendLine(");");
                break;
            case "uint64":
                s.Append(ind);
                s.Append("yw.WriteUInt64(");
                s.Append(accessor);
                s.AppendLine(");");
                break;
            case "float32":
            case "float64":
                s.Append(ind);
                s.Append("yw.WriteDouble(");
                s.Append(accessor);
                s.AppendLine(");");
                break;
            case "boolean":
                s.Append(ind);
                s.Append("yw.WriteBoolean(");
                s.Append(accessor);
                s.AppendLine(");");
                break;
            case "datetime":
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                if (p.DateTimeFormat is not null)
                {
                    s.Append(".ToString(\"");
                    s.Append(p.DateTimeFormat);
                    s.Append("\")));");
                    s.AppendLine();
                }
                else
                    s.AppendLine(".ToString(\"O\")));");
                break;
            case "dateonly":
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine(
                    ".ToString(\"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture)));"
                );
                break;
            case "timeonly":
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine(
                    ".ToString(\"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture)));"
                );
                break;
            case "timespan":
                // C-14: TimeSpan has no ToString(IFormatProvider); the "c" format is
                // the invariant one the reader expects.
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine(
                    ".ToString(\"c\", System.Globalization.CultureInfo.InvariantCulture)));"
                );
                break;
            case "decimal":
                // C-14: decimal.ToString() is culture-sensitive ("1,5" under de-DE)
                // while the reader parses invariantly, so 1.5m round-tripped to 15.
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine(".ToString(System.Globalization.CultureInfo.InvariantCulture)));");
                break;
            case "guid":
            case "enum":
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine(".ToString()));");
                break;
            case "object":
                if (p.NestedProperties.Length > 0)
                {
                    var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlInner",
                        p.TypeFullName!
                    );
                    s.Append(ind);
                    s.Append(sn);
                    s.Append(".Serialize(yw, ");
                    s.Append(accessor);
                    s.AppendLine(");");
                }
                else
                {
                    s.Append(ind);
                    s.AppendLine("yw.WriteNull();");
                }
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
                // Nested sequences get one block per level (see
                // EmitYamlSequenceWrite); everything else keeps the flat shape.
                // The extended collection kinds (S7) are sequences on the wire, so they
                // take exactly the same path; only the reader has to rebuild them.
                if (IsYamlNestedList(p))
                {
                    EmitYamlSequenceWrite(s, p, accessor, ind, 0, p.TypeKind ?? "list");
                }
                else
                {
                    // The start/end pair is what makes an empty list write " []":
                    // without it a zero-item loop left the writer "after a key" and
                    // the next property name continued on the same line.
                    s.Append(ind);
                    s.AppendLine("yw.WriteStartSequence();");
                    s.Append(ind);
                    s.Append("foreach (var __item in ");
                    s.Append(accessor);
                    if (PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(p))
                        s.Append(" ?? []");
                    s.AppendLine(")");
                    s.Append(ind);
                    s.AppendLine("{");
                    // Same element writer as a top-level list member: `__item.ToString()`
                    // wrote booleans as "True" and dates in the ambient culture, which
                    // the reader could not read back (the C-22 write half).
                    EmitSerializeListElement(s, p, ind + "    ");
                    s.Append(ind);
                    s.AppendLine("}");
                    s.Append(ind);
                    s.AppendLine("yw.WriteEndSequence();");
                }
                break;
            case "dict":
                s.Append(ind);
                s.AppendLine("yw.WriteStartMapping();");
                // Same nullable-VALUE handling as the member arm (see EmitSerialize):
                // re-bind the entry to the unwrapped value after the null branch so the
                // kind switch stays one copy and keeps its bytes for non-nullable values.
                s.Append(ind);
                s.Append("foreach (var ");
                s.Append(p.ElementIsNullableValue ? "__kvpHolder" : "__kvp");
                s.Append(" in ");
                s.Append(accessor);
                if (PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(p))
                    s.Append(" ?? []");
                s.AppendLine(")");
                s.Append(ind);
                s.AppendLine("{");
                if (p.ElementIsNullableValue)
                {
                    s.Append(ind);
                    s.AppendLine("    yw.WritePropertyName(__kvpHolder.Key);");
                    s.Append(ind);
                    s.AppendLine(
                        "    if (__kvpHolder.Value == null) { yw.WriteNull(); continue; }"
                    );
                    s.Append(ind);
                    s.AppendLine(
                        "    var __kvp = System.Collections.Generic.KeyValuePair.Create(__kvpHolder.Key, __kvpHolder.Value.Value);"
                    );
                }
                else
                {
                    s.Append(ind);
                    s.AppendLine("    yw.WritePropertyName(__kvp.Key);");
                }
                switch (p.ElementTypeKind)
                {
                    case "list":
                    case "array":
                    case "hashset":
                    case "queue":
                    case "stack":
                    case "linkedlist":
                    case "immutablearray":
                    case "memory":
                    case "readonlymemory":
                        // Same S10 value position as the member arm: the inline inner helper
                        // writes an object's members, so a dict-of-collections member of a
                        // NESTED object goes through here.
                        EmitYamlDictValueSequenceWrite(s, p, ind);
                        break;
                    case "string":
                        s.Append(ind);
                        s.AppendLine("    yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value));");
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
                        s.Append(ind);
                        s.AppendLine(
                            "    yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));"
                        );
                        break;
                    case "boolean":
                        s.Append(ind);
                        s.AppendLine("    yw.WriteString(__kvp.Value ? \"true\"u8 : \"false\"u8);");
                        break;
                    case "object":
                    {
                        var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                            "YamlInner",
                            p.ElementTypeName!
                        );
                        // Nullable object value: same null-literal branch as the member arm.
                        if (p.ElementIsNullableReference)
                        {
                            s.Append(ind);
                            s.AppendLine("    if (__kvp.Value == null)");
                            s.Append(ind);
                            s.AppendLine("        yw.WriteNull();");
                            s.Append(ind);
                            s.AppendLine("    else");
                            s.Append(ind);
                            s.Append("        ");
                            s.Append(dsn);
                            s.AppendLine(".Serialize(yw, __kvp.Value);");
                        }
                        else
                        {
                            s.Append(ind);
                            s.Append("    ");
                            s.Append(dsn);
                            s.AppendLine(".Serialize(yw, __kvp.Value);");
                        }
                        break;
                    }
                    case "dict":
                    {
                        var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                            "YamlDictInner",
                            p.ElementTypeName!
                        );
                        s.Append(ind);
                        s.Append("    ");
                        s.Append(dsn);
                        s.AppendLine(".Serialize(yw, __kvp.Value);");
                        break;
                    }
                    case "any":
                        s.Append(ind);
                        s.AppendLine("    if (__kvp.Value == null) yw.WriteString(\"null\"u8);");
                        s.Append(ind);
                        s.AppendLine(
                            "    else yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()!));"
                        );
                        break;
                    default:
                        s.Append(ind);
                        s.AppendLine(
                            "    yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));"
                        );
                        break;
                }
                s.Append(ind);
                s.AppendLine("}");
                s.Append(ind);
                s.AppendLine("yw.WriteEndMapping();");
                break;
            default:
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine(".ToString()));");
                break;
        }
    }

    private static void EmitDeserializeInline(
        StringBuilder s,
        PropertyInfo p,
        string tgt,
        string pad
    )
    {
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(pad);
            s.Append("var __cnv = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(" = __cnv.Read(ref reader);");
            return;
        }
        switch (p.TypeKind)
        {
            case "string":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = Encoding.UTF8.GetString(reader.ValueSpan);");
                break;
            case "int32":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __nv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __nv;");
                break;
            case "int64":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = long.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
                break;
            case "int16":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __nv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = checked((short)__nv);");
                break;
            case "uint16":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __nv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = checked((ushort)__nv);");
                break;
            case "sbyte":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __nv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = checked((sbyte)__nv);");
                break;
            case "byte":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __nv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = checked((byte)__nv);");
                break;
            case "uint32":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = uint.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
                break;
            case "uint64":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = ulong.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
                break;
            case "float32":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(
                    " = float.Parse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                );
                break;
            case "float64":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(
                    " = double.Parse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                );
                break;
            case "boolean":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'t';");
                break;
            case "datetime":
                s.Append(pad);
                s.AppendLine("var __raw = Encoding.UTF8.GetString(reader.ValueSpan);");
                s.Append(pad);
                if (p.DateTimeFormat is not null)
                {
                    s.Append("DateTime.TryParseExact(__raw, \"");
                    s.Append(p.DateTimeFormat);
                    s.AppendLine(
                        "\", CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt);"
                    );
                }
                else
                    s.AppendLine(
                        "DateTime.TryParse(__raw, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                    );
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __dt;");
                break;
            case "guid":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = Guid.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
                break;
            case "dateonly":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(
                    " = System.DateOnly.ParseExact(Encoding.UTF8.GetString(reader.ValueSpan), \"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture);"
                );
                break;
            case "timeonly":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(
                    " = System.TimeOnly.ParseExact(Encoding.UTF8.GetString(reader.ValueSpan), \"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture);"
                );
                break;
            case "timespan":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = TimeSpan.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
                break;
            case "enum":
                s.Append(pad);
                s.AppendLine(
                    "if (!Enum.TryParse<"
                        + PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(p.TypeFullName)
                        + ">(Encoding.UTF8.GetString(reader.ValueSpan), out var __ev)) throw new System.FormatException($\"Invalid enum value at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __ev;");
                break;
            case "decimal":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(
                    " = decimal.Parse(Encoding.UTF8.GetString(reader.ValueSpan), CultureInfo.InvariantCulture);"
                );
                break;
            case "object":
                if (p.NestedProperties.Length > 0)
                {
                    var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlInner",
                        p.TypeFullName!
                    );
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.Append(" = ");
                    s.Append(sn);
                    s.AppendLine(".Deserialize(ref reader);");
                }
                if (p.NestedProperties.Length == 0)
                {
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = default!;");
                }
                break;
            case "list":
            case "array":
                s.Append(pad);
                s.Append("var __tmpList = new System.Collections.Generic.List<");
                s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object");
                s.AppendLine(">(16);");
                if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
                {
                    // Block sequence of objects: skip the indentation ObjectStart,
                    // then each String token is one item and the inner helper
                    // reads that item's ObjectStart.
                    var objElemSn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlInner",
                        p.ElementTypeName ?? "object"
                    );
                    s.Append(pad);
                    s.AppendLine("bool __more;");
                    s.Append(pad);
                    s.AppendLine(
                        "reader.Read(); // skip indentation ObjectStart after PropertyName"
                    );
                    s.Append(pad);
                    s.AppendLine("while ((__more = reader.Read())) {");
                    s.Append(pad);
                    s.AppendLine(
                        "    if (reader.TokenType != TokenType.String) break; // sequence item"
                    );
                    s.Append(pad);
                    s.Append("    var __item = ");
                    s.Append(objElemSn);
                    s.AppendLine(".Deserialize(ref reader);");
                    s.Append(pad);
                    s.AppendLine("    __tmpList.Add(__item);");
                    s.Append(pad);
                    s.AppendLine("}");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.Append(" = __tmpList");
                    if (p.TypeKind == "array")
                        s.Append(".ToArray()");
                    s.AppendLine(";");
                    break;
                }
                s.Append(pad);
                s.AppendLine("bool __more = reader.Read();");
                s.Append(pad);
                s.AppendLine("if (__more && reader.TokenType == TokenType.ObjectStart)");
                s.Append(pad);
                s.AppendLine("{");
                s.Append(pad);
                s.AppendLine(
                    "    while ((__more = reader.Read()) && reader.TokenType == TokenType.String) {"
                );
                s.Append(pad);
                s.AppendLine("        __tmpList.Add(Encoding.UTF8.GetString(reader.ValueSpan));");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine(
                    "    if (__more) __more = reader.Read(); // skip the wrapper ObjectEnd"
                );
                s.Append(pad);
                s.AppendLine("}");
                s.Append(pad);
                s.AppendLine("else");
                s.Append(pad);
                s.AppendLine("{");
                s.Append(pad);
                s.AppendLine("    while (__more && reader.TokenType == TokenType.String) {");
                s.Append(pad);
                s.AppendLine("        __tmpList.Add(Encoding.UTF8.GetString(reader.ValueSpan));");
                s.Append(pad);
                s.AppendLine("        __more = reader.Read();");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("}");
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.Append(" = __tmpList");
                if (p.TypeKind == "array")
                    s.Append(".ToArray()");
                s.AppendLine(";");
                break;
            case "dict":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.Append(" ??= new System.Collections.Generic.Dictionary<");
                s.Append(p.KeyTypeName ?? "string");
                s.Append(", ");
                s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "int");
                s.AppendLine(">();");
                s.Append(pad);
                s.AppendLine("if (reader.Read() && reader.TokenType == TokenType.ObjectStart) {");
                s.Append(pad);
                s.AppendLine(
                    "    while (reader.Read() && reader.TokenType == TokenType.PropertyName) {"
                );
                s.Append(pad);
                s.AppendLine("        var __dk = Encoding.UTF8.GetString(reader.KeySpan);");
                // Same nullable VALUE guard as the sync arm, so streaming and buffered
                // reads cannot disagree about what `n: null` means (the two arms are
                // hand-maintained twins).
                if (p.ElementIsNullableValue)
                {
                    s.Append(pad);
                    s.AppendLine("        if (reader.IsNullLiteral)");
                    s.Append(pad);
                    s.AppendLine("        {");
                    s.Append(pad);
                    s.Append("            ");
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine("[__dk] = null;");
                    s.Append(pad);
                    s.AppendLine("        }");
                    s.Append(pad);
                    s.AppendLine("        else");
                    s.Append(pad);
                    s.AppendLine("        {");
                }
                switch (p.ElementTypeKind)
                {
                    case "int32":
                        s.Append(pad);
                        s.AppendLine(
                            "        if (!reader.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                        );
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine("[__dk] = __dv;");
                        break;
                    case "int16":
                        s.Append(pad);
                        s.AppendLine(
                            "        if (!reader.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                        );
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine("[__dk] = checked((short)__dv);");
                        break;
                    case "uint16":
                        s.Append(pad);
                        s.AppendLine(
                            "        if (!reader.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                        );
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine("[__dk] = checked((ushort)__dv);");
                        break;
                    case "sbyte":
                        s.Append(pad);
                        s.AppendLine(
                            "        if (!reader.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                        );
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine("[__dk] = checked((sbyte)__dv);");
                        break;
                    case "byte":
                        s.Append(pad);
                        s.AppendLine(
                            "        if (!reader.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                        );
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine("[__dk] = checked((byte)__dv);");
                        break;
                    case "uint32":
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine(
                            "[__dk] = uint.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                        );
                        break;
                    case "uint64":
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine(
                            "[__dk] = ulong.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                        );
                        break;
                    case "object":
                    {
                        var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                            "YamlInner",
                            p.ElementTypeName!
                        );
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.Append("[__dk] = ");
                        s.Append(dsn);
                        s.AppendLine(".Deserialize(ref reader);");
                        break;
                    }
                    case "dict":
                    {
                        var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                            "YamlDictInner",
                            p.ElementTypeName!
                        );
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.Append("[__dk] = ");
                        s.Append(dsn);
                        s.AppendLine(".Deserialize(ref reader);");
                        break;
                    }
                    default:
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine("[__dk] = Encoding.UTF8.GetString(reader.ValueSpan);");
                        break;
                }
                if (p.ElementIsNullableValue)
                {
                    s.Append(pad);
                    s.AppendLine("        }");
                }
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("} // exits at ObjectEnd");
                break;
            default:
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = Encoding.UTF8.GetString(reader.ValueSpan);");
                break;
        }
    }

    private static string Gen(TypeInfo t)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>\n#nullable enable");
        sb.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        sb.AppendLine("using PicoSerDe.Core; using PicoYaml;");
        if (!string.IsNullOrEmpty(t.Namespace))
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(t.Namespace);
            sb.AppendLine(";");
        }
        sb.AppendLine();
        sb.AppendLine();

        sb.Append("file readonly struct ");
        sb.Append(t.Name);
        sb.Append("_YS : ISerializer<");
        sb.Append(t.Name);
        sb.AppendLine(">, global::PicoSerDe.Core.IOptionsSerializer<");
        sb.Append(t.Name);
        sb.AppendLine("> {");
        sb.Append("    public void Serialize(IBufferWriter<byte> w, ");
        sb.Append(t.Name);
        sb.AppendLine(" v) => Serialize(w, v, null);");
        sb.Append("    public void Serialize(IBufferWriter<byte> w, ");
        sb.Append(t.Name);
        sb.AppendLine(" v, global::PicoSerDe.Core.SerOptions? options) {");
        sb.AppendLine("        var __opts = (global::PicoYaml.YamlOptions?)options;");
        sb.AppendLine("        var yw = new YamlWriter(w, indented: __opts?.Indented ?? false);");
        if (t.TypeTag is { } tag && tag.Length > 0)
        {
            sb.Append("        yw.WriteTag(\"");
            sb.Append(tag);
            sb.AppendLine("\");");
        }
        foreach (var p in t.Properties)
            EmitSerialize(sb, p, "v", "        ");
        sb.AppendLine("    } }");

        sb.Append("file readonly struct ");
        sb.Append(t.Name);
        sb.Append("_YD : IDeserializer<");
        sb.Append(t.Name);
        sb.AppendLine("> {");
        sb.Append("    public ");
        sb.Append(t.Name);
        sb.AppendLine(" Deserialize(ReadOnlySpan<byte> d) {");
        sb.AppendLine("        var r = new YamlReader(d);");
        var ylHasCtor = !t.CtorParams.IsDefaultOrEmpty && t.CtorParams.Length > 0;
        Dictionary<string, int>? ylCtorMap = null;
        if (ylHasCtor)
        {
            ylCtorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int ylCi = 0; ylCi < t.CtorParams.Length; ylCi++)
                ylCtorMap[t.CtorParams[ylCi].Name] = ylCi;
        }
        if (ylHasCtor)
        {
            for (int yci = 0; yci < t.CtorParams.Length; yci++)
            {
                var cp = t.CtorParams[yci];
                var tn = cp.TypeFullName;
                sb.Append("        ");
                sb.Append(tn);
                sb.Append(" __cp_");
                sb.Append(yci);
                sb.AppendLine(cp.TypeKind == "string" ? " = null!;" : " = default!;");
            }
        }
        else
        {
            var ylReq = t.Properties.Where(p => p.IsRequired).ToArray();
            if (ylReq.Length > 0)
            {
                sb.Append("        var o = new ");
                sb.Append(t.Name);
                sb.AppendLine(" {");
                foreach (var rp in ylReq)
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
                sb.Append("        var o = new ");
                sb.Append(t.Name);
                sb.AppendLine("();");
            }
        }
        if (!ylHasCtor)
            sb.AppendLine("        if (!r.Read()) return o;");
        sb.AppendLine("        while (true) {");
        sb.AppendLine("            if (r.TokenType != TokenType.PropertyName) {");
        sb.AppendLine("                if (!r.Read()) break;");
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine("            var k = r.KeySpan;");
        if (t.Properties.Length == 0)
        {
            // Every member was dropped as unrepresentable: skip unknown values.
            sb.AppendLine("            if (!r.Read()) break;");
        }
        else
        {
            for (int i = 0; i < t.Properties.Length; i++)
            {
                var p = t.Properties[i];
                sb.Append("            ");
                sb.Append(i == 0 ? "if" : "else if");
                sb.Append(" (TextHelpers.Eq(k, \"");
                sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                sb.AppendLine("\"u8)) {");
                EmitDeserialize(sb, p, "o", "                ", ctorMap: ylCtorMap);
                sb.AppendLine("            }");
            }
            sb.AppendLine("            else {");
            sb.AppendLine(
                "                // Unknown property — skip its value to avoid infinite loop"
            );
            sb.AppendLine("                if (!r.Read()) break;");
            sb.AppendLine("            }");
        }
        sb.AppendLine("        }");
        if (ylHasCtor)
        {
            sb.Append("        return new ");
            sb.Append(t.Name);
            sb.Append("(");
            for (int yri = 0; yri < t.CtorParams.Length; yri++)
            {
                if (yri > 0)
                    sb.Append(", ");
                sb.Append("__cp_");
                sb.Append(yri);
            }
            sb.AppendLine(");");
        }
        else
            sb.AppendLine("        return o;");
        sb.AppendLine("    } }");
        sb.AppendLine();

        // Streaming: property-level resume reusing the synchronous dispatch
        // (ctor types fall back to buffering).
        if (!ylHasCtor)
            EmitYamlStreaming(sb, t, ylCtorMap);
        sb.AppendLine();

        sb.Append("file static class ");
        sb.Append(t.Name);
        sb.Append("_YR { [ModuleInitializer] internal static void R() { YamlSerializer.Register<");
        sb.Append(t.Name);
        sb.Append(">(new ");
        sb.Append(t.Name);
        sb.Append("_YS(), new ");
        sb.Append(t.Name);
        sb.AppendLine("_YD());");
        if (ylHasCtor)
            sb.AppendLine("            // Streaming skipped for constructor type");
        else
        {
            sb.Append("YamlSerializer.RegisterStreaming<");
            sb.Append(t.Name);
            sb.Append(">(");
            sb.Append(t.Name);
            sb.AppendLine("_YamlStreaming.DeserializeStreaming);");
        }
        sb.AppendLine("    } }");
        return sb.ToString();
    }

    /// <summary>Ref struct serializer — static class + delegate registration.</summary>
    /// <summary>
    /// Emits the streaming deserializer: the synchronous dispatch wrapped in a
    /// snapshot/rewind guard, so an incomplete chunk resumes at the member
    /// start with previously parsed members carried by <c>partial</c>.
    /// </summary>
    /// <summary>
    /// Emits the streaming deserializer: the synchronous dispatch with a
    /// per-member snapshot. Failures inside a member rewind to its start;
    /// failures while reading the next member's token keep the safe token
    /// position so the resume always advances.
    /// </summary>
    private static void EmitYamlStreaming(
        StringBuilder sb,
        TypeInfo t,
        Dictionary<string, int>? ctorMap
    )
    {
        var ysReq = t.Properties.Where(p => p.IsRequired).ToArray();
        sb.Append("file static class ");
        sb.Append(t.Name);
        sb.AppendLine("_YamlStreaming {");
        sb.Append("    internal static ReadStatus DeserializeStreaming(ref YamlReader r, ");
        sb.Append(t.Name);
        sb.AppendLine("? partial, out ");
        sb.Append(t.Name);
        sb.AppendLine("? result) {");
        sb.Append("        var o = partial ?? new ");
        sb.Append(t.Name);
        if (ysReq.Length > 0)
        {
            sb.Append(" { ");
            foreach (var rp in ysReq)
            {
                sb.Append(rp.Name);
                sb.Append(" = ");
                sb.Append(rp.TypeKind == "string" ? "\"\"" : "default");
                sb.Append(", ");
            }
            sb.Append("}");
        }
        else
        {
            sb.Append("()");
        }
        sb.AppendLine(";");
        sb.AppendLine("        result = o;");
        sb.AppendLine("        if (!r.IsResumed) {");
        sb.AppendLine("            r.Mark();");
        sb.AppendLine(
            "            if (!r.Read()) return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
        );
        sb.AppendLine("        }");
        sb.AppendLine("        while (true) {");
        sb.AppendLine("            if (r.TokenType != TokenType.PropertyName) {");
        sb.AppendLine("                r.Mark();");
        sb.AppendLine(
            "                if (!r.Read()) { result = o; return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success; }"
        );
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine("            var k = r.KeySpan;");
        if (t.Properties.Length == 0)
        {
            // Every member was dropped as unrepresentable: skip unknown values.
            sb.AppendLine("            r.Mark();");
            sb.AppendLine(
                "            if (!r.Read()) { result = o; return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success; }"
            );
        }
        else
        {
            for (int i = 0; i < t.Properties.Length; i++)
            {
                var p = t.Properties[i];
                sb.Append("            ");
                sb.Append(i == 0 ? "if" : "else if");
                sb.Append(" (TextHelpers.Eq(k, \"");
                sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                sb.AppendLine("\"u8)) {");
                EmitDeserialize(sb, p, "o", "                ", ctorMap: ctorMap);
                // A member dispatch (e.g. a nested-object helper) may stop on a
                // chunk boundary without breaking the loop; rewind to the member.
                sb.AppendLine("                if (r.NeedsMoreData) {");
                sb.AppendLine("                    r.RewindToMark();");
                sb.AppendLine("                    return ReadStatus.NeedMoreData;");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
            sb.AppendLine("            else {");
            sb.AppendLine("                r.Mark();");
            sb.AppendLine(
                "                if (!r.Read()) { result = o; return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success; }"
            );
            sb.AppendLine("            }");
        }
        sb.AppendLine("        }");
        sb.AppendLine("        // Reached via a container read that hit the buffer end.");
        sb.AppendLine(
            "#pragma warning disable CS0162 // generated: branch shape decides reachability"
        );
        sb.AppendLine("        if (r.NeedsMoreData) {");
        sb.AppendLine("            r.RewindToMark();");
        sb.AppendLine("            return ReadStatus.NeedMoreData;");
        sb.AppendLine("        }");
        sb.AppendLine("#pragma warning restore CS0162");
        sb.AppendLine("        result = o;");
        sb.AppendLine("        return ReadStatus.Success;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static string GenRefStruct(TypeInfo t)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoYaml;");
        if (!string.IsNullOrEmpty(t.Namespace))
        {
            s.Append("using ");
            s.Append(t.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        // Serializer
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("YamlSer {");
        s.Append("    public static void Serialize(IBufferWriter<byte> w, ");
        s.Append(t.Name);
        s.AppendLine(" v, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoYaml.YamlOptions?)options;");
        s.AppendLine("        var yw = new YamlWriter(w, indented: __opts?.Indented ?? false);");
        s.AppendLine("        yw.WriteStartMapping();");
        foreach (var p in t.Properties)
        {
            s.Append("        yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            s.Append("        yw.WriteNumber(");
            s.Append($"v.{p.Name}");
            s.AppendLine(");");
        }
        s.AppendLine("        yw.WriteEndMapping();");
        s.AppendLine("    } }");
        s.AppendLine();

        // Registration
        var typeRef = string.IsNullOrEmpty(t.Namespace) ? t.Name : $"{t.Namespace}.{t.Name}";
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        YamlSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            ");
        s.Append(t.Name);
        s.AppendLine("YamlSer.Serialize);");
        s.AppendLine("    } }");
        return s.ToString();
    }

    /// <summary>Generates serializer/deserializer for a top-level List&lt;T&gt; using existing element helpers.</summary>
    private static string GenList(TypeInfo t)
    {
        var s = new StringBuilder();
        var listFqn = t.FullyQualifiedName;
        var ek = t.ArrayElementKind!;
        var et = t.ArrayElementName!;

        // Synthetic PropertyInfo — drives EmitSerializeListElement / EmitDeserializeListElementTemp
        var elemP = new PropertyInfo(
            Name: "__elem",
            JsonName: "__elem",
            TypeKind: ek,
            TypeFullName: et,
            IsNullable: false,
            ElementTypeKind: ek,
            ElementTypeName: et,
            KeyTypeKind: null,
            KeyTypeName: null,
            NestedProperties: t.ArrayElementNestedProps.IsDefaultOrEmpty
                ? ImmutableArray<PropertyInfo>.Empty
                : t.ArrayElementNestedProps,
            ConverterTypeFullName: null
        );

        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using System.Runtime.CompilerServices;");
        s.AppendLine("using System.Collections.Generic; using PicoSerDe.Core; using PicoYaml;");
        s.AppendLine();

        // ── Serializer (reuses EmitSerializeListElement — YamlWriter handles top-level sequences) ──
        s.Append("file readonly struct ");
        s.Append(t.Name);
        s.Append("_YamlSer : ISerializer<");
        s.Append(listFqn);
        s.AppendLine(">, global::PicoSerDe.Core.IOptionsSerializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(listFqn);
        s.AppendLine(" v) => Serialize(w, v, null);");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(listFqn);
        s.AppendLine(" v, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoYaml.YamlOptions?)options;");
        s.AppendLine("        var yw = new YamlWriter(w, indented: __opts?.Indented ?? false);");
        s.AppendLine("        foreach (var __item in v) {");
        EmitSerializeListElement(s, elemP, "            ");
        s.AppendLine("        }");
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Deserializer (YamlReader reads top-level sequences as String tokens) ──
        s.Append("file readonly struct ");
        s.Append(t.Name);
        s.Append("_YamlDes : IDeserializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(listFqn);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.AppendLine("        var r = new YamlReader(data);");
        s.Append("        var __tmpList = new List<");
        s.Append(et);
        s.AppendLine(">(16);");
        s.AppendLine("        while (r.Read())");
        s.AppendLine("        {");
        // YamlReader reads top-level '- value' as TokenType.String, not ArrayStart.
        // EmitDeserializeListElementTemp relies on the reader already positioned at a value.
        s.AppendLine("            if (r.TokenType == TokenType.String)");
        s.AppendLine("            {");
        EmitDeserializeListElementTemp(s, elemP, "                ");
        s.AppendLine("            }");
        s.AppendLine("        }");
        s.AppendLine("        return __tmpList;");
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Registration ──
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("_YR {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void R() {");
        s.Append("        YamlSerializer.Register<");
        s.Append(listFqn);
        s.Append(">(new ");
        s.Append(t.Name);
        s.Append("_YamlSer(), new ");
        s.Append(t.Name);
        s.AppendLine("_YamlDes());");
        s.AppendLine("    } }");

        return s.ToString();
    }

    private static void EmitSerialize(StringBuilder s, PropertyInfo p, string target, string ind)
    {
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            s.Append(ind);
            s.Append("var __cnv = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(ind);
            s.Append("__cnv.Write(w, ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(");");
            return;
        }

        if (PicoSerDe.Gen.GenInfrastructure.IsSequenceKind(p.TypeKind))
        {
            // Extended collection kinds (S7) are sequences on the wire, so they write the
            // same block/flow sequence a List<T> writes. Spelling `is "list" or "array"`
            // here is what sent HashSet<int> to the scalar fallback (`yw.WriteString(...ToString())`).
            bool collGuard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                s,
                p,
                $"{target}.{p.Name}",
                ind
            );
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            // A nested sequence: one block per level, so each item's inner sequence is
            // the indented block under its dash.
            if (IsYamlNestedList(p))
            {
                EmitYamlSequenceWrite(s, p, $"{target}.{p.Name}", ind, 0, p.TypeKind ?? "list");
            }
            // Array of complex objects: use block sequence ("- " + indented properties)
            else if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
            {
                var elemTypeName = p.ElementTypeName ?? "object";
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("YamlInner", elemTypeName);
                s.Append(ind);
                s.Append("yw.WriteStartMapping(); // increase depth for sequence indentation");
                s.AppendLine();
                // The object list opens a sequence ONLY for the writer's bookkeeping: its
                // items are written with WriteStartSequenceBlock ("-" line + depth), which
                // emits the bytes, so WriteStartSequence/WriteEndSequence add nothing to an
                // existing wire — they make `_sequenceDepth` truthful so a null ELEMENT can
                // be written as "- null" by YamlWriter.WriteNull. An empty object list still
                // writes nothing at all (WriteEndSequence stays silent while `_depth > 0`).
                s.Append(ind);
                s.AppendLine("yw.WriteStartSequence();");
                s.Append(ind);
                s.Append("foreach (var __item in ");
                s.Append(
                    PicoSerDe.Gen.GenInfrastructure.SequenceItemsExpression(
                        p.TypeKind,
                        target + "." + p.Name
                    )
                );
                if (!PicoSerDe.Gen.GenInfrastructure.IsNullableSequenceKind(p.TypeKind))
                {
                    // Struct sequence (ImmutableArray/Memory/ReadOnlyMemory): no null to
                    // guard, so no `??` at all.
                    s.AppendLine(")");
                }
                else if (p.TypeKind == "array")
                {
                    s.Append(" ?? System.Array.Empty<");
                    // The ANNOTATED element name: `List<Dto?>` must get a `Dto?[]`
                    // fallback, or the `??` result is a `List<Dto>` and the assignment
                    // back into the member is CS8619 (R2 Task 4 made this shape emittable).
                    s.Append(p.ElementTypeNameAnnotated ?? elemTypeName);
                    s.AppendLine(">())");
                }
                else if (PicoSerDe.Gen.GenInfrastructure.IsExtendedCollectionKind(p.TypeKind))
                {
                    // Target-typed by the `??` left operand: the empty value of the
                    // collection the kind names (a `new List<T>(0)` fallback would not
                    // convert to HashSet<T>/Queue<T>/Stack<T>).
                    s.AppendLine(" ?? [])");
                }
                else
                {
                    s.Append(" ?? new System.Collections.Generic.List<");
                    s.Append(p.ElementTypeNameAnnotated ?? elemTypeName);
                    s.AppendLine(">(0))");
                }
                s.Append(ind);
                s.AppendLine("{");
                if (p.ElementIsNullableReference)
                {
                    // A null ELEMENT of an object list is the null literal, not a skipped
                    // item: skipping shifts every following element by one.
                    s.Append(ind);
                    s.AppendLine("    if (__item == null) { yw.WriteNull(); continue; }");
                }
                s.Append(ind);
                s.AppendLine("    yw.WriteStartSequenceBlock();");
                s.Append(ind);
                s.Append("    ");
                s.Append(sn);
                s.Append(".SerializeBlock(yw, __item);");
                s.AppendLine();
                s.Append(ind);
                s.AppendLine("    yw.WriteEndSequenceBlock();");
                s.Append(ind);
                s.AppendLine("}");
                s.Append(ind);
                s.AppendLine("yw.WriteEndSequence();");
                s.Append(ind);
                s.AppendLine("yw.WriteEndMapping(); // restore depth");
            }
            else
            {
                s.Append(ind);
                s.AppendLine("yw.WriteStartSequence();");
                s.Append(ind);
                s.Append("foreach (var __item in ");
                s.Append(
                    PicoSerDe.Gen.GenInfrastructure.SequenceItemsExpression(
                        p.TypeKind,
                        target + "." + p.Name
                    )
                );
                if (!PicoSerDe.Gen.GenInfrastructure.IsNullableSequenceKind(p.TypeKind))
                {
                    // Struct sequence: no `??` (see the array-of-objects branch above).
                    s.AppendLine(")");
                }
                else if (p.TypeKind == "array")
                {
                    s.AppendLine(" ?? [])");
                }
                else if (PicoSerDe.Gen.GenInfrastructure.IsExtendedCollectionKind(p.TypeKind))
                {
                    // See above: `?? []` is the only empty value that works for every
                    // reference-type extended collection.
                    s.AppendLine(" ?? [])");
                }
                else
                {
                    s.Append(" ?? new System.Collections.Generic.List<");
                    s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object");
                    s.AppendLine(">(0))");
                }
                s.Append(ind);
                s.AppendLine("{");
                EmitSerializeListElement(s, p, ind + "    ");
                s.Append(ind);
                s.AppendLine("}");
                s.Append(ind);
                s.AppendLine("yw.WriteEndSequence();");
            }
            if (collGuard)
            {
                s.Append(ind);
                s.AppendLine("}");
            }
        }
        else if (p.TypeKind is "object")
        {
            bool nullGuard = PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(p);
            if (nullGuard)
            {
                s.Append(ind);
                s.Append("if (");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" != null)");
                s.Append(ind);
                s.AppendLine("{");
            }
            s.Append(ind);
            if (nullGuard)
                s.Append("    ");
            s.Append("yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            if (p.NestedProperties.Length > 0)
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.TypeFullName!
                );
                s.Append(ind);
                if (nullGuard)
                    s.Append("    ");
                s.Append(sn);
                s.Append(".Serialize(yw, ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(");");
            }
            if (nullGuard)
            {
                s.Append(ind);
                s.AppendLine("}");
            }
            else
            {
                s.Append(ind);
                s.AppendLine("yw.WriteStartMapping();");
                s.Append(ind);
                s.AppendLine("yw.WriteEndMapping();");
            }
        }
        else if (p.TypeKind is "dict")
        {
            bool dictGuard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                s,
                p,
                $"{target}.{p.Name}",
                ind
            );
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            s.Append(ind);
            s.AppendLine("yw.WriteStartMapping();");
            // R2 Task 4: a nullable VALUE (Dictionary<string,int?>) cannot go through the
            // per-kind emitters verbatim — none of them takes an `int?` (WriteNumber(int)
            // would be CS1503, `.ToString()` would silently write ""). The entry is
            // re-bound to a KeyValuePair of the UNWRAPPED value after the null branch, so
            // the kind switch below stays exactly one copy and its emitted text does not
            // change for any non-nullable value.
            s.Append(ind);
            s.Append("foreach (var ");
            s.Append(p.ElementIsNullableValue ? "__kvpHolder" : "__kvp");
            s.Append(" in ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" ?? new System.Collections.Generic.Dictionary<");
            s.Append(p.KeyTypeName ?? "string");
            s.Append(", ");
            s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object");
            s.AppendLine(">(0))");
            s.Append(ind);
            s.AppendLine("{");
            if (p.ElementIsNullableValue)
            {
                s.Append(ind);
                s.AppendLine("    yw.WritePropertyName(__kvpHolder.Key);");
                s.Append(ind);
                s.AppendLine("    if (__kvpHolder.Value == null) { yw.WriteNull(); continue; }");
                s.Append(ind);
                s.AppendLine(
                    "    var __kvp = System.Collections.Generic.KeyValuePair.Create(__kvpHolder.Key, __kvpHolder.Value.Value);"
                );
            }
            else
            {
                s.Append(ind);
                s.AppendLine("    yw.WritePropertyName(__kvp.Key);");
            }
            switch (p.ElementTypeKind)
            {
                case "list":
                case "array":
                case "hashset":
                case "queue":
                case "stack":
                case "linkedlist":
                case "immutablearray":
                case "memory":
                case "readonlymemory":
                    // S10: the dict VALUE is a collection, written as one block sequence
                    // under the entry's own key (see EmitYamlDictValueSequenceWrite).
                    EmitYamlDictValueSequenceWrite(s, p, ind);
                    break;
                case "string":
                    s.Append(ind);
                    s.AppendLine("    yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value));");
                    break;
                case "int32":
                    s.Append(ind);
                    s.AppendLine("    yw.WriteNumber(__kvp.Value);");
                    break;
                case "int64":
                case "float32":
                case "float64":
                case "int16":
                case "uint16":
                case "sbyte":
                case "byte":
                case "uint32":
                case "uint64":
                    s.Append(ind);
                    s.AppendLine(
                        "    yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));"
                    );
                    break;
                case "boolean":
                    s.Append(ind);
                    s.AppendLine("    yw.WriteString(__kvp.Value ? \"true\"u8 : \"false\"u8);");
                    break;
                case "dict":
                {
                    var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlDictInner",
                        p.ElementTypeName!
                    );
                    s.Append(ind);
                    s.Append("    ");
                    s.Append(dsn);
                    s.AppendLine(".Serialize(yw, __kvp.Value);");
                    break;
                }
                case "object":
                {
                    var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlInner",
                        p.ElementTypeName!
                    );
                    // A nullable object value (Dictionary<string,Dto?>): null literal, not a
                    // call into the inner helper with a possibly-null argument.
                    if (p.ElementIsNullableReference)
                    {
                        s.Append(ind);
                        s.AppendLine("    if (__kvp.Value == null)");
                        s.Append(ind);
                        s.AppendLine("        yw.WriteNull();");
                        s.Append(ind);
                        s.AppendLine("    else");
                        s.Append(ind);
                        s.Append("        ");
                        s.Append(dsn);
                        s.AppendLine(".Serialize(yw, __kvp.Value);");
                    }
                    else
                    {
                        s.Append(ind);
                        s.Append("    ");
                        s.Append(dsn);
                        s.AppendLine(".Serialize(yw, __kvp.Value);");
                    }
                    break;
                }
                case "any":
                    s.Append(ind);
                    s.AppendLine("    if (__kvp.Value == null) yw.WriteString(\"null\"u8);");
                    s.Append(ind);
                    s.AppendLine(
                        "    else yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()!));"
                    );
                    break;
                default:
                    s.Append(ind);
                    s.AppendLine("    yw.WriteString(__kvp.Value.ToString());");
                    break;
            }
            s.Append(ind);
            s.AppendLine("}");
            s.Append(ind);
            s.AppendLine("yw.WriteEndMapping();");
            if (dictGuard)
            {
                s.Append(ind);
                s.AppendLine("}");
            }
        }
        else if (p.IsNullable)
        {
            // YAML reader has no null-literal support yet — writing 'key:' reads
            // back as default, breaking round-trip fidelity. Null values are
            // therefore always omitted (same group as TOML/INI).
            PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(s, p, $"{target}.{p.Name}", ind);
            s.Append(ind);
            s.Append("    yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            string valAccessor = p.IsNullableReference
                ? $"{target}.{p.Name}"
                : $"{target}.{p.Name}!.Value";
            switch (p.TypeKind)
            {
                case "string":
                    if (p.IsNullableReference)
                    {
                        s.Append(ind);
                        s.Append("    if (");
                        s.Append(valAccessor);
                        s.AppendLine(" != null)");
                        s.Append(ind);
                        s.Append("        yw.WriteString(Encoding.UTF8.GetBytes(");
                        s.Append(valAccessor);
                        s.AppendLine("));");
                    }
                    else
                    {
                        s.Append(ind);
                        s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                        s.Append(valAccessor);
                        s.AppendLine("));");
                    }
                    break;
                case "int32":
                    s.Append(ind);
                    s.Append("    yw.WriteNumber(");
                    s.Append(valAccessor);
                    s.AppendLine(");");
                    break;
                case "int64":
                    s.Append(ind);
                    s.Append("    yw.WriteInt64(");
                    s.Append(valAccessor);
                    s.AppendLine(");");
                    break;
                case "int16":
                case "uint16":
                case "sbyte":
                case "byte":
                case "uint32":
                    s.Append(ind);
                    s.Append("    yw.WriteInt64(");
                    s.Append(valAccessor);
                    s.AppendLine(");");
                    break;
                case "uint64":
                    s.Append(ind);
                    s.Append("    yw.WriteUInt64(");
                    s.Append(valAccessor);
                    s.AppendLine(");");
                    break;
                case "float32":
                case "float64":
                    s.Append(ind);
                    s.Append("    yw.WriteDouble(");
                    s.Append(valAccessor);
                    s.AppendLine(");");
                    break;
                case "boolean":
                    s.Append(ind);
                    s.Append("    yw.WriteBoolean(");
                    s.Append(valAccessor);
                    s.AppendLine(");");
                    break;
                case "datetime":
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(valAccessor);
                    if (p.DateTimeFormat is not null)
                    {
                        s.Append(".ToString(\"");
                        s.Append(p.DateTimeFormat);
                        s.Append("\")));");
                        s.AppendLine();
                    }
                    else
                        s.AppendLine(".ToString(\"O\")));");
                    break;
                case "dateonly":
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(valAccessor);
                    s.AppendLine(
                        ".ToString(\"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture)));"
                    );
                    break;
                case "timeonly":
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(valAccessor);
                    s.AppendLine(
                        ".ToString(\"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture)));"
                    );
                    break;
                case "timespan":
                    // C-14: TimeSpan has no ToString(IFormatProvider); "c" is invariant.
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(valAccessor);
                    s.AppendLine(
                        ".ToString(\"c\", System.Globalization.CultureInfo.InvariantCulture)));"
                    );
                    break;
                case "decimal":
                    // C-14: culture-sensitive ToString() emits "1,5" under de-DE
                    // while the reader parses invariantly (round-trip gives 15).
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(valAccessor);
                    s.AppendLine(".ToString(System.Globalization.CultureInfo.InvariantCulture)));");
                    break;
                default:
                    // guid/enum: ToString() is already culture-independent.
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(valAccessor);
                    s.AppendLine(".ToString()));");
                    break;
            }
            s.Append(ind);
            s.AppendLine("}");
        }
        else
        {
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            switch (p.TypeKind)
            {
                case "string":
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine("));");
                    break;
                case "int32":
                    s.Append(ind);
                    s.Append("yw.WriteNumber(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(");");
                    break;
                case "int64":
                    s.Append(ind);
                    s.Append("yw.WriteInt64(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(");");
                    break;
                case "int16":
                case "uint16":
                case "sbyte":
                case "byte":
                case "uint32":
                    s.Append(ind);
                    s.Append("yw.WriteInt64(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(");");
                    break;
                case "uint64":
                    s.Append(ind);
                    s.Append("yw.WriteUInt64(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(");");
                    break;
                case "float32":
                case "float64":
                    s.Append(ind);
                    s.Append("yw.WriteDouble(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(");");
                    break;
                case "boolean":
                    s.Append(ind);
                    s.Append("yw.WriteBoolean(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(");");
                    break;
                case "datetime":
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    if (p.DateTimeFormat is not null)
                    {
                        s.Append(".ToString(\"");
                        s.Append(p.DateTimeFormat);
                        s.Append("\")));");
                        s.AppendLine();
                    }
                    else
                        s.AppendLine(".ToString(\"O\")));");
                    break;
                case "dateonly":
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(
                        ".ToString(\"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture)));"
                    );
                    break;
                case "timeonly":
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(
                        ".ToString(\"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture)));"
                    );
                    break;
                case "timespan":
                    // C-14: TimeSpan has no ToString(IFormatProvider); "c" is invariant.
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(
                        ".ToString(\"c\", System.Globalization.CultureInfo.InvariantCulture)));"
                    );
                    break;
                case "decimal":
                    // C-14: culture-sensitive ToString() emits "1,5" under de-DE
                    // while the reader parses invariantly (round-trip gives 15).
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(".ToString(System.Globalization.CultureInfo.InvariantCulture)));");
                    break;
                default:
                    // guid/enum: ToString() is already culture-independent.
                    s.Append(ind);
                    s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(".ToString()));");
                    break;
            }
        }
    }

    private static void EmitSerializeListElement(
        StringBuilder s,
        PropertyInfo p,
        string ind,
        string io = "__item"
    )
    {
        // A nullable VALUE element (List<int?>, List<List<DateTime?>>) needs the null branch
        // first: no writer overload takes an int?, so the unwrapped value goes to the
        // non-nullable core and a null becomes the YAML null literal. Same two-step shape the
        // JSON generator uses (EmitSerializeElement) — "unwrapped core" + "null literal",
        // never "skip the element", which is what an omission cannot distinguish from a
        // missing element after the round trip.
        if (p.ElementIsNullableValue)
        {
            s.Append(ind);
            s.AppendLine("if (" + io + " == null)");
            s.Append(ind);
            s.AppendLine("    yw.WriteNull();");
            s.Append(ind);
            s.AppendLine("else");
            s.Append(ind);
            s.AppendLine("{");
            EmitSerializeListElement(
                s,
                p with
                {
                    ElementIsNullableValue = false,
                },
                ind + "    ",
                io + ".Value"
            );
            s.Append(ind);
            s.AppendLine("}");
            return;
        }
        switch (p.ElementTypeKind)
        {
            case "string":
                if (p.ElementIsNullableReference)
                {
                    s.Append(ind);
                    s.AppendLine("if (" + io + " != null)");
                    s.Append(ind);
                    s.AppendLine("    yw.WriteSequenceItem(Encoding.UTF8.GetBytes(" + io + "));");
                    // R2 Task 4: a null ELEMENT is written as the null literal, not skipped.
                    // Skipping is what made `List<string?>` read back one element short, and
                    // the reader has always been able to take `- null`.
                    s.Append(ind);
                    s.AppendLine("else");
                    s.Append(ind);
                    s.AppendLine("    yw.WriteNull();");
                }
                else
                {
                    s.Append(ind);
                    s.AppendLine("yw.WriteSequenceItem(Encoding.UTF8.GetBytes(" + io + "));");
                }
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlDictInner",
                    p.ElementTypeName!
                );
                // Nullable dict element: null literal, not a call with a null argument.
                if (p.ElementIsNullableReference)
                {
                    s.Append(ind);
                    s.AppendLine("if (" + io + " == null)");
                    s.Append(ind);
                    s.AppendLine("    yw.WriteNull();");
                    s.Append(ind);
                    s.AppendLine("else");
                    s.Append(ind);
                    s.Append("    ");
                    s.Append(sn);
                    s.AppendLine(".Serialize(yw, " + io + ");");
                }
                else
                {
                    s.Append(ind);
                    s.Append(sn);
                    s.AppendLine(".Serialize(yw, " + io + ");");
                }
                break;
            }
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.ElementTypeName!
                );
                // A nullable object element (S14): the null literal, never a call into the
                // inner helper with a null (that is a CS8604 today and an NRE after).
                if (p.ElementIsNullableReference)
                {
                    s.Append(ind);
                    s.AppendLine("if (" + io + " == null)");
                    s.Append(ind);
                    s.AppendLine("    yw.WriteNull();");
                    s.Append(ind);
                    s.AppendLine("else");
                    s.Append(ind);
                    s.AppendLine("{");
                }
                var inner = p.ElementIsNullableReference ? ind + "    " : ind;
                s.Append(inner);
                s.AppendLine("yw.WriteStartSequenceBlock();");
                s.Append(inner);
                s.Append("    ");
                s.Append(sn);
                s.AppendLine(".SerializeBlock(yw, " + io + ");");
                s.Append(inner);
                s.AppendLine("yw.WriteEndSequenceBlock();");
                if (p.ElementIsNullableReference)
                {
                    s.Append(ind);
                    s.AppendLine("}");
                }
                break;
            }
            case "int32":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(" + io + ");");
                break;
            case "int64":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem((long)" + io + ");");
                break;
            case "float32":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(" + io + ");");
                break;
            case "float64":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(" + io + ");");
                break;
            case "boolean":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(" + io + " ? \"true\"u8 : \"false\"u8);");
                break;
            case "datetime":
                s.Append(ind);
                s.AppendLine(
                    "yw.WriteSequenceItem(Encoding.UTF8.GetBytes("
                        + io
                        + ".ToString(\"O\", System.Globalization.CultureInfo.InvariantCulture)));"
                );
                break;
            case "dateonly":
                s.Append(ind);
                s.AppendLine(
                    "yw.WriteSequenceItem(Encoding.UTF8.GetBytes("
                        + io
                        + ".ToString(\"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture)));"
                );
                break;
            case "timeonly":
                s.Append(ind);
                s.AppendLine(
                    "yw.WriteSequenceItem(Encoding.UTF8.GetBytes("
                        + io
                        + ".ToString(\"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture)));"
                );
                break;
            default:
                s.Append(ind);
                s.AppendLine(
                    "yw.WriteSequenceItem(Encoding.UTF8.GetBytes(" + io + ".ToString()));"
                );
                break;
        }
    }

    private static void EmitDeserialize(
        StringBuilder s,
        PropertyInfo p,
        string tgt,
        string pad,
        bool ctorAssign = false,
        IReadOnlyDictionary<string, int>? ctorMap = null
    )
    {
        // If property is in ctorMap, redirect to __cp_N
        if (!ctorAssign && ctorMap is not null && ctorMap.TryGetValue(p.Name, out var __ci))
        {
            EmitDeserialize(s, p, $"__cp_{__ci}", pad, ctorAssign: true);
            return;
        }
        void EmitTgt()
        {
            if (ctorAssign)
                s.Append(tgt);
            else
            {
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
            }
        }
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(pad);
            s.Append("var __cnv = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(pad);
            EmitTgt();
            s.AppendLine(" = __cnv.Read(ref r);");
            s.Append(pad);
            s.AppendLine("if (!r.Read()) break;");
            return;
        }

        if (PicoSerDe.Gen.GenInfrastructure.IsSequenceKind(p.TypeKind))
        {
            // Nested sequences (List<List<T>>, List<T[]>, ...) decode level by level;
            // the flat shapes below stay byte-identical for every other list member.
            // Extended collection kinds (S7) are flat sequences here and are converted to
            // the DECLARED type by EmitCollectionFromList at the end.
            if (IsYamlNestedList(p))
            {
                EmitYamlNestedListDeserialize(
                    s,
                    p,
                    pad,
                    ctorAssign ? tgt : tgt + "." + p.Name,
                    p.TypeKind!
                );
                return;
            }
            s.Append(pad);
            s.Append("var __tmpList = new System.Collections.Generic.List<");
            s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object");
            s.AppendLine(">(16);");
            s.Append(pad);
            s.AppendLine("bool __more;");
            // The wrapper `ObjectStart` is optional: `Items:` followed by a sibling
            // property has no wrapper at all, while an indented block sequence has
            // one. Probing it must never consume the sibling `PropertyName` — that
            // is C-21 — so the read is only kept when it really is the wrapper,
            // and the reader is never marked (the streaming dispatcher owns the
            // mark, and RewindToMark also clears NeedsMoreData). Both paths share
            // this one shape, so sync and streaming cannot drift apart again.
            s.Append(pad);
            s.AppendLine("__more = r.Read();");
            s.Append(pad);
            s.AppendLine("if (!__more)");
            s.Append(pad);
            s.AppendLine("{");
            s.Append(pad);
            s.AppendLine("    // The document ended at this key: there is no list to read.");
            s.Append(pad);
            s.AppendLine("}");
            s.Append(pad);
            s.AppendLine("else if (r.TokenType == TokenType.ObjectStart)");
            s.Append(pad);
            s.AppendLine("{");
            s.Append(pad);
            s.AppendLine("    // Indented block sequence: the wrapper ObjectStart was consumed.");
            if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.ElementTypeName ?? "object"
                );
                s.Append(pad);
                s.AppendLine("    while ((__more = r.Read()) && r.TokenType == TokenType.String)");
                s.Append(pad);
                s.AppendLine("    {");
                if (p.ElementIsNullableReference)
                {
                    // A null ELEMENT of an object list (S14): the null literal token belongs
                    // in the list as null, not through the inner helper (which would build a
                    // default object out of a token it cannot read). `IsNestedSequenceItem`
                    // excludes the BARE DASH: an object item is written as "-" plus its
                    // indented block, and that dash reads as an empty value (`IsNullLiteral`
                    // is true for it), so a guard without this check eats every object item.
                    s.Append(pad);
                    s.AppendLine(
                        "        if (r.IsNullLiteral && !r.IsNestedSequenceItem) { __tmpList.Add(null!); continue; }"
                    );
                }
                s.Append(pad);
                s.Append("        var __item = ");
                s.Append(sn);
                s.AppendLine(
                    ".Deserialize(ref r); // Deserialize reads ObjectStart(item) internally"
                );
                s.Append(pad);
                s.AppendLine("        __tmpList.Add(__item);");
                s.Append(pad);
                s.AppendLine("    }");
            }
            else
            {
                s.Append(pad);
                s.AppendLine("    while ((__more = r.Read()) && r.TokenType == TokenType.String)");
                s.Append(pad);
                s.AppendLine("    {");
                EmitDeserializeListElementTemp(s, p, pad + "        ");
                s.Append(pad);
                s.AppendLine("    }");
            }
            s.Append(pad);
            s.AppendLine(
                "    // Skip the wrapper ObjectEnd — but only when the loop really stopped on"
            );
            s.Append(pad);
            s.AppendLine(
                "    // it. `__more` then reports whether a member token is still available."
            );
            s.Append(pad);
            s.AppendLine(
                "    if (__more && r.TokenType == TokenType.ObjectEnd) __more = r.Read();"
            );
            s.Append(pad);
            s.AppendLine("}");
            s.Append(pad);
            s.AppendLine("else if (r.TokenType == TokenType.String)");
            s.Append(pad);
            s.AppendLine("{");
            s.Append(pad);
            s.AppendLine(
                "    // No wrapper: a flat sequence follows the key (`- item` at the same"
            );
            s.Append(pad);
            s.AppendLine(
                "    // indentation). The probe token belongs to the list, so consume it here."
            );
            if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
            {
                var sn2 = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.ElementTypeName ?? "object"
                );
                s.Append(pad);
                s.AppendLine("    do");
                s.Append(pad);
                s.AppendLine("    {");
                if (p.ElementIsNullableReference)
                {
                    // Same bare-dash exclusion as the block-sequence arm above.
                    s.Append(pad);
                    s.AppendLine(
                        "        if (r.IsNullLiteral && !r.IsNestedSequenceItem) { __tmpList.Add(null!); }"
                    );
                    s.Append(pad);
                    s.AppendLine("        else");
                    s.Append(pad);
                    s.AppendLine("        {");
                }
                var objPad = p.ElementIsNullableReference ? pad + "            " : pad + "        ";
                s.Append(objPad);
                s.Append("var __item = ");
                s.Append(sn2);
                s.AppendLine(".Deserialize(ref r);");
                s.Append(objPad);
                s.AppendLine("__tmpList.Add(__item);");
                if (p.ElementIsNullableReference)
                {
                    s.Append(pad);
                    s.AppendLine("        }");
                }
                s.Append(pad);
                s.AppendLine(
                    "    } while ((__more = r.Read()) && r.TokenType == TokenType.String);"
                );
            }
            else
            {
                s.Append(pad);
                s.AppendLine("    while (__more && r.TokenType == TokenType.String)");
                s.Append(pad);
                s.AppendLine("    {");
                EmitDeserializeListElementTemp(s, p, pad + "        ");
                s.Append(pad);
                s.AppendLine("        __more = r.Read();");
                s.Append(pad);
                s.AppendLine("    }");
            }
            s.Append(pad);
            s.AppendLine("}");
            s.Append(pad);
            s.AppendLine("else");
            s.Append(pad);
            s.AppendLine("{");
            s.Append(pad);
            s.AppendLine(
                "    // No wrapper and no sequence item: the list is empty and the probe token"
            );
            s.Append(pad);
            s.AppendLine(
                "    // is already the sibling property this member must not eat (C-21). It is"
            );
            s.Append(pad);
            s.AppendLine(
                "    // left in place, so `__more` stays true and the loop re-dispatches it."
            );
            s.Append(pad);
            s.AppendLine("}");
            PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
                s,
                p.TypeKind!,
                "__tmpList",
                ctorAssign ? tgt : tgt + "." + p.Name,
                p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object",
                pad
            );
            s.Append(pad);
            s.AppendLine("if (!__more) break;");
        }
        else if (p.TypeKind is "object" && p.NestedProperties.Length > 0)
        {
            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("YamlInner", p.TypeFullName!);
            s.Append(pad);
            EmitTgt();
            s.Append(" = ");
            s.Append(sn);
            s.AppendLine(".Deserialize(ref r);");
            s.Append(pad);
            s.AppendLine("if (r.TokenType == TokenType.None) break; // document ended here");
        }
        else if (p.TypeKind is "dict")
        {
            s.Append(pad);
            EmitTgt();
            s.Append(" ??= new System.Collections.Generic.Dictionary<");
            s.Append(p.KeyTypeName ?? "string");
            s.Append(", ");
            s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "int");
            s.AppendLine(">();");
            s.Append(pad);
            s.AppendLine("if (!r.Read()) break; // document ended here");
            s.Append(pad);
            var sequenceValue = PicoSerDe.Gen.GenInfrastructure.IsSequenceKind(p.ElementTypeKind);
            s.AppendLine("if (r.TokenType == TokenType.ObjectStart) {");
            s.Append(pad);
            if (sequenceValue)
            {
                // A SEQUENCE value arm ends on the wrapper's ObjectEnd, one Read away from
                // the next key, while every scalar arm never reads and leaves the reader ON
                // the current "key: value" token. `__dkHaveNext` lets the entry loop accept
                // both alignments; without it the loop either eats a key after every
                // sequence entry or re-processes a scalar one forever. Emitted only for the
                // sequence case, so a flat dict's generated code is unchanged.
                s.AppendLine("    bool __dkHaveNext = false;");
                s.Append(pad);
                s.AppendLine("    while (true) {");
                s.Append(pad);
                s.AppendLine("        if (!__dkHaveNext && !r.Read()) break;");
                s.Append(pad);
                s.AppendLine("        __dkHaveNext = false;");
                s.Append(pad);
                s.AppendLine("        if (r.TokenType != TokenType.PropertyName) break;");
            }
            else
                s.AppendLine("    while (r.Read() && r.TokenType == TokenType.PropertyName) {");
            s.Append(pad);
            s.AppendLine("        var __dk = Encoding.UTF8.GetString(r.KeySpan);");
            // R2 Task 4: `null` is a null literal, not a value the scalar parsers may see
            // (they throw). A nullable VALUE reads it into the entry as null; every other
            // value kind keeps the unguarded switch below, byte for byte.
            if (p.ElementIsNullableValue)
            {
                s.Append(pad);
                s.AppendLine("        if (r.IsNullLiteral)");
                s.Append(pad);
                s.AppendLine("        {");
                s.Append(pad);
                s.Append("            ");
                EmitTgt();
                s.AppendLine("[__dk] = null;");
                s.Append(pad);
                s.AppendLine("        }");
                s.Append(pad);
                s.AppendLine("        else");
                s.Append(pad);
                s.AppendLine("        {");
            }
            switch (p.ElementTypeKind)
            {
                case "int32":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = __dv;");
                    break;
                case "int16":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = checked((short)__dv);");
                    break;
                case "uint16":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = checked((ushort)__dv);");
                    break;
                case "sbyte":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = checked((sbyte)__dv);");
                    break;
                case "byte":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = checked((byte)__dv);");
                    break;
                case "uint32":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = checked((uint)__dv);");
                    break;
                case "uint64":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetUInt64(out var __dv)) throw new System.FormatException($\"Expected a 64-bit unsigned integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = __dv;");
                    break;
                case "int64":
                    s.Append(pad);
                    s.AppendLine(
                        "        if (!r.TryGetInt64(out var __dv)) throw new System.FormatException($\"Expected a 64-bit integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = __dv;");
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
                    // S10: the dict VALUE is a collection. Read the block/flow sequence into
                    // a temp list of the value's ITEM type and convert it to the declared
                    // kind - the same pair the member arm uses, one level down.
                    var __tgtSb = new StringBuilder();
                    __tgtSb.Append(pad);
                    __tgtSb.Append("        ");
                    __tgtSb.Append(tgt);
                    if (!ctorAssign)
                    {
                        __tgtSb.Append('.');
                        __tgtSb.Append(p.Name);
                    }
                    __tgtSb.Append("[__dk]");
                    EmitYamlDictValueSequenceRead(s, p, __tgtSb.ToString(), pad + "        ");
                    s.Append(pad);
                    // The sequence arm ends on the wrapper's ObjectEnd, one Read away from
                    // the next key: tell the entry loop so it does not eat that token.
                    s.AppendLine("        __dkHaveNext = true;");
                    break;
                }
                case "dict":
                {
                    var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlDictInner",
                        p.ElementTypeName!
                    );
                    if (p.ElementIsNullableReference)
                    {
                        s.Append(pad);
                        // Only an EXPLICIT null token (`n: null` / `n: ~`) is a null value
                        // here: in a dictionary entry an empty value followed by an indented
                        // block IS the object/dict value, and that token looks like an empty
                        // (i.e. null-ish) value to IsNullLiteral.
                        s.AppendLine("        if (r.IsNullLiteral && r.ValueSpan.Length > 0)");
                        s.Append(pad);
                        s.AppendLine("        {");
                        s.Append(pad);
                        s.Append("            ");
                        EmitTgt();
                        s.AppendLine("[__dk] = null;");
                        s.Append(pad);
                        s.AppendLine("        }");
                        s.Append(pad);
                        s.AppendLine("        else");
                        s.Append(pad);
                        s.AppendLine("        {");
                        s.Append(pad);
                        s.Append("            ");
                        EmitTgt();
                        s.Append("[__dk] = ");
                        s.Append(dsn);
                        s.AppendLine(".Deserialize(ref r);");
                        s.Append(pad);
                        s.AppendLine("        }");
                    }
                    else
                    {
                        s.Append(pad);
                        s.Append("        ");
                        EmitTgt();
                        s.Append("[__dk] = ");
                        s.Append(dsn);
                        s.AppendLine(".Deserialize(ref r);");
                    }
                    break;
                }
                case "object":
                {
                    var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlInner",
                        p.ElementTypeName!
                    );
                    // S14: a null object VALUE is a null literal, not an inner-helper call
                    // on a token the helper cannot read.
                    if (p.ElementIsNullableReference)
                    {
                        s.Append(pad);
                        // Only an EXPLICIT null token (`n: null` / `n: ~`) is a null value
                        // here: in a dictionary entry an empty value followed by an indented
                        // block IS the object/dict value, and that token looks like an empty
                        // (i.e. null-ish) value to IsNullLiteral.
                        s.AppendLine("        if (r.IsNullLiteral && r.ValueSpan.Length > 0)");
                        s.Append(pad);
                        s.AppendLine("        {");
                        s.Append(pad);
                        s.Append("            ");
                        EmitTgt();
                        s.AppendLine("[__dk] = null;");
                        s.Append(pad);
                        s.AppendLine("        }");
                        s.Append(pad);
                        s.AppendLine("        else");
                        s.Append(pad);
                        s.AppendLine("        {");
                        s.Append(pad);
                        s.Append("            ");
                        EmitTgt();
                        s.Append("[__dk] = ");
                        s.Append(dsn);
                        s.AppendLine(".Deserialize(ref r);");
                        s.Append(pad);
                        s.AppendLine("        }");
                    }
                    else
                    {
                        s.Append(pad);
                        s.Append("        ");
                        EmitTgt();
                        s.Append("[__dk] = ");
                        s.Append(dsn);
                        s.AppendLine(".Deserialize(ref r);");
                    }
                    break;
                }
                default:
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
            }
            if (p.ElementIsNullableValue)
            {
                s.Append(pad);
                s.AppendLine("        }");
            }
            s.Append(pad);
            s.AppendLine("    }");
            s.Append(pad);
            s.AppendLine("} // exits at ObjectEnd");
        }
        else
        {
            // C-30: a null literal must not reach the scalar parsers.
            EmitNullLiteralGuard(s, p, EmitTgt, pad);
            switch (p.TypeKind)
            {
                case "string":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
                case "int32":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = __v;");
                    break;
                case "int64":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = long.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "int16":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = checked((short)__v);");
                    break;
                case "uint16":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = checked((ushort)__v);");
                    break;
                case "sbyte":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = checked((sbyte)__v);");
                    break;
                case "byte":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = checked((byte)__v);");
                    break;
                case "uint32":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = uint.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "uint64":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = ulong.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "float32":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(
                        " = float.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                    );
                    break;
                case "float64":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(
                        " = double.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                    );
                    break;
                case "boolean":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetBool(out var __v)) throw new System.FormatException($\"Expected a boolean at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = __v;");
                    break;
                case "datetime":
                    s.Append(pad);
                    s.AppendLine("var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                    s.Append(pad);
                    if (p.DateTimeFormat is not null)
                    {
                        s.Append("if (!System.DateTime.TryParseExact(__raw, \"");
                        s.Append(p.DateTimeFormat);
                        s.AppendLine(
                            "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt)) throw new System.FormatException($\"Invalid DateTime at offset {r.BytesConsumed}\");"
                        );
                    }
                    else
                        s.AppendLine(
                            "if (!System.DateTime.TryParse(__raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt)) throw new System.FormatException($\"Invalid DateTime at offset {r.BytesConsumed}\");"
                        );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = __dt;");
                    break;
                case "long":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = long.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "guid":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = System.Guid.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "dateonly":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(
                        " = System.DateOnly.ParseExact(Encoding.UTF8.GetString(r.ValueSpan), \"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture);"
                    );
                    break;
                case "timeonly":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(
                        " = System.TimeOnly.ParseExact(Encoding.UTF8.GetString(r.ValueSpan), \"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture);"
                    );
                    break;
                case "timespan":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = System.TimeSpan.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "enum":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!System.Enum.TryParse<"
                            + PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(p.TypeFullName)
                            + ">(Encoding.UTF8.GetString(r.ValueSpan), out var __v)) throw new System.FormatException($\"Invalid enum value at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = __v;");
                    break;
                case "decimal":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(
                        " = decimal.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                    );
                    break;
                default:
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
            }
            s.Append(pad);
            s.AppendLine("}");
            s.Append(pad);
            s.AppendLine("if (!r.Read()) break;");
        }
    }

    /// <summary>
    /// Review C-30: emits the null-literal guard that guards the scalar parse
    /// branch. A nullable member takes <c>null</c>, a non-nullable value type
    /// fails loudly, and a non-nullable string keeps the existing NRT-lenient
    /// contract (<c>null!</c>). The guard opens an <c>else</c> block the caller
    /// must close after the parse branch.
    /// </summary>
    private static void EmitNullLiteralGuard(
        StringBuilder s,
        PropertyInfo p,
        System.Action emitTarget,
        string pad
    )
    {
        s.Append(pad);
        s.AppendLine("if (r.IsNullLiteral)");
        s.Append(pad);
        s.AppendLine("{");
        if (p.IsNullable || p.IsNullableReference)
        {
            s.Append(pad);
            s.Append("    ");
            emitTarget();
            s.AppendLine(" = null;");
        }
        else if (PicoSerDe.Gen.GenInfrastructure.IsValueTypeKind(p.TypeKind))
        {
            s.Append(pad);
            s.Append("    throw new System.FormatException($\"null value for non-nullable member ");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\");");
        }
        else
        {
            s.Append(pad);
            s.Append("    ");
            emitTarget();
            s.AppendLine(" = null!;");
        }
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else");
        s.Append(pad);
        s.AppendLine("{");
    }

    private static void EmitDeserializeListElementTemp(
        StringBuilder s,
        PropertyInfo p,
        string pad,
        string lo = "__tmpList"
    )
    {
        // A nullable VALUE element (List<int?>, List<List<DateTime?>>): `null` is a null
        // literal and must never reach the scalar parsers (they throw), and the value goes
        // into the nullable slot unchanged — `T` converts to `T?` implicitly, so the
        // unwrapped core below needs no cast. The unguarded core is byte-identical to what
        // every non-nullable element kind already emits.
        if (p.ElementIsNullableValue)
        {
            s.Append(pad);
            s.AppendLine("if (r.IsNullLiteral)");
            s.Append(pad);
            s.AppendLine("{");
            s.Append(pad);
            s.AppendLine("    " + lo + ".Add(null);");
            s.Append(pad);
            s.AppendLine("}");
            s.Append(pad);
            s.AppendLine("else");
            s.Append(pad);
            s.AppendLine("{");
            EmitDeserializeListElementTemp(
                s,
                p with
                {
                    ElementIsNullableValue = false,
                },
                pad + "    ",
                lo
            );
            s.Append(pad);
            s.AppendLine("}");
            return;
        }
        switch (p.ElementTypeKind)
        {
            case "float32":
                s.Append(pad);
                s.AppendLine(
                    lo
                        + ".Add(float.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture));"
                );
                break;
            case "float64":
                s.Append(pad);
                s.AppendLine(
                    lo
                        + ".Add(double.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture));"
                );
                break;
            case "decimal":
                s.Append(pad);
                s.AppendLine(
                    lo
                        + ".Add(decimal.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture));"
                );
                break;
            case "boolean":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetBool(out var __evb)) throw new System.FormatException($\"Expected a boolean at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(__evb);");
                break;
            case "guid":
                s.Append(pad);
                s.AppendLine(lo + ".Add(System.Guid.Parse(Encoding.UTF8.GetString(r.ValueSpan)));");
                break;
            case "datetime":
                s.Append(pad);
                s.AppendLine("var __eraw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.AppendLine(
                    "if (!System.DateTime.TryParse(__eraw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __edt)) throw new System.FormatException($\"Invalid DateTime at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(__edt);");
                break;
            case "timespan":
                s.Append(pad);
                s.AppendLine(
                    lo
                        + ".Add(System.TimeSpan.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture));"
                );
                break;
            case "dateonly":
                s.Append(pad);
                s.AppendLine(
                    lo
                        + ".Add(System.DateOnly.ParseExact(Encoding.UTF8.GetString(r.ValueSpan), \"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture));"
                );
                break;
            case "timeonly":
                s.Append(pad);
                s.AppendLine(
                    lo
                        + ".Add(System.TimeOnly.ParseExact(Encoding.UTF8.GetString(r.ValueSpan), \"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture));"
                );
                break;
            case "enum":
                s.Append(pad);
                s.Append(lo + ".Add(System.Enum.Parse<");
                s.Append(
                    PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(
                        p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "string"
                    )
                );
                s.AppendLine(">(Encoding.UTF8.GetString(r.ValueSpan)));");
                break;
            case "string":
                // A `null` element is a null literal, not the four characters "null"
                // (the same confusion C-30 fixed for members). Flow sequences make this
                // shape common: `G: [a, null, c]`.
                s.Append(pad);
                s.AppendLine($"if (r.IsNullLiteral) {{ {lo}.Add(null!); }}");
                s.Append(pad);
                s.AppendLine($"else {{ {lo}.Add(Encoding.UTF8.GetString(r.ValueSpan)); }}");
                break;
            case "int32":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetInt32(out var __ev)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(__ev);");
                break;
            case "int16":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetInt32(out var __ev)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(checked((short)__ev));");
                break;
            case "uint16":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetInt32(out var __ev)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(checked((ushort)__ev));");
                break;
            case "sbyte":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetInt32(out var __ev)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(checked((sbyte)__ev));");
                break;
            case "byte":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetInt32(out var __ev)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(checked((byte)__ev));");
                break;
            case "uint32":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetInt32(out var __ev)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(checked((uint)__ev));");
                break;
            case "uint64":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetUInt64(out var __ev)) throw new System.FormatException($\"Expected a 64-bit unsigned integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(__ev);");
                break;
            case "int64":
                s.Append(pad);
                s.AppendLine(
                    "if (!r.TryGetInt64(out var __ev)) throw new System.FormatException($\"Expected a 64-bit integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine(lo + ".Add(__ev);");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlDictInner",
                    p.ElementTypeName!
                );
                if (p.ElementIsNullableReference)
                {
                    s.Append(pad);
                    s.AppendLine(
                        $"if (r.IsNullLiteral && !r.IsNestedSequenceItem) {{ {lo}.Add(null!); }}"
                    );
                    s.Append(pad);
                    s.AppendLine("else");
                    s.Append(pad);
                    s.AppendLine("{");
                    s.Append(pad);
                    s.Append("    " + lo + ".Add(");
                    s.Append(sn);
                    s.AppendLine(".Deserialize(ref r));");
                    s.Append(pad);
                    s.AppendLine("}");
                }
                else
                {
                    s.Append(pad);
                    s.Append(lo + ".Add(");
                    s.Append(sn);
                    s.AppendLine(".Deserialize(ref r));");
                }
                break;
            }
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.ElementTypeName!
                );
                // S14: a null object ELEMENT is a null literal, not an inner-helper call.
                if (p.ElementIsNullableReference)
                {
                    s.Append(pad);
                    s.AppendLine(
                        $"if (r.IsNullLiteral && !r.IsNestedSequenceItem) {{ {lo}.Add(null!); }}"
                    );
                    s.Append(pad);
                    s.AppendLine("else");
                    s.Append(pad);
                    s.AppendLine("{");
                    s.Append(pad);
                    s.Append("    " + lo + ".Add(");
                    s.Append(sn);
                    s.AppendLine(".Deserialize(ref r));");
                    s.Append(pad);
                    s.AppendLine("}");
                }
                else
                {
                    s.Append(pad);
                    s.Append(lo + ".Add(");
                    s.Append(sn);
                    s.AppendLine(".Deserialize(ref r));");
                }
                break;
            }
            default:
                s.Append(pad);
                s.AppendLine(lo + ".Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
        }
    }

    // ── Recursive nested-sequence helpers ──
    //
    // A nested sequence is written as a block sequence whose item carries the inner
    // sequence as the indented block below its dash:
    //
    //     Grid:
    //     -
    //       - 1
    //       - 2
    //
    // YamlReader has no ArrayStart/ArrayEnd token: a sequence is one String token per
    // item line, and a deeper indentation opens a synthetic ObjectStart block. So a
    // nested item is the bare dash line (empty unquoted value, IsNestedSequenceItem)
    // followed by its block, or the "- []" flow marker (IsEmptySequenceMarker) for an
    // empty one. Both halves of the wire are therefore readable from plain token types.

    /// <summary>
    /// True when a list/array level's items are themselves sequences. The extraction
    /// pass keeps one synthetic descriptor per level in <c>NestedProperties</c>: its
    /// <c>ElementTypeKind</c> is that level's item kind and its <c>ElementTypeName</c>
    /// the item type, so one descriptor walks the whole chain.
    /// </summary>
    private static bool IsYamlNestedList(PropertyInfo p) =>
        PicoSerDe.Gen.GenInfrastructure.IsSequenceKind(p.ElementTypeKind)
        && p.NestedProperties.Length > 0;

    /// <summary>
    /// Emits the write of ONE dictionary value that is itself a collection (S10): a block
    /// sequence under the entry's own key. The level descriptor is the extractor's chain for
    /// the VALUE (<c>NestedProperties[0]</c>, one level below the value), so a nested chain
    /// keeps the same per-level writer a list member uses instead of a second implementation.
    /// <para>
    /// Callers emit <c>yw.WritePropertyName(__kvp.Key)</c> first; this writes only the value,
    /// so both dict arms (top-level member and inline inner helper) share one body.
    /// </para>
    /// </summary>
    private static void EmitYamlDictValueSequenceWrite(StringBuilder s, PropertyInfo p, string ind)
    {
        EmitYamlSequenceWrite(
            s,
            p.NestedProperties[0],
            "__kvp.Value",
            ind + "    ",
            0,
            p.ElementTypeKind ?? "list"
        );
    }

    /// <summary>
    /// Emits the read of ONE dictionary value that is itself a collection (S10) into
    /// <paramref name="assignTarget"/> (already spelled <c>obj.Map[__dk]</c>).
    /// <para>
    /// Same four-way token probe as <see cref="EmitYamlNestedListDeserialize"/> — wrapper
    /// <c>ObjectStart</c>, bare first item, empty, or end of document — because YAML's
    /// sequence encoding does not depend on WHERE the sequence sits. The one difference is
    /// the trailing behavior: a member may <c>break</c> out of its dispatch loop, a dict
    /// entry must not, so the reader is simply left on the next token.
    /// </para>
    /// </summary>
    private static void EmitYamlDictValueSequenceRead(
        StringBuilder s,
        PropertyInfo dp,
        string assignTarget,
        string pad
    )
    {
        var level = dp.NestedProperties[0];
        var elemType = level.ElementTypeNameAnnotated ?? level.ElementTypeName ?? "object";
        s.Append(pad);
        s.Append("var __dvl = new System.Collections.Generic.List<");
        s.Append(elemType);
        s.AppendLine(">(16);");
        s.Append(pad);
        s.AppendLine("bool __more;");
        s.Append(pad);
        s.AppendLine("__more = r.Read();");
        s.Append(pad);
        s.AppendLine("if (!__more)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // The document ended at this key: there is no sequence to read.");
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else if (r.TokenType == TokenType.ObjectStart)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // Indented block sequence: the wrapper ObjectStart was consumed.");
        EmitYamlSequenceReadLoop(s, level, "__dvl", pad + "    ", 1, readFirst: true);
        s.Append(pad);
        s.AppendLine("    if (__more && r.TokenType == TokenType.ObjectEnd) __more = r.Read();");
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else if (r.TokenType == TokenType.String)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // No wrapper: the probe token is the first item of a flat sequence.");
        EmitYamlSequenceReadLoop(s, level, "__dvl", pad + "    ", 1, readFirst: false);
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // No wrapper and no sequence item: the value is an empty sequence.");
        s.Append(pad);
        s.AppendLine("}");
        PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
            s,
            dp.ElementTypeKind!,
            "__dvl",
            assignTarget,
            elemType,
            pad
        );
    }

    /// <summary>Emits the write of <paramref name="accessor"/> as a YAML block sequence.</summary>
    private static void EmitYamlSequenceWrite(
        StringBuilder s,
        PropertyInfo level,
        string accessor,
        string ind,
        int depth,
        string valueKind
    )
    {
        var itemVar = depth == 0 ? "__item" : "__item" + depth;
        s.Append(ind);
        s.AppendLine("yw.WriteStartSequence();");
        s.Append(ind);
        s.Append("foreach (var ");
        s.Append(itemVar);
        s.Append(" in ");
        s.Append(PicoSerDe.Gen.GenInfrastructure.SequenceItemsExpression(valueKind, accessor));
        if (!PicoSerDe.Gen.GenInfrastructure.IsNullableSequenceKind(valueKind))
        {
            // ImmutableArray<T>/Memory<T>/ReadOnlyMemory<T> are STRUCTS: neither `?? []`
            // nor `?? new List<T>(0)` compiles on them, and there is no null to guard.
        }
        else if (valueKind == "list")
        {
            s.Append(" ?? new System.Collections.Generic.List<");
            s.Append(level.ElementTypeNameAnnotated ?? level.ElementTypeName ?? "object");
            s.Append(">(0)");
        }
        else
        {
            // array + the reference-type extended collections: a collection expression is
            // target-typed by the `??` left operand, so it is the empty value of whatever
            // collection the kind names (`new List<T>(0)` would not convert to HashSet<T>).
            s.Append(" ?? []");
        }
        s.AppendLine(")");
        s.Append(ind);
        s.AppendLine("{");
        EmitYamlSequenceItemWrite(s, level, itemVar, ind + "    ", depth);
        s.Append(ind);
        s.AppendLine("}");
        s.Append(ind);
        s.AppendLine("yw.WriteEndSequence();");
    }

    /// <summary>Emits one item of the sequence described by <paramref name="level"/>.</summary>
    private static void EmitYamlSequenceItemWrite(
        StringBuilder s,
        PropertyInfo level,
        string itemVar,
        string ind,
        int depth
    )
    {
        if (!IsYamlNestedList(level))
        {
            // Scalar/object element: the same writer a flat list member uses.
            EmitSerializeListElement(s, level, ind, itemVar);
            return;
        }
        var countExpr = PicoSerDe.Gen.GenInfrastructure.SequenceCountExpression(
            level.ElementTypeKind,
            itemVar
        );
        s.Append(ind);
        s.Append("if (");
        if (PicoSerDe.Gen.GenInfrastructure.IsNullableSequenceKind(level.ElementTypeKind))
        {
            s.Append(itemVar);
            s.Append(" is null || ");
        }
        s.Append(countExpr);
        s.AppendLine(" == 0)");
        s.Append(ind);
        s.AppendLine("{");
        s.Append(ind);
        s.AppendLine("    yw.WriteEmptySequenceItem();");
        s.Append(ind);
        s.AppendLine("}");
        s.Append(ind);
        s.AppendLine("else");
        s.Append(ind);
        s.AppendLine("{");
        s.Append(ind);
        s.AppendLine("    yw.WriteStartSequenceBlock();");
        EmitYamlSequenceWrite(
            s,
            level.NestedProperties[0],
            itemVar,
            ind + "    ",
            depth + 1,
            level.ElementTypeKind ?? "list"
        );
        s.Append(ind);
        s.AppendLine("    yw.WriteEndSequenceBlock();");
        s.Append(ind);
        s.AppendLine("}");
    }

    /// <summary>
    /// Emits the read of a nested-sequence member. <paramref name="p"/> is the member's
    /// list level; the innermost element is produced by
    /// <see cref="EmitDeserializeListElementTemp"/> so nested sequences keep the same
    /// element fidelity as flat ones.
    /// </summary>
    private static void EmitYamlNestedListDeserialize(
        StringBuilder s,
        PropertyInfo p,
        string pad,
        string assignTarget,
        string kind
    )
    {
        s.Append(pad);
        s.Append("var __tmpList = new System.Collections.Generic.List<");
        s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object");
        s.AppendLine(">(16);");
        s.Append(pad);
        s.AppendLine("bool __more;");
        s.Append(pad);
        s.AppendLine("__more = r.Read();");
        s.Append(pad);
        s.AppendLine("if (!__more)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // The document ended at this key: there is no list to read.");
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else if (r.TokenType == TokenType.ObjectStart)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // Indented block sequence: the wrapper ObjectStart was consumed.");
        EmitYamlSequenceReadLoop(s, p, "__tmpList", pad + "    ", 1, readFirst: true);
        s.Append(pad);
        s.AppendLine("    if (__more && r.TokenType == TokenType.ObjectEnd) __more = r.Read();");
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else if (r.TokenType == TokenType.String)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // No wrapper: the probe token is the first item of a flat sequence.");
        EmitYamlSequenceReadLoop(s, p, "__tmpList", pad + "    ", 1, readFirst: false);
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine(
            "    // No wrapper and no sequence item: the list is empty and the probe token"
        );
        s.Append(pad);
        s.AppendLine("    // is already the sibling property this member must not eat (C-21).");
        s.Append(pad);
        s.AppendLine("}");
        // T2 review I-1: the last step used to spell `isArray ? "array" : "list"`, which is
        // the member KIND only for lean List/array members. A member whose declared kind is
        // an extended collection (HashSet<List<int>>, Queue<int[]>) reaches this path too —
        // IsYamlNestedList is true for any sequence of sequences — and the hardcoded kind
        // assigned a List<List<int>> to a HashSet<List<int>> (CS0029). The kind is the
        // caller's now, so the conversion this path emits is the one the DECLARATION asks for.
        PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
            s,
            kind,
            "__tmpList",
            assignTarget,
            p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object",
            pad
        );
        s.Append(pad);
        s.AppendLine("if (!__more) break;");
    }

    /// <summary>
    /// Emits the item loop of one sequence level. Every iteration consumes exactly one
    /// item token and leaves the reader on the next token, so the loop head only has to
    /// test the current one.
    /// </summary>
    private static void EmitYamlSequenceReadLoop(
        StringBuilder s,
        PropertyInfo level,
        string listVar,
        string pad,
        int depth,
        bool readFirst
    )
    {
        if (readFirst)
        {
            s.Append(pad);
            s.AppendLine("__more = r.Read();");
        }
        s.Append(pad);
        s.AppendLine("while (__more && r.TokenType == TokenType.String)");
        s.Append(pad);
        s.AppendLine("{");
        if (IsYamlNestedList(level))
            EmitYamlNestedSequenceItemRead(s, level, listVar, pad + "    ", depth);
        else
        {
            EmitDeserializeListElementTemp(s, level, pad + "    ", listVar);
            s.Append(pad);
            s.AppendLine("    __more = r.Read();");
        }
        s.Append(pad);
        s.AppendLine("}");
    }

    /// <summary>Emits one item of a nested-sequence level into <paramref name="listVar"/>.</summary>
    private static void EmitYamlNestedSequenceItemRead(
        StringBuilder s,
        PropertyInfo level,
        string listVar,
        string pad,
        int depth
    )
    {
        var inner = level.NestedProperties[0];
        var itemVar = "__seq" + depth;
        s.Append(pad);
        s.Append("var ");
        s.Append(itemVar);
        s.Append(" = new System.Collections.Generic.List<");
        s.Append(inner.ElementTypeNameAnnotated ?? inner.ElementTypeName ?? "object");
        s.AppendLine(">(16);");
        s.Append(pad);
        s.AppendLine("if (r.IsEmptySequenceMarker)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // A \"- []\" item: the inner sequence has no elements.");
        s.Append(pad);
        s.AppendLine("    __more = r.Read();");
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else if (r.IsNestedSequenceItem)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    // A bare dash: the inner sequence is the indented block that follows.");
        s.Append(pad);
        s.AppendLine("    if (!r.Read())");
        s.Append(pad);
        s.AppendLine("    {");
        s.Append(pad);
        s.AppendLine("        // The chunk (or the document) ended inside the item: a resumed");
        s.Append(pad);
        s.AppendLine("        // read restarts the member from its mark, so stopping here is safe");
        s.Append(pad);
        s.AppendLine("        // and the streaming dispatcher decides whether to wait for more.");
        s.Append(pad);
        s.AppendLine("        __more = false;");
        s.Append(pad);
        s.AppendLine("    }");
        s.Append(pad);
        s.AppendLine("    else if (r.TokenType != TokenType.ObjectStart)");
        s.Append(pad);
        s.AppendLine("    {");
        s.Append(pad);
        s.AppendLine(
            "        // A dash with no indented block below it (\"-\", \"- # note\"): the item has no"
        );
        s.Append(pad);
        s.AppendLine(
            "        // value, so it reads like an empty inner sequence instead of failing."
        );
        s.Append(pad);
        s.AppendLine("    }");
        s.Append(pad);
        s.AppendLine("    else");
        s.Append(pad);
        s.AppendLine("    {");
        EmitYamlSequenceReadLoop(s, inner, itemVar, pad + "        ", depth + 1, readFirst: true);
        s.Append(pad);
        s.AppendLine(
            "        if (__more && r.TokenType == TokenType.ObjectEnd) __more = r.Read();"
        );
        s.Append(pad);
        s.AppendLine("    }");
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else if (r.IsNullLiteral)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine(
            "    // A null item (\"- null\", \"- *alias\"): no element for the inner sequence."
        );
        s.Append(pad);
        s.AppendLine("    __more = r.Read();");
        s.Append(pad);
        s.AppendLine("}");
        s.Append(pad);
        s.AppendLine("else");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine(
            "    throw new System.FormatException($\"Expected a nested sequence item at offset {r.BytesConsumed}\");"
        );
        s.Append(pad);
        s.AppendLine("}");
        // The item's elements are in itemVar. When the level IS an extended collection,
        // the declared element type is that collection — convert before adding.
        var converted = itemVar;
        if (PicoSerDe.Gen.GenInfrastructure.IsExtendedCollectionKind(level.ElementTypeKind))
        {
            var convVar =
                "__seq" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture) + "c";
            s.Append(pad);
            s.Append(level.ElementTypeNameAnnotated ?? level.ElementTypeName ?? "object");
            s.Append(' ');
            s.Append(convVar);
            s.AppendLine(" = default!;");
            PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
                s,
                level.ElementTypeKind!,
                itemVar,
                convVar,
                inner.ElementTypeNameAnnotated ?? inner.ElementTypeName ?? "object",
                pad
            );
            converted = convVar;
        }
        s.Append(pad);
        s.Append(listVar);
        s.Append(".Add(");
        s.Append(converted);
        if (level.ElementTypeKind == "array")
            s.Append(".ToArray()");
        s.AppendLine(");");
    }

    static string GenPoly(TypeInfo type, Dictionary<string, TypeInfo> typeMap)
    {
        var dpn = type.DiscriminatorPropertyName ?? "$type";
        // Concrete bases get a synthesized discriminator so a base-typed
        // instance serializes as a valid value (and round-trips).
        var __baseDisc = PicoSerDe.Gen.GenInfrastructure.BaseDiscriminator(type);
        var __cases = __baseDisc is null
            ? type.DerivedTypes
            : type.DerivedTypes.Add(
                new PicoSerDe.Gen.DerivedTypeInfo(type.FullyQualifiedName!, __baseDisc)
            );

        var dpnEsc = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dpn);
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using System.Runtime.CompilerServices;");
        s.AppendLine("using PicoSerDe.Core; using PicoYaml;");
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            s.Append("using ");
            s.Append(type.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        // ── Serializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("YamlSerializer : ISerializer<");
        s.Append(type.Name);
        s.AppendLine(">, global::PicoSerDe.Core.IOptionsSerializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value) => Serialize(writer, value, null);");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoYaml.YamlOptions?)options;");
        s.AppendLine(
            "        var yw = new YamlWriter(writer, indented: __opts?.Indented ?? false);"
        );
        s.AppendLine("        yw.WriteStartMapping();");
        s.Append("        yw.WritePropertyName(\"");
        s.Append(dpnEsc);
        s.AppendLine("\"u8);");
        s.AppendLine("        switch (value)");
        s.AppendLine("        {");
        foreach (var dt in __cases)
        {
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out var dti))
                continue;
            var ds = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            var desc = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dt.TypeDiscriminator);
            s.Append("            case ");
            s.Append(ds);
            s.AppendLine(" __v:");
            s.Append("                yw.WriteString(\"");
            s.Append(desc);
            s.AppendLine("\"u8);");
            foreach (var prop in dti.Properties)
            {
                // Shared member emitter: scalars, nested objects/dicts and
                // object collections (the poly branch previously handled
                // scalars only).
                EmitSerialize(s, prop, "__v", "                ");
            }
            s.AppendLine("                break;");
        }
        s.AppendLine("        }");
        s.AppendLine("        yw.WriteEndMapping();");
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();

        // ── Deserializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("YamlDeserializer : IDeserializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(type.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        // The shared member emitter expects `r` as the reader variable.
        s.AppendLine("        var r = new YamlReader(data);");
        s.AppendLine("        r.Read(); // mapping start");
        s.AppendLine("        r.Read(); // discriminator key");
        s.AppendLine("        var __discVal = r.ValueSpan;");
        s.Append("        if (!MemoryExtensions.SequenceEqual(r.KeySpan, \"");
        s.Append(dpnEsc);
        s.AppendLine("\"u8))");
        s.Append("            throw new FormatException(\"Expected discriminator '");
        s.Append(dpnEsc);
        s.AppendLine("'\");");

        for (int i = 0; i < __cases.Length; i++)
        {
            var dt = __cases[i];
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out var dti))
                continue;
            var kw = i == 0 ? "if" : "else if";
            var ds = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            var desc = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dt.TypeDiscriminator);

            s.Append("        ");
            s.Append(kw);
            s.Append(" (MemoryExtensions.SequenceEqual(__discVal, \"");
            s.Append(desc);
            s.AppendLine("\"u8)) {");

            var hasCtor = !dti.CtorParams.IsDefaultOrEmpty && dti.CtorParams.Length > 0;
            if (hasCtor)
            {
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    var cp = dti.CtorParams[ci];
                    s.Append("            ");
                    s.Append(cp.TypeFullNameAnnotated ?? cp.TypeFullName);
                    s.Append(" __cp_");
                    s.Append(ci);
                    s.AppendLine(cp.TypeKind == "string" ? " = null!;" : " = default!;");
                }
            }
            else
            {
                s.Append("            var obj = new ");
                s.Append(ds);
                s.AppendLine("();");
            }

            // Plain-path loop shape: `EmitDeserialize` advances the reader
            // itself, so the loop must not read at the top.
            s.AppendLine("            r.Read();");
            s.AppendLine("            while (true) {");
            s.AppendLine("                if (r.TokenType == TokenType.PropertyName) {");
            s.AppendLine("                var __k = r.KeySpan;");
            var __polyCtorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!dti.CtorParams.IsDefaultOrEmpty)
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                    if (!string.IsNullOrEmpty(dti.CtorParams[ci].Name))
                        __polyCtorMap[dti.CtorParams[ci].Name!] = ci;
            var first = true;
            foreach (var prop in dti.Properties)
            {
                if (hasCtor && !__polyCtorMap.ContainsKey(prop.Name))
                    continue;
                var kw2 = PicoSerDe.Gen.GenInfrastructure.ChainKeyword(ref first);
                var pn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName);
                s.Append("                ");
                s.Append(kw2);
                s.Append(" (MemoryExtensions.SequenceEqual(__k, \"");
                s.Append(pn);
                s.AppendLine("\"u8)) {");
                // Shared member read (scalars, collections, nested objects)
                // with ctor-parameter redirection.
                EmitDeserialize(s, prop, "obj", "                    ", ctorMap: __polyCtorMap);
                s.AppendLine("                }");
            }
            s.AppendLine("                    continue;");
            s.AppendLine("                }");
            s.AppendLine("                if (!r.Read()) break;");
            s.AppendLine("            }");
            if (hasCtor)
            {
                s.Append("            return new ");
                s.Append(ds);
                s.Append("(");
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    if (ci > 0)
                        s.Append(", ");
                    s.Append("__cp_");
                    s.Append(ci);
                }
                s.AppendLine(");");
            }
            else
            {
                s.AppendLine("            return obj;");
            }
            s.AppendLine("        }");
        }

        s.AppendLine(
            "        throw new FormatException($\"Unknown discriminator: {Encoding.UTF8.GetString(__discVal)}\");"
        );
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();

        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"{type.Namespace}.{type.Name}";
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        YamlSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("YamlSerializer(),");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("YamlDeserializer());");
        s.AppendLine("    }");
        s.AppendLine("}");

        return s.ToString();
    }

    private static string EmitYamlValue(AnonFieldInfo f, string vv, string wv) =>
        f.TypeKind switch
        {
            "string" => $"{wv}.WriteString({vv});",
            "int32" or "int64" or "int16" or "uint16" or "sbyte" or "byte" =>
                $"{wv}.WriteNumber({vv});",
            "uint32" or "uint64" => $"{wv}.WriteInt64({vv});",
            "float32" or "float64" => $"{wv}.WriteNumber({vv});",
            "boolean" => $"{wv}.WriteBoolean({vv});",
            _ => $"{wv}.WriteString({vv}.ToString());",
        };

    static void WriteYamlValue(StringBuilder s, PropertyInfo p, string acc)
    {
        if (p.TypeKind == "string")
        {
            s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
            s.Append(acc);
            s.Append("));");
        }
        else if (p.TypeKind == "int32")
        {
            s.Append("yw.WriteNumber(");
            s.Append(acc);
            s.Append(");");
        }
        else if (p.TypeKind == "int64")
        {
            s.Append("yw.WriteInt64(");
            s.Append(acc);
            s.Append(");");
        }
        else if (
            p.TypeKind == "int16"
            || p.TypeKind == "uint16"
            || p.TypeKind == "sbyte"
            || p.TypeKind == "byte"
            || p.TypeKind == "uint32"
        )
        {
            s.Append("yw.WriteInt64(");
            s.Append(acc);
            s.Append(");");
        }
        else if (p.TypeKind == "uint64")
        {
            s.Append("yw.WriteUInt64(");
            s.Append(acc);
            s.Append(");");
        }
        else if (p.TypeKind == "float64" || p.TypeKind == "float32")
        {
            s.Append("yw.WriteNumber((int)");
            s.Append(acc);
            s.Append(");");
        }
        else if (p.TypeKind == "boolean")
        {
            s.Append("yw.WriteBoolean(");
            s.Append(acc);
            s.Append(");");
        }
        else
        {
            s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
            s.Append(acc);
            s.Append(".ToString()));");
        }
    }

    static void ReadYamlValue(StringBuilder s, PropertyInfo p)
    {
        if (p.TypeKind == "string")
            s.Append("Encoding.UTF8.GetString(__v)");
        else if (p.TypeKind == "int32")
            s.Append("int.TryParse(__v, out var __iv) ? __iv : 0");
        else if (p.TypeKind == "int64")
            s.Append("long.TryParse(__v, out var __lv) ? __lv : 0");
        else if (p.TypeKind == "int16")
            s.Append("short.TryParse(__v, out var __v16) ? __v16 : (short)0");
        else if (p.TypeKind == "uint16")
            s.Append("ushort.TryParse(__v, out var __v16) ? __v16 : (ushort)0");
        else if (p.TypeKind == "sbyte")
            s.Append("sbyte.TryParse(__v, out var __v8) ? __v8 : (sbyte)0");
        else if (p.TypeKind == "byte")
            s.Append("byte.TryParse(__v, out var __v8) ? __v8 : (byte)0");
        else if (p.TypeKind == "uint32")
            s.Append("uint.TryParse(__v, out var __vu) ? __vu : 0u");
        else if (p.TypeKind == "uint64")
            s.Append("ulong.TryParse(__v, out var __vul) ? __vul : 0ul");
        else if (p.TypeKind == "float64" || p.TypeKind == "float32")
            s.Append("double.TryParse(__v, out var __dv) ? __dv : 0");
        else if (p.TypeKind == "boolean")
            s.Append("bool.TryParse(__v, out var __bv) ? __bv : false");
        else
            s.Append("default");
    }
}
