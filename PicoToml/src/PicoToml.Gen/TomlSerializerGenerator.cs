namespace PicoToml.Gen;

using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class TomlSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "TomlSerializer",
        "PicoToml",
        "toml"
    );

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        HasTomlCamelCase,
        GetTomlKey,
        HasTomlIgnore,
        GetTomlConverter,
        GetTomlDateTimeFormat,
        OverrideKindWithStringOnConverter: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline A: usage-driven (existing)
        var usageDriven = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (n, _) => IsCandidate(n),
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

        // Pipeline C: format-specific attribute — discover types via [PicoTomlSerializable]
        var formatAttr = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoToml.PicoTomlSerializableAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) =>
                    PicoSerDe.Gen.GenInfrastructure.ExpandAttributes(ctx, Config, Attrs)
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
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

        context.RegisterSourceOutput(all, static (spc, types) => GenerateAll(spc, types));
    }

    private static bool IsCandidate(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        bool hasCtor = false;
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol method
            && method.TypeArguments.Length == 1
            && method.TypeArguments[0] is INamedTypeSymbol namedType
        )
        {
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
                        if (attr.AttributeClass?.Name == "TomlConstructorAttribute")
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
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method2
            || method2.TypeArguments.Length != 1
            || method2.TypeArguments[0] is not INamedTypeSymbol namedType2
        )
            return ti;
        var ctorParams = PicoSerDe.Gen.GenInfrastructure.DetectConstructor(
            namedType2,
            Config.FormatTag,
            "TomlConstructorAttribute"
        );
        if (ctorParams is not { } cp)
            return ti;
        return ti with { CtorParams = cp };
    }

    // ── Attribute helpers ──

    private static bool HasTomlCamelCase(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "TomlCamelCaseAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoToml"
            )
                return true;
        }
        return false;
    }

    private static string? GetTomlKey(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "TomlKeyAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoToml"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string key
            )
                return key;
        }
        return null;
    }

    private static bool HasTomlIgnore(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "TomlIgnoreAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoToml"
            )
                return true;
        }
        return false;
    }

    private static string? GetTomlConverter(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "TomlConverterAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoToml"
            )
            {
                if (
                    attr.ConstructorArguments.Length >= 1
                    && attr.ConstructorArguments[0].Value is INamedTypeSymbol nts
                )
                    return nts.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (attr.AttributeClass?.TypeArguments.Length == 1)
                    return attr
                        .AttributeClass.TypeArguments[0]
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }
        return null;
    }

    private static string? GetTomlDateTimeFormat(IPropertySymbol p)
    {
        foreach (var attr in p.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "TomlDateTimeFormatAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoToml"
                && attr.ConstructorArguments.Length >= 1
                && attr.ConstructorArguments[0].Value is string fmt
            )
                return fmt;
        }
        return null;
    }

    // ── Source generation ──

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
        foreach (var t in types)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedTypes(t, nestedTypes);

        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var safeName = PicoSerDe.Gen.GenInfrastructure.SafeName(fullName);
            spc.AddSource(
                $"{safeName}_TomlInner.g.cs",
                SourceText.From(GenInner(fullName, safeName, props), Encoding.UTF8)
            );
        }

        var seen = new HashSet<string>();
        foreach (var t in types)
        {
            if (seen.Add(t.FullyQualifiedName))
                spc.AddSource(
                    $"{t.Name}_TomlSerializer.g.cs",
                    SourceText.From(Gen(t), Encoding.UTF8)
                );
        }
    }

    static string GenInner(string fqn, string shortName, ImmutableArray<PropertyInfo> props)
    {
        var clean = fqn.Replace("global::", "");
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using PicoSerDe.Core; using PicoToml;");
        var lastDot = clean.LastIndexOf('.');
        if (lastDot > 0)
        {
            s.Append("namespace ");
            s.Append(clean.Substring(0, lastDot));
            s.AppendLine(";");
        }
        s.AppendLine();
        s.Append("internal static class ");
        s.Append(shortName);
        s.AppendLine("TomlInner {");

        s.Append("    internal static void Serialize(TomlWriter tw, ");
        s.Append(clean);
        s.AppendLine(" value) {");
        foreach (var p in props.OrderBy(x => x.JsonName))
            EmitSerializeProp(s, p, "value", "        ");
        s.AppendLine("    }");

        s.Append("    internal static ");
        s.Append(clean);
        s.AppendLine(" Deserialize(ref TomlReader r) {");
        s.Append("        var o = new ");
        s.Append(clean);
        s.AppendLine("();");
        s.AppendLine("        while (r.Read() && r.TokenType != TokenType.ObjectEnd) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        var sorted = props.OrderBy(x => x.JsonName).ToImmutableArray();
        EmitPropertyDispatch(s, sorted, "k", "o", "                ", "                    ", null);
        s.AppendLine("            }");
        s.AppendLine("        }");
        s.AppendLine("        return o;");
        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    private static string Gen(TypeInfo t)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>\n#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoToml;");
        if (!string.IsNullOrEmpty(t.Namespace))
        {
            s.AppendLine();
            s.Append("namespace ");
            s.Append(t.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        s.Append("file sealed class ");
        s.Append(t.Name);
        s.Append("_TomlSer : ISerializer<");
        s.Append(t.Name);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(t.Name);
        s.AppendLine(" v) {");
        s.AppendLine("        var tw = new TomlWriter(w);");
        foreach (var p in t.Properties)
            EmitSerializeProp(s, p, "v", "        ");
        s.AppendLine("    } }");

        s.Append("file sealed class ");
        s.Append(t.Name);
        s.Append("_TomlDes : IDeserializer<");
        s.Append(t.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(t.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> d) {");
        s.AppendLine("        var r = new TomlReader(d);");
        var hasCtor = !t.CtorParams.IsDefaultOrEmpty && t.CtorParams.Length > 0;
        Dictionary<string, int>? ctorMap = null;
        if (hasCtor)
        {
            ctorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int ci = 0; ci < t.CtorParams.Length; ci++)
                ctorMap[t.CtorParams[ci].Name] = ci;
        }
        if (hasCtor)
        {
            for (int ci = 0; ci < t.CtorParams.Length; ci++)
            {
                var cp = t.CtorParams[ci];
                var tn = PicoSerDe.Gen.TypeKindResolver.MapTypeName(cp.TypeKind, null!);
                s.Append("        ");
                s.Append(tn);
                s.Append(" __cp_");
                s.Append(ci);
                s.AppendLine(cp.TypeKind == "string" ? " = null!;" : " = default;");
            }
        }
        else
        {
            s.Append("        var o = new ");
            s.Append(t.Name);
            s.AppendLine("();");
        }
        s.AppendLine("        r.Read();");
        s.AppendLine("        while (true) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        // Scalar types, flat lists AND dicts handled via PropertyName dispatch.
        // Only complex-object lists (ArrayStart/[[key]]) and nested objects (ObjectStart/[key]) excluded.
        var scalarProps = t
            .Properties.Where(x =>
                x.TypeKind != "object"
                && !(
                    (x.TypeKind == "list" || x.TypeKind == "array")
                    && x.ElementTypeKind == "object"
                    && x.NestedProperties.Length > 0
                )
            )
            .ToImmutableArray();
        EmitPropertyDispatch(
            s,
            scalarProps,
            "k",
            "o",
            "                ",
            "                    ",
            ctorMap
        );
        s.AppendLine("                if (!r.Read()) break;");
        s.AppendLine("                continue;");
        s.AppendLine("            }");
        var objProps = t.Properties.Where(x => x.TypeKind == "object").ToImmutableArray();
        var dictProps = t.Properties.Where(x => x.TypeKind == "dict").ToImmutableArray();
        var listObjProps = t
            .Properties.Where(x =>
                (x.TypeKind == "list" || x.TypeKind == "array")
                && x.ElementTypeKind == "object"
                && x.NestedProperties.Length > 0
            )
            .ToImmutableArray();
        // ObjectStart branch (dicts and nested objects)
        if (objProps.Length > 0 || dictProps.Length > 0)
        {
            s.AppendLine("            if (r.TokenType == TokenType.ObjectStart) {");
            s.AppendLine("                var tbl = r.TablePath;");
            for (int i = 0; i < objProps.Length; i++)
            {
                s.Append("                ");
                s.Append(i == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(tbl, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(objProps[i].JsonName));
                s.AppendLine("\"u8)) {");
                EmitNestedObjectRead(s, objProps[i], "o", "                    ");
                s.AppendLine("                }");
            }
            for (int i = 0; i < dictProps.Length; i++)
            {
                s.Append("                ");
                s.Append(i == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(tbl, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dictProps[i].JsonName));
                s.AppendLine("\"u8)) {");
                EmitDictRead(s, dictProps[i], "o", "                    ");
                s.AppendLine("                }");
            }
            s.AppendLine("                continue;");
            s.AppendLine("            }");
        }
        // ArrayStart branch ([[key]] array of tables for List<ComplexObject>)
        if (listObjProps.Length > 0)
        {
            s.AppendLine("            if (r.TokenType == TokenType.ArrayStart) {");
            for (int ai = 0; ai < listObjProps.Length; ai++)
            {
                var ap = listObjProps[ai];
                s.Append("                ");
                s.Append(ai == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(r.TablePath, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName));
                s.AppendLine("\"u8)) {");
                var elemTypeName = ap.ElementTypeName ?? "object";
                s.Append("                    var __item = new ");
                s.Append(elemTypeName);
                s.AppendLine("();");
                s.AppendLine("                    while (true) {");
                s.AppendLine(
                    "                        while (r.Read() && r.TokenType == TokenType.PropertyName) {"
                );
                s.AppendLine("                            var __k = r.KeySpan;");
                EmitPropertyDispatch(
                    s,
                    ap.NestedProperties,
                    "__k",
                    "__item",
                    "                            ",
                    "                                "
                );
                s.AppendLine("                        }");
                s.AppendLine(
                    "                        o."
                        + ap.Name
                        + " ??= new System.Collections.Generic.List<"
                        + (ap.ElementTypeName ?? "object")
                        + ">();"
                );
                s.AppendLine("                        o." + ap.Name + ".Add(__item);");
                s.AppendLine(
                    "                        if (r.TokenType != TokenType.ArrayStart) break;"
                );
                s.AppendLine(
                    "                        __item = new "
                        + (ap.ElementTypeName ?? "object")
                        + "();"
                );
                s.AppendLine("                    }");
                s.AppendLine("                    continue;");
                s.AppendLine("                }");
            }
            s.AppendLine("            }");
        }
        s.AppendLine("            if (!r.Read()) break;");
        s.AppendLine("        }");
        if (hasCtor)
        {
            s.Append("        return new ");
            s.Append(t.Name);
            s.Append("(");
            for (int ci = 0; ci < t.CtorParams.Length; ci++)
            {
                if (ci > 0)
                    s.Append(", ");
                s.Append("__cp_");
                s.Append(ci);
            }
            s.AppendLine(");");
        }
        else
            s.AppendLine("        return o;");
        s.AppendLine("    } }");
        s.AppendLine();

        // Streaming (scalar properties only, skip nested objects; not supported for ctor types)
        if (hasCtor)
        { /* skip streaming */
        }
        else
        {
            s.Append("file static class ");
            s.Append(t.Name);
            s.AppendLine("_TomlStreaming {");
            s.AppendLine(
                "    internal static ReadStatus DeserializeStreaming(ref TomlReader r, out "
                    + t.Name
                    + "? result) {"
            );
            s.AppendLine("        result = default;");
            s.Append("        var o = new ");
            s.Append(t.Name);
            s.AppendLine("();");
            s.AppendLine("        while (true) {");
            s.AppendLine(
                "            if (!r.Read()) return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.Success;"
            );
            s.AppendLine("            if (r.TokenType != TokenType.PropertyName) break;");
            s.AppendLine("            var __sk = r.KeySpan;");
            s.AppendLine(
                "            if (!r.Read()) return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
            );
            var simpleProps = t
                .Properties.Where(p => p.TypeKind is not "object" and not "dict")
                .ToImmutableArray();
            EmitPropertyDispatch(s, simpleProps, "__sk", "o", "        ", "            ");
            foreach (var p in t.Properties.Where(p => p.TypeKind is "object" or "dict"))
            {
                s.Append("            if (TextHelpers.Eq(__sk, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                s.AppendLine("\"u8)) {");
                s.AppendLine("                r.Skip();");
                s.AppendLine("            }");
            }
            s.AppendLine("        }");
            s.AppendLine("        result = o;");
            s.AppendLine("        return ReadStatus.Success;");
            s.AppendLine("    }");
            s.AppendLine("}");
        } // end skip-streaming else
        s.AppendLine();

        // Registration
        s.Append("file static class ");
        s.Append(t.Name);
        s.Append("_Reg { [ModuleInitializer] internal static void R() { TomlSerializer.Register<");
        s.Append(t.Name);
        s.Append(">(new ");
        s.Append(t.Name);
        s.Append("_TomlSer(), new ");
        s.Append(t.Name);
        s.AppendLine("_TomlDes());");
        if (hasCtor)
            s.AppendLine("            // Streaming skipped for constructor type");
        else
        {
            s.Append("TomlSerializer.RegisterStreaming<");
            s.Append(t.Name);
            s.Append(">(");
            s.Append(t.Name);
            s.AppendLine("_TomlStreaming.DeserializeStreaming);");
        }
        s.AppendLine("    } }");
        return s.ToString();
    }

    private static void EmitPropertyDispatch(
        StringBuilder s,
        ImmutableArray<PropertyInfo> props,
        string keyVar,
        string target,
        string indent,
        string bodyIndent,
        IReadOnlyDictionary<string, int>? ctorMap = null
    )
    {
        if (props.Length == 0)
            return;

        if (props.Length <= 2)
        {
            for (int i = 0; i < props.Length; i++)
            {
                s.Append(indent);
                s.Append(i == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(");
                s.Append(keyVar);
                s.Append(", \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(props[i].JsonName));
                s.AppendLine("\"u8)) {");
                EmitDeserializeProp(s, props[i], target, bodyIndent);
                s.Append(indent);
                s.AppendLine("}");
            }
            return;
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
                EmitDeserializePropOrAssign(
                    s,
                    groupProps[i],
                    target,
                    bodyIndent + "        ",
                    ctorMap
                );
                s.Append(indent);
                s.AppendLine("        }");
            }
            s.Append(indent);
            s.AppendLine("        break;");
        }
        s.Append(indent);
        s.AppendLine("}");
    }

    private static void EmitSerializeProp(
        StringBuilder s,
        PropertyInfo p,
        string target,
        string indent
    )
    {
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(indent);
            s.Append("var __cnv = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(indent);
            s.AppendLine("var __bw = new System.Buffers.ArrayBufferWriter<byte>();");
            s.Append(indent);
            s.Append("__cnv.Write(__bw, ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(");");
            s.Append(indent);
            s.Append("tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, System.Text.Encoding.UTF8.GetString(__bw.WrittenSpan));");
            s.AppendLine();
            return;
        }

        if (p.TypeKind is "list" or "array")
        {
            // Array of tables ([[key]]) for List<ComplexObject>
            if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
            {
                var elemTypeName = p.ElementTypeName ?? "object";
                var innerSn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlInner",
                    elemTypeName
                );
                s.Append(indent);
                s.Append("foreach (var __item in ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(")");
                s.Append(indent);
                s.AppendLine("{");
                s.Append(indent);
                s.Append("    tw.WriteArrayTable(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                s.AppendLine("\"u8);");
                s.Append(indent);
                s.Append("    ");
                s.Append(innerSn);
                s.Append(".Serialize(tw, __item);");
                s.AppendLine();
                s.Append(indent);
                s.AppendLine("}");
            }
            else
            {
                s.Append(indent);
                s.Append("tw.WriteStartArray(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                s.AppendLine("\"u8);");
                s.Append(indent);
                s.Append("foreach (var __item in ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(")");
                s.Append(indent);
                s.AppendLine("{");
                EmitSerializeListElement(s, p, indent + "    ");
                s.Append(indent);
                s.AppendLine("}");
                s.Append(indent);
                s.AppendLine("tw.WriteEndArray();");
            }
        }
        else if (p.TypeKind is "dict")
        {
            s.Append(indent);
            s.Append("tw.WriteTable(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            s.Append(indent);
            s.Append("foreach (var __kvp in ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(")");
            s.Append(indent);
            s.AppendLine("{");
            s.Append(indent);
            s.Append("    tw.WriteKeyValue(__kvp.Key, ");
            EmitValueAccessor(
                s,
                new PropertyInfo(
                    "",
                    "",
                    p.ElementTypeKind ?? "string",
                    "",
                    false,
                    null,
                    null,
                    null,
                    null,
                    default,
                    null
                ),
                "__kvp.Value"
            );
            s.AppendLine(");");
            s.Append(indent);
            s.AppendLine("}");
        }
        else if (p.TypeKind is "object")
        {
            if (p.NestedProperties.Length > 0)
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlInner",
                    p.TypeFullName!
                );
                s.Append(indent);
                s.Append("tw.WriteTable(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                s.AppendLine("\"u8);");
                s.Append(indent);
                s.Append(sn);
                s.Append(".Serialize(tw, ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(");");
            }
        }
        else if (p.IsNullable && !p.IsNullableReference)
        {
            s.Append(indent);
            s.Append("if (");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(".HasValue)");
            s.Append(indent);
            s.AppendLine("{");
            s.Append(indent);
            s.Append("    tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, ");
            EmitValueAccessor(s, p, $"{target}.{p.Name}.Value");
            s.AppendLine(");");
            s.Append(indent);
            s.AppendLine("}");
        }
        else if (p.IsNullable && p.IsNullableReference)
        {
            s.Append(indent);
            s.Append("if (");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(" != null)");
            s.Append(indent);
            s.AppendLine("{");
            s.Append(indent);
            s.Append("    tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, ");
            EmitValueAccessor(s, p, $"{target}.{p.Name}");
            s.AppendLine(");");
            s.Append(indent);
            s.AppendLine("}");
        }
        else
        {
            s.Append(indent);
            s.Append("tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, ");
            EmitValueAccessor(s, p, $"{target}.{p.Name}");
            s.AppendLine(");");
        }
    }

    private static void EmitNestedObjectRead(
        StringBuilder s,
        PropertyInfo op,
        string tgt,
        string pad
    )
    {
        if (op.NestedProperties.Length > 0)
        {
            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("TomlInner", op.TypeFullName!);
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(op.Name);
            s.Append(" = ");
            s.Append(sn);
            s.AppendLine(".Deserialize(ref r);");
        }
    }

    private static void EmitDictRead(StringBuilder s, PropertyInfo dp, string tgt, string pad)
    {
        s.Append(pad);
        s.Append(tgt);
        s.Append('.');
        s.Append(dp.Name);
        s.Append(" ??= new System.Collections.Generic.Dictionary<");
        s.Append(dp.KeyTypeName ?? "string");
        s.Append(", ");
        s.Append(dp.ElementTypeName ?? "int");
        s.AppendLine(">();");
        s.Append(pad);
        s.AppendLine("while (r.Read() && r.TokenType == TokenType.PropertyName) {");
        s.Append(pad);
        s.Append("    var __dk = Encoding.UTF8.GetString(r.KeySpan);");
        switch (dp.ElementTypeKind)
        {
            case "int32":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    r.TryGetInt32(out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "int64":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    r.TryGetInt64(out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "float32":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    r.TryGetFloat64(out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = (float)__dv;");
                break;
            case "float64":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    r.TryGetFloat64(out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "boolean":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    r.TryGetBool(out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "datetime":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.AppendLine(
                    "    System.DateTime.TryParse(__raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dv);"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "guid":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.AppendLine("    System.Guid.TryParse(__raw, out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "decimal":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.AppendLine(
                    "    decimal.TryParse(__raw, System.Globalization.CultureInfo.InvariantCulture, out var __dv);"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "dateonly":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.AppendLine("    System.DateOnly.TryParse(__raw, out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "timeonly":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.AppendLine("    System.TimeOnly.TryParse(__raw, out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "timespan":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.AppendLine("    System.TimeSpan.TryParse(__raw, out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "enum":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine("    var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.Append(pad);
                s.Append("    System.Enum.TryParse<");
                s.Append(dp.ElementTypeName ?? "object");
                s.AppendLine(">(__raw, out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            default:
                s.AppendLine();
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
                break;
        }
        s.Append(pad);
        s.AppendLine("}");
    }

    private static void EmitValueAccessor(StringBuilder s, PropertyInfo p, string accessor)
    {
        switch (p.TypeKind)
        {
            case "datetime":
                s.Append(accessor);
                if (p.DateTimeFormat is not null)
                {
                    s.Append(".ToString(\"");
                    s.Append(p.DateTimeFormat);
                    s.Append("\")");
                }
                else
                    s.Append(".ToString(\"O\")");
                break;
            case "dateonly":
                s.Append(accessor);
                s.Append(".ToString(\"O\")");
                break;
            case "timeonly":
                s.Append(accessor);
                s.Append(".ToString(\"O\")");
                break;
            case "timespan":
                s.Append(accessor);
                s.Append(".ToString()");
                break;
            case "guid":
            case "decimal":
            case "enum":
                s.Append(accessor);
                s.Append(".ToString()");
                break;
            default:
                s.Append(accessor);
                break;
        }
    }

    private static void EmitSerializeListElement(StringBuilder s, PropertyInfo p, string indent)
    {
        switch (p.ElementTypeKind)
        {
            case "string":
                s.Append(indent);
                s.AppendLine("tw.WriteArrayValue(__item);");
                break;
            case "int32":
                s.Append(indent);
                s.AppendLine("tw.WriteArrayValue(__item);");
                break;
            case "int64":
                s.Append(indent);
                s.AppendLine("tw.WriteArrayValue(__item);");
                break;
            case "float32":
            case "float64":
                s.Append(indent);
                s.AppendLine("tw.WriteArrayValue(__item);");
                break;
            case "boolean":
                s.Append(indent);
                s.AppendLine("tw.WriteArrayValue(__item);");
                break;
            default:
                s.Append(indent);
                s.AppendLine("tw.WriteArrayValue(__item.ToString());");
                break;
        }
    }

    /// <summary>Dispatch to <see cref="EmitDeserializeProp"/> or generate constructor param assign.</summary>
    private static void EmitDeserializePropOrAssign(
        StringBuilder s,
        PropertyInfo p,
        string tgt,
        string pad,
        IReadOnlyDictionary<string, int>? ctorMap
    )
    {
        if (ctorMap is not null && ctorMap.TryGetValue(p.Name, out var cpIdx))
            EmitDeserializeProp(s, p, $"__cp_{cpIdx}", pad, ctorAssign: true);
        else
            EmitDeserializeProp(s, p, tgt, pad);
    }

    private static void EmitDeserializeProp(
        StringBuilder s,
        PropertyInfo p,
        string tgt,
        string pad,
        bool ctorAssign = false
    )
    {
        // Assign-to: tgt.Name (normal) or tgt (ctor param, tgt = __cp_N)
        void EmitAssign()
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
            EmitAssign();
            s.AppendLine(" = __cnv.Read(ref r);");
            return;
        }

        if (p.TypeKind is "list" or "array")
        {
            s.Append(pad);
            s.Append("var __tmpList = new System.Collections.Generic.List<");
            s.Append(p.ElementTypeName ?? "object");
            s.AppendLine(">(16);");
            // Array of tables ([[key]]) for List<ComplexObject>
            if (p.ElementTypeKind == "object" && p.NestedProperties.Length > 0)
            {
                // Loop: each ArrayStart from [[key]] is one item; read its properties inline.
                var elemTypeName = p.ElementTypeName ?? "object";
                s.Append(pad);
                s.AppendLine("while (r.Read())");
                s.Append(pad);
                s.AppendLine("{");
                s.Append(pad);
                s.AppendLine("    if (r.TokenType != TokenType.ArrayStart) break;");
                s.Append(pad);
                s.Append("    var __item = new ");
                s.Append(elemTypeName);
                s.AppendLine("();");
                s.Append(pad);
                s.AppendLine("    while (r.Read() && r.TokenType == TokenType.PropertyName)");
                s.Append(pad);
                s.AppendLine("    {");
                s.Append(pad);
                s.AppendLine("        var __k = r.KeySpan;");
                EmitPropertyDispatch(
                    s,
                    p.NestedProperties,
                    "__k",
                    "__item",
                    pad + "        ",
                    pad + "            "
                );
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("    __tmpList.Add(__item);");
                s.Append(pad);
                s.AppendLine("}");
            }
            else
            {
                s.Append(pad);
                s.AppendLine("if (r.Read() && r.TokenType == TokenType.ArrayStart)");
                s.Append(pad);
                s.AppendLine("{");
                s.Append(pad);
                s.AppendLine("    while (r.Read() && r.TokenType != TokenType.ArrayEnd)");
                s.Append(pad);
                s.AppendLine("    {");
                EmitDeserializeListElementTemp(s, p, pad + "        ");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("}");
            }
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" = __tmpList");
            if (p.TypeKind == "array")
                s.Append(".ToArray()");
            s.AppendLine(";");
        }
        else
        {
            switch (p.TypeKind)
            {
                case "string":
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
                case "int32":
                    s.Append(pad);
                    s.AppendLine("r.TryGetInt32(out var __v);");
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = __v;");
                    break;
                case "int64":
                    s.Append(pad);
                    s.AppendLine("r.TryGetInt64(out var __v);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __v;");
                    break;
                case "float32":
                    s.Append(pad);
                    s.AppendLine("r.TryGetFloat64(out var __v);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = (float)__v;");
                    break;
                case "float64":
                    s.Append(pad);
                    s.AppendLine("r.TryGetFloat64(out var __v);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __v;");
                    break;
                case "boolean":
                    s.Append(pad);
                    s.AppendLine("r.TryGetBool(out var __v);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __v;");
                    break;
                case "datetime":
                    s.Append(pad);
                    s.AppendLine("var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                    s.Append(pad);
                    if (p.DateTimeFormat is not null)
                    {
                        s.Append("System.DateTime.TryParseExact(__raw, \"");
                        s.Append(p.DateTimeFormat);
                        s.Append(
                            "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt);"
                        );
                    }
                    else
                        s.AppendLine(
                            "System.DateTime.TryParse(__raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                        );
                    s.AppendLine();
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
                    s.AppendLine(" = System.Guid.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "dateonly":
                    s.Append(pad);
                    s.AppendLine("var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                    s.Append(pad);
                    s.AppendLine("System.DateOnly.TryParse(__raw, out var __dateOnly);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __dateOnly;");
                    break;
                case "timeonly":
                    s.Append(pad);
                    s.AppendLine("var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                    s.Append(pad);
                    s.AppendLine("System.TimeOnly.TryParse(__raw, out var __timeOnly);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __timeOnly;");
                    break;
                case "timespan":
                    s.Append(pad);
                    s.AppendLine("var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                    s.Append(pad);
                    s.AppendLine("System.TimeSpan.TryParse(__raw, out var __timeSpan);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __timeSpan;");
                    break;
                case "decimal":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(
                        " = decimal.Parse(Encoding.UTF8.GetString(r.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"
                    );
                    break;
                case "enum":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.Append(" = System.Enum.TryParse<");
                    s.Append(p.TypeFullName);
                    s.AppendLine(
                        ">(Encoding.UTF8.GetString(r.ValueSpan), out var __ev) ? __ev : default;"
                    );
                    break;
                case "dict":
                    // Dict from inline table: StringDict = {k1 = "v1"}
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.Append(" ??= new System.Collections.Generic.Dictionary<");
                    s.Append(p.KeyTypeName ?? "string");
                    s.Append(", ");
                    s.Append(p.ElementTypeName ?? "string");
                    s.AppendLine(">();");
                    s.Append(pad);
                    s.AppendLine("r.Read(); // skip ObjectStart from inline table");
                    s.Append(pad);
                    s.AppendLine("while (r.Read() && r.TokenType == TokenType.PropertyName) {");
                    s.Append(pad);
                    s.Append("    var __dk = Encoding.UTF8.GetString(r.KeySpan);");
                    switch (p.ElementTypeKind)
                    {
                        case "int32":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetInt32(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = __dv;");
                            break;
                        case "int64":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetInt64(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = __dv;");
                            break;
                        case "float64":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetFloat64(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = __dv;");
                            break;
                        case "boolean":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetBool(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = __dv;");
                            break;
                        default:
                            s.AppendLine();
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
                            break;
                    }
                    s.Append(pad);
                    s.AppendLine("}");
                    break;
                default:
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
            }
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
            case "int64":
                s.Append(pad);
                s.AppendLine("r.TryGetInt64(out var __ev);");
                s.Append(pad);
                s.AppendLine("__tmpList.Add(__ev);");
                break;
            case "float32":
                s.Append(pad);
                s.AppendLine("r.TryGetFloat64(out var __ev);");
                s.Append(pad);
                s.AppendLine("__tmpList.Add((float)__ev);");
                break;
            case "float64":
                s.Append(pad);
                s.AppendLine("r.TryGetFloat64(out var __ev);");
                s.Append(pad);
                s.AppendLine("__tmpList.Add(__ev);");
                break;
            case "boolean":
                s.Append(pad);
                s.AppendLine("r.TryGetBool(out var __ev);");
                s.Append(pad);
                s.AppendLine("__tmpList.Add(__ev);");
                break;
            default:
                s.Append(pad);
                s.AppendLine("__tmpList.Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
        }
    }
}
