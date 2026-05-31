namespace PicoToml.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class TomlSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var providers = context
            .SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (n, _) => IsCandidate(n),
                transform: static (ctx, _) => Transform(ctx)
            )
            .Where(static t => t.HasValue)
            .Select(static (t, _) => t!.Value);
        context.RegisterSourceOutput(providers.Collect(), GenerateAll);
    }

    private static bool IsCandidate(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol m)
            return null;
        if (
            m.ContainingType.Name != "TomlSerializer"
            || m.ContainingType.ContainingNamespace?.ToDisplayString() != "PicoToml"
        )
            return null;
        if (m.TypeArguments.Length != 1)
            return null;
        var t = m.TypeArguments[0];
        if (t.SpecialType != SpecialType.None)
            return null;
        if (t is not INamedTypeSymbol nt)
            return null;
        if (nt.ContainingType is not null)
            return null;
        var ns = nt.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>")
            ns = "";
        var useCamelCase = HasTomlCamelCase(nt);
        var props = new List<PropInfo>();
        foreach (var member in nt.GetMembers())
        {
            if (member is not IPropertySymbol p)
                continue;
            if (p.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (p.IsStatic || p.IsIndexer)
                continue;
            if (p.IsReadOnly && p.SetMethod is null)
                continue;
            if (p.GetMethod is null)
                continue;
            if (HasTomlIgnore(p))
                continue;
            var converterType = GetTomlConverter(p);
            var (k, isNullable, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(p.Type);
            // If converter is present, override type kind to avoid object/table serialization
            if (converterType is not null)
                k = "string";
            if (k is null)
                continue;
            var jsonName = GetTomlKey(p) ?? (useCamelCase ? PicoSerDe.Gen.GenInfrastructure.ToCamelCase(p.Name) : p.Name);

            string? elemTk = null;
            string? elemTf = null;
            string? keyTk = null;
            string? keyTf = null;
            ImmutableArray<PropInfo> nestedProps = default;
            if (k is "list" or "array")
            {
                if (p.Type is INamedTypeSymbol nts && nts.TypeArguments.Length == 1)
                {
                    var et = nts.TypeArguments[0];
                    var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(et);
                    elemTk = ek;
                    elemTf = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek ?? "string", et);
                }
                else if (p.Type is IArrayTypeSymbol ats)
                {
                    var et = ats.ElementType;
                    var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(et);
                    elemTk = ek;
                    elemTf = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek ?? "string", et);
                }
            }
            else if (k is "object" && p.Type is INamedTypeSymbol objNts)
            {
                nestedProps = ExtractNested(objNts, useCamelCase);
            }
            else if (k is "dict" && p.Type is INamedTypeSymbol nd && nd.TypeArguments.Length == 2)
            {
                var kt = nd.TypeArguments[0];
                var vt = nd.TypeArguments[1];
                var (ktk, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(kt);
                var (vtk, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(vt);
                keyTk = ktk;
                keyTf = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ktk ?? "string", kt);
                elemTk = vtk;
                elemTf = PicoSerDe.Gen.TypeKindResolver.MapTypeName(vtk ?? "string", vt);
            }

            props.Add(
                new PropInfo(
                    p.Name,
                    jsonName,
                    k,
                    p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    elemTk,
                    elemTf,
                    isNullable,
                    nestedProps,
                    keyTk,
                    keyTf,
                    converterType,
                    GetTomlDateTimeFormat(p)
                )
            );
        }
        return new TypeInfo(
            nt.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            nt.Name,
            props.ToImmutableArray()
        );
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
                // Fallback: try to get type from AttributeData
                if (attr.AttributeClass?.TypeArguments.Length == 1)
                    return attr.AttributeClass
                        .TypeArguments[0]
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }
        return null;
    }

    private static bool HasTomlCamelCase(INamedTypeSymbol type)
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

    private static ImmutableArray<PropInfo> ExtractNested(INamedTypeSymbol type, bool useCamelCase)
    {
        var list = new List<PropInfo>();
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol p)
                continue;
            if (p.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (p.IsStatic || p.IsIndexer)
                continue;
            if (p.IsReadOnly && p.SetMethod is null)
                continue;
            if (p.GetMethod is null)
                continue;
            if (HasTomlIgnore(p))
                continue;
            var nestedConverter = GetTomlConverter(p);
            var (k, isNullable, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(p.Type);
            if (nestedConverter is not null)
                k = "string";
            if (k is null)
                continue;
            string? elemTk2 = null,
                elemTf2 = null;
            ImmutableArray<PropInfo> nested2 = default;
            if (k is "list" or "array")
            {
                if (p.Type is INamedTypeSymbol nts && nts.TypeArguments.Length == 1)
                {
                    var et = nts.TypeArguments[0];
                    var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(et);
                    elemTk2 = ek;
                    elemTf2 = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek ?? "string", et);
                }
                else if (p.Type is IArrayTypeSymbol ats)
                {
                    var et = ats.ElementType;
                    var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(et);
                    elemTk2 = ek;
                    elemTf2 = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek ?? "string", et);
                }
            }
            else if (k is "object" && p.Type is INamedTypeSymbol objNts2)
            {
                nested2 = ExtractNested(objNts2, useCamelCase);
            }
            list.Add(
                new PropInfo(
                    p.Name,
                    GetTomlKey(p) ?? (useCamelCase ? PicoSerDe.Gen.GenInfrastructure.ToCamelCase(p.Name) : p.Name),
                    k,
                    p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    elemTk2,
                    elemTf2,
                    isNullable,
                    nested2,
                    null,
                    null,
                    GetTomlConverter(p),
                    GetTomlDateTimeFormat(p)
                )
            );
        }
        return list.ToImmutableArray();
    }

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        // Collect unique nested types for inner helper generation
        var nestedTypes = new Dictionary<string, ImmutableArray<PropInfo>>();
        foreach (var t in types)
            CollectNested(t, nestedTypes);

        // Generate inner helpers for shared nested types
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
            if (seen.Add(t.Fqn))
                spc.AddSource(
                    $"{t.Name}_TomlSerializer.g.cs",
                    SourceText.From(Gen(t), Encoding.UTF8)
                );
        }
    }

    static void CollectNested(TypeInfo t, Dictionary<string, ImmutableArray<PropInfo>> d)
    {
        foreach (var p in t.Props)
        {
            if (p.Tk == "object" && !string.IsNullOrEmpty(p.Tf) && p.NestedProps.Length > 0)
            {
                if (!d.ContainsKey(p.Tf))
                {
                    d[p.Tf] = p.NestedProps;
                    // Recursively collect deeper nested types
                    foreach (var np in p.NestedProps)
                        if (
                            np.Tk == "object"
                            && !string.IsNullOrEmpty(np.Tf)
                            && np.NestedProps.Length > 0
                            && !d.ContainsKey(np.Tf)
                        )
                            d[np.Tf] = np.NestedProps;
                }
            }
        }
    }

    static string GenInner(string fqn, string shortName, ImmutableArray<PropInfo> props)
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
        s.AppendLine(
            "file static class __T { internal static bool __Tk(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) { byte x = a[i], y = b[i]; if (x != y && (x | 0x20) != (y | 0x20)) return false; } return true; } }"
        );
        s.AppendLine();
        s.Append("internal static class ");
        s.Append(shortName);
        s.AppendLine("TomlInner {");

        // Serialize
        s.Append("    internal static void Serialize(TomlWriter tw, ");
        s.Append(clean);
        s.AppendLine(" value) {");
        foreach (var p in props.OrderBy(x => x.Jn))
            EmitSerializeProp(s, p, "value", "        ");
        s.AppendLine("    }");

        // Deserialize
        s.Append("    internal static ");
        s.Append(clean);
        s.AppendLine(" Deserialize(ref TomlReader r) {");
        s.Append("        var o = new ");
        s.Append(clean);
        s.AppendLine("();");
        s.AppendLine("        while (r.Read() && r.TokenType != TokenType.ObjectEnd) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        var sorted = props.OrderBy(x => x.Jn).ToImmutableArray();
        for (int i = 0; i < sorted.Length; i++)
        {
            s.Append("                ");
            s.Append(i == 0 ? "if" : "else if");
            s.Append(" (__T.__Tk(k, \"");
            s.Append(sorted[i].Jn);
            s.AppendLine("\"u8)) {");
            EmitDeserializeProp(s, sorted[i], "o", "                    ");
            s.AppendLine("                }");
        }
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
        if (!string.IsNullOrEmpty(t.Ns))
        {
            s.AppendLine();
            s.Append("namespace ");
            s.Append(t.Ns);
            s.AppendLine(";");
        }
        s.AppendLine();
        s.AppendLine("file static class __T {");
        s.AppendLine(
            "    internal static bool __Tk(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if ((a[i] | 0x20) != (b[i] | 0x20)) return false; return true; }"
        );
        s.AppendLine("}");
        s.AppendLine();

        // Serializer
        s.Append("file sealed class ");
        s.Append(t.Name);
        s.Append("_TomlSer : ISerializer<");
        s.Append(t.Name);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(t.Name);
        s.AppendLine(" v) {");
        s.AppendLine("        var tw = new TomlWriter(w);");
        foreach (var p in t.Props)
        {
            EmitSerializeProp(s, p, "v", "        ");
        }
        s.AppendLine("    } }");

        // Deserializer
        s.Append("file sealed class ");
        s.Append(t.Name);
        s.Append("_TomlDes : IDeserializer<");
        s.Append(t.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(t.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> d) {");
        s.AppendLine("        var r = new TomlReader(d);");
        s.Append("        var o = new ");
        s.Append(t.Name);
        s.AppendLine("();");
        s.AppendLine("        while (r.Read()) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        var scalarProps = t.Props.Where(x => x.Tk != "object" && x.Tk != "dict").ToImmutableArray();
        for (int i = 0; i < scalarProps.Length; i++)
        {
            s.Append("                ");
            s.Append(i == 0 ? "if" : "else if");
            s.Append(" (__T.__Tk(k, \"");
            s.Append(scalarProps[i].Jn);
            s.AppendLine("\"u8)) {");
            EmitDeserializeProp(s, scalarProps[i], "o", "                    ");
            s.AppendLine("                }");
        }
        if (scalarProps.Length > 0)
            s.AppendLine("                else { }");
        s.AppendLine("            }");
        // Handle table headers (nested objects + dicts)
        var objProps = t.Props.Where(x => x.Tk == "object").ToImmutableArray();
        var dictProps = t.Props.Where(x => x.Tk == "dict").ToImmutableArray();
        if (objProps.Length > 0 || dictProps.Length > 0)
        {
            s.AppendLine("            else if (r.TokenType == TokenType.ObjectStart) {");
            s.AppendLine("                var tbl = r.TablePath;");
            // Object table headers
            for (int i = 0; i < objProps.Length; i++)
            {
                s.Append("                ");
                s.Append(i == 0 && dictProps.Length == 0 ? "if" : "else if");
                s.Append(" (__T.__Tk(tbl, \"");
                s.Append(objProps[i].Jn);
                s.AppendLine("\"u8)) {");
                EmitNestedObjectRead(s, objProps[i], "o", "                    ");
                s.AppendLine("                }");
            }
            // Dict table headers
            for (int i = 0; i < dictProps.Length; i++)
            {
                s.Append("                ");
                s.Append(i == 0 && objProps.Length == 0 ? "if" : "else if");
                s.Append(" (__T.__Tk(tbl, \"");
                s.Append(dictProps[i].Jn);
                s.AppendLine("\"u8)) {");
                EmitDictRead(s, dictProps[i], "o", "                    ");
                s.AppendLine("                }");
            }
            s.AppendLine("            }");
        }
        s.AppendLine("        }");
        s.AppendLine("        return o;");
        s.AppendLine("    } }");

        // Registration
        s.Append("file static class ");
        s.Append(t.Name);
        s.Append("_Reg { [ModuleInitializer] internal static void R() { TomlSerializer.Register<");
        s.Append(t.Name);
        s.Append(">(new ");
        s.Append(t.Name);
        s.Append("_TomlSer(), new ");
        s.Append(t.Name);
        s.AppendLine("_TomlDes()); } }");
        return s.ToString();
    }

    private static void EmitSerializeProp(StringBuilder s, PropInfo p, string target, string indent)
    {
        if (p.ConverterType is not null)
        {
            s.Append(indent);
            s.Append("var __cnv = new ");
            s.Append(p.ConverterType);
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
            s.Append(p.Jn);
            s.Append("\", System.Text.Encoding.UTF8.GetString(__bw.WrittenSpan));");
            s.AppendLine();
            return;
        }

        if (p.Tk is "list" or "array")
        {
            var ename = p.Name;
            s.Append(indent);
            s.Append("tw.WriteStartArray(\"");
            s.Append(p.Jn);
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
        else if (p.Tk is "dict")
        {
            s.Append(indent);
            s.Append("tw.WriteTable(\"");
            s.Append(p.Jn);
            s.AppendLine("\");");
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
                new PropInfo(
                    "",
                    "",
                    p.ElemTk ?? "string",
                    "",
                    null,
                    null,
                    false,
                    default,
                    null,
                    null
                ),
                "__kvp.Value"
            );
            s.AppendLine(");");
            s.Append(indent);
            s.AppendLine("}");
        }
        else if (p.Tk is "object")
        {
            if (p.NestedProps.Length > 0)
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("TomlInner", p.Tf!);
                s.Append(indent);
                s.Append("tw.WriteTable(\"");
                s.Append(p.Jn);
                s.AppendLine("\");");
                s.Append(indent);
                s.Append(sn);
                s.Append(".Serialize(tw, ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(");");
            }
        }
        else if (p.IsNullable)
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
            s.Append(p.Jn);
            s.Append("\", ");
            EmitValueAccessor(s, p, target + "." + p.Name + ".Value");
            s.AppendLine(");");
            s.Append(indent);
            s.AppendLine("}");
        }
        else
        {
            s.Append(indent);
            s.Append("tw.WriteKeyValue(\"");
            s.Append(p.Jn);
            s.Append("\", ");
            EmitValueAccessor(s, p, target + "." + p.Name);
            s.AppendLine(");");
        }
    }

    private static void EmitNestedObjectRead(StringBuilder s, PropInfo op, string tgt, string pad)
    {
        if (op.NestedProps.Length > 0)
        {
            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("TomlInner", op.Tf!);
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(op.Name);
            s.Append(" = ");
            s.Append(sn);
            s.AppendLine(".Deserialize(ref r);");
        }
    }

    private static void EmitDictRead(StringBuilder s, PropInfo dp, string tgt, string pad)
    {
        s.Append(pad);
        s.Append(tgt);
        s.Append('.');
        s.Append(dp.Name);
        s.Append(" ??= new System.Collections.Generic.Dictionary<");
        s.Append(dp.KeyTf ?? "string");
        s.Append(", ");
        s.Append(dp.ElemTf ?? "int");
        s.AppendLine(">();");
        s.Append(pad);
        s.AppendLine("while (r.Read() && r.TokenType == TokenType.PropertyName) {");
        s.Append(pad);
        s.Append("    var __dk = Encoding.UTF8.GetString(r.KeySpan);");
        if (dp.ElemTk == "int32")
        {
            s.AppendLine();
            s.Append(pad);
            s.AppendLine("    r.TryGetInt32(out var __dv);");
            s.Append(pad);
            s.Append("    ");
            s.Append(tgt);
            s.Append('.');
            s.Append(dp.Name);
            s.AppendLine("[__dk] = __dv;");
        }
        else
        {
            s.AppendLine();
            s.Append(pad);
            s.Append("    ");
            s.Append(tgt);
            s.Append('.');
            s.Append(dp.Name);
            s.AppendLine("[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
        }
        s.Append(pad);
        s.AppendLine("}");
    }

    private static void EmitValueAccessor(StringBuilder s, PropInfo p, string accessor)
    {
        switch (p.Tk)
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

    private static void EmitSerializeListElement(StringBuilder s, PropInfo p, string indent)
    {
        switch (p.ElemTk)
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

    private static void EmitDeserializeProp(StringBuilder s, PropInfo p, string tgt, string pad)
    {
        if (p.ConverterType is not null)
        {
            s.Append(pad);
            s.Append("var __cnv = new ");
            s.Append(p.ConverterType);
            s.AppendLine("();");
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(" = __cnv.Read(ref r);");
            return;
        }

        if (p.Tk is "list" or "array")
        {
            // Use temp List<T> for all list types (interfaces like IReadOnlyList don't have Add)
            s.Append(pad);
            s.Append("var __tmpList = new System.Collections.Generic.List<");
            s.Append(p.ElemTf ?? "object");
            s.AppendLine(">(16);");
            // Read array tokens
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
            // Assign back
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" = __tmpList");
            if (p.Tk == "array")
                s.Append(".ToArray()");
            s.AppendLine(";");
        }
        else
        {
            switch (p.Tk)
            {
                case "string":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
                case "int32":
                    s.Append(pad);
                    s.AppendLine("r.TryGetInt32(out var __v);");
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
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

    private static void EmitDeserializeListElement(
        StringBuilder s,
        PropInfo p,
        string tgt,
        string pad
    )
    {
        switch (p.ElemTk)
        {
            case "string":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(".Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
            case "int32":
                s.Append(pad);
                s.AppendLine("r.TryGetInt32(out var __ev);");
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(".Add(__ev);");
                break;
            case "int64":
                s.Append(pad);
                s.AppendLine("r.TryGetInt64(out var __ev);");
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(".Add(__ev);");
                break;
            case "float64":
                s.Append(pad);
                s.AppendLine("r.TryGetFloat64(out var __ev);");
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(".Add(__ev);");
                break;
            case "boolean":
                s.Append(pad);
                s.AppendLine("r.TryGetBool(out var __ev);");
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(".Add(__ev);");
                break;
            default:
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(".Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
        }
    }

    private static void EmitDeserializeListElementTemp(StringBuilder s, PropInfo p, string pad)
    {
        switch (p.ElemTk)
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

    internal readonly record struct TypeInfo(
        string Fqn,
        string Ns,
        string Name,
        ImmutableArray<PropInfo> Props
    );

    internal readonly record struct PropInfo(
        string Name,
        string Jn,
        string Tk,
        string Tf,
        string? ElemTk,
        string? ElemTf,
        bool IsNullable,
        ImmutableArray<PropInfo> NestedProps,
        string? KeyTk,
        string? KeyTf,
        string? ConverterType = null,
        string? DateTimeFormat = null
    );
}
