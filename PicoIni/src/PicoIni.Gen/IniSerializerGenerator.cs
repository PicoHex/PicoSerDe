namespace PicoIni.Gen;

using Microsoft.CodeAnalysis.CSharp;
using AnonFieldInfo = PicoSerDe.Gen.AnonFieldInfo;
using CtorParamInfo = PicoSerDe.Gen.CtorParamInfo;
using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class IniSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "IniSerializer",
        "PicoIni",
        "ini",
        "IniConstructorAttribute"
    );

    private static readonly DiagnosticDescriptor AnonRequiresCSharp12 = new(
        id: "PICOINI003",
        title: "Anonymous types require C# 12+",
        messageFormat: "Anonymous type serialization requires C# 12 or later",
        category: "PicoIni.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor AnonRequiresUnsafe = new(
        id: "PICOINI004",
        title: "Requires AllowUnsafeBlocks",
        messageFormat: "Anonymous type serialization requires AllowUnsafeBlocks to be enabled",
        category: "PicoIni.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        HasIniCamelCase,
        GetIniKey,
        HasIniIgnore,
        GetIniConverter,
        GetIniDateTimeFormat,
        GetSectionName: GetIniSection,
        GetComment: GetIniComment,
        GetPropertyComment: GetIniPropertyComment
    );

    private static readonly PicoSerDe.Gen.AnonFormatConfig AnonCfg = new(
        HasNullLiteral: false,
        EmbedsKeyInValue: true,
        ObjectStartMethod: null,
        ObjectEndMethod: null,
        ObjectStartNeedsCount: false,
        HasIndentedMaxDepth: false,
        KeyIsEncodedString: false,
        HasNamingPolicy: false,
        HasOptionsParam: false,
        FacadeTakesOptions: true,
        HasIndented: true,
        NestedSectionMethod: "WriteSection"
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

        // Pipeline C: format-specific attribute — discover types via [PicoIniSerializable]
        var formatAttr = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoIni.PicoIniSerializableAttribute",
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
                        (f, vv, wv) => EmitIniValue(f, vv, wv)
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
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

        context.RegisterSourceOutput(
            all,
            static (spc, types) =>
            {
                PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = null;
                GenerateAll(spc, types);
            }
        );
    }

    // ── Candidate detection ──

    private static bool IsCandidate(SyntaxNode node) =>
        PicoSerDe.Gen.GenInfrastructure.IsCandidate(node);

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        // Detect [IniConstructor] and record primary constructors
        bool hasCtor = false;
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol method
            && method.TypeArguments.Length == 1
        )
        {
            // ── Top-level List<T> (e.g. List<int>, List<SomeDto>) ──
            if (
                method.TypeArguments[0] is INamedTypeSymbol ntsList
                && PicoSerDe.Gen.GenInfrastructure.IsGenericList(ntsList)
            )
                return PicoSerDe.Gen.GenInfrastructure.TransformTopLevelList(
                    ntsList,
                    Config,
                    Attrs
                );

            if (method.TypeArguments[0] is not INamedTypeSymbol namedType)
                return null;
            if (namedType.IsAnonymousType)
                return null; // handled by anonDriven pipeline

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

            // Record primary constructor auto-detection
            if (namedType.IsRecord)
            {
                var ctors = namedType
                    .Constructors.Where(c => c.DeclaredAccessibility == Accessibility.Public)
                    .ToArray();
                if (ctors.Length == 1 && !ctors[0].IsImplicitlyDeclared)
                    hasCtor = true;
            }
            // [IniConstructor] attribute
            if (!hasCtor)
            {
                foreach (var ctor in namedType.Constructors)
                {
                    if (ctor.DeclaredAccessibility != Accessibility.Public)
                        continue;
                    foreach (var attr in ctor.GetAttributes())
                    {
                        if (attr.AttributeClass?.Name == "IniConstructorAttribute")
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

        if (!hasCtor)
            return ti;

        // Extract constructor parameters
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method2
            || method2.TypeArguments.Length != 1
            || method2.TypeArguments[0] is not INamedTypeSymbol namedType2
        )
            return ti;

        var ctorParams = PicoSerDe.Gen.GenInfrastructure.DetectConstructor(
            namedType2,
            Config.FormatTag,
            "IniConstructorAttribute"
        );
        if (ctorParams is not { } cp)
            return ti;

        return ti with
        {
            CtorParams = cp,
        };
    }

    // ── Attribute helpers ──

    private static bool HasIniCamelCase(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniCamelCaseAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
            )
                return true;
        }
        return false;
    }

    private static string? GetIniKey(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniKeyAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string name
            )
                return name;
        }
        return null;
    }

    private static bool HasIniIgnore(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniIgnoreAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
            )
                return true;
        }
        return false;
    }

    private static string? GetIniConverter(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniConverterAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol ct
            )
                return ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        return null;
    }

    private static string? GetIniDateTimeFormat(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniDateTimeFormatAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && attr.ConstructorArguments.Length >= 1
                && attr.ConstructorArguments[0].Value is string fmt
            )
                return fmt;
        }
        return null;
    }

    private static string? GetIniSection(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniSectionAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string name
            )
                return name;
        }
        return null;
    }

    private static string? GetIniComment(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniCommentAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string text
            )
                return text;
        }
        return null;
    }

    private static string? GetIniPropertyComment(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "IniCommentAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string text
            )
                return text;
        }
        return null;
    }

    // ── Source generation ──

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        // Recursive members cannot be represented by INI sections: surface the
        // per-format skip as the shared PICOSERDE003 diagnostic.
        PicoSerDe.Gen.GenInfrastructure.ReportSkippedRecursiveMembers(spc, types, Config.FormatTag);
        PicoSerDe.Gen.GenInfrastructure.ReportUnsupportedMembers(spc, types, Config.FormatTag);
        // PICOSERDE001: a member whose type is unresolvable for this format is
        // recorded during extraction (it cannot be reported there — a transform must
        // stay pure), so the drop becomes visible here, once per member (D-6).
        PicoSerDe.Gen.GenInfrastructure.ReportCollectedDiagnostics(
            spc,
            PicoSerDe.Gen.GenInfrastructure.CollectUnsupportedTypeMemberDiagnostics(
                types,
                Config.FormatTag
            )
        );
        // Merge duplicate FQNs, preferring poly entries (same as JSON SG)
        var typeMap = new Dictionary<string, TypeInfo>();
        foreach (var t in types)
        {
            if (string.IsNullOrEmpty(t.FullyQualifiedName))
                continue;
            if (typeMap.TryGetValue(t.FullyQualifiedName, out var existing))
            {
                if (!t.DerivedTypes.IsDefaultOrEmpty && existing.DerivedTypes.IsDefaultOrEmpty)
                    typeMap[t.FullyQualifiedName] = t;
            }
            else
            {
                typeMap[t.FullyQualifiedName] = t;
            }
        }

        var hintNames = new HashSet<string>();
        foreach (var kv in typeMap)
        {
            var t = kv.Value;
            if (string.IsNullOrEmpty(t.Name))
                continue;
            var safeFq = PicoSerDe.Gen.GenInfrastructure.UniqueName(t.FullyQualifiedName ?? "");
            var hintName = $"{safeFq}_IniSerializer.g.cs";
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

    private static string Gen(TypeInfo type)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoIni;");
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            s.Append("using ");
            s.Append(type.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        // Serializer
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("IniSerializer : ISerializer<");
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
        s.AppendLine("        var __opts = (global::PicoIni.IniOptions?)options;");
        s.AppendLine(
            "        var iw = new IniWriter(writer, indented: __opts?.Indented ?? false);"
        );

        // Emit class-level comment if any property has one (they share the same containing type)
        var typeComment = type.Properties.Select(p => p.Comment).FirstOrDefault(c => c is not null);
        if (typeComment is not null)
        {
            // Comment text is arbitrary user text (it may carry quotes or
            // backslashes): it has to be escaped into the C# literal, not pasted
            // raw (review C-16).
            s.Append("        iw.WriteComment(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(typeComment));
            s.AppendLine("\");");
        }

        // Top-level properties first
        for (int pi = 0; pi < type.Properties.Length; pi++)
        {
            var p = type.Properties[pi];
            if (p.TypeKind == "object" || p.TypeKind == "dict")
                continue;
            if (p.Comment is not null)
            {
                s.Append("        iw.WriteComment(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.Comment));
                s.AppendLine("\");");
            }
            bool checkNull = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                s,
                p,
                "value." + p.Name,
                "        "
            );
            // INI has no null literal — null values are always omitted,
            // regardless of DefaultIgnoreCondition.
            var pad = checkNull ? "            " : "        ";
            var acc =
                p.IsNullable && !p.IsNullableReference
                    ? $"value.{p.Name}!.Value"
                    : $"value.{p.Name}";
            // The slot keeps converter locals unique across properties (C-1).
            EmitWriteKeyValue(s, p.JsonName, p, acc, pad, slot: pi.ToString());
            if (checkNull)
                s.AppendLine("        }");
        }

        // Dicts as sections (before objects)
        var dictProps = type.Properties.Where(p => p.TypeKind == "dict").ToArray();
        // Whether a section is actually written is a runtime fact once a null
        // guard is involved, so the separator has to be runtime decidable there.
        // Driving it from "dicts visited" emitted a leading blank line when the
        // first dict was null (review I-4).
        bool runtimeDictFlag =
            dictProps.Length > 1
            && dictProps.Any(PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable);
        if (runtimeDictFlag)
            s.AppendLine("        bool __wroteSection = false;");
        bool dictFirst = true;
        foreach (var p in dictProps)
        {
            // A null dictionary has no section to write (C-31). The guard also
            // owns the blank-line separator so a skipped dict leaves no stray
            // blank line behind.
            bool dictGuard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                s,
                p,
                "value." + p.Name,
                "        "
            );
            var dictPad = dictGuard ? "            " : "        ";
            if (runtimeDictFlag)
            {
                s.AppendLine(dictPad + "if (__wroteSection) iw.WriteBlankLine();");
                s.AppendLine(dictPad + "__wroteSection = true;");
            }
            else if (!dictFirst)
                s.AppendLine(dictPad + "iw.WriteBlankLine();");
            s.Append(dictPad + "iw.WriteSection(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            s.Append(dictPad + "foreach (var __kvp in value.");
            s.Append(p.Name);
            s.AppendLine(")");
            s.AppendLine(dictPad + "{");
            s.Append(dictPad + "    iw.WriteKeyValue(__kvp.Key, ");
            WriteValue(s, p, "__kvp.Value");
            s.AppendLine(");");
            s.AppendLine(dictPad + "}");
            if (dictGuard)
                s.AppendLine("        }");
            dictFirst = false;
        }

        // Sections after (nested objects)
        bool first = true;
        foreach (var p in type.Properties)
        {
            if (p.TypeKind != "object")
                continue;
            bool nullGuard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                s,
                p,
                "value." + p.Name,
                "        "
            );
            var objPad = nullGuard ? "            " : "        ";
            if (!first)
                s.AppendLine(objPad + "iw.WriteBlankLine();");
            // [IniSection("...")] is arbitrary user text — escape it like any
            // other emitted literal (review C-16).
            var sn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(
                p.SectionName ?? p.JsonName
            );
            s.Append(objPad + "iw.WriteSection(\"");
            s.Append(sn);
            s.AppendLine("\"u8);");
            for (int npi = 0; npi < p.NestedProperties.Length; npi++)
            {
                var np = p.NestedProperties[npi];
                // INI sections are flat — a nested object inside a section cannot
                // be represented. Fail loudly instead of writing "TypeName" text.
                if (np.TypeKind == "object")
                {
                    s.Append(
                        objPad
                            + "throw new System.NotSupportedException(\"INI sections cannot represent nested objects deeper than one level yet.\");"
                    );
                    s.AppendLine();
                    continue;
                }
                // DefaultIgnoreCondition: INI has no null literal — null
                // section values are always omitted.
                bool npCheck = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                    s,
                    np,
                    $"value.{p.Name}.{np.Name}",
                    objPad
                );
                // Section members honour [IniConverter] on the write path, exactly
                // like top-level properties (review C-3).
                var npAcc =
                    np.IsNullable && !np.IsNullableReference
                        ? $"value.{p.Name}.{np.Name}!.Value"
                        : $"value.{p.Name}.{np.Name}";
                var npPad = npCheck ? objPad + "    " : objPad;
                EmitWriteKeyValue(s, np.JsonName, np, npAcc, npPad, slot: npi.ToString());
                if (npCheck)
                    s.AppendLine(objPad + "}");
            }
            if (nullGuard)
                s.AppendLine("        }");
            first = false;
        }
        s.AppendLine("    } }");
        s.AppendLine();

        var top = new List<PropertyInfo>();
        foreach (var p in type.Properties)
            if (!PicoSerDe.Gen.GenInfrastructure.IsComplexMember(p))
                top.Add(p);
        var sec = new List<PropertyInfo>();
        foreach (var p in type.Properties)
            if (p.TypeKind == "object")
                sec.Add(p);
        var dicts = new List<PropertyInfo>();
        foreach (var p in type.Properties)
            if (p.TypeKind == "dict")
                dicts.Add(p);

        // Deserializer
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("IniDeserializer : IDeserializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(type.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.AppendLine("        var reader = new IniReader(data);");
        var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;
        if (hasCtor)
        {
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
            {
                var cp = type.CtorParams[ci];
                var tn = cp.TypeFullName;
                // 'default' (not 'default!') on a reference-typed parameter is a
                // CS8600 in the generated code; every non-string parameter is
                // overwritten before the constructor call, so the suppression is
                // accurate — mirroring the poly path's initializer.
                var def = cp.TypeKind == "string" ? "\"\"" : "default!";
                s.Append("        ");
                s.Append(tn);
                s.Append(" __cp_");
                s.Append(ci);
                s.Append(" = ");
                s.Append(def);
                s.AppendLine(";");
            }
        }
        else
        {
            var reqProps = type.Properties.Where(p => p.IsRequired).ToArray();
            if (reqProps.Length > 0)
            {
                s.Append("        var obj = new ");
                s.Append(type.Name);
                s.AppendLine(" {");
                foreach (var rp in reqProps)
                {
                    s.Append("            ");
                    s.Append(rp.Name);
                    s.Append(" = ");
                    switch (rp.TypeKind)
                    {
                        case "string":
                            s.Append("\"\"");
                            break;
                        default:
                            s.Append("default");
                            break;
                    }
                    s.AppendLine(",");
                }
                s.Append("        };");
            }
            else
            {
                s.Append("        var obj = new ");
                s.Append(type.Name);
                s.AppendLine("();");
            }
        }
        if (sec.Count > 0 || dicts.Count > 0)
            s.AppendLine("        int __sec = -1;");
        EmitIniWhileLoop(s, type, hasCtor, top, sec, dicts, streaming: false);
        if (hasCtor)
        {
            s.Append("        return new ");
            s.Append(type.Name);
            s.Append("(");
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
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
            s.AppendLine("        return obj;");
        }
        s.AppendLine("    } }");
        s.AppendLine();

        // Streaming: property-level resume (ctor types fall back to buffering).
        if (!hasCtor)
            EmitIniStreaming(s, type, top, sec, dicts);
        s.AppendLine();

        // Registration
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("__Reg {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        IniSerializer.Register<");
        s.Append(type.Name);
        s.Append(">(new ");
        s.Append(type.Name);
        s.Append("IniSerializer(), new ");
        s.Append(type.Name);
        s.AppendLine("IniDeserializer());");
        if (hasCtor)
            s.AppendLine("        // Streaming deserializer skipped for constructor types");
        else
        {
            s.Append("        IniSerializer.RegisterStreaming<");
            s.Append(type.Name);
            s.Append(">(");
            s.Append(type.Name);
            s.AppendLine("IniStreaming.DeserializeStreaming);");
        }
        s.AppendLine("    } }");
        return s.ToString();
    }

    private static Dictionary<string, int> BuildCtorMap(TypeInfo type)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int ci = 0; ci < type.CtorParams.Length; ci++)
            map[type.CtorParams[ci].Name] = ci;
        return map;
    }

    /// <summary>
    /// Emits the shared INI read loop (synchronous and streaming variants). In
    /// streaming mode an incomplete chunk rewinds to the member — or to the
    /// current section start — and asks for more data.
    /// </summary>
    private static void EmitIniWhileLoop(
        StringBuilder s,
        TypeInfo type,
        bool hasCtor,
        List<PropertyInfo> top,
        List<PropertyInfo> sec,
        List<PropertyInfo> dicts,
        bool streaming
    )
    {
        if (streaming)
        {
            s.AppendLine("        while (true) {");
            s.AppendLine("            reader.Mark();");
            s.AppendLine("            if (!reader.Read()) break;");
        }
        else
            s.AppendLine("        while (reader.Read()) {");
        s.AppendLine("            if (reader.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var __k = reader.GetStringRaw();");
        s.AppendLine("                reader.ReadValue(); // fast path: consume pending value");

        // Build ctor param name → index map for constructor deserialization
        var ctorMap = hasCtor ? BuildCtorMap(type) : null;

        // Top-level key matching
        if (top.Count > 0)
        {
            EmitKeyDispatch(
                s,
                top,
                "__k",
                "obj",
                sec.Count > 0 ? "__sec < 0" : null,
                "                ",
                "                    ",
                ctorMap
            );
        }
        if (sec.Count > 0 || dicts.Count > 0)
        {
            bool firstSection = true;
            for (int si = 0; si < sec.Count; si++)
            {
                foreach (var np in sec[si].NestedProperties)
                {
                    s.Append(firstSection ? "                if" : "                else if");
                    firstSection = false;
                    s.Append(" (__sec == ");
                    s.Append(si);
                    s.Append(" && TextHelpers.Eq(__k, \"");
                    s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(np.JsonName));
                    s.Append("\"u8)) { ");
                    EmitRead(s, np, $"obj.{sec[si].Name}!", "");
                    s.AppendLine(" }");
                }
            }
            // Dict section key matching — match any key within the dict section
            for (int di = 0; di < dicts.Count; di++)
            {
                s.Append(firstSection ? "                if" : "                else if");
                firstSection = false;
                s.Append(" (__sec == ");
                s.Append(sec.Count + di);
                s.AppendLine(") {");
                s.Append("                    obj.");
                s.Append(dicts[di].Name);
                s.Append(" ??= new System.Collections.Generic.Dictionary<");
                s.Append(dicts[di].KeyTypeName ?? "string");
                s.Append(", ");
                s.Append(dicts[di].ElementTypeNameAnnotated ?? dicts[di].ElementTypeName ?? "int");
                s.AppendLine(">();");
                s.Append("                    { var __dk = Encoding.UTF8.GetString(__k); ");
                if (dicts[di].ElementTypeKind == "int32")
                {
                    s.Append(
                        "if (!reader.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\"); obj."
                    );
                    s.Append(dicts[di].Name);
                    s.AppendLine("[__dk] = __dv; }");
                }
                else if (
                    dicts[di].ElementTypeKind
                    is "int16"
                        or "uint16"
                        or "sbyte"
                        or "byte"
                        or "uint32"
                        or "int64"
                )
                {
                    s.Append(
                        "if (!reader.TryGetInt64(out var __dv)) { if (!reader.TryGetUInt64(out var __duv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\"); __dv = checked((long)__duv); } obj."
                    );
                    s.Append(dicts[di].Name);
                    s.Append("[__dk] = checked((");
                    s.Append(IniNumericCastExpr(dicts[di].ElementTypeKind!));
                    s.AppendLine(")__dv); }");
                }
                else if (dicts[di].ElementTypeKind == "uint64")
                {
                    s.Append(
                        "if (!reader.TryGetUInt64(out var __dv)) throw new System.FormatException($\"Expected a 64-bit unsigned integer at offset {reader.BytesConsumed}\"); obj."
                    );
                    s.Append(dicts[di].Name);
                    s.AppendLine("[__dk] = __dv; }");
                }
                else if (dicts[di].ElementTypeKind == "string")
                {
                    s.Append("obj.");
                    s.Append(dicts[di].Name);
                    s.AppendLine("[__dk] = Encoding.UTF8.GetString(reader.GetStringRaw()); }");
                }
                else
                {
                    s.Append("obj.");
                    s.Append(dicts[di].Name);
                    s.AppendLine("[__dk] = Encoding.UTF8.GetString(reader.GetStringRaw()); }");
                }
                s.AppendLine("                }");
            }
        }
        s.AppendLine("            }");
        if (sec.Count > 0 || dicts.Count > 0)
        {
            s.AppendLine("            else if (reader.TokenType == TokenType.ObjectStart) {");
            s.AppendLine("                __sec = -1;");

            for (int i = 0; i < sec.Count; i++)
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(
                    sec[i].SectionName ?? sec[i].JsonName
                );
                s.Append("                ");
                s.Append(i == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(reader.GetStringRaw(), \"");
                s.Append(sn);
                s.AppendLine("\"u8)) {");
                s.Append("                    obj.");
                s.Append(sec[i].Name);
                s.Append(" ??= new ");
                s.Append(sec[i].TypeFullName);
                s.AppendLine("();");
                s.Append("                    __sec = ");
                s.Append(i);
                s.AppendLine(";");
                s.AppendLine("                }");
            }
            for (int i = 0; i < dicts.Count; i++)
            {
                s.Append("                ");
                s.Append(i == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(reader.GetStringRaw(), \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dicts[i].JsonName));
                s.AppendLine("\"u8)) {");
                s.Append("                    obj.");
                s.Append(dicts[i].Name);
                s.Append(" ??= new System.Collections.Generic.Dictionary<");
                s.Append(dicts[i].KeyTypeName ?? "string");
                s.Append(", ");
                s.Append(dicts[i].ElementTypeNameAnnotated ?? dicts[i].ElementTypeName ?? "int");
                s.AppendLine(">();");
                s.Append("                    __sec = ");
                s.Append(sec.Count + i);
                s.AppendLine(";");
                s.AppendLine("                }");
            }
            s.AppendLine("            }");
        }
        s.AppendLine("        }");
    }

    /// <summary>
    /// Emits the streaming deserializer: the shared read loop plus a
    /// snapshot/rewind epilogue so an incomplete chunk resumes at the member or
    /// section start with <c>partial</c> carrying previously parsed members.
    /// </summary>
    private static void EmitIniStreaming(
        StringBuilder s,
        TypeInfo type,
        List<PropertyInfo> top,
        List<PropertyInfo> sec,
        List<PropertyInfo> dicts
    )
    {
        var reqProps = type.Properties.Where(p => p.IsRequired).ToArray();
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("IniStreaming {");
        s.Append("    internal static ReadStatus DeserializeStreaming(ref IniReader reader, ");
        s.Append(type.Name);
        s.AppendLine("? partial, out ");
        s.Append(type.Name);
        s.AppendLine("? result) {");
        s.Append("        var obj = partial ?? new ");
        s.Append(type.Name);
        if (reqProps.Length > 0)
        {
            s.Append(" { ");
            foreach (var rp in reqProps)
            {
                s.Append(rp.Name);
                s.Append(" = ");
                s.Append(rp.TypeKind == "string" ? "\"\"" : "default");
                s.Append(", ");
            }
            s.Append("}");
        }
        else
        {
            s.Append("()");
        }
        s.AppendLine(";");
        s.AppendLine("        result = obj;");
        if (sec.Count > 0 || dicts.Count > 0)
            s.AppendLine("        int __sec = -1;");
        // Reuse the sync loop emission with per-read marks; every NeedMoreData
        // return rewinds to the last mark (a member or section header start).
        EmitIniWhileLoop(s, type, hasCtor: false, top, sec, dicts, streaming: true);
        s.AppendLine("        if (reader.NeedsMoreData) {");
        s.AppendLine("            reader.RewindToMark();");
        s.AppendLine("            return ReadStatus.NeedMoreData;");
        s.AppendLine("        }");
        s.AppendLine("        result = obj;");
        s.AppendLine("        return ReadStatus.Success;");
        s.AppendLine("    }");
        s.AppendLine("}");
    }

    /// <summary>Generates ref struct serializer — static class + delegate registration.</summary>
    private static string GenRefStruct(TypeInfo type)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoIni;");
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            s.Append("using ");
            s.Append(type.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        // Serializer — file static class
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("IniSer {");
        s.Append("    public static void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoIni.IniOptions?)options;");
        s.AppendLine(
            "        var iw = new IniWriter(writer, indented: __opts?.Indented ?? false);"
        );

        var top = type
            .Properties.Where(p => !PicoSerDe.Gen.GenInfrastructure.IsComplexMember(p))
            .ToList();
        foreach (var p in top)
        {
            s.Append("        iw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, ");
            WriteValue(s, p, $"value.{p.Name}");
            s.AppendLine(");");
        }

        s.AppendLine("    } }");
        s.AppendLine();

        // Registration — delegate-based
        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"{type.Namespace}.{type.Name}";
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        IniSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            ");
        s.Append(type.Name);
        s.AppendLine("IniSer.Serialize);");
        s.AppendLine("    } }");

        return s.ToString();
    }

    /// <summary>Generates serializer/deserializer for a top-level List&lt;T&gt;. Primitives use comma-separated; objects use sections [0]/[1]/...</summary>
    private static string GenList(TypeInfo type)
    {
        var s = new StringBuilder();
        var listFqn = type.FullyQualifiedName;
        var elemKind = type.ArrayElementKind!;
        var elemTypeName = type.ArrayElementName!;

        // Synthetic PropertyInfo — drives WriteValue (ser) and EmitReadValue (deser)
        var elemP = new PropertyInfo(
            Name: "__elem",
            JsonName: "__elem",
            TypeKind: elemKind,
            TypeFullName: elemTypeName,
            IsNullable: false,
            ElementTypeKind: elemKind,
            ElementTypeName: elemTypeName,
            KeyTypeKind: null,
            KeyTypeName: null,
            NestedProperties: type.ArrayElementNestedProps.IsDefaultOrEmpty
                ? ImmutableArray<PropertyInfo>.Empty
                : type.ArrayElementNestedProps,
            ConverterTypeFullName: null
        );

        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using System.Collections.Generic; using System.Runtime.CompilerServices;");
        s.AppendLine("using PicoSerDe.Core; using PicoIni;");
        s.AppendLine();

        var isObj = elemKind == "object" && elemP.NestedProperties.Length > 0;

        // ── Serializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("IniSerializer : ISerializer<");
        s.Append(listFqn);
        s.AppendLine(">, global::PicoSerDe.Core.IOptionsSerializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(listFqn);
        s.AppendLine(" value) => Serialize(writer, value, null);");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(listFqn);
        s.AppendLine(" value, global::PicoSerDe.Core.SerOptions? options) {");
        if (isObj)
        {
            s.AppendLine("        var __opts = (global::PicoIni.IniOptions?)options;");
            s.AppendLine(
                "        var iw = new IniWriter(writer, indented: __opts?.Indented ?? false);"
            );
            s.AppendLine("        for (int i = 0; i < value.Count; i++) {");
            s.AppendLine("            if (i > 0) iw.WriteBlankLine();");
            s.AppendLine("            iw.WriteSection(i.ToString());");
            foreach (var np in elemP.NestedProperties)
            {
                s.Append("            iw.WriteKeyValue(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(np.JsonName));
                s.Append("\", ");
                WriteValue(s, np, $"value[i].{np.Name}");
                s.AppendLine(");");
            }
            s.AppendLine("        }");
        }
        else
        {
            s.AppendLine("        var sb = new StringBuilder();");
            s.AppendLine("        for (int i = 0; i < value.Count; i++) {");
            s.AppendLine("            if (i > 0) sb.Append(',');");
            s.Append("            var __elem = value[i]; sb.Append(");
            WriteValue(s, elemP, "__elem");
            s.AppendLine(");");
            s.AppendLine("        }");
            s.AppendLine("        var bytes = Encoding.UTF8.GetBytes(sb.ToString());");
            s.AppendLine("        writer.Write(bytes);");
        }
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Deserializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("IniDeserializer : IDeserializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(listFqn);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.Append("        var __list = new List<");
        s.Append(elemTypeName);
        s.AppendLine(">(16);");
        s.AppendLine("        var __str = Encoding.UTF8.GetString(data);");
        if (isObj)
        {
            s.AppendLine("        int __p = 0;");
            s.Append(elemTypeName);
            s.AppendLine("? __cur = null;");
            s.AppendLine("        while (__p < __str.Length)");
            s.AppendLine("        {");
            s.AppendLine(
                "            while (__p < __str.Length && (__str[__p] == '\\r' || __str[__p] == '\\n')) __p++;"
            );
            s.AppendLine("            if (__p >= __str.Length) break;");
            s.AppendLine("            if (__str[__p] == '[')");
            s.AppendLine("            {");
            s.AppendLine("                if (__cur != null) __list.Add(__cur);");
            s.Append("                __cur = new ");
            s.Append(elemTypeName);
            s.AppendLine("();");
            s.AppendLine(
                "                while (__p < __str.Length && __str[__p] != '\\n') __p++;"
            );
            s.AppendLine("                __p++;");
            s.AppendLine("            }");
            s.AppendLine("            else if (__cur != null)");
            s.AppendLine("            {");
            s.AppendLine("                int __eq = __p;");
            s.AppendLine(
                "                while (__eq < __str.Length && __str[__eq] != '\\n' && __str[__eq] != '=') __eq++;"
            );
            s.AppendLine("                if (__eq < __str.Length && __str[__eq] == '=')");
            s.AppendLine("                {");
            s.AppendLine(
                "                    var __k = __str.Substring(__p, __eq - __p).TrimEnd();"
            );
            s.AppendLine("                    __eq++;");
            s.AppendLine("                    int __ve = __eq;");
            s.AppendLine(
                "                    while (__ve < __str.Length && __str[__ve] != '\\n' && __str[__ve] != '\\r') __ve++;"
            );
            s.AppendLine(
                "                    var __v = __str.Substring(__eq, __ve - __eq).TrimStart().Trim('\"');"
            );
            s.AppendLine("                    var __rv = Encoding.UTF8.GetBytes(__v).AsSpan();");
            for (int i = 0; i < elemP.NestedProperties.Length; i++)
            {
                var np = elemP.NestedProperties[i];
                s.Append(i == 0 ? "                    if" : "                    else if");
                s.Append(" (string.Equals(__k, \"");
                s.Append(np.JsonName);
                s.AppendLine("\", StringComparison.OrdinalIgnoreCase))");
                s.AppendLine("                    {");
                s.Append("                        __cur.");
                s.Append(np.Name);
                s.Append(" = ");
                EmitReadValueStrict(s, np);
                s.AppendLine(";");
                s.AppendLine("                    }");
            }
            s.AppendLine("                }");
            s.AppendLine(
                "                while (__p < __str.Length && __str[__p] != '\\n') __p++;"
            );
            s.AppendLine("                __p++;");
            s.AppendLine("            }");
            s.AppendLine("            else");
            s.AppendLine("            {");
            s.AppendLine(
                "                while (__p < __str.Length && __str[__p] != '\\n') __p++;"
            );
            s.AppendLine("                __p++;");
            s.AppendLine("            }");
            s.AppendLine("        }");
            s.AppendLine("        if (__cur != null) __list.Add(__cur);");
        }
        else
        {
            s.AppendLine("        int __p = 0, __seg = 0;");
            s.AppendLine("        while (__p < __str.Length) {");
            s.AppendLine(
                "            if (__str[__p] == '\\\\' && __p + 1 < __str.Length) __p += 2;"
            );
            s.AppendLine("            else if (__str[__p] == ',') {");
            s.AppendLine(
                "                var __raw = __str.Substring(__seg, __p - __seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\");"
            );
            s.AppendLine("                var __rv = Encoding.UTF8.GetBytes(__raw).AsSpan();");
            s.Append("                __list.Add(");
            EmitReadValueStrict(s, elemP);
            s.AppendLine(");");
            s.AppendLine("                __seg = ++__p;");
            s.AppendLine("            }");
            s.AppendLine("            else __p++;");
            s.AppendLine("        }");
            s.AppendLine("        if (__seg < __str.Length) {");
            s.AppendLine(
                "            var __raw = __str.Substring(__seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\");"
            );
            s.AppendLine("            var __rv = Encoding.UTF8.GetBytes(__raw).AsSpan();");
            s.Append("            __list.Add(");
            EmitReadValueStrict(s, elemP);
            s.AppendLine(");");
            s.AppendLine("        }");
        }
        s.AppendLine("        return __list;");
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Registration ──
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("__Reg {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        IniSerializer.Register<");
        s.Append(listFqn);
        s.Append(">(new ");
        s.Append(type.Name);
        s.Append("IniSerializer(), new ");
        s.Append(type.Name);
        s.AppendLine("IniDeserializer());");
        s.AppendLine("    } }");

        return s.ToString();
    }

    /// <summary>
    /// Emits one statement-level key/value write. Unlike <see cref="WriteValue"/>
    /// (a pure expression builder used inside <c>sb.Append(</c> and
    /// <c>string.Join</c>), this can open statements, which converters need:
    /// <c>[IniConverter]</c> writes through an <see cref="ArrayBufferWriter{T}"/>
    /// rather than returning a formatted string.
    /// </summary>
    /// <param name="key">Raw key name; escaped into a <c>"..."u8</c> literal here.</param>
    /// <param name="slot">
    /// Unique stem for the locals declared here. Every converter property of a
    /// type shares one C# scope (only null guards open blocks), so a fixed local
    /// name is a CS0128 duplicate-local error as soon as a DTO has two of them
    /// (review C-1).
    /// </param>
    private static void EmitWriteKeyValue(
        StringBuilder s,
        string key,
        PropertyInfo p,
        string accessor,
        string indent,
        string slot = ""
    )
    {
        var keyText = "\"" + PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(key) + "\"u8";
        if (p.ConverterTypeFullName is not null)
        {
            var cVar = "__c" + slot;
            var bwVar = "__bw" + slot;
            s.Append(indent);
            s.Append("var ");
            s.Append(cVar);
            s.Append(" = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(indent);
            s.Append("var ");
            s.Append(bwVar);
            s.AppendLine(" = new System.Buffers.ArrayBufferWriter<byte>();");
            s.Append(indent);
            s.Append(cVar);
            s.Append(".Write(");
            s.Append(bwVar);
            s.Append(", ");
            s.Append(accessor);
            s.AppendLine(");");
            s.Append(indent);
            s.Append("iw.WriteKeyValue(");
            s.Append(keyText);
            s.Append(", ");
            s.Append(bwVar);
            s.AppendLine(".WrittenSpan);");
            return;
        }
        s.Append(indent);
        s.Append("iw.WriteKeyValue(");
        s.Append(keyText);
        s.Append(", ");
        WriteValue(s, p, accessor);
        s.AppendLine(");");
    }

    private static void WriteValue(StringBuilder s, PropertyInfo p, string acc)
    {
        switch (p.TypeKind)
        {
            case "string":
                s.Append(acc);
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
            case "boolean":
            case "decimal":
            case "guid":
            case "timespan":
            case "dateonly":
            case "timeonly":
                s.Append(acc);
                break;
            case "datetime":
                if (p.DateTimeFormat is not null)
                {
                    s.Append("Encoding.UTF8.GetBytes(");
                    s.Append(acc);
                    s.Append(".ToString(\"");
                    s.Append(p.DateTimeFormat);
                    s.Append("\"))");
                }
                else
                {
                    s.Append(acc);
                }
                break;
            case "enum":
                s.Append("Encoding.UTF8.GetBytes(");
                s.Append(acc);
                s.Append(".ToString())");
                break;
            case "list":
            case "array":
            // R2 Task 6: the SEVEN extended collection kinds (S7) are the same
            // comma-separated line a List<T> is — spec §6 says S7 x ini is
            // representable, and it is: the wire has no notion of "collection kind",
            // only the declared type on the way back has. Spelling `is "list" or
            // "array"` here is what sent HashSet<int> to the scalar fallback
            // (`value.H.ToString()`, i.e. "System.Collections.Generic.HashSet`1[...]").
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
            case "memory":
            case "readonlymemory":
                s.Append("string.Join(\",\", ");
                s.Append(IniSequenceSource(p, acc));
                if (p.ElementTypeKind == "string")
                    s.Append(
                        ".Select(__s => __s.Replace(\"\\\\\", \"\\\\\\\\\").Replace(\",\", \"\\\\,\")))"
                    );
                else if (p.ElementTypeKind is "float32" or "float64" or "decimal")
                    s.Append(
                        ".Select(__s => __s.ToString(System.Globalization.CultureInfo.InvariantCulture)))"
                    );
                else if (p.ElementTypeKind == "datetime")
                    s.Append(
                        ".Select(__s => __s.ToString(\"O\", System.Globalization.CultureInfo.InvariantCulture)))"
                    );
                else if (p.ElementTypeKind is "dateonly" or "timeonly")
                    s.Append(
                        ".Select(__s => __s.ToString(\"O\", System.Globalization.CultureInfo.InvariantCulture)))"
                    );
                else if (p.ElementTypeKind == "timespan")
                    s.Append(
                        ".Select(__s => __s.ToString(\"c\", System.Globalization.CultureInfo.InvariantCulture)))"
                    );
                else if (p.ElementTypeKind == "boolean")
                    s.Append(".Select(__s => __s ? \"true\" : \"false\"))");
                else
                    s.Append(")");
                break;
            default:
                s.Append(acc);
                s.Append(".ToString()");
                break;
        }
    }

    /// <summary>
    /// The enumerable a comma-joined INI list is built from.
    /// <para>
    /// <c>Memory&lt;T&gt;</c>/<c>ReadOnlyMemory&lt;T&gt;</c> are VIEWS, not sequences: they have
    /// to be materialised before <c>string.Join</c>/LINQ can see them. Every other kind
    /// (List, array, HashSet, Queue, Stack, LinkedList, ImmutableArray) enumerates as
    /// itself, which is why this is the only difference the sequence arm has to know.
    /// </para>
    /// </summary>
    private static string IniSequenceSource(PropertyInfo p, string acc) =>
        p.TypeKind is "memory" or "readonlymemory" ? acc + ".ToArray()" : acc;

    private static void EmitKeyDispatch(
        StringBuilder s,
        IReadOnlyList<PropertyInfo> props,
        string keyVar,
        string target,
        string? guard,
        string indent,
        string bodyIndent,
        IReadOnlyDictionary<string, int>? ctorMap = null
    )
    {
        if (props.Count <= 2)
        {
            for (int i = 0; i < props.Count; i++)
            {
                s.Append(i == 0 ? indent + "if" : indent + "else if");
                s.Append(" (");
                if (guard is not null)
                {
                    s.Append(guard);
                    s.Append(" && ");
                }
                s.Append("TextHelpers.Eq(");
                s.Append(keyVar);
                s.Append(", \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(props[i].JsonName));
                s.AppendLine("\"u8)) {");
                EmitReadOrAssign(s, props[i], target, bodyIndent, ctorMap);
                s.Append(indent);
                s.AppendLine("}");
            }
            return;
        }

        var outerIndent = indent;
        if (guard is not null)
        {
            s.Append(indent);
            s.Append("if (");
            s.Append(guard);
            s.AppendLine(") {");
            indent += "    ";
            bodyIndent += "    ";
        }

        s.Append(indent);
        s.Append("switch (");
        s.Append(keyVar);
        s.AppendLine(".Length)");
        s.Append(indent);
        s.AppendLine("{");
        foreach (
            var group in props
                .GroupBy(p => Encoding.UTF8.GetByteCount(p.JsonName))
                .OrderBy(g => g.Key)
        )
        {
            s.Append(indent);
            s.Append("    case ");
            s.Append(group.Key);
            s.AppendLine(":");
            var groupProps = group.OrderBy(p => p.JsonName).ToArray();
            for (int i = 0; i < groupProps.Length; i++)
            {
                s.Append(indent);
                s.Append("        ");
                s.Append(i == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(");
                s.Append(keyVar);
                s.Append(", \"");
                s.Append(
                    PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(groupProps[i].JsonName)
                );
                s.AppendLine("\"u8)) {");
                EmitReadOrAssign(s, groupProps[i], target, bodyIndent + "        ", ctorMap);
                s.Append(indent);
                s.AppendLine("        }");
            }
            s.Append(indent);
            s.AppendLine("        break;");
        }
        s.Append(indent);
        s.AppendLine("}");

        if (guard is not null)
        {
            s.Append(outerIndent);
            s.AppendLine("}");
        }
    }

    /// <summary>
    /// Call <see cref="EmitRead"/> directly, or generate constructor parameter assign if <paramref name="ctorMap"/> matches.
    /// </summary>
    /// <summary>
    /// Call <see cref="EmitRead"/> directly, or generate constructor parameter assign if <paramref name="ctorMap"/> matches.
    /// </summary>
    private static void EmitReadOrAssign(
        StringBuilder s,
        PropertyInfo p,
        string target,
        string pad,
        IReadOnlyDictionary<string, int>? ctorMap
    )
    {
        if (ctorMap is not null && ctorMap.TryGetValue(p.Name, out var cpIdx))
        {
            var assignTarget = $"__cp_{cpIdx}";
            EmitRead(s, p, assignTarget, pad, ctorAssign: true);
            return;
        }
        EmitRead(s, p, target, pad);
    }

    /// <summary>
    /// Emits the left-hand side of a read assignment: the constructor parameter
    /// itself (<c>__cp_N</c>) for <c>[IniConstructor]</c>/record types, otherwise
    /// the property accessor on the instance (<c>target.Prop</c>). Emitting
    /// <c>target.Prop</c> unconditionally produced CS1061 on a <c>__cp_N</c>
    /// target for every kind whose case forgot the branch (review C-15).
    /// </summary>
    private static void EmitAssignTarget(
        StringBuilder s,
        PropertyInfo p,
        string target,
        bool ctorAssign
    )
    {
        s.Append(target);
        if (ctorAssign)
            return;
        s.Append('.');
        s.Append(p.Name);
    }

    private static void EmitRead(
        StringBuilder s,
        PropertyInfo p,
        string target,
        string pad,
        bool ctorAssign = false
    )
    {
        // ctorAssign: assign to target directly (__cp_N) instead of target.PropName
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(pad);
            s.Append("var __c = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(pad);
            EmitAssignTarget(s, p, target, ctorAssign);
            s.AppendLine(" = __c.Read(reader.GetStringRaw());");
            return;
        }
        var elemP = new PropertyInfo(
            "__elem",
            "__elem",
            p.ElementTypeKind ?? "string",
            p.ElementTypeName ?? "object",
            false,
            null,
            null,
            null,
            null,
            p.NestedProperties,
            null
        );
        // C-30: a null literal must not reach the scalar parsers. A sequence member is
        // not a scalar: its empty value keeps the "empty collection" meaning (list, array
        // and the seven extended kinds alike), so only real scalars take the guard.
        var __scalar = !PicoSerDe.Gen.GenInfrastructure.IsSequenceKind(p.TypeKind);
        if (__scalar)
            EmitNullLiteralGuard(s, p, () => EmitAssignTarget(s, p, target, ctorAssign), pad);
        switch (p.TypeKind)
        {
            case "string":
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "int64":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt64(out var __v)) throw new System.FormatException($\"Expected a 64-bit integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "int16":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = checked((short)__v);");
                break;
            case "uint16":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = checked((ushort)__v);");
                break;
            case "sbyte":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = checked((sbyte)__v);");
                break;
            case "byte":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = checked((byte)__v);");
                break;
            case "uint32":
                s.Append(pad);
                s.AppendLine("if (!reader.TryGetInt64(out var __v))");
                s.Append(pad);
                s.AppendLine("{");
                s.Append(pad);
                s.AppendLine(
                    "    if (!reader.TryGetUInt64(out var __uv)) throw new System.FormatException($\"Expected an integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                s.AppendLine("    __v = checked((long)__uv);");
                s.Append(pad);
                s.AppendLine("}");
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = checked((uint)__v);");
                break;
            case "uint64":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetUInt64(out var __v)) throw new System.FormatException($\"Expected a 64-bit unsigned integer at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "float32":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetFloat64(out var __v)) throw new System.FormatException($\"Expected a floating-point number at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = (float)__v;");
                break;
            case "float64":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetFloat64(out var __v)) throw new System.FormatException($\"Expected a floating-point number at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "boolean":
                s.Append(pad);
                s.AppendLine(
                    "if (!reader.TryGetBool(out var __v)) throw new System.FormatException($\"Expected a boolean at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "decimal":
                s.Append(pad);
                s.AppendLine(
                    "if (!System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out decimal __v, out _)) throw new System.FormatException($\"Invalid decimal at offset {reader.BytesConsumed}\");"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "datetime":
                if (p.DateTimeFormat is not null)
                {
                    s.Append(pad);
                    s.AppendLine("var __dtStr = Encoding.UTF8.GetString(reader.GetStringRaw());");
                    s.Append(pad);
                    s.Append("if (!System.DateTime.TryParseExact(__dtStr, \"");
                    s.Append(p.DateTimeFormat);
                    s.AppendLine(
                        "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime __v))"
                    );
                    s.Append(pad);
                    s.Append("    throw new System.FormatException($\"Invalid DateTime for '");
                    s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                    s.AppendLine(
                        "' at offset {reader.BytesConsumed}: '{(__dtStr.Length > 32 ? __dtStr[..32] : __dtStr)}'\");"
                    );
                }
                else
                {
                    s.Append(pad);
                    EmitInvalidValueThrow(
                        s,
                        pad,
                        "System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out DateTime __v, out _, 'O')",
                        "DateTime",
                        EscapeInterpolation(p.JsonName),
                        "reader.GetStringRaw()"
                    );
                }
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "guid":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out Guid __v, out _, 'D')",
                    "Guid",
                    EscapeInterpolation(p.JsonName),
                    "reader.GetStringRaw()"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "enum":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "Enum.TryParse<"
                        + PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(p.TypeFullName)
                        + ">(Encoding.UTF8.GetString(reader.GetStringRaw()), out var __ev)",
                    "enum value",
                    EscapeInterpolation(p.JsonName),
                    "reader.GetStringRaw()"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __ev;");
                break;
            case "dateonly":
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = DateOnly.Parse(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
            case "timeonly":
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = TimeOnly.Parse(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
            case "timespan":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out TimeSpan __v, out _, 'c')",
                    "TimeSpan",
                    EscapeInterpolation(p.JsonName),
                    "reader.GetStringRaw()"
                );
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.AppendLine(" = __v;");
                break;
            case "list":
                s.Append(pad);
                EmitAssignTarget(s, p, target, ctorAssign);
                s.Append(" = new System.Collections.Generic.List<");
                s.Append(p.ElementTypeName);
                s.AppendLine(">(16);");
                s.Append(pad);
                s.AppendLine("var __raw = Encoding.UTF8.GetString(reader.GetStringRaw());");
                s.Append(pad);
                s.AppendLine("int __p = 0, __seg = 0;");
                s.Append(pad);
                s.AppendLine("while (__p < __raw.Length) {");
                s.Append(pad);
                s.AppendLine("    if (__raw[__p] == '\\\\' && __p + 1 < __raw.Length) __p += 2;");
                s.Append(pad);
                s.AppendLine("    else if (__raw[__p] == ',') {");
                s.Append(pad);
                s.Append(pad);
                s.AppendLine(
                    "        var __rv = Encoding.UTF8.GetBytes(__raw.Substring(__seg, __p - __seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\")).AsSpan();"
                );
                s.Append(pad);
                s.Append("        ");
                EmitAssignTarget(s, p, target, ctorAssign);
                s.Append(".Add(");
                EmitReadValueStrict(s, elemP);
                s.AppendLine(");");
                s.Append(pad);
                s.AppendLine("        __seg = ++__p;");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("    else __p++;");
                s.Append(pad);
                s.AppendLine("}");
                s.Append(pad);
                s.AppendLine("if (__seg < __raw.Length) {");
                s.Append(pad);
                s.AppendLine(
                    "    var __rv = Encoding.UTF8.GetBytes(__raw.Substring(__seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\")).AsSpan();"
                );
                s.Append(pad);
                s.Append("    ");
                EmitAssignTarget(s, p, target, ctorAssign);
                s.Append(".Add(");
                EmitReadValueStrict(s, elemP);
                s.AppendLine(");");
                s.Append(pad);
                s.AppendLine("}");
                break;
            // R2 Task 6: `array` and the seven extended kinds share ONE body. They differ
            // only in how the temp list becomes the DECLARED type, and that half is
            // GenInfrastructure.EmitCollectionFromList — the same helper the other four
            // formats use, so a new kind is added there and nowhere here.
            case "array":
            case "hashset":
            case "queue":
            case "stack":
            case "linkedlist":
            case "immutablearray":
            case "memory":
            case "readonlymemory":
                s.Append(pad);
                s.Append("var __tmpList_");
                s.Append(p.Name);
                s.Append(" = new System.Collections.Generic.List<");
                s.Append(p.ElementTypeName);
                s.AppendLine(">(16);");
                s.Append(pad);
                s.AppendLine("var __raw = Encoding.UTF8.GetString(reader.GetStringRaw());");
                s.Append(pad);
                s.AppendLine("int __p = 0, __seg = 0;");
                s.Append(pad);
                s.AppendLine("while (__p < __raw.Length) {");
                s.Append(pad);
                s.AppendLine("    if (__raw[__p] == '\\\\' && __p + 1 < __raw.Length) __p += 2;");
                s.Append(pad);
                s.AppendLine("    else if (__raw[__p] == ',') {");
                s.Append(pad);
                s.AppendLine(
                    "        var __rv = Encoding.UTF8.GetBytes(__raw.Substring(__seg, __p - __seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\")).AsSpan();"
                );
                s.Append(pad);
                s.Append("        __tmpList_");
                s.Append(p.Name);
                s.Append(".Add(");
                EmitReadValueStrict(s, elemP);
                s.AppendLine(");");
                s.Append(pad);
                s.AppendLine("        __seg = ++__p;");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("    else __p++;");
                s.Append(pad);
                s.AppendLine("}");
                s.Append(pad);
                s.AppendLine("if (__seg < __raw.Length) {");
                s.Append(pad);
                s.AppendLine(
                    "    var __rv = Encoding.UTF8.GetBytes(__raw.Substring(__seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\")).AsSpan();"
                );
                s.Append(pad);
                s.Append("    __tmpList_");
                s.Append(p.Name);
                s.Append(".Add(");
                EmitReadValueStrict(s, elemP);
                s.AppendLine(");");
                s.Append(pad);
                s.AppendLine("}");
                PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
                    s,
                    p.TypeKind!,
                    "__tmpList_" + p.Name,
                    ctorAssign ? target : target + "." + p.Name,
                    p.ElementTypeName ?? "object",
                    pad
                );
                break;
        }
        if (__scalar)
        {
            s.Append(pad);
            s.AppendLine("}");
        }
    }

    /// <summary>
    /// Review C-30: emits the null-literal guard that protects the scalar parse
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
        s.AppendLine("if (reader.IsNullLiteral)");
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

    private static string EmitIniValue(AnonFieldInfo f, string vv, string wv)
    {
        var kn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(f.JsonName);
        return f.TypeKind switch
        {
            "string" => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv});",
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
            or "boolean"
            or "decimal" => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv});",
            _ => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv}.ToString());",
        };
    }

    private static string GenPoly(TypeInfo type, Dictionary<string, TypeInfo> typeMap)
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

        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoIni;");
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
        s.Append("IniSerializer : ISerializer<");
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
        s.AppendLine("        var __opts = (global::PicoIni.IniOptions?)options;");
        s.AppendLine(
            "        var iw = new IniWriter(writer, indented: __opts?.Indented ?? false);"
        );
        s.Append("        iw.WriteKeyValue(\"");
        s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dpn));
        s.Append("\"u8, value switch { ");
        foreach (var dt in __cases)
        {
            var dtShort = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            s.Append(dtShort);
            s.Append(" __v => \"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dt.TypeDiscriminator));
            s.Append("\"u8, ");
        }
        s.AppendLine("_ => \"\"u8 });");

        s.AppendLine("        switch (value)");
        s.AppendLine("        {");
        foreach (var dt in __cases)
        {
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out var dti))
                continue;
            var dtShort = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            s.Append("            case ");
            s.Append(dtShort);
            s.AppendLine(" __v:");
            foreach (var prop in dti.Properties)
            {
                if (PicoSerDe.Gen.GenInfrastructure.IsComplexMember(prop))
                    continue;
                var pn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName);
                // DefaultIgnoreCondition: INI has no null literal — null
                // values are always omitted.
                bool pCheck = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                    s,
                    prop,
                    $"__v.{prop.Name}",
                    "                "
                );
                s.Append("                iw.WriteKeyValue(\"");
                s.Append(pn);
                s.Append("\"u8, ");
                if (prop.IsNullable && !prop.IsNullableReference)
                    s.Append($"__v.{prop.Name}!.Value");
                else
                    WriteValue(s, prop, $"__v.{prop.Name}");
                s.AppendLine(");");
                if (pCheck)
                    s.AppendLine("                }");
            }
            s.AppendLine("                break;");
        }
        s.AppendLine("        }");
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();

        // ── Deserializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("IniDeserializer : IDeserializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(type.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.AppendLine("        var reader = new IniReader(data);");
        s.AppendLine("        reader.Read();");
        s.AppendLine("        var __discKey = reader.GetStringRaw();");
        s.AppendLine("        reader.ReadValue();");
        s.AppendLine("        var __discVal = reader.GetStringRaw();");
        s.Append("        if (!MemoryExtensions.SequenceEqual(__discKey, \"");
        s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dpn));
        s.AppendLine("\"u8))");
        s.Append("            throw new FormatException(\"Expected discriminator '");
        s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dpn));
        s.AppendLine("'\");");

        for (int i = 0; i < __cases.Length; i++)
        {
            var dt = __cases[i];
            var kw = i == 0 ? "if" : "else if";
            var dtShort = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out var dti))
                continue;

            s.Append("        ");
            s.Append(kw);
            s.Append(" (MemoryExtensions.SequenceEqual(__discVal, \"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dt.TypeDiscriminator));
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
                    s.Append(" = ");
                    s.Append(
                        cp.TypeKind switch
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
                            or "float32"
                            or "float64"
                            or "decimal" => "0",
                            "boolean" => "false",
                            _ => "default!",
                        }
                    );
                    // This is a statement, not an argument list: the missing ';'
                    // made every polymorphic constructor type fail to compile.
                    s.AppendLine(";");
                }
            }
            else
            {
                s.Append("            var obj = new ");
                s.AppendLine(dtShort);
                s.AppendLine("();");
            }

            s.AppendLine("            while (reader.Read()) {");
            s.AppendLine("                if (reader.TokenType == TokenType.PropertyName) {");
            s.AppendLine("                    var __k = reader.GetStringRaw();");
            s.AppendLine("                    reader.ReadValue();");
            var first = true;
            for (int pi = 0; pi < dti.Properties.Length; pi++)
            {
                var prop = dti.Properties[pi];
                if (PicoSerDe.Gen.GenInfrastructure.IsComplexMember(prop))
                    continue;
                var kw2 = PicoSerDe.Gen.GenInfrastructure.ChainKeyword(ref first);
                var pn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName);
                s.Append("                    ");
                s.Append(kw2);
                s.Append(" (MemoryExtensions.SequenceEqual(__k, \"");
                s.Append(pn);
                s.AppendLine("\"u8)) {");
                s.Append("                        var __rv = reader.GetStringRaw();");
                if (hasCtor)
                {
                    s.AppendLine();
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
                        var cp = dti.CtorParams[matchIdx];
                        EmitReadPolyMember(
                            s,
                            cp.TypeKind,
                            cp.TypeFullName,
                            cp.DateTimeFormat,
                            "__cp_" + matchIdx,
                            "                        ",
                            prop.JsonName
                        );
                    }
                }
                else
                {
                    s.AppendLine();
                    EmitReadPolyMember(
                        s,
                        prop.TypeKind,
                        prop.TypeFullName,
                        prop.DateTimeFormat,
                        "obj." + prop.Name,
                        "                        ",
                        prop.JsonName
                    );
                }
                s.AppendLine("                    }");
            }
            s.AppendLine("                } else break;");
            s.AppendLine("            }");

            if (hasCtor)
            {
                s.Append("            return new ");
                s.Append(dtShort);
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
                s.AppendLine("            return obj;");

            s.AppendLine("        }");
        }

        s.AppendLine(
            "        throw new FormatException($\"Unknown discriminator: {Encoding.UTF8.GetString(__discVal)}\");"
        );
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();

        // ── Registration ──
        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"{type.Namespace}.{type.Name}";
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        IniSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("IniSerializer(),");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("IniDeserializer());");
        s.AppendLine("    }");
        s.AppendLine("}");

        return s.ToString();
    }

    /// <summary>
    /// Emits a fail-loud guard: when the TryParse-style expression
    /// <paramref name="cond"/> (already evaluated) is false, an excerpt of the
    /// offending text is materialised and a FormatException thrown. The message
    /// names the owning key and shows what was actually read — a bare offset into
    /// a 50-key file is not actionable (review I-5). The excerpt is built only on
    /// the failure path, so the happy path stays allocation-free.
    /// </summary>
    /// <param name="label">Key name, already escaped for an interpolated string.</param>
    private static void EmitInvalidValueThrow(
        StringBuilder s,
        string pad,
        string cond,
        string what,
        string label,
        string rawExpr
    )
    {
        s.Append(pad);
        s.Append("if (!(");
        s.Append(cond);
        s.AppendLine("))");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.Append("    var __txt = Encoding.UTF8.GetString(");
        s.Append(rawExpr);
        s.AppendLine(");");
        s.Append(pad);
        s.Append("    throw new System.FormatException($\"Invalid ");
        s.Append(what);
        s.Append(" for '");
        s.Append(label);
        s.Append(
            "' at offset {reader.BytesConsumed}: '{(__txt.Length > 32 ? __txt[..32] : __txt)}'\");"
        );
        s.AppendLine();
        s.Append(pad);
        s.AppendLine("}");
    }

    /// <summary>Escapes text embedded inside a generated interpolated string.</summary>
    private static string EscapeInterpolation(string text) =>
        PicoSerDe
            .Gen.GenInfrastructure.EscapeCSharpString(text)
            .Replace("{", "{{")
            .Replace("}", "}}");

    /// <summary>
    /// Strict single-member read for the polymorphic pipeline. <see cref="GenPoly"/>
    /// emits its own member readers and never goes through <see cref="EmitRead"/>,
    /// so C-18's fail-loud contract has to be mirrored here — otherwise a
    /// <c>$type</c>-correct payload still silently defaulted Guid/DateTime/enum/
    /// TimeSpan members (review C-2). Shapes with no single-value INI form fail
    /// loudly at runtime instead of emitting uncompilable code.
    /// </summary>
    /// <param name="assignLeft">Assignment target, e.g. <c>obj.Id</c> or <c>__cp_0</c>.</param>
    /// <param name="label">Key name this member is read from.</param>
    private static void EmitReadPolyMember(
        StringBuilder s,
        string typeKind,
        string typeFullName,
        string? dateTimeFormat,
        string assignLeft,
        string pad,
        string label
    )
    {
        const string INV = "System.Globalization.CultureInfo.InvariantCulture";
        const string DT = "System.Globalization.DateTimeStyles";
        var lbl = EscapeInterpolation(label);

        void Assign(string valueExpr)
        {
            s.Append(pad);
            s.Append(assignLeft);
            s.Append(" = ");
            s.Append(valueExpr);
            s.AppendLine(";");
        }

        switch (typeKind)
        {
            case "string":
                Assign("Encoding.UTF8.GetString(__rv)");
                return;
            case "int32":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetInt32(out var __v)",
                    "integer",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "int64":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetInt64(out var __v)",
                    "64-bit integer",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "int16":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetInt32(out var __v)",
                    "16-bit integer",
                    lbl,
                    "__rv"
                );
                Assign("checked((short)__v)");
                return;
            case "uint16":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetInt32(out var __v)",
                    "16-bit unsigned integer",
                    lbl,
                    "__rv"
                );
                Assign("checked((ushort)__v)");
                return;
            case "sbyte":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetInt32(out var __v)",
                    "8-bit integer",
                    lbl,
                    "__rv"
                );
                Assign("checked((sbyte)__v)");
                return;
            case "byte":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetInt32(out var __v)",
                    "8-bit unsigned integer",
                    lbl,
                    "__rv"
                );
                Assign("checked((byte)__v)");
                return;
            case "uint32":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetUInt64(out var __v)",
                    "32-bit unsigned integer",
                    lbl,
                    "__rv"
                );
                Assign("checked((uint)__v)");
                return;
            case "uint64":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetUInt64(out var __v)",
                    "64-bit unsigned integer",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "float32":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetFloat64(out var __v)",
                    "floating-point number",
                    lbl,
                    "__rv"
                );
                Assign("(float)__v");
                return;
            case "float64":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetFloat64(out var __v)",
                    "floating-point number",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "boolean":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "reader.TryGetBool(out var __v)",
                    "boolean",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "decimal":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "System.Buffers.Text.Utf8Parser.TryParse(__rv, out decimal __v, out _)",
                    "decimal",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "datetime":
                if (dateTimeFormat is not null)
                {
                    s.Append(pad);
                    s.AppendLine("var __dtStr = Encoding.UTF8.GetString(__rv);");
                    EmitInvalidValueThrow(
                        s,
                        pad,
                        "System.DateTime.TryParseExact(__dtStr, \""
                            + dateTimeFormat
                            + "\", "
                            + INV
                            + ", "
                            + DT
                            + ".None, out DateTime __v)",
                        "DateTime",
                        lbl,
                        "__rv"
                    );
                }
                else
                {
                    EmitInvalidValueThrow(
                        s,
                        pad,
                        "System.Buffers.Text.Utf8Parser.TryParse(__rv, out DateTime __v, out _, 'O')",
                        "DateTime",
                        lbl,
                        "__rv"
                    );
                }
                Assign("__v");
                return;
            case "guid":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "System.Buffers.Text.Utf8Parser.TryParse(__rv, out Guid __v, out _, 'D')",
                    "Guid",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "enum":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "Enum.TryParse<"
                        + PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(typeFullName)
                        + ">(Encoding.UTF8.GetString(__rv), out var __v)",
                    "enum value",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "dateonly":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "System.DateOnly.TryParse(Encoding.UTF8.GetString(__rv), "
                        + INV
                        + ", "
                        + DT
                        + ".None, out var __v)",
                    "DateOnly",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "timeonly":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "System.TimeOnly.TryParse(Encoding.UTF8.GetString(__rv), "
                        + INV
                        + ", "
                        + DT
                        + ".None, out var __v)",
                    "TimeOnly",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            case "timespan":
                EmitInvalidValueThrow(
                    s,
                    pad,
                    "System.Buffers.Text.Utf8Parser.TryParse(__rv, out TimeSpan __v, out _, 'c')",
                    "TimeSpan",
                    lbl,
                    "__rv"
                );
                Assign("__v");
                return;
            default:
                // Collections and any other shape have no single-value INI form
                // here: reading the raw text would silently corrupt data, and
                // emitting nothing would not compile.
                s.Append(pad);
                s.Append(
                    "throw new System.NotSupportedException(\"INI polymorphism cannot read member '"
                );
                s.Append(lbl);
                s.Append("' of kind ");
                s.Append(typeKind);
                s.AppendLine("\");");
                return;
        }
    }

    /// <summary>
    /// Strict variant used by the property list/array deserializer: malformed
    /// elements throw FormatException instead of silently defaulting.
    /// </summary>
    private static void EmitReadValueStrict(StringBuilder s, PropertyInfo prop)
    {
        const string INV = "System.Globalization.CultureInfo.InvariantCulture";
        switch (prop.TypeKind)
        {
            case "string":
                s.Append("Encoding.UTF8.GetString(__rv)");
                break;
            case "int32":
                s.Append($"int.Parse(__rv, {INV})");
                break;
            case "int64":
                s.Append($"long.Parse(__rv, {INV})");
                break;
            case "int16":
                s.Append($"short.Parse(__rv, {INV})");
                break;
            case "uint16":
                s.Append($"ushort.Parse(__rv, {INV})");
                break;
            case "sbyte":
                s.Append($"sbyte.Parse(__rv, {INV})");
                break;
            case "byte":
                s.Append($"byte.Parse(__rv, {INV})");
                break;
            case "uint32":
                s.Append($"uint.Parse(__rv, {INV})");
                break;
            case "uint64":
                s.Append($"ulong.Parse(__rv, {INV})");
                break;
            case "float32":
                s.Append($"float.Parse(__rv, System.Globalization.NumberStyles.Float, {INV})");
                break;
            case "float64":
                s.Append($"double.Parse(__rv, System.Globalization.NumberStyles.Float, {INV})");
                break;
            case "decimal":
                s.Append(
                    $"decimal.Parse(Encoding.UTF8.GetString(__rv), System.Globalization.NumberStyles.Number, {INV})"
                );
                break;
            case "boolean":
                s.Append("bool.Parse(Encoding.UTF8.GetString(__rv))");
                break;
            case "datetime":
                s.Append(
                    $"System.DateTime.Parse(Encoding.UTF8.GetString(__rv), {INV}, System.Globalization.DateTimeStyles.RoundtripKind)"
                );
                break;
            case "guid":
                s.Append("System.Guid.Parse(Encoding.UTF8.GetString(__rv))");
                break;
            case "dateonly":
                s.Append($"System.DateOnly.Parse(Encoding.UTF8.GetString(__rv), {INV})");
                break;
            case "timeonly":
                s.Append($"System.TimeOnly.Parse(Encoding.UTF8.GetString(__rv), {INV})");
                break;
            case "timespan":
                s.Append($"System.TimeSpan.Parse(Encoding.UTF8.GetString(__rv), {INV})");
                break;
            case "enum":
                s.Append(
                    $"System.Enum.Parse<{PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(prop.TypeFullName)}>(Encoding.UTF8.GetString(__rv))"
                );
                break;
            default:
                s.Append("Encoding.UTF8.GetString(__rv)");
                break;
        }
    }

    private static string IniNumericCastExpr(string kind) =>
        kind switch
        {
            "int16" => "short",
            "uint16" => "ushort",
            "sbyte" => "sbyte",
            "byte" => "byte",
            "uint32" => "uint",
            "int64" => "long",
            _ => "long",
        };

    private static string GetCSharpType(string typeKind) =>
        typeKind switch
        {
            "int16" => "short",
            "uint16" => "ushort",
            "sbyte" => "sbyte",
            "byte" => "byte",
            "uint32" => "uint",
            "uint64" => "ulong",
            "float32" => "float",
            "float64" => "double",
            _ => "object",
        };
}
