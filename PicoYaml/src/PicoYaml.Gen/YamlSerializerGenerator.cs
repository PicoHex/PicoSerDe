namespace PicoYaml.Gen;

using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class YamlSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "YamlSerializer",
        "PicoYaml",
        "yaml"
    );

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        HasYamlCamelCase,
        GetYamlKey,
        HasYamlIgnore,
        GetYamlConverter,
        GetYamlDateTimeFormat,
        OverrideKindWithStringOnConverter: true
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

        // Merge all pipelines into one output
        var all = usageD
            .Collect()
            .Combine(attrD.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(formatD.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(shortD.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

        ctx.RegisterSourceOutput(all, GenerateAll);
    }

    private static bool IsC(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    private static TypeInfo? Tf(GeneratorSyntaxContext ctx)
    {
        var info = PicoSerDe.Gen.GenInfrastructure.TransformType(ctx, Config, Attrs);
        if (info is not { } ti)
            return null;

        // Detect [YamlTag] on the target type
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method
            || method.TypeArguments.Length != 1
            || method.TypeArguments[0] is not INamedTypeSymbol namedType
        )
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
        var s = new HashSet<string>();

        var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
        foreach (var t in ts)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedTypes(t, nestedTypes);

        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var sn = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanName);
            spc.AddSource(
                $"{sn}_YamlInner.g.cs",
                SourceText.From(GenerateInnerHelper(cleanName, sn, props), Encoding.UTF8)
            );
        }

        foreach (var t in ts)
            if (s.Add(t.FullyQualifiedName))
                spc.AddSource($"{t.Name}_Yaml.g.cs", SourceText.From(Gen(t), Encoding.UTF8));
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
        foreach (var prop in props)
        {
            sb.Append("        yw.WritePropertyName(\"");
            sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\"u8);");
            EmitSerializeInline(sb, prop, "value." + prop.Name, "        ");
        }
        sb.AppendLine("        yw.WriteEndMapping();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.Append("    internal static void SerializeBlock(YamlWriter yw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        foreach (var prop in props)
        {
            sb.Append("        yw.WritePropertyName(\"");
            sb.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName));
            sb.AppendLine("\"u8);");
            EmitSerializeInline(sb, prop, "value." + prop.Name, "        ");
        }
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
                    s.AppendLine(")");
                    s.Append(ind);
                    s.AppendLine("{");
                    s.Append(ind);
                    s.Append("    yw.WriteSequenceItem(Encoding.UTF8.GetBytes(");
                    if (p.ElementTypeKind == "string")
                        s.Append("__item");
                    else
                        s.Append("__item.ToString()");
                    s.AppendLine("));");
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
                s.AppendLine(")");
                s.Append(ind);
                s.AppendLine("{");
                s.Append(ind);
                s.AppendLine("    yw.WritePropertyName(__kvp.Key);");
                s.Append(ind);
                s.AppendLine("    yw.WriteString(Encoding.UTF8.GetBytes(__kvp.Value.ToString()));");
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
                if (p.ElementTypeKind == "int32")
                {
                    s.Append(pad);
                    s.AppendLine("        reader.TryGetInt32(out var __dv);");
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
                    s.AppendLine("[__dk] = Encoding.UTF8.GetString(reader.ValueSpan);");
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

        sb.Append("file sealed class ");
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

        sb.Append("file sealed class ");
        sb.Append(t.Name);
        sb.Append("_YD : IDeserializer<");
        sb.Append(t.Name);
        sb.AppendLine("> {");
        sb.Append("    public ");
        sb.Append(t.Name);
        sb.AppendLine(" Deserialize(ReadOnlySpan<byte> d) {");
        sb.AppendLine("        var r = new YamlReader(d);");
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
                        sb.Append("null!");
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
            EmitDeserialize(sb, p, "o", "                ");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            else {");
        sb.AppendLine(
            "                // Unknown property — skip its value to avoid infinite loop"
        );
        sb.AppendLine("                if (!r.Read()) break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return o;");
        sb.AppendLine("    } }");
        sb.AppendLine();

        // Streaming (scalar properties only, skip nested objects/dicts)
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
                        sb.Append("null!");
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
        sb.AppendLine(
            "            if (!r.Read()) return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
        );
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
        sb.AppendLine(
            "            else { if (!r.Read()) return r.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput; }"
        );
        sb.AppendLine("        }");
        sb.AppendLine("        result = o;");
        sb.AppendLine("        return ReadStatus.Success;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
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
        sb.Append("YamlSerializer.RegisterStreaming<");
        sb.Append(t.Name);
        sb.Append(">(");
        sb.Append(t.Name);
        sb.AppendLine("_YamlStreaming.DeserializeStreaming); } }");
        return sb.ToString();
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
        }
        else if (p.TypeKind is "object")
        {
            s.Append(ind);
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
                s.Append(sn);
                s.Append(".Serialize(yw, ");
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
        else if (p.TypeKind is "dict")
        {
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
            if (p.ElementTypeKind == "int32")
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
            s.Append(
                "if (PicoYaml.YamlOptions.Current?.DefaultIgnoreCondition == PicoYaml.YamlIgnoreCondition.Never"
            );
            s.Append(" || ");
            s.Append(target);
            s.Append('.');
            s.Append(p.Name);
            if (p.IsNullableReference)
            {
                s.AppendLine(" != null)");
            }
            else
            {
                s.AppendLine(".HasValue)");
            }
            s.Append(ind);
            s.AppendLine("{");
            s.Append(ind);
            s.Append("    yw.WritePropertyName(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.AppendLine("\"u8);");
            string valAccessor = p.IsNullableReference
                ? $"{target}.{p.Name}"
                : $"{target}.{p.Name}.Value";
            switch (p.TypeKind)
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
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item));");
                break;
            default:
                s.Append(ind);
                s.AppendLine("yw.WriteSequenceItem(Encoding.UTF8.GetBytes(__item.ToString()));");
                break;
        }
    }

    private static void EmitDeserialize(StringBuilder s, PropertyInfo p, string tgt, string pad)
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
                s.AppendLine("r.Read(); // skip indentation ObjectStart after PropertyName");
                s.Append(pad);
                s.AppendLine("while (r.Read()) {");
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
                s.AppendLine("while (r.Read() && r.TokenType == TokenType.String) {");
                EmitDeserializeListElementTemp(s, p, pad + "    ");
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
        else if (p.TypeKind is "object" && p.NestedProperties.Length > 0)
        {
            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("YamlInner", p.TypeFullName!);
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(p.Name);
            s.Append(" = ");
            s.Append(sn);
            s.AppendLine(".Deserialize(ref r);");
        }
        else if (p.TypeKind is "dict")
        {
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
            s.AppendLine("if (r.Read() && r.TokenType == TokenType.ObjectStart) {");
            s.Append(pad);
            s.AppendLine("    while (r.Read() && r.TokenType == TokenType.PropertyName) {");
            s.Append(pad);
            s.AppendLine("        var __dk = Encoding.UTF8.GetString(r.KeySpan);");
            if (p.ElementTypeKind == "int32")
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
            switch (p.TypeKind)
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
                case "float32":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = float.Parse(Encoding.UTF8.GetString(r.ValueSpan));");
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
                    s.AppendLine(
                        " = System.DateOnly.ParseExact(Encoding.UTF8.GetString(r.ValueSpan), \"yyyy-MM-dd\", System.Globalization.CultureInfo.InvariantCulture);"
                    );
                    break;
                case "timeonly":
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(
                        " = System.TimeOnly.ParseExact(Encoding.UTF8.GetString(r.ValueSpan), \"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture);"
                    );
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
                    s.Append(p.TypeFullName);
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
}
