namespace PicoIni.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class IniSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var typeProviders = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidate(node),
                transform: static (ctx, _) => Transform(ctx))
            .Where(static t => t.HasValue)
            .Select(static (t, _) => t!.Value);

        context.RegisterSourceOutput(
            typeProviders.Collect(),
            static (spc, types) => GenerateAll(spc, types));
    }

    private static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: var expr }) return false;
        SimpleNameSyntax? name = expr switch
        {
            MemberAccessExpressionSyntax { Name: var n } => n,
            MemberBindingExpressionSyntax { Name: var n } => n,
            _ => null
        };
        string? m = name switch
        {
            GenericNameSyntax gn => gn.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => null
        };
        return m is "Serialize" or "SerializeToUtf8Bytes" or "Deserialize";
    }

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method) return null;
        if (method.ContainingType.Name != "IniSerializer"
            || method.ContainingType.ContainingNamespace?.ToDisplayString() != "PicoIni")
            return null;
        if (method.TypeArguments.Length != 1) return null;
        var typeArg = method.TypeArguments[0];
        if (typeArg.SpecialType != SpecialType.None) return null;
        if (typeArg is not INamedTypeSymbol namedType) return null;

        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>") ns = "";
        var properties = new List<PropInfo>();

        foreach (var member in namedType.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.IsStatic || prop.IsIndexer) continue;
            if (prop.IsReadOnly && prop.SetMethod is null) continue;
            if (prop.GetMethod is null) continue;
            if (HasIniIgnore(prop)) continue;

            var (typeKind, isNullable, innerTypeSymbol) = TypeKindResolver.Resolve(prop.Type);
            if (typeKind is null) continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? et = prop.Type switch
                {
                    IArrayTypeSymbol arr => arr.ElementType,
                    INamedTypeSymbol nts => nts.TypeArguments[0],
                    _ => null
                };
                if (et is null) continue;
                var (ek, _, _) = TypeKindResolver.Resolve(et);
                if (ek is null) continue;
                elementTypeKind = ek;
                elementTypeName = TypeKindResolver.MapTypeName(ek, et);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol nd && nd.TypeArguments.Length == 2)
                {
                    var (kk, _, _) = TypeKindResolver.Resolve(nd.TypeArguments[0]);
                    var (vk, _, _) = TypeKindResolver.Resolve(nd.TypeArguments[1]);
                    if (kk is null || vk is null) continue;
                    keyTypeKind = kk;
                    keyTypeName = TypeKindResolver.MapTypeName(kk, nd.TypeArguments[0]);
                    elementTypeKind = vk;
                    elementTypeName = TypeKindResolver.MapTypeName(vk, nd.TypeArguments[1]);
                }
                else continue;
            }

            properties.Add(new PropInfo(
                prop.Name,
                GetIniKey(prop) ?? prop.Name,
                typeKind,
                prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                isNullable,
                elementTypeKind, elementTypeName,
                keyTypeKind, keyTypeName,
                GetIniSection(prop),
                GetIniConverter(prop)));
        }

        return new TypeInfo(
            namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns, namedType.Name,
            properties.ToImmutableArray());
    }

    private static string? GetIniKey(IPropertySymbol prop)
    {
        foreach (var a in prop.GetAttributes())
            if (a.AttributeClass?.Name == "IniKeyAttribute"
                && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && a.ConstructorArguments.Length == 1
                && a.ConstructorArguments[0].Value is string n)
                return n;
        return null;
    }

    private static string? GetIniSection(IPropertySymbol prop)
    {
        foreach (var a in prop.GetAttributes())
            if (a.AttributeClass?.Name == "IniSectionAttribute"
                && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && a.ConstructorArguments.Length == 1
                && a.ConstructorArguments[0].Value is string n)
                return n;
        return null;
    }

    private static bool HasIniIgnore(IPropertySymbol prop)
    {
        foreach (var a in prop.GetAttributes())
            if (a.AttributeClass?.Name == "IniIgnoreAttribute"
                && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni")
                return true;
        return false;
    }

    private static string? GetIniConverter(IPropertySymbol prop)
    {
        foreach (var a in prop.GetAttributes())
            if (a.AttributeClass?.Name == "IniConverterAttribute"
                && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && a.ConstructorArguments.Length == 1
                && a.ConstructorArguments[0].Value is INamedTypeSymbol ct)
                return ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return null;
    }

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var seen = new HashSet<string>();
        foreach (var t in types)
        {
            if (!seen.Add(t.FullyQualifiedName)) continue;
            spc.AddSource($"{t.Name}_IniSerializer.g.cs",
                SourceText.From(GenerateTypeCode(t), Encoding.UTF8));
        }
    }

    private static string GenerateTypeCode(TypeInfo type)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using System; using System.Buffers; using System.Text;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using PicoSerDe.Abs; using PicoIni;");
        if (!string.IsNullOrEmpty(type.Namespace))
        { sb.AppendLine(); sb.Append("namespace "); sb.Append(type.Namespace); sb.AppendLine(";"); }
        sb.AppendLine();
        sb.AppendLine("file static class __IniHelp {");
        sb.AppendLine("    internal static bool Eq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) {");
        sb.AppendLine("        if (a.Length != b.Length) return false;");
        sb.AppendLine("        for (int i = 0; i < a.Length; i++) { byte x = a[i], y = b[i]; if (x != y && (x | 0x20) != (y | 0x20)) return false; }");
        sb.AppendLine("        return true; } }");

        // Serializer
        sb.Append("file readonly struct "); sb.Append(type.Name);
        sb.Append("IniSerializer : ISerializer<"); sb.Append(type.Name); sb.AppendLine("> {");
        sb.Append("    public void Serialize(IBufferWriter<byte> writer, "); sb.Append(type.Name); sb.AppendLine(" value) {");
        sb.AppendLine("        var iw = new IniWriter(writer);");
        bool firstSection = true;
        foreach (var p in type.Properties)
        {
            if (p.TypeKind is "object")
            {
                if (!firstSection) sb.AppendLine("        iw.WriteBlankLine();");
                var sn = p.SectionName ?? p.JsonName;
                sb.Append("        iw.WriteSection(\""); sb.Append(sn); sb.AppendLine("\");");
                sb.Append("        ");
                EmitSerializeObject(sb, p, "value");
                firstSection = false;
            }
            else
            {
                sb.Append("        iw.WriteKeyValue(\""); sb.Append(p.JsonName); sb.Append("\", ");
                EmitValue(sb, p, $"value.{p.Name}");
                sb.AppendLine(");");
            }
        }
        sb.AppendLine("    } }");

        // Deserializer
        sb.Append("file readonly struct "); sb.Append(type.Name);
        sb.Append("IniDeserializer : IDeserializer<"); sb.Append(type.Name); sb.AppendLine("> {");
        sb.Append("    public "); sb.Append(type.Name); sb.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        sb.AppendLine("        var reader = new IniReader(data);");
        sb.Append("        var obj = new "); sb.Append(type.Name); sb.AppendLine("();");
        sb.AppendLine("        while (reader.Read()) {");

        // Top-level key-value properties
        var topList = new List<PropInfo>();
        foreach (var p in type.Properties) if (p.TypeKind != "object") topList.Add(p);
        var topProps = topList.ToArray();
        if (topProps.Length > 0)
        {
            for (int i = 0; i < topProps.Length; i++)
            {
                var kw = i == 0 ? "if" : "else if";
                sb.Append("            "); sb.Append(kw); sb.Append(" (reader.TokenType == IniTokenType.Key && __IniHelp.Eq(reader.Key, \"");
                sb.Append(topProps[i].JsonName); sb.AppendLine("\"u8)) {");
                EmitDeserAssign(sb, topProps[i], "obj", "                ");
                sb.AppendLine("            }");
            }
            sb.AppendLine("            else if (reader.TokenType == IniTokenType.SectionStart) {");
            sb.AppendLine("                var __sn = reader.SectionName;");
        }
        else
        {
            sb.AppendLine("            if (reader.TokenType == IniTokenType.SectionStart) {");
            sb.AppendLine("                var __sn = reader.SectionName;");
        }

        // Section properties
        var objList = new List<PropInfo>();
        foreach (var p in type.Properties) if (p.TypeKind == "object") objList.Add(p);
        var objProps = objList.ToArray();
        for (int i = 0; i < objProps.Length; i++)
        {
            var kw = i == 0 ? "if" : "else if";
            var sn = objProps[i].SectionName ?? objProps[i].JsonName;
            sb.Append("                "); sb.Append(kw); sb.Append(" (__IniHelp.Eq(__sn, \"");
            sb.Append(sn); sb.AppendLine("\"u8)) {");
            sb.Append("                    obj."); sb.Append(objProps[i].Name); sb.Append(" ??= new ");
            sb.Append(objProps[i].TypeFullName); sb.AppendLine("();");
            sb.AppendLine("                    while (reader.Read() && reader.TokenType != IniTokenType.SectionStart) {");
            sb.AppendLine("                        if (reader.TokenType != IniTokenType.Key) continue;");
            var sp = GetSectionProps(objProps[i]);
            for (int j = 0; j < sp.Length; j++)
            {
                var kw2 = j == 0 ? "if" : "else if";
                sb.Append("                        "); sb.Append(kw2); sb.Append(" (__IniHelp.Eq(reader.Key, \"");
                sb.Append(sp[j].JsonName); sb.Append("\"u8)) { ");
                EmitDeserAssign(sb, sp[j], $"obj.{objProps[i].Name}", "                            ");
                sb.AppendLine(" }");
            }
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
        }
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        return obj;");
        sb.AppendLine("    } }");

        // Registration
        sb.AppendLine("file static class __Reg {");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Register() {");
        sb.Append("        IniSerializer.Register<"); sb.Append(type.Name); sb.Append(">(new ");
        sb.Append(type.Name); sb.Append("IniSerializer(), new "); sb.Append(type.Name); sb.AppendLine("IniDeserializer());");
        sb.AppendLine("    } }");
        return sb.ToString();
    }

    private static PropInfo[] GetSectionProps(PropInfo objProp) => System.Array.Empty<PropInfo>();

    private static void EmitSerializeObject(StringBuilder sb, PropInfo p, string accessor)
    {
        // Section properties are handled at call site
    }

    private static void EmitValue(StringBuilder sb, PropInfo p, string accessor)
    {
        switch (p.TypeKind)
        {
            case "string": sb.Append("Encoding.UTF8.GetBytes("); sb.Append(accessor); sb.Append(')'); break;
            case "int32": case "int64": case "float64": case "boolean": sb.Append(accessor); break;
            case "decimal": sb.Append(accessor); sb.Append(".ToString(System.Globalization.CultureInfo.InvariantCulture)"); break;
            case "datetime": sb.Append(accessor); sb.Append(".ToString(\"O\")"); break;
            case "dateonly": sb.Append(accessor); sb.Append(".ToString(\"O\")"); break;
            case "timeonly": sb.Append(accessor); sb.Append(".ToString(\"O\")"); break;
            case "timespan": sb.Append(accessor); sb.Append(".ToString()"); break;
            case "guid": case "enum": sb.Append(accessor); sb.Append(".ToString()"); break;
            case "list": case "array": sb.Append("string.Join(\",\", "); sb.Append(accessor); sb.Append(')'); break;
            case "dict": sb.Append("string.Join(\",\", System.Linq.Enumerable.Select("); sb.Append(accessor); sb.Append(", kvp => kvp.Key + \"=\" + kvp.Value))"); break;
            default: sb.Append(accessor); sb.Append(".ToString()"); break;
        }
    }

    private static void EmitDeserAssign(StringBuilder sb, PropInfo p, string target, string indent)
    {
        if (p.ConverterTypeFullName is not null)
        {
            sb.Append(indent); sb.Append("var __c = new "); sb.Append(p.ConverterTypeFullName); sb.AppendLine("();");
            sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name); sb.Append(" = __c.Read(reader.ValueSpan);");
            return;
        }
        switch (p.TypeKind)
        {
            case "string":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" = Encoding.UTF8.GetString(reader.ValueSpan);"); break;
            case "int32":
                sb.Append(indent); sb.AppendLine("reader.TryGetInt32(out var __v);");
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name); sb.AppendLine(" = __v;"); break;
            case "int64":
                sb.Append(indent); sb.AppendLine("reader.TryGetInt64(out var __v);");
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name); sb.AppendLine(" = __v;"); break;
            case "float64":
                sb.Append(indent); sb.AppendLine("reader.TryGetFloat64(out var __v);");
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name); sb.AppendLine(" = __v;"); break;
            case "boolean":
                sb.Append(indent); sb.AppendLine("reader.TryGetBool(out var __v);");
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name); sb.AppendLine(" = __v;"); break;
            case "decimal":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" = decimal.Parse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"); break;
            case "datetime":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" = DateTime.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "guid":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" = Guid.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "enum":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.Append(" = Enum.Parse<"); sb.Append(p.TypeFullName); sb.AppendLine(">(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "list":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" ??= new System.Collections.Generic.List<");
                sb.Append(p.ElementTypeName); sb.AppendLine(">();");
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(".AddRange(Encoding.UTF8.GetString(reader.ValueSpan).Split(',').Select(s => ");
                EmitParseElement(sb, p);
                sb.AppendLine("));"); break;
            case "array":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.Append(" = Encoding.UTF8.GetString(reader.ValueSpan).Split(',').Select(s => ");
                EmitParseElement(sb, p);
                sb.AppendLine(").ToArray();"); break;
            case "dict":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" ??= new System.Collections.Generic.Dictionary<");
                sb.Append(p.KeyTypeName); sb.Append(','); sb.Append(p.ElementTypeName); sb.AppendLine(">();");
                sb.Append(indent); sb.AppendLine("foreach (var __pair in Encoding.UTF8.GetString(reader.ValueSpan).Split(',')) {");
                sb.Append(indent); sb.AppendLine("    var __eq = __pair.IndexOf('=');");
                sb.Append(indent); sb.AppendLine("    if (__eq >= 0) {");
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine("[__pair[..__eq]] = __pair[(__eq+1)..]; } }"); break;
            case "dateonly":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" = DateOnly.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "timeonly":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" = TimeOnly.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "timespan":
                sb.Append(indent); sb.Append(target); sb.Append('.'); sb.Append(p.Name);
                sb.AppendLine(" = TimeSpan.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            default:
                sb.Append(indent); sb.Append("// unhandled: "); sb.Append(p.TypeKind); break;
        }
    }

    private static void EmitParseElement(StringBuilder sb, PropInfo p)
    {
        switch (p.ElementTypeKind)
        {
            case "string": sb.Append("s"); break;
            case "int32": sb.Append("int.Parse(s)"); break;
            case "int64": sb.Append("long.Parse(s)"); break;
            case "float64": sb.Append("double.Parse(s)"); break;
            case "boolean": sb.Append("bool.Parse(s)"); break;
            default: sb.Append("s"); break;
        }
    }
}

internal readonly record struct TypeInfo(
    string FullyQualifiedName, string Namespace, string Name,
    ImmutableArray<PropInfo> Properties);

internal readonly record struct PropInfo(
    string Name, string JsonName, string TypeKind, string TypeFullName,
    bool IsNullable,
    string? ElementTypeKind, string? ElementTypeName,
    string? KeyTypeKind, string? KeyTypeName,
    string? SectionName, string? ConverterTypeFullName);
