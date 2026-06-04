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
        var typeProviders = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (n, _) => IsCandidate(n),
                transform: static (ctx, _) => Transform(ctx)
            )
            .Where(static t => t is not null)
            .Select(static (t, _) => t!.Value);

        context.RegisterSourceOutput(
            typeProviders.Collect(),
            static (spc, types) => GenerateAll(spc, types)
        );
    }

    private static bool IsCandidate(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx) =>
        PicoSerDe.Gen.GenInfrastructure.TransformType(ctx, Config, Attrs);

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
        EmitPropertyDispatch(s, sorted, "k", "o", "                ", "                    ");
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
        s.Append("        var o = new ");
        s.Append(t.Name);
        s.AppendLine("();");
        s.AppendLine("        while (r.Read()) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        var scalarProps = t
            .Properties.Where(x => x.TypeKind != "object" && x.TypeKind != "dict")
            .ToImmutableArray();
        EmitPropertyDispatch(s, scalarProps, "k", "o", "                ", "                    ");
        s.AppendLine("            }");
        var objProps = t.Properties.Where(x => x.TypeKind == "object").ToImmutableArray();
        var dictProps = t.Properties.Where(x => x.TypeKind == "dict").ToImmutableArray();
        if (objProps.Length > 0 || dictProps.Length > 0)
        {
            s.AppendLine("            else if (r.TokenType == TokenType.ObjectStart) {");
            s.AppendLine("                var tbl = r.TablePath;");
            for (int i = 0; i < objProps.Length; i++)
            {
                s.Append("                ");
                s.Append(i == 0 && dictProps.Length == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(tbl, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(objProps[i].JsonName));
                s.AppendLine("\"u8)) {");
                EmitNestedObjectRead(s, objProps[i], "o", "                    ");
                s.AppendLine("                }");
            }
            for (int i = 0; i < dictProps.Length; i++)
            {
                s.Append("                ");
                s.Append(i == 0 && objProps.Length == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(tbl, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dictProps[i].JsonName));
                s.AppendLine("\"u8)) {");
                EmitDictRead(s, dictProps[i], "o", "                    ");
                s.AppendLine("                }");
            }
            s.AppendLine("            }");
        }
        s.AppendLine("        }");
        s.AppendLine("        return o;");
        s.AppendLine("    } }");

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

    private static void EmitPropertyDispatch(
        StringBuilder s,
        ImmutableArray<PropertyInfo> props,
        string keyVar,
        string target,
        string indent,
        string bodyIndent
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
                EmitDeserializeProp(s, groupProps[i], target, bodyIndent + "        ");
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

    private static void EmitDeserializeProp(StringBuilder s, PropertyInfo p, string tgt, string pad)
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
            return;
        }

        if (p.TypeKind is "list" or "array")
        {
            s.Append(pad);
            s.Append("var __tmpList = new System.Collections.Generic.List<");
            s.Append(p.ElementTypeName ?? "object");
            s.AppendLine(">(16);");
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
