namespace PicoYaml.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class YamlSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var p = ctx.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (n, _) => IsC(n),
                transform: static (c, _) => Tf(c)
            )
            .Where(static t => t.HasValue)
            .Select(static (t, _) => t!.Value);
        ctx.RegisterSourceOutput(p.Collect(), GenerateAll);
    }

    private static bool IsC(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    private static TypeInfo? Tf(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol m)
            return null;
        if (
            m.ContainingType.Name != "YamlSerializer"
            || m.ContainingType.ContainingNamespace?.ToDisplayString() != "PicoYaml"
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
        var useCamelCase = HasYamlCamelCase(nt);
        var props = new List<PropInfo>();
        foreach (var mem in nt.GetMembers())
        {
            if (mem is not IPropertySymbol p)
                continue;
            if (p.DeclaredAccessibility != Accessibility.Public || p.IsStatic || p.IsIndexer)
                continue;
            if (p.GetMethod is null || (p.IsReadOnly && p.SetMethod is null))
                continue;
            if (HasYamlIgnore(p))
                continue;
            var converterType = GetYamlConverter(p);
            var (k, isNullable, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(p.Type);
            if (converterType is not null)
                k = "string";
            if (k is null)
                continue;
            var jsonName =
                GetYamlKey(p)
                ?? (useCamelCase ? PicoSerDe.Gen.GenInfrastructure.ToCamelCase(p.Name) : p.Name);

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
                    GetYamlDateTimeFormat(p)
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

    private static bool HasYamlCamelCase(INamedTypeSymbol type)
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

    private static ImmutableArray<PropInfo> ExtractNested(INamedTypeSymbol type, bool useCamelCase)
    {
        var list = new List<PropInfo>();
        foreach (var m in type.GetMembers())
        {
            if (m is not IPropertySymbol p)
                continue;
            if (p.DeclaredAccessibility != Accessibility.Public || p.IsStatic || p.IsIndexer)
                continue;
            if (p.GetMethod is null || (p.IsReadOnly && p.SetMethod is null))
                continue;
            if (HasYamlIgnore(p))
                continue;
            var nestedConverter = GetYamlConverter(p);
            var (k, isNullable, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(p.Type);
            if (nestedConverter is not null)
                k = "string";
            if (k is null)
                continue;
            string? ekt = null,
                etf = null;
            ImmutableArray<PropInfo> np2 = default;
            if (k is "list" or "array")
            {
                if (p.Type is INamedTypeSymbol nts && nts.TypeArguments.Length == 1)
                {
                    var et = nts.TypeArguments[0];
                    var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(et);
                    ekt = ek;
                    etf = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek ?? "string", et);
                }
                else if (p.Type is IArrayTypeSymbol ats)
                {
                    var et = ats.ElementType;
                    var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(et);
                    ekt = ek;
                    etf = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek ?? "string", et);
                }
            }
            else if (k is "object" && p.Type is INamedTypeSymbol o2)
            {
                np2 = ExtractNested(o2, useCamelCase);
            }
            var nestedJsonName =
                GetYamlKey(p)
                ?? (useCamelCase ? PicoSerDe.Gen.GenInfrastructure.ToCamelCase(p.Name) : p.Name);
            list.Add(
                new PropInfo(
                    p.Name,
                    nestedJsonName,
                    k,
                    p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ekt,
                    etf,
                    isNullable,
                    np2,
                    null,
                    null,
                    nestedConverter,
                    GetYamlDateTimeFormat(p)
                )
            );
        }
        return list.ToImmutableArray();
    }

    // P7: Collect shared nested types to avoid code duplication (inner helper pattern)
    private static void CollectNestedTypes(
        TypeInfo type,
        Dictionary<string, ImmutableArray<PropInfo>> nestedTypes
    )
    {
        foreach (var prop in type.Props)
        {
            if (prop.Tk == "object" && !string.IsNullOrEmpty(prop.Tf))
                AddNestedType(prop.Tf, prop.NestedProps, nestedTypes);
            if (
                (prop.Tk == "list" || prop.Tk == "array")
                && prop.ElemTk == "object"
                && !string.IsNullOrEmpty(prop.ElemTf)
            )
                AddNestedType(prop.ElemTf!, prop.NestedProps, nestedTypes);
            if (prop.Tk == "dict" && prop.ElemTk == "object" && !string.IsNullOrEmpty(prop.ElemTf))
                AddNestedType(prop.ElemTf!, prop.NestedProps, nestedTypes);
        }
    }

    private static void AddNestedType(
        string fullName,
        ImmutableArray<PropInfo> props,
        Dictionary<string, ImmutableArray<PropInfo>> nestedTypes
    )
    {
        if (props.IsDefaultOrEmpty)
            return;
        if (nestedTypes.ContainsKey(fullName))
            return;
        nestedTypes[fullName] = props;

        foreach (var np in props)
        {
            if (np.Tk == "object" && !string.IsNullOrEmpty(np.Tf))
                AddNestedType(np.Tf, np.NestedProps, nestedTypes);
            if (
                (np.Tk == "list" || np.Tk == "array")
                && np.ElemTk == "object"
                && !string.IsNullOrEmpty(np.ElemTf)
            )
                AddNestedType(np.ElemTf!, np.NestedProps, nestedTypes);
            if (np.Tk == "dict" && np.ElemTk == "object" && !string.IsNullOrEmpty(np.ElemTf))
                AddNestedType(np.ElemTf!, np.NestedProps, nestedTypes);
        }
    }

    private static bool HasConverterProp(ImmutableArray<PropInfo> props)
    {
        foreach (var p in props)
            if (p.ConverterType is not null)
                return true;
        return false;
    }

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> ts)
    {
        var s = new HashSet<string>();

        // P7: Collect nested types for deduplication
        var nestedTypes = new Dictionary<string, ImmutableArray<PropInfo>>();
        foreach (var t in ts)
            CollectNestedTypes(t, nestedTypes);

        // P7: Generate inner helpers for shared nested types
        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var sn = PicoSerDe.Gen.GenInfrastructure.ShortName(cleanName);
            spc.AddSource(
                $"{sn}_YamlInner.g.cs",
                SourceText.From(GenerateInnerHelper(cleanName, sn, props), Encoding.UTF8)
            );
        }

        foreach (var t in ts)
            if (s.Add(t.Fqn))
                spc.AddSource($"{t.Name}_Yaml.g.cs", SourceText.From(Gen(t), Encoding.UTF8));
    }

    private static string GenerateInnerHelper(
        string fullName,
        string shortName,
        ImmutableArray<PropInfo> props
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

        // Serialize helper
        sb.Append("    internal static void Serialize(YamlWriter yw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        yw.WriteStartMapping();");
        foreach (var prop in props)
        {
            sb.Append("        yw.WritePropertyName(\"");
            sb.Append(prop.Jn);
            sb.AppendLine("\"u8);");
            EmitSerializeInline(sb, prop, "value." + prop.Name, "        ");
        }
        sb.AppendLine("        yw.WriteEndMapping();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Deserialize helper
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
            sb.Append(np.Jn);
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

    private static void EmitSerializeInline(
        StringBuilder s,
        PropInfo p,
        string accessor,
        string ind
    )
    {
        if (p.ConverterType is not null)
        {
            // Converters in inner helpers: create temp buffer, write, output as string
            s.Append(ind);
            s.Append("{ var __tmp = new ArrayBufferWriter<byte>(); var __cnv = new ");
            s.Append(p.ConverterType);
            s.AppendLine("();");
            s.Append(ind);
            s.Append("  __cnv.Write(__tmp, ");
            s.Append(accessor);
            s.AppendLine(");");
            s.Append(ind);
            s.AppendLine("  yw.WriteString(__tmp.WrittenSpan); }");
            return;
        }
        switch (p.Tk)
        {
            case "string":
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine("));");
                break;
            case "int32":
            case "int64":
                s.Append(ind);
                s.Append("yw.WriteNumber(");
                s.Append(accessor);
                s.AppendLine(");");
                break;
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
            case "timeonly":
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
                if (p.NestedProps.Length > 0 && !HasConverterProp(p.NestedProps))
                {
                    var sn = PicoSerDe.Gen.GenInfrastructure.ShortName(p.Tf!);
                    s.Append(ind);
                    s.Append(sn);
                    s.Append("YamlInner.Serialize(yw, ");
                    s.Append(accessor);
                    s.AppendLine(");");
                }
                else if (p.NestedProps.Length > 0)
                {
                    // Has converter — inline serialization
                    s.Append(ind);
                    s.AppendLine("yw.WriteStartMapping();");
                    foreach (var np in p.NestedProps)
                    {
                        s.Append(ind);
                        s.Append("    yw.WritePropertyName(\"");
                        s.Append(np.Jn);
                        s.AppendLine("\"u8);");
                        EmitSerializeInline(s, np, accessor + "." + np.Name, ind + "    ");
                    }
                    s.Append(ind);
                    s.AppendLine("yw.WriteEndMapping();");
                }
                else
                {
                    s.Append(ind);
                    s.AppendLine("yw.WriteNull();");
                }
                break;
            default:
                s.Append(ind);
                s.Append("yw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(accessor);
                s.AppendLine(".ToString()));");
                break;
        }
    }

    private static void EmitDeserializeInline(StringBuilder s, PropInfo p, string tgt, string pad)
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
            s.AppendLine(" = __cnv.Read(ref reader);");
            return;
        }
        switch (p.Tk)
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
                s.AppendLine(" = DateOnly.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
                break;
            case "timeonly":
                s.Append(pad);
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(" = TimeOnly.Parse(Encoding.UTF8.GetString(reader.ValueSpan));");
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
                s.Append(p.Tf);
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
                if (p.NestedProps.Length > 0)
                {
                    var sn = PicoSerDe.Gen.GenInfrastructure.ShortName(p.Tf!);
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.Append(" = ");
                    s.Append(sn);
                    s.AppendLine("YamlInner.Deserialize(ref reader);");
                }
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
        if (!string.IsNullOrEmpty(t.Ns))
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(t.Ns);
            sb.AppendLine(";");
        }
        sb.AppendLine();
        sb.AppendLine();

        // Serializer
        sb.Append("file sealed class ");
        sb.Append(t.Name);
        sb.Append("_YS : ISerializer<");
        sb.Append(t.Name);
        sb.AppendLine("> {");
        sb.Append("    public void Serialize(IBufferWriter<byte> w, ");
        sb.Append(t.Name);
        sb.AppendLine(" v) {");
        sb.AppendLine("        var yw = new YamlWriter(w);");
        foreach (var p in t.Props)
        {
            EmitSerialize(sb, p, "v", "        ");
        }
        sb.AppendLine("    } }");

        // Deserializer
        sb.Append("file sealed class ");
        sb.Append(t.Name);
        sb.Append("_YD : IDeserializer<");
        sb.Append(t.Name);
        sb.AppendLine("> {");
        sb.Append("    public ");
        sb.Append(t.Name);
        sb.AppendLine(" Deserialize(ReadOnlySpan<byte> d) {");
        sb.AppendLine("        var r = new YamlReader(d);");
        sb.Append("        var o = new ");
        sb.Append(t.Name);
        sb.AppendLine("();");
        sb.AppendLine("        if (!r.Read()) return o;");
        sb.AppendLine("        while (true) {");
        sb.AppendLine("            if (r.TokenType != TokenType.PropertyName) {");
        sb.AppendLine("                if (!r.Read()) break;");
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine("            var k = r.KeySpan;");
        for (int i = 0; i < t.Props.Length; i++)
        {
            var p = t.Props[i];
            sb.Append("            ");
            sb.Append(i == 0 ? "if" : "else if");
            sb.Append(" (TextHelpers.Eq(k, \"");
            sb.Append(p.Jn);
            sb.AppendLine("\"u8)) {");
            EmitDeserialize(sb, p, "o", "                ");
            sb.AppendLine("            }");
        }
        sb.AppendLine("        }");
        sb.AppendLine("        return o;");
        sb.AppendLine("    } }");

        // Registration
        sb.Append("file static class ");
        sb.Append(t.Name);
        sb.Append("_YR { [ModuleInitializer] internal static void R() { YamlSerializer.Register<");
        sb.Append(t.Name);
        sb.Append(">(new ");
        sb.Append(t.Name);
        sb.Append("_YS(), new ");
        sb.Append(t.Name);
        sb.AppendLine("_YD()); } }");
        return sb.ToString();
    }

    private static void EmitSerialize(StringBuilder s, PropInfo p, string target, string ind)
    {
        if (p.ConverterType is not null)
        {
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(p.Jn);
            s.AppendLine("\"u8);");
            s.Append(ind);
            s.Append("var __cnv = new ");
            s.Append(p.ConverterType);
            s.AppendLine("();");
            s.Append(ind);
            s.Append("__cnv.Write(w, ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(");");
            return;
        }

        if (p.Tk is "list" or "array")
        {
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(p.Jn);
            s.AppendLine("\"u8);");
            s.Append(ind);
            s.AppendLine("foreach (var __item in v.");
            s.Append(p.Name);
            s.AppendLine(")");
            s.Append(ind);
            s.AppendLine("{");
            EmitSerializeListElement(s, p, ind + "    ");
            s.Append(ind);
            s.AppendLine("}");
        }
        else if (p.Tk is "object")
        {
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(p.Jn);
            s.AppendLine("\"u8);");
            // P7: use inner helper for nested objects if available
            if (p.NestedProps.Length > 0)
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.ShortName(p.Tf!);
                s.Append(ind);
                s.Append(sn);
                s.Append("YamlInner.Serialize(yw, ");
                s.Append(target);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine(");");
            }
            else
            {
                s.Append(ind);
                s.AppendLine("yw.WriteStartMapping();");
                s.Append(ind);
                s.AppendLine("yw.WriteEndMapping();");
            }
        }
        else if (p.Tk is "dict")
        {
            s.Append(ind);
            s.Append("yw.WritePropertyName(\"");
            s.Append(p.Jn);
            s.AppendLine("\"u8);");
            s.Append(ind);
            s.AppendLine("yw.WriteStartMapping();");
            s.Append(ind);
            s.Append("foreach (var __kvp in ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(")");
            s.Append(ind);
            s.AppendLine("{");
            s.Append(ind);
            s.AppendLine("    yw.WritePropertyName(__kvp.Key);");
            if (p.ElemTk == "int32")
            {
                s.Append(ind);
                s.AppendLine("    yw.WriteNumber(__kvp.Value);");
            }
            else
            {
                s.Append(ind);
                s.AppendLine("    yw.WriteString(__kvp.Value.ToString());");
            }
            s.Append(ind);
            s.AppendLine("}");
            s.Append(ind);
            s.AppendLine("yw.WriteEndMapping();");
        }
        else if (p.IsNullable)
        {
            s.Append(ind);
            s.Append("if (");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            s.AppendLine(".HasValue)");
            s.Append(ind);
            s.AppendLine("{");
            s.Append(ind);
            s.Append("    yw.WritePropertyName(\"");
            s.Append(p.Jn);
            s.AppendLine("\"u8);");
            string valAccessor = target + "." + p.Name + ".Value";
            switch (p.Tk)
            {
                case "string":
                    s.Append(ind);
                    s.Append("    yw.WriteString(Encoding.UTF8.GetBytes(");
                    s.Append(valAccessor);
                    s.AppendLine("));");
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
            s.Append(p.Jn);
            s.AppendLine("\"u8);");
            switch (p.Tk)
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

    private static void EmitSerializeListElement(StringBuilder s, PropInfo p, string ind)
    {
        switch (p.ElemTk)
        {
            case "string":
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item));");
                break;
            default:
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item.ToString()));");
                break;
        }
    }

    private static void EmitDeserialize(StringBuilder s, PropInfo p, string tgt, string pad)
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
            s.Append(pad);
            s.AppendLine("if (!r.Read()) break;");
            return;
        }

        if (p.Tk is "list" or "array")
        {
            s.Append(pad);
            s.Append("var __tmpList = new System.Collections.Generic.List<");
            s.Append(p.ElemTf ?? "object");
            s.AppendLine(">(16);");
            s.Append(pad);
            s.AppendLine("while (r.Read() && r.TokenType == TokenType.String) {");
            EmitDeserializeListElementTemp(s, p, pad + "    ");
            s.Append(pad);
            s.AppendLine("}");
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" = __tmpList");
            if (p.Tk == "array")
                s.Append(".ToArray()");
            s.AppendLine(";");
        }
        else if (p.Tk is "object" && p.NestedProps.Length > 0)
        {
            // P7: use inner helper for nested objects
            var sn = PicoSerDe.Gen.GenInfrastructure.ShortName(p.Tf!);
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" = ");
            s.Append(sn);
            s.AppendLine("YamlInner.Deserialize(ref r);");
        }
        else if (p.Tk is "dict")
        {
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" ??= new System.Collections.Generic.Dictionary<");
            s.Append(p.KeyTf ?? "string");
            s.Append(", ");
            s.Append(p.ElemTf ?? "int");
            s.AppendLine(">();");
            s.Append(pad);
            s.AppendLine("if (r.Read() && r.TokenType == TokenType.ObjectStart) {");
            s.Append(pad);
            s.AppendLine("    while (r.Read() && r.TokenType == TokenType.PropertyName) {");
            s.Append(pad);
            s.AppendLine("        var __dk = Encoding.UTF8.GetString(r.KeySpan);");
            if (p.ElemTk == "int32")
            {
                s.Append(pad);
                s.AppendLine("        r.TryGetInt32(out var __dv);");
                s.Append(pad);
                s.Append("        ");
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine("[__dk] = __dv;");
            }
            else
            {
                s.Append(pad);
                s.Append("        ");
                s.Append(tgt);
                s.Append('.');
                s.Append(p.Name);
                s.AppendLine("[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
            }
            s.Append(pad);
            s.AppendLine("    }");
            s.Append(pad);
            s.AppendLine("} // exits at ObjectEnd");
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
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = long.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "float64":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = double.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "boolean":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
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
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = System.DateOnly.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "timeonly":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = System.TimeOnly.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "timespan":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = System.TimeSpan.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
                    break;
                case "enum":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.Append(" = System.Enum.Parse<");
                    s.Append(p.Tf);
                    s.AppendLine(">(Encoding.UTF8.GetString(r.ValueSpan));");
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
            s.Append(pad);
            s.AppendLine("if (!r.Read()) break;");
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
        ImmutableArray<PropInfo> NestedProps = default,
        string? KeyTk = null,
        string? KeyTf = null,
        string? ConverterType = null,
        string? DateTimeFormat = null
    );
}
