namespace PicoYaml.Gen;

using AnonFieldInfo = PicoSerDe.Gen.AnonFieldInfo;
using Microsoft.CodeAnalysis.CSharp;
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
        id: "PICOYAML003", title: "Anonymous types require C# 12+",
        messageFormat: "Anonymous type serialization requires C# 12 or later.",
        category: "PicoYaml.Gen", defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor AnonRequiresUnsafe = new(
        id: "PICOYAML004", title: "Requires AllowUnsafeBlocks",
        messageFormat: "Anonymous type serialization requires <AllowUnsafeBlocks>true</AllowUnsafeBlocks>.",
        category: "PicoYaml.Gen", defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        HasYamlCamelCase,
        GetYamlKey,
        HasYamlIgnore,
        GetYamlConverter,
        GetYamlDateTimeFormat,
        OverrideKindWithStringOnConverter: true
    );

    private static readonly PicoSerDe.Gen.AnonFormatConfig AnonCfg = new(
        HasNullLiteral: false, EmbedsKeyInValue: false,
        ObjectStartMethod: "WriteStartMapping", ObjectEndMethod: "WriteEndMapping",
        ObjectStartNeedsCount: false, HasIndentedMaxDepth: false,
        KeyIsEncodedString: false, HasOptionsParam: false
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
        var anonDriven = ctx.SyntaxProvider.CreateSyntaxProvider(
            predicate: (n, _) => IsC(n),
            transform: (cx, _) =>
            {
                var comp = (CSharpCompilation)cx.SemanticModel.Compilation;
                if (comp.LanguageVersion < LanguageVersion.CSharp12) return null;
                if (!comp.Options.AllowUnsafe) return null;
                if (cx.SemanticModel.GetSymbolInfo(cx.Node).Symbol is not IMethodSymbol m) return null;
                if (m.TypeArguments.Length != 1) return null;
                if (m.TypeArguments[0] is not INamedTypeSymbol nt || !nt.IsAnonymousType) return null;
                if (m.ContainingType.Name != Config.SerializerClassName || m.ContainingType.ContainingNamespace?.ToDisplayString() != Config.Namespace) return null;
                return PicoSerDe.Gen.AnonTypeHandler.BuildAnonTypeInfo(nt, cx, Config, Attrs);
            }
        ).Where(a => a is not null);

        var anonOut = anonDriven.Collect().Combine(asmName);
        ctx.RegisterSourceOutput(anonOut, (spc, pair) =>
        {
            PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = $"__PicoSerDe_{pair.Right}";
            foreach (var ai in pair.Left)
            {
                if (ai is not { } info) continue;
                PicoSerDe.Gen.AnonTypeHandler.GenerateInterceptorClass(spc, info, Config, AnonCfg,
                    (f, vv, wv) => EmitYamlValue(f, vv, wv));
            }
        });

        ctx.RegisterSourceOutput(ctx.CompilationProvider, (spc, comp) =>
        {
            var csComp = (CSharpCompilation)comp;
            if (csComp.LanguageVersion < LanguageVersion.CSharp12)
                spc.ReportDiagnostic(Diagnostic.Create(AnonRequiresCSharp12, null));
            else if (!csComp.Options.AllowUnsafe)
                spc.ReportDiagnostic(Diagnostic.Create(AnonRequiresUnsafe, null));
        });

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

        ctx.RegisterSourceOutput(all, static (spc, types) =>
        {
            PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = null;
            GenerateAll(spc, types);
        });
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
        foreach (var t in ts)
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
                var elemFqn = t.ArrayElementName!.Replace("global::", "");
                if (!nestedTypes.ContainsKey(elemFqn))
                    nestedTypes[elemFqn] = t.ArrayElementNestedProps;
            }
        }

        // Collect nested Dictionary types
        var nestedDictTypes = new Dictionary<string, PropertyInfo>();
        foreach (var t in ts)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedDictTypes(t, nestedDictTypes);

        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var sn = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanName);
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
            var sn = PicoSerDe.Gen.GenInfrastructure.SafeName(fullName);
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
            var safeFq = PicoSerDe.Gen.GenInfrastructure.SafeName(t.FullyQualifiedName ?? "");
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

        var lastDot = fullName.LastIndexOf('.');
        if (lastDot > 0)
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
        sb.AppendLine("                var nk = reader.KeySpan;");
        for (int i = 0; i < props.Length; i++)
        {
            var np = props[i];
            var kw = i == 0 ? "if" : "else if";
            sb.Append("                ");
            sb.Append(kw);
            sb.Append(" (TextHelpers.Eq(nk, \"");
            sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(np.JsonName));
            sb.AppendLine("\"u8))");
            sb.AppendLine("                {");
            EmitDeserializeInline(sb, np, "obj", "                    ");
            sb.AppendLine("                }");
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
                    "                reader.TryGetInt32(out var __dv); obj[__dk] = __dv;"
                );
                break;
            case "int64":
                sb.AppendLine(
                    "                obj[__dk] = long.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "float32":
                sb.AppendLine(
                    "                obj[__dk] = float.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
                );
                break;
            case "float64":
                sb.AppendLine(
                    "                obj[__dk] = double.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"
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
    /// DefaultIgnoreCondition guard for parity with the top-level emit path.
    /// </summary>
    private static void EmitInnerSerializeProps(
        StringBuilder sb,
        ImmutableArray<PropertyInfo> props
    )
    {
        foreach (var prop in props)
        {
            bool guard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                sb,
                prop,
                "value." + prop.Name,
                "        "
            );
            sb.Append("        yw.WritePropertyName(\"");
            sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\"u8);");
            EmitSerializeInline(sb, prop, "value." + prop.Name, "        ");
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
            case "guid":
            case "enum":
            case "decimal":
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
                // Recursively handle nested List<List<...<T>>>
                if (IsYamlNestedList(p))
                {
                    EmitYamlNestedListSerialize(s, p.NestedProperties[0], accessor, ind);
                }
                else
                {
                    s.Append(ind);
                    s.Append("foreach (var __item in ");
                    s.Append(accessor);
                    if (PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(p))
                        s.Append(" ?? []");
                    s.AppendLine(")");
                    s.Append(ind);
                    s.AppendLine("{");
                    if (p.ElementTypeKind == "string" && p.ElementIsNullableReference)
                    {
                        s.Append(ind);
                        s.AppendLine("    if (__item != null)");
                        s.Append(ind);
                        s.AppendLine(
                            "        yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item));"
                        );
                    }
                    else
                    {
                        s.Append(ind);
                        s.Append("    yw.WriteSequenceItem(Encoding.UTF8.GetBytes(");
                        if (p.ElementTypeKind == "string")
                            s.Append("__item");
                        else
                            s.Append("__item.ToString()");
                        s.AppendLine("));");
                    }
                    s.Append(ind);
                    s.AppendLine("}");
                }
                break;
            case "dict":
                s.Append(ind);
                s.AppendLine("yw.WriteStartMapping();");
                s.Append(ind);
                s.Append("foreach (var __kvp in ");
                s.Append(accessor);
                if (PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(p))
                    s.Append(" ?? []");
                s.AppendLine(")");
                s.Append(ind);
                s.AppendLine("{");
                s.Append(ind);
                s.AppendLine("    yw.WritePropertyName(__kvp.Key);");
                switch (p.ElementTypeKind)
                {
                    case "string":
                        s.Append(ind);
                        s.AppendLine("    yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value));");
                        break;
                    case "int32":
                    case "int64":
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
                        s.Append(ind);
                        s.Append("    ");
                        s.Append(dsn);
                        s.AppendLine(".Serialize(yw, __kvp.Value);");
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
                s.AppendLine("reader.TryGetInt32(out var __nv);");
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
            case "float32":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = float.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
                break;
            case "float64":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = double.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
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
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.Append(" = Enum.Parse<");
                s.Append(p.TypeFullName);
                s.AppendLine(">(Encoding.UTF8.GetString(reader.ValueSpan));");
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
                s.Append(p.ElementTypeName ?? "object");
                s.AppendLine(">(16);");
                s.Append(pad);
                s.AppendLine("while (reader.Read() && reader.TokenType == TokenType.String) {");
                s.Append(pad);
                s.AppendLine("    __tmpList.Add(Encoding.UTF8.GetString(reader.ValueSpan));");
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
                s.Append(p.ElementTypeName ?? "int");
                s.AppendLine(">();");
                s.Append(pad);
                s.AppendLine("if (reader.Read() && reader.TokenType == TokenType.ObjectStart) {");
                s.Append(pad);
                s.AppendLine(
                    "    while (reader.Read() && reader.TokenType == TokenType.PropertyName) {"
                );
                s.Append(pad);
                s.AppendLine("        var __dk = Encoding.UTF8.GetString(reader.KeySpan);");
                switch (p.ElementTypeKind)
                {
                    case "int32":
                        s.Append(pad);
                        s.AppendLine("        reader.TryGetInt32(out var __dv);");
                        s.Append(pad);
                        s.Append("        ");
                        s.Append(tgt);
                        s.Append('.');
                        s.Append(p.Name);
                        s.AppendLine("[__dk] = __dv;");
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
        sb.AppendLine("> {");
        sb.Append("    public void Serialize(IBufferWriter<byte> w, ");
        sb.Append(t.Name);
        sb.AppendLine(" v) {");
        sb.AppendLine("        var yw = new YamlWriter(w);");
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
                sb.AppendLine(cp.TypeKind == "string" ? " = null!;" : " = default;");
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

        // Streaming (scalar properties only, skip nested objects/dicts)
        if (ylHasCtor)
        { /* skip streaming */
        }
        else
        {
            sb.Append("file static class ");
            sb.Append(t.Name);
            sb.AppendLine("_YamlStreaming {");
            sb.AppendLine(
                "    internal static ReadStatus DeserializeStreaming(ref YamlReader r, out "
                    + t.Name
                    + "? result) {"
            );
            sb.AppendLine("        result = default;");
            var ysReq = t.Properties.Where(p => p.IsRequired).ToArray();
            if (ysReq.Length > 0)
            {
                sb.Append("        var o = new ");
                sb.Append(t.Name);
                sb.AppendLine(" {");
                foreach (var rp in ysReq)
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
            sb.AppendLine("        while (true) {");
            sb.AppendLine(
                "            if (!r.Read()) return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
            );
            sb.AppendLine("            if (r.TokenType != TokenType.PropertyName) break;");
            sb.AppendLine("            var __k = r.KeySpan;");
            int yi = 0;
            foreach (var p in t.Properties.Where(p => p.TypeKind is not "object" and not "dict"))
            {
                var kw = yi++ == 0 ? "if" : "else if";
                sb.Append("            ");
                sb.Append(kw);
                sb.Append(" (TextHelpers.Eq(__k, \"");
                sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                sb.AppendLine("\"u8)) {");
                EmitDeserialize(sb, p, "o", "                ");
                sb.AppendLine("            }");
            }
            if (yi > 0)
            {
                sb.AppendLine(
                    "            else { if (!r.Read()) { result = o; return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success; } }"
                );
            }
            sb.AppendLine("        }");
            sb.AppendLine("        result = o;");
            sb.AppendLine("        return ReadStatus.Success;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        } // end skip-streaming else
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
        s.AppendLine(" v) {");
        s.AppendLine("        var yw = new YamlWriter(w);");
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
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(listFqn);
        s.AppendLine(" v) {");
        s.AppendLine("        var yw = new YamlWriter(w);");
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

        if (p.TypeKind is "list" or "array")
        {
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
            // Array of complex objects: use block sequence ("- " + indented properties)
            if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
            {
                var elemTypeName = p.ElementTypeName ?? "object";
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("YamlInner", elemTypeName);
                s.Append(ind);
                s.Append("yw.WriteStartMapping(); // increase depth for sequence indentation");
                s.AppendLine();
                s.Append(ind);
                s.Append("foreach (var __item in ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.Append(" ?? new System.Collections.Generic.List<");
                s.Append(elemTypeName);
                s.AppendLine(">(0))");
                s.Append(ind);
                s.AppendLine("{");
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
                s.AppendLine("yw.WriteEndMapping(); // restore depth");
            }
            else
            {
                s.Append(ind);
                s.Append("foreach (var __item in ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                if (p.TypeKind == "array")
                {
                    s.AppendLine(" ?? [])");
                }
                else
                {
                    s.Append(" ?? new System.Collections.Generic.List<");
                    s.Append(p.ElementTypeName ?? "object");
                    s.AppendLine(">(0))");
                }
                s.Append(ind);
                s.AppendLine("{");
                EmitSerializeListElement(s, p, ind + "    ");
                s.Append(ind);
                s.AppendLine("}");
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
            s.Append(ind);
            s.Append("foreach (var __kvp in ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" ?? new System.Collections.Generic.Dictionary<");
            s.Append(p.KeyTypeName ?? "string");
            s.Append(", ");
            s.Append(p.ElementTypeName ?? "object");
            s.AppendLine(">(0))");
            s.Append(ind);
            s.AppendLine("{");
            s.Append(ind);
            s.AppendLine("    yw.WritePropertyName(__kvp.Key);");
            switch (p.ElementTypeKind)
            {
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
                default:
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
                default:
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

    private static void EmitSerializeListElement(StringBuilder s, PropertyInfo p, string ind)
    {
        switch (p.ElementTypeKind)
        {
            case "string":
                if (p.ElementIsNullableReference)
                {
                    s.Append(ind);
                    s.AppendLine("if (__item != null)");
                    s.Append(ind);
                    s.AppendLine("    yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item));");
                }
                else
                {
                    s.Append(ind);
                    s.AppendLine("yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item));");
                }
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlDictInner",
                    p.ElementTypeName!
                );
                s.Append(ind);
                s.Append(sn);
                s.AppendLine(".Serialize(yw, __item);");
                break;
            }
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.ElementTypeName!
                );
                s.Append(ind);
                s.AppendLine("yw.WriteStartSequenceBlock();");
                s.Append(ind);
                s.Append("    ");
                s.Append(sn);
                s.AppendLine(".SerializeBlock(yw, __item);");
                s.Append(ind);
                s.AppendLine("yw.WriteEndSequenceBlock();");
                break;
            }
            case "int32":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(__item);");
                break;
            case "int64":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem((long)__item);");
                break;
            case "float32":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(__item);");
                break;
            case "float64":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(__item);");
                break;
            default:
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item.ToString()));");
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

        if (p.TypeKind is "list" or "array")
        {
            s.Append(pad);
            s.Append("var __tmpList = new System.Collections.Generic.List<");
            s.Append(p.ElementTypeName ?? "object");
            s.AppendLine(">(16);");
            if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
            {
                // Block sequence: PropertyName(NestedList) → ObjectStart → String("") → ObjectStart(item) → ...
                // Skip the indentation ObjectStart; then loop: String("") → let Deserialize read ObjectStart(item)
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.ElementTypeName ?? "object"
                );
                s.Append(pad);
                s.AppendLine("bool __more;");
                s.Append(pad);
                s.AppendLine("r.Read(); // skip indentation ObjectStart after PropertyName");
                s.Append(pad);
                s.AppendLine("while ((__more = r.Read())) {");
                s.Append(pad);
                s.AppendLine(
                    "    if (r.TokenType != TokenType.String) break; // String('') = sequence item; ObjectEnd = list end"
                );
                s.Append(pad);
                s.Append("    var __item = ");
                s.Append(sn);
                s.AppendLine(
                    ".Deserialize(ref r); // Deserialize reads ObjectStart(item) internally"
                );
                s.Append(pad);
                s.AppendLine("    __tmpList.Add(__item);");
                s.Append(pad);
                s.AppendLine("}");
            }
            else
            {
                s.Append(pad);
                s.AppendLine("bool __more;");
                s.Append(pad);
                s.AppendLine("while ((__more = r.Read()) && r.TokenType == TokenType.String) {");
                EmitDeserializeListElementTemp(s, p, pad + "    ");
                s.Append(pad);
                s.AppendLine("}");
            }
            s.Append(pad);
            EmitTgt();
            s.Append(" = __tmpList");
            if (p.TypeKind == "array")
                s.Append(".ToArray()");
            s.AppendLine(";");
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
        }
        else if (p.TypeKind is "dict")
        {
            s.Append(pad);
            EmitTgt();
            s.Append(" ??= new System.Collections.Generic.Dictionary<");
            s.Append(p.KeyTypeName ?? "string");
            s.Append(", ");
            s.Append(p.ElementTypeName ?? "int");
            s.AppendLine(">();");
            s.Append(pad);
            s.AppendLine("if (r.Read() && r.TokenType == TokenType.ObjectStart) {");
            s.Append(pad);
            s.AppendLine("    while (r.Read() && r.TokenType == TokenType.PropertyName) {");
            s.Append(pad);
            s.AppendLine("        var __dk = Encoding.UTF8.GetString(r.KeySpan);");
            switch (p.ElementTypeKind)
            {
                case "int32":
                    s.Append(pad);
                    s.AppendLine("        r.TryGetInt32(out var __dv);");
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = __dv;");
                    break;
                case "dict":
                {
                    var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlDictInner",
                        p.ElementTypeName!
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.Append("[__dk] = ");
                    s.Append(dsn);
                    s.AppendLine(".Deserialize(ref r);");
                    break;
                }
                case "object":
                {
                    var dsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "YamlInner",
                        p.ElementTypeName!
                    );
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.Append("[__dk] = ");
                    s.Append(dsn);
                    s.AppendLine(".Deserialize(ref r);");
                    break;
                }
                default:
                    s.Append(pad);
                    s.Append("        ");
                    EmitTgt();
                    s.AppendLine("[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
            }
            s.Append(pad);
            s.AppendLine("    }");
            s.Append(pad);
            s.AppendLine("} // exits at ObjectEnd");
        }
        else
        {
            switch (p.TypeKind)
            {
                case "string":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
                case "int32":
                    s.Append(pad);
                    s.AppendLine("r.TryGetInt32(out var __v);");
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = __v;");
                    break;
                case "int64":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = long.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "float32":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = float.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "float64":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = double.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "boolean":
                    s.Append(pad);
                    EmitTgt();
                    s.AppendLine(" = r.ValueSpan.Length > 0 && r.ValueSpan[0] == (byte)'t';");
                    break;
                case "datetime":
                    s.Append(pad);
                    s.AppendLine("var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                    s.Append(pad);
                    if (p.DateTimeFormat is not null)
                    {
                        s.Append("System.DateTime.TryParseExact(__raw, \"");
                        s.Append(p.DateTimeFormat);
                        s.AppendLine(
                            "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt);"
                        );
                    }
                    else
                        s.AppendLine(
                            "System.DateTime.TryParse(__raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
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
                    EmitTgt();
                    s.Append(" = System.Enum.Parse<");
                    s.Append(p.TypeFullName);
                    s.AppendLine(">(Encoding.UTF8.GetString(r.ValueSpan));");
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
            s.AppendLine("if (!r.Read()) break;");
        }
    }

    private static void EmitDeserializeListElementTemp(StringBuilder s, PropertyInfo p, string pad)
    {
        switch (p.ElementTypeKind)
        {
            case "string":
                s.Append(pad);
                s.AppendLine("__tmpList.Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
            case "int32":
                s.Append(pad);
                s.AppendLine("r.TryGetInt32(out var __ev);");
                s.Append(pad);
                s.AppendLine("__tmpList.Add(__ev);");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlDictInner",
                    p.ElementTypeName!
                );
                s.Append(pad);
                s.Append("__tmpList.Add(");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref r));");
                break;
            }
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "YamlInner",
                    p.ElementTypeName!
                );
                s.Append(pad);
                s.Append("__tmpList.Add(");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref r));");
                break;
            }
            default:
                s.Append(pad);
                s.AppendLine("__tmpList.Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
        }
    }

    // ── Recursive nested-list helpers ──

    private static bool IsYamlNestedList(PropertyInfo p) =>
        (p.ElementTypeKind is "list" or "array") && p.NestedProperties.Length > 0;

    private static void EmitYamlNestedListSerialize(
        StringBuilder s,
        PropertyInfo p,
        string accessor,
        string ind
    )
    {
        s.Append(ind);
        s.AppendLine("yw.WriteStartSequence();");
        s.Append(ind);
        s.Append("foreach (var __item in ");
        s.Append(accessor);
        s.AppendLine(")");
        s.Append(ind);
        s.AppendLine("{");

        if (IsYamlNestedList(p))
        {
            EmitYamlNestedListSerialize(s, p.NestedProperties[0], "__item", ind + "    ");
        }
        else
        {
            s.Append(ind);
            s.AppendLine("    yw.WriteStartSequence();");
            s.Append(ind);
            s.AppendLine("    foreach (var __inner in __item)");
            s.Append(ind);
            s.AppendLine("    {");
            var innerKind = p.TypeKind;
            s.Append(ind);
            s.Append("        yw.WriteSequenceItem(Encoding.UTF8.GetBytes(");
            if (innerKind == "string")
                s.Append("__inner");
            else
                s.Append("__inner.ToString()");
            s.AppendLine("));");
            s.Append(ind);
            s.AppendLine("    }");
            s.Append(ind);
            s.AppendLine("    yw.WriteEndSequence();");
        }

        s.Append(ind);
        s.AppendLine("}");
        s.Append(ind);
        s.AppendLine("yw.WriteEndSequence();");
    }

    static string GenPoly(TypeInfo type, Dictionary<string, TypeInfo> typeMap)
    {
        var dpn = type.DiscriminatorPropertyName ?? "$type";
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
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value) {");
        s.AppendLine("        var yw = new YamlWriter(writer);");
        s.AppendLine("        yw.WriteStartMapping();");
        s.Append("        yw.WritePropertyName(\"");
        s.Append(dpnEsc);
        s.AppendLine("\"u8);");
        s.AppendLine("        switch (value)");
        s.AppendLine("        {");
        foreach (var dt in type.DerivedTypes)
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
                if (PicoSerDe.Gen.GenInfrastructure.IsComplexMember(prop))
                    continue;
                var pn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName);
                // DefaultIgnoreCondition: same guard as the other YAML emit paths
                var acc = $"__v.{prop.Name}";
                bool guard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                    s,
                    prop,
                    acc,
                    "                "
                );
                s.Append("                yw.WritePropertyName(\"");
                s.Append(pn);
                s.AppendLine("\"u8);");
                if (guard)
                {
                    // Never + null → property name only ('key:' == YAML null)
                    s.Append("                if (");
                    s.Append(acc);
                    s.AppendLine(" != null)");
                    s.Append("                    ");
                    WriteYamlValue(
                        s,
                        prop,
                        prop.IsNullable && !prop.IsNullableReference ? acc + "!.Value" : acc
                    );
                    s.AppendLine();
                    s.AppendLine("                }");
                }
                else
                {
                    s.Append("                ");
                    WriteYamlValue(s, prop, acc);
                    s.AppendLine();
                }
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
        s.AppendLine("        var reader = new YamlReader(data);");
        s.AppendLine("        reader.Read(); // mapping start");
        s.AppendLine("        reader.Read(); // discriminator key");
        s.AppendLine("        var __discVal = reader.ValueSpan;");
        s.Append("        if (!MemoryExtensions.SequenceEqual(reader.KeySpan, \"");
        s.Append(dpnEsc);
        s.AppendLine("\"u8))");
        s.Append("            throw new FormatException(\"Expected discriminator '");
        s.Append(dpnEsc);
        s.AppendLine("'\");");

        for (int i = 0; i < type.DerivedTypes.Length; i++)
        {
            var dt = type.DerivedTypes[i];
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
                    s.Append(cp.TypeFullName);
                    s.Append(" __cp_");
                    s.Append(ci);
                    s.AppendLine(cp.TypeKind == "string" ? " = null!;" : " = default;");
                }
            }
            else
            {
                s.Append("            var obj = new ");
                s.Append(ds);
                s.AppendLine("();");
            }

            s.AppendLine(
                "            while (reader.Read() && reader.TokenType == TokenType.PropertyName) {"
            );
            s.AppendLine("                var __k = reader.KeySpan;");
            s.AppendLine("                var __v = reader.ValueSpan;");
            for (int pi = 0; pi < dti.Properties.Length; pi++)
            {
                var prop = dti.Properties[pi];
                if (PicoSerDe.Gen.GenInfrastructure.IsComplexMember(prop))
                    continue;
                var kw2 = pi == 0 ? "if" : "else if";
                var pn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName);
                s.Append("                ");
                s.Append(kw2);
                s.Append(" (MemoryExtensions.SequenceEqual(__k, \"");
                s.Append(pn);
                s.AppendLine("\"u8)) {");
                if (hasCtor)
                {
                    int matchIdx = -1;
                    for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                        if (
                            string.Equals(
                                dti.CtorParams[ci].Name,
                                prop.Name,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            matchIdx = ci;
                            break;
                        }
                    if (matchIdx >= 0)
                    {
                        s.Append("                    __cp_");
                        s.Append(matchIdx);
                        s.Append(" = ");
                        ReadYamlValue(s, prop);
                        s.AppendLine(";");
                    }
                }
                else
                {
                    s.Append("                    obj.");
                    s.Append(prop.Name);
                    s.Append(" = ");
                    ReadYamlValue(s, prop);
                    s.AppendLine(";");
                }
                s.AppendLine("                }");
            }
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

    private static string EmitYamlValue(AnonFieldInfo f, string vv, string wv)
        => f.TypeKind switch
        {
            "string"   => $"{wv}.WriteString({vv});",
            "int32" or "int64" => $"{wv}.WriteNumber({vv});",
            "float32" or "float64" => $"{wv}.WriteNumber({vv});",
            "boolean"  => $"{wv}.WriteBoolean({vv});",
            _          => $"{wv}.WriteString({vv}.ToString());",
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
        else if (p.TypeKind == "float64" || p.TypeKind == "float32")
            s.Append("double.TryParse(__v, out var __dv) ? __dv : 0");
        else if (p.TypeKind == "boolean")
            s.Append("bool.TryParse(__v, out var __bv) ? __bv : false");
        else
            s.Append("default");
    }
}
