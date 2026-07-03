namespace PicoIni.Gen;

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

        context.RegisterSourceOutput(all, static (spc, types) => GenerateAll(spc, types));
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
            && method.TypeArguments[0] is INamedTypeSymbol namedType
        )
        {
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

        foreach (var kv in typeMap)
        {
            var t = kv.Value;
            if (string.IsNullOrEmpty(t.Name))
                continue;
            var safeFq = PicoSerDe.Gen.GenInfrastructure.SafeName(t.FullyQualifiedName ?? "");
            var hintName = $"{safeFq}_IniSerializer.g.cs";
            string code;
            if (t.IsRefLikeType)
                code = GenRefStruct(t);
            else if (!t.DerivedTypes.IsDefaultOrEmpty)
                code = GenPoly(t, typeMap);
            else
                code = Gen(t);
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
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value) {");
        s.AppendLine("        var iw = new IniWriter(writer);");

        // Emit class-level comment if any property has one (they share the same containing type)
        var typeComment = type.Properties.Select(p => p.Comment).FirstOrDefault(c => c is not null);
        if (typeComment is not null)
        {
            s.Append("        iw.WriteComment(\"");
            s.Append(typeComment);
            s.AppendLine("\");");
        }

        // Top-level properties first
        foreach (var p in type.Properties)
        {
            if (p.TypeKind == "object" || p.TypeKind == "dict")
                continue;
            if (p.Comment is not null)
            {
                s.Append("        iw.WriteComment(\"");
                s.Append(p.Comment);
                s.AppendLine("\");");
            }
            bool checkNull = p.IsNullable || p.IsNullableReference;
            if (checkNull)
            {
                s.Append(
                    "        if (PicoIni.IniOptions.Current?.DefaultIgnoreCondition != PicoIni.IniIgnoreCondition.Never\n"
                );
                s.Append("            ? value.");
                s.Append(p.Name);
                if (p.IsNullable && !p.IsNullableReference)
                    s.Append(" != null\n");
                else
                    s.Append(" != null\n");
                s.Append("            : true)\n");
                s.Append("        {\n");
                s.Append("            iw.WriteKeyValue(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                s.Append("\"u8, ");
                if (p.IsNullable && !p.IsNullableReference)
                {
                    s.Append($"value.{p.Name}!.Value");
                }
                else
                {
                    WriteValue(s, p, $"value.{p.Name}");
                }
                s.AppendLine(");");
                s.AppendLine("        }");
            }
            else
            {
                s.Append("        iw.WriteKeyValue(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                s.Append("\"u8, ");
                WriteValue(s, p, $"value.{p.Name}");
                s.AppendLine(");");
            }
        }

        // Dicts as sections (before objects)
        bool dictFirst = true;
        foreach (var p in type.Properties)
        {
            if (p.TypeKind != "dict")
                continue;
            if (!dictFirst)
                s.AppendLine("        iw.WriteBlankLine();");
            s.Append("        iw.WriteSection(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            s.Append("        foreach (var __kvp in value.");
            s.Append(p.Name);
            s.AppendLine(")");
            s.AppendLine("        {");
            s.Append("            iw.WriteKeyValue(__kvp.Key, ");
            WriteValue(s, p, "__kvp.Value");
            s.AppendLine(");");
            s.AppendLine("        }");
            dictFirst = false;
        }

        // Sections after (nested objects)
        bool first = true;
        foreach (var p in type.Properties)
        {
            if (p.TypeKind != "object")
                continue;
            bool nullGuard = p.IsNullable || p.IsNullableReference;
            if (nullGuard)
            {
                s.Append("        if (value.");
                s.Append(p.Name);
                s.AppendLine(" != null) {");
            }
            if (!first)
                s.AppendLine("        iw.WriteBlankLine();");
            var sn = p.SectionName ?? p.JsonName;
            s.Append("        iw.WriteSection(\"");
            s.Append(sn);
            s.AppendLine("\"u8);");
            foreach (var np in p.NestedProperties)
            {
                s.Append("        iw.WriteKeyValue(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(np.JsonName));
                s.Append("\"u8, ");
                WriteValue(s, np, $"value.{p.Name}.{np.Name}");
                s.AppendLine(");");
            }
            if (nullGuard)
                s.AppendLine("        }");
            first = false;
        }
        s.AppendLine("    } }");
        s.AppendLine();

        var top = new List<PropertyInfo>();
        foreach (var p in type.Properties)
            if (p.TypeKind != "object" && p.TypeKind != "dict")
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
                var tn = PicoSerDe.Gen.TypeKindResolver.MapTypeName(cp.TypeKind, null!);
                var def = cp.TypeKind == "string" ? "\"\"" : "default";
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
        s.AppendLine("        while (reader.Read()) {");
        s.AppendLine("            if (reader.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var __k = reader.GetStringRaw();");
        s.AppendLine("                reader.ReadValue(); // fast path: consume pending value");

        // Build ctor param name → index map for constructor deserialization
        Dictionary<string, int>? ctorMap = null;
        if (hasCtor)
        {
            ctorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
                ctorMap[type.CtorParams[ci].Name] = ci;
        }

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
                s.Append(dicts[di].ElementTypeName ?? "int");
                s.AppendLine(">();");
                s.Append("                    { var __dk = Encoding.UTF8.GetString(__k); ");
                if (dicts[di].ElementTypeKind == "int32")
                {
                    s.Append("reader.TryGetInt32(out var __dv); obj.");
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
                var sn = sec[i].SectionName ?? sec[i].JsonName;
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
                s.Append(dicts[i].ElementTypeName ?? "int");
                s.AppendLine(">();");
                s.Append("                    __sec = ");
                s.Append(sec.Count + i);
                s.AppendLine(";");
                s.AppendLine("                }");
            }
            s.AppendLine("            }");
        }
        s.AppendLine("        }");
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

        // Streaming deserializer (skip for constructor types — needs deferred construction)
        if (!hasCtor)
        {
            s.Append("file static class ");
            s.Append(type.Name);
            s.AppendLine("IniStreaming {");
            s.AppendLine(
                "    internal static ReadStatus DeserializeStreaming(ref IniReader reader, out "
                    + type.Name
                    + "? result) {"
            );
            s.AppendLine("        result = default;");
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
            s.AppendLine("        while (true) {");
            s.AppendLine(
                "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
            );
            s.AppendLine("            if (reader.TokenType == TokenType.ObjectEnd) break;");
            s.AppendLine("            if (reader.TokenType != TokenType.PropertyName) continue;");
            s.AppendLine("            var __k = reader.GetStringRaw();");
            s.AppendLine("            reader.ReadValue();");
            if (top.Count > 0)
            {
                EmitKeyDispatch(s, top, "__k", "obj", null, "            ", "                ");
            }
            s.AppendLine("        }");
            s.AppendLine("        result = obj;");
            s.AppendLine("        return ReadStatus.Success;");
            s.AppendLine("    }");
            s.AppendLine("}");
        } // end if (!hasCtor)
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
        s.AppendLine(" value) {");
        s.AppendLine("        var iw = new IniWriter(writer);");

        var top = type
            .Properties.Where(p => p.TypeKind != "object" && p.TypeKind != "dict")
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

    private static void WriteValue(StringBuilder s, PropertyInfo p, string acc)
    {
        switch (p.TypeKind)
        {
            case "string":
                s.Append(acc);
                break;
            case "int32":
            case "int64":
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
                s.Append("string.Join(\",\", ");
                s.Append(acc);
                if (p.ElementTypeKind == "string")
                    s.Append(
                        ".Select(__s => __s.Replace(\"\\\\\", \"\\\\\\\\\").Replace(\",\", \"\\\\,\")))"
                    );
                else
                    s.Append(")");
                break;
            default:
                s.Append(acc);
                s.Append(".ToString()");
                break;
        }
    }

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
            if (ctorAssign)
                s.Append(target);
            else
            {
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
            }
            s.AppendLine(" = __c.Read(reader.GetStringRaw());");
            return;
        }
        switch (p.TypeKind)
        {
            case "string":
                s.Append(pad);
                if (ctorAssign)
                    s.Append(target);
                else
                {
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                }
                s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                s.Append(pad);
                s.AppendLine("reader.TryGetInt32(out var __v);");
                s.Append(pad);
                if (ctorAssign)
                    s.Append(target);
                else
                {
                    s.Append(target);
                    s.Append('.');
                    s.Append(p.Name);
                }
                s.AppendLine(" = __v;");
                break;
            case "int64":
                s.Append(pad);
                s.AppendLine("reader.TryGetInt64(out var __v);");
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __v;");
                break;
            case "float32":
                s.Append(pad);
                s.AppendLine("reader.TryGetFloat64(out var __v);");
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = (float)__v;");
                break;
            case "float64":
                s.Append(pad);
                s.AppendLine("reader.TryGetFloat64(out var __v);");
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __v;");
                break;
            case "boolean":
                s.Append(pad);
                s.AppendLine("reader.TryGetBool(out var __v);");
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __v;");
                break;
            case "decimal":
                s.Append(pad);
                s.AppendLine(
                    "System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out decimal __v, out _);"
                );
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __v;");
                break;
            case "datetime":
                if (p.DateTimeFormat is not null)
                {
                    s.Append(pad);
                    s.AppendLine("var __dtStr = Encoding.UTF8.GetString(reader.GetStringRaw());");
                    s.Append(pad);
                    s.Append("System.DateTime.TryParseExact(__dtStr, \"");
                    s.Append(p.DateTimeFormat);
                    s.AppendLine(
                        "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime __v);"
                    );
                }
                else
                {
                    s.Append(pad);
                    s.AppendLine(
                        "System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out DateTime __v, out _, 'O');"
                    );
                }
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __v;");
                break;
            case "guid":
                s.Append(pad);
                s.AppendLine(
                    "System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out Guid __v, out _, 'D');"
                );
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __v;");
                break;
            case "enum":
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.Append(" = Enum.TryParse<");
                s.Append(p.TypeFullName);
                s.AppendLine(
                    ">(Encoding.UTF8.GetString(reader.GetStringRaw()), out var __ev) ? __ev : default;"
                );
                break;
            case "dateonly":
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = DateOnly.Parse(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
            case "timeonly":
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = TimeOnly.Parse(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
            case "timespan":
                s.Append(pad);
                s.AppendLine(
                    "System.Buffers.Text.Utf8Parser.TryParse(reader.GetStringRaw(), out TimeSpan __v, out _, 'c');"
                );
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = __v;");
                break;
            case "list":
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.Append(" ??= new System.Collections.Generic.List<");
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
                s.Append("        ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(
                    ".Add(__raw.Substring(__seg, __p - __seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\"));"
                );
                s.Append(pad);
                s.AppendLine("        __seg = ++__p;");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("    else __p++;");
                s.Append(pad);
                s.AppendLine("}");
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(
                    ".Add(__raw.Substring(__seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\"));"
                );
                break;
            case "array":
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
                s.Append("        __tmpList_");
                s.Append(p.Name);
                s.AppendLine(
                    ".Add(__raw.Substring(__seg, __p - __seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\"));"
                );
                s.Append(pad);
                s.AppendLine("        __seg = ++__p;");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("    else __p++;");
                s.Append(pad);
                s.AppendLine("}");
                s.Append(pad);
                s.Append("__tmpList_");
                s.Append(p.Name);
                s.AppendLine(
                    ".Add(__raw.Substring(__seg).Replace(\"\\\\\\\\\", \"\\\\\").Replace(\"\\\\,\", \",\"));"
                );
                s.Append(pad);
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.Append(" = __tmpList_");
                s.Append(p.Name);
                s.AppendLine(".ToArray();");
                break;
        }
    }

    private static string GenPoly(TypeInfo type, Dictionary<string, TypeInfo> typeMap)
    {
        var dpn = type.DiscriminatorPropertyName ?? "$type";
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
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value) {");
        s.AppendLine("        var iw = new IniWriter(writer);");
        s.Append("        iw.WriteKeyValue(\"");
        s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dpn));
        s.Append("\"u8, value switch { ");
        foreach (var dt in type.DerivedTypes)
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
        foreach (var dt in type.DerivedTypes)
        {
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out var dti))
                continue;
            var dtShort = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            s.Append("            case ");
            s.Append(dtShort);
            s.AppendLine(" __v:");
            foreach (var prop in dti.Properties)
            {
                if (prop.TypeKind == "object" || prop.TypeKind == "dict")
                    continue;
                var pn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName);
                s.Append("                iw.WriteKeyValue(\"");
                s.Append(pn);
                s.Append("\"u8, ");
                WriteValue(s, prop, $"__v.{prop.Name}");
                s.AppendLine(");");
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

        for (int i = 0; i < type.DerivedTypes.Length; i++)
        {
            var dt = type.DerivedTypes[i];
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
                    s.Append(PicoSerDe.Gen.TypeKindResolver.MapTypeName(cp.TypeKind, null!));
                    s.Append(" __cp_");
                    s.Append(ci);
                    s.Append(" = ");
                    s.AppendLine(
                        cp.TypeKind switch
                        {
                            "string" => "\"\"",
                            "int32" or "int64" or "float64" => "0",
                            "boolean" => "false",
                            _ => "default!",
                        }
                    );
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
            for (int pi = 0; pi < dti.Properties.Length; pi++)
            {
                var prop = dti.Properties[pi];
                if (prop.TypeKind == "object" || prop.TypeKind == "dict")
                    continue;
                var kw2 = pi == 0 ? "if" : "else if";
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
                        s.Append("                        ");
                        s.Append("__cp_");
                        s.Append(matchIdx);
                        s.Append(" = ");
                        EmitReadCtorValue(s, cp);
                        s.AppendLine(";");
                    }
                }
                else
                {
                    s.AppendLine();
                    s.Append("                        obj.");
                    s.Append(prop.Name);
                    s.Append(" = ");
                    EmitReadValue(s, prop);
                    s.AppendLine(";");
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

    private static void EmitReadCtorValue(StringBuilder s, CtorParamInfo cp)
    {
        if (cp.TypeKind == "string")
            s.Append("Encoding.UTF8.GetString(__rv)");
        else if (cp.TypeKind == "int32")
        {
            s.Append("int.TryParse(__rv, out var __iv) ? __iv : 0");
        }
        else if (cp.TypeKind == "int64")
        {
            s.Append("long.TryParse(__rv, out var __lv) ? __lv : 0");
        }
        else if (cp.TypeKind == "float64")
        {
            s.Append("double.TryParse(__rv, out var __dv) ? __dv : 0");
        }
        else if (cp.TypeKind == "boolean")
        {
            s.Append("bool.TryParse(__rv, out var __bv) ? __bv : false");
        }
        else
            s.Append("default");
    }

    private static void EmitReadValue(StringBuilder s, PropertyInfo prop)
    {
        if (prop.TypeKind == "string")
            s.Append("Encoding.UTF8.GetString(__rv)");
        else if (prop.TypeKind == "int32")
            s.Append("int.TryParse(__rv, out var __iv) ? __iv : 0");
        else if (prop.TypeKind == "int64")
            s.Append("long.TryParse(__rv, out var __lv) ? __lv : 0");
        else if (prop.TypeKind == "float32" || prop.TypeKind == "float64")
            s.Append(
                $"double.TryParse(__rv, out var __dv) ? ({GetCSharpType(prop.TypeKind)})__dv : 0"
            );
        else if (prop.TypeKind == "boolean")
            s.Append("bool.TryParse(__rv, out var __bv) ? __bv : false");
        else if (prop.TypeKind == "decimal")
            s.Append(
                "decimal.TryParse(__rv, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __dec) ? __dec : 0"
            );
        else if (prop.TypeKind == "datetime")
            s.Append(
                "DateTime.TryParse(__rv, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt) ? __dt : default"
            );
        else if (prop.TypeKind == "guid")
            s.Append("Guid.TryParse(__rv, out var __g) ? __g : default");
        else if (prop.TypeKind == "enum")
            s.Append($"Enum.TryParse<{prop.TypeFullName}>(__rv, out var __ev) ? __ev : default");
        else
            s.Append("Encoding.UTF8.GetString(__rv)");
    }

    private static string GetCSharpType(string typeKind) =>
        typeKind switch
        {
            "float32" => "float",
            "float64" => "double",
            _ => "object",
        };
}
