namespace PicoIni.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class IniSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var providers = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (n, _) => IsCandidate(n),
            transform: static (ctx, _) => Transform(ctx))
            .Where(static t => t.HasValue)
            .Select(static (t, _) => t!.Value);

        context.RegisterSourceOutput(providers.Collect(),
            static (spc, types) => GenerateAll(spc, types));
    }

    private static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: var e }) return false;
        var name = e switch
        {
            MemberAccessExpressionSyntax { Name: var n } => n,
            MemberBindingExpressionSyntax { Name: var n } => n,
            _ => null
        };
        return name switch
        {
            GenericNameSyntax gn => gn.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => null
        } is "Serialize" or "SerializeToUtf8Bytes" or "Deserialize";
    }

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol m) return null;
        if (m.ContainingType.Name != "IniSerializer" || m.ContainingType.ContainingNamespace?.ToDisplayString() != "PicoIni")
            return null;
        if (m.TypeArguments.Length != 1) return null;

        var t = m.TypeArguments[0];
        if (t.SpecialType != SpecialType.None) return null;
        if (t is not INamedTypeSymbol nt) return null;

        var ns = nt.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>") ns = "";
        var props = new List<PropInfo>();

        foreach (var member in nt.GetMembers())
        {
            if (member is not IPropertySymbol p) continue;
            if (p.DeclaredAccessibility != Accessibility.Public) continue;
            if (p.IsStatic || p.IsIndexer) continue;
            if (p.IsReadOnly && p.SetMethod is null) continue;
            if (p.GetMethod is null) continue;
            if (HasAttr(p, "IniIgnoreAttribute")) continue;

            var (kind, nullable, inner) = TypeKindResolver.Resolve(p.Type);
            if (kind is null) continue;

            string? elemKind = null, elemName = null, keyKind = null, keyName = null;
            ImmutableArray<PropInfo> nested = ImmutableArray<PropInfo>.Empty;

            if (kind is "list" or "array")
            {
                ITypeSymbol? et = p.Type switch { IArrayTypeSymbol a => a.ElementType, INamedTypeSymbol n2 => n2.TypeArguments[0], _ => null };
                if (et is not null)
                {
                    var (ek, _, _) = TypeKindResolver.Resolve(et);
                    if (ek is not null) { elemKind = ek; elemName = TypeKindResolver.MapTypeName(ek, et); }
                }
            }
            else if (kind is "dict" && p.Type is INamedTypeSymbol nd && nd.TypeArguments.Length == 2)
            {
                var (kk, _, _) = TypeKindResolver.Resolve(nd.TypeArguments[0]);
                var (vk, _, _) = TypeKindResolver.Resolve(nd.TypeArguments[1]);
                if (kk is not null && vk is not null)
                {
                    keyKind = kk; keyName = TypeKindResolver.MapTypeName(kk, nd.TypeArguments[0]);
                    elemKind = vk; elemName = TypeKindResolver.MapTypeName(vk, nd.TypeArguments[1]);
                }
            }
            else if (kind is "object" && p.Type is INamedTypeSymbol onts)
            {
                nested = ExtractNested(onts);
            }

            props.Add(new PropInfo(p.Name, GetKey(p) ?? p.Name, kind,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                nullable, elemKind, elemName, keyKind, keyName, GetSection(p), GetConv(p), nested));
        }

        return new TypeInfo(nt.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), ns, nt.Name, props.ToImmutableArray());
    }

    private static string? GetKey(IPropertySymbol p) => GetStrAttr(p, "IniKeyAttribute");
    private static string? GetSection(IPropertySymbol p) => GetStrAttr(p, "IniSectionAttribute");
    private static string? GetStrAttr(IPropertySymbol p, string attrName)
    {
        foreach (var a in p.GetAttributes())
            if (a.AttributeClass?.Name == attrName && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is string s)
                return s;
        return null;
    }
    private static bool HasAttr(IPropertySymbol p, string attrName)
    {
        foreach (var a in p.GetAttributes())
            if (a.AttributeClass?.Name == attrName && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni")
                return true;
        return false;
    }
    private static string? GetConv(IPropertySymbol p)
    {
        foreach (var a in p.GetAttributes())
            if (a.AttributeClass?.Name == "IniConverterAttribute" && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoIni"
                && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is INamedTypeSymbol ct)
                return ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return null;
    }

    private static ImmutableArray<PropInfo> ExtractNested(INamedTypeSymbol t)
    {
        var list = new List<PropInfo>();
        foreach (var m in t.GetMembers())
        {
            if (m is not IPropertySymbol p) continue;
            if (p.DeclaredAccessibility != Accessibility.Public) continue;
            if (p.IsStatic || p.IsIndexer) continue;
            if (p.IsReadOnly && p.SetMethod is null) continue;
            if (p.GetMethod is null) continue;
            if (HasAttr(p, "IniIgnoreAttribute")) continue;
            var (k, n, _) = TypeKindResolver.Resolve(p.Type);
            if (k is null) continue;
            list.Add(new PropInfo(p.Name, GetKey(p) ?? p.Name, k,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                n, null, null, null, null, null, null, ImmutableArray<PropInfo>.Empty));
        }
        return list.ToImmutableArray();
    }

    // ── Code emission ──

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var seen = new HashSet<string>();
        foreach (var t in types)
        {
            if (!seen.Add(t.FullyQualifiedName)) continue;
            spc.AddSource($"{t.Name}_IniSerializer.g.cs", SourceText.From(Gen(t), Encoding.UTF8));
        }
    }

    private static string Gen(TypeInfo type)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;");
        s.AppendLine("using PicoSerDe.Abs; using PicoIni;");
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            s.Append("using "); s.Append(type.Namespace); s.AppendLine(";");
        }
        s.AppendLine();

        // Helper
        s.AppendLine("file static class __IniHelp {");
        s.AppendLine("    internal static bool Eq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) {");
        s.AppendLine("        if (a.Length != b.Length) return false;");
        s.AppendLine("        for (int i = 0; i < a.Length; i++) { byte x = a[i], y = b[i]; if (x != y && (x | 0x20) != (y | 0x20)) return false; }");
        s.AppendLine("        return true; } }");
        s.AppendLine();

        // Serializer
        s.Append("file readonly struct "); s.Append(type.Name); s.Append("IniSerializer : ISerializer<"); s.Append(type.Name); s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, "); s.Append(type.Name); s.AppendLine(" value) {");
        s.AppendLine("        var iw = new IniWriter(writer);");

        // Top-level properties first
        foreach (var p in type.Properties)
        {
            if (p.TypeKind == "object") continue;
            s.Append("        iw.WriteKeyValue(\""); s.Append(p.JsonName); s.Append("\", ");
            WriteValue(s, p, $"value.{p.Name}");
            s.AppendLine(");");
        }

        // Sections after
        bool first = true;
        foreach (var p in type.Properties)
        {
            if (p.TypeKind != "object") continue;
            if (!first) s.AppendLine("        iw.WriteBlankLine();");
            var sn = p.SectionName ?? p.JsonName;
            s.Append("        iw.WriteSection(\""); s.Append(sn); s.AppendLine("\");");
            foreach (var np in p.NestedProperties)
            {
                s.Append("        iw.WriteKeyValue(\""); s.Append(np.JsonName); s.Append("\", ");
                WriteValue(s, np, $"value.{p.Name}.{np.Name}");
                s.AppendLine(");");
            }
            first = false;
        }
        s.AppendLine("    } }");
        s.AppendLine();

        var top = new List<PropInfo>();
        foreach (var p in type.Properties) if (p.TypeKind != "object") top.Add(p);
        var sec = new List<PropInfo>();
        foreach (var p in type.Properties) if (p.TypeKind == "object") sec.Add(p);

        // Deserializer
        s.Append("file readonly struct "); s.Append(type.Name); s.Append("IniDeserializer : IDeserializer<"); s.Append(type.Name); s.AppendLine("> {");
        s.Append("    public "); s.Append(type.Name); s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.AppendLine("        var reader = new IniReader(data);");
        s.Append("        var obj = new "); s.Append(type.Name); s.AppendLine("();");
        if (sec.Count > 0) s.AppendLine("        int __sec = -1;");
        s.AppendLine("        while (reader.Read()) {");
        s.AppendLine("            if (reader.TokenType == IniTokenType.Key) {");

        // Top-level key matching
        if (top.Count > 0)
        {
            for (int i = 0; i < top.Count; i++)
            {
                s.Append(i == 0 ? "                if" : "                else if");
                s.Append(" (");
                if (sec.Count > 0) s.Append("__sec < 0 && ");
                s.Append("__IniHelp.Eq(reader.Key, \"");
                s.Append(top[i].JsonName); s.AppendLine("\"u8)) {");
                EmitRead(s, top[i], "obj", "                    ");
                s.AppendLine("                }");
            }
        }
        if (sec.Count > 0)
        {
            // Section key matching (when inside a section)
            for (int si = 0; si < sec.Count; si++)
            {
                foreach (var np in sec[si].NestedProperties)
                {
                    s.Append("                else if (__sec == "); s.Append(si); s.Append(" && __IniHelp.Eq(reader.Key, \"");
                    s.Append(np.JsonName); s.Append("\"u8)) { ");
                    EmitRead(s, np, $"obj.{sec[si].Name}", "");
                    s.AppendLine(" }");
                }
            }
        }
        s.AppendLine("            }");
        if (sec.Count > 0)
        {
            s.AppendLine("            else if (reader.TokenType == IniTokenType.SectionStart) {");
            s.AppendLine("                __sec = -1;");

            for (int i = 0; i < sec.Count; i++)
            {
                var sn = sec[i].SectionName ?? sec[i].JsonName;
                s.Append("                ");
                s.Append(i == 0 ? "if" : "else if");
                s.Append(" (__IniHelp.Eq(reader.SectionName, \"");
                s.Append(sn); s.AppendLine("\"u8)) {");
                s.Append("                    obj."); s.Append(sec[i].Name); s.Append(" ??= new ");
                s.Append(sec[i].TypeFullName); s.AppendLine("();");
                s.Append("                    __sec = "); s.Append(i); s.AppendLine(";");
                s.AppendLine("                }");
            }
            s.AppendLine("            }");
        }
        s.AppendLine("        }");
        s.AppendLine("        return obj;");
        s.AppendLine("    } }");
        s.AppendLine();

        // Registration
        s.Append("file static class "); s.Append(type.Name); s.AppendLine("__Reg {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        IniSerializer.Register<"); s.Append(type.Name); s.Append(">(new ");
        s.Append(type.Name); s.Append("IniSerializer(), new "); s.Append(type.Name); s.AppendLine("IniDeserializer());");
        s.AppendLine("    } }");
        return s.ToString();
    }

    private static void WriteValue(StringBuilder s, PropInfo p, string acc)
    {
        switch (p.TypeKind)
        {
            case "string": s.Append("Encoding.UTF8.GetBytes("); s.Append(acc); s.Append(')'); break;
            case "int32": case "int64": case "float64": case "boolean": s.Append(acc); break;
            case "decimal": s.Append(acc); s.Append(".ToString(System.Globalization.CultureInfo.InvariantCulture)"); break;
            case "datetime": case "dateonly": case "timeonly": s.Append(acc); s.Append(".ToString(\"O\")"); break;
            case "timespan": s.Append(acc); s.Append(".ToString()"); break;
            case "guid": case "enum": s.Append(acc); s.Append(".ToString()"); break;
            case "list": case "array": s.Append("string.Join(\",\", "); s.Append(acc); s.Append(')'); break;
            default: s.Append(acc); s.Append(".ToString()"); break;
        }
    }

    private static void EmitRead(StringBuilder s, PropInfo p, string target, string pad)
    {
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(pad); s.Append("var __c = new "); s.Append(p.ConverterTypeFullName); s.AppendLine("();");
            s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name); s.AppendLine(" = __c.Read(reader.ValueSpan);");
            return;
        }
        switch (p.TypeKind)
        {
            case "string":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.AppendLine(" = Encoding.UTF8.GetString(reader.ValueSpan);"); break;
            case "int32":
                s.Append(pad); s.AppendLine("reader.TryGetInt32(out var __v);");
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name); s.AppendLine(" = __v;"); break;
            case "int64":
                s.Append(pad); s.AppendLine("reader.TryGetInt64(out var __v);");
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name); s.AppendLine(" = __v;"); break;
            case "float64":
                s.Append(pad); s.AppendLine("reader.TryGetFloat64(out var __v);");
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name); s.AppendLine(" = __v;"); break;
            case "boolean":
                s.Append(pad); s.AppendLine("reader.TryGetBool(out var __v);");
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name); s.AppendLine(" = __v;"); break;
            case "decimal":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.AppendLine(" = decimal.Parse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.CultureInfo.InvariantCulture);"); break;
            case "datetime":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.AppendLine(" = DateTime.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "guid":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.AppendLine(" = Guid.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "enum":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.Append(" = Enum.Parse<"); s.Append(p.TypeFullName); s.AppendLine(">(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "dateonly":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.AppendLine(" = DateOnly.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "timeonly":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.AppendLine(" = TimeOnly.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "timespan":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.AppendLine(" = TimeSpan.Parse(Encoding.UTF8.GetString(reader.ValueSpan));"); break;
            case "list":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.Append(" ??= new System.Collections.Generic.List<"); s.Append(p.ElementTypeName); s.AppendLine(">();");
                s.Append(pad); s.Append("foreach (var __s in Encoding.UTF8.GetString(reader.ValueSpan).Split(',')) ");
                s.Append(target); s.Append('.'); s.Append(p.Name); s.Append(".Add(");
                WriteParseElem(s, p); s.AppendLine(");"); break;
            case "array":
                s.Append(pad); s.Append(target); s.Append('.'); s.Append(p.Name);
                s.Append(" = Encoding.UTF8.GetString(reader.ValueSpan).Split(',').Select(__s => ");
                WriteParseElem(s, p); s.AppendLine(").ToArray();"); break;
        }
    }

    private static void WriteParseElem(StringBuilder s, PropInfo p)
    {
        switch (p.ElementTypeKind)
        {
            case "string": s.Append("__s"); break;
            case "int32": s.Append("int.Parse(__s)"); break;
            case "int64": s.Append("long.Parse(__s)"); break;
            case "float64": s.Append("double.Parse(__s)"); break;
            case "boolean": s.Append("bool.Parse(__s)"); break;
            default: s.Append("__s"); break;
        }
    }
}

internal readonly record struct TypeInfo(string FullyQualifiedName, string Namespace, string Name, ImmutableArray<PropInfo> Properties);
internal readonly record struct PropInfo(
    string Name, string JsonName, string TypeKind, string TypeFullName, bool IsNullable,
    string? ElementTypeKind, string? ElementTypeName, string? KeyTypeKind, string? KeyTypeName,
    string? SectionName, string? ConverterTypeFullName, ImmutableArray<PropInfo> NestedProperties);
