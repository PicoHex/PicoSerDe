namespace PicoMsgPack.Gen;

using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class MsgPackSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "MsgPackSerializer",
        "PicoMsgPack",
        "msgpack"
    );

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        _ => false,
        _ => null,
        HasMsgPackIgnore,
        GetMsgPackConverter,
        _ => null,
        GetIntKey: GetMsgPackKey,
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

        // Pipeline C: format-specific attribute — discover types via [PicoMsgPackSerializable]
        var formatAttr = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "PicoMsgPack.PicoMsgPackSerializableAttribute",
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

    static bool IsCandidate(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        bool hasCtor = false;
        INamedTypeSymbol? namedType = null;
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol m
            && m.TypeArguments.Length == 1
            && m.TypeArguments[0] is INamedTypeSymbol nt
        )
        {
            namedType = nt;
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
                        if (attr.AttributeClass?.Name == "MsgPackConstructorAttribute")
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
            return info;

        if (hasCtor)
        {
            if (namedType is null)
                return ti;
            var ctorParams = PicoSerDe.Gen.GenInfrastructure.DetectConstructor(
                namedType,
                Config.FormatTag,
                "MsgPackConstructorAttribute"
            );
            if (ctorParams is { } cp)
                ti = ti with { CtorParams = cp };
            return ti;
        }

        // Check for [MsgPackExtensionTag] on properties and override type kind
        if (
            namedType is null
            || ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method
            || method.TypeArguments.Length != 1
            || method.TypeArguments[0] is not INamedTypeSymbol
        )
            return ti;

        var props = ti.Properties;
        var builder = ImmutableArray.CreateBuilder<PropertyInfo>();
        bool changed = false;
        foreach (var p in props)
        {
            var member = namedType.GetMembers(p.Name).FirstOrDefault();
            if (member is IPropertySymbol ps)
            {
                var extTag = GetMsgPackExtensionTag(ps);
                if (extTag.HasValue)
                {
                    builder.Add(p with { TypeKind = "extension", ExtensionTag = extTag });
                    changed = true;
                    continue;
                }
            }
            builder.Add(p);
        }
        return changed ? ti with { Properties = builder.ToImmutable() } : ti;
    }

    // ── Attribute helpers ──

    static bool HasMsgPackIgnore(IPropertySymbol p)
    {
        foreach (var a in p.GetAttributes())
            if (a.AttributeClass?.Name == "MsgPackIgnoreAttribute")
                return true;
        return false;
    }

    static string? GetMsgPackConverter(IPropertySymbol p)
    {
        foreach (var a in p.GetAttributes())
            if (
                a.AttributeClass?.Name == "MsgPackConverterAttribute"
                && a.ConstructorArguments.Length >= 1
                && a.ConstructorArguments[0].Value is INamedTypeSymbol ct
            )
                return ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return null;
    }

    static byte? GetMsgPackExtensionTag(IPropertySymbol p)
    {
        foreach (var a in p.GetAttributes())
            if (
                a.AttributeClass?.Name == "MsgPackExtensionTagAttribute"
                && a.ConstructorArguments.Length == 1
                && a.ConstructorArguments[0].Value is byte tag
            )
                return tag;
        return null;
    }

    static int? GetMsgPackKey(IPropertySymbol p)
    {
        foreach (var a in p.GetAttributes())
            if (
                a.AttributeClass?.Name == "MsgPackKeyAttribute"
                && a.ConstructorArguments.Length == 1
                && a.ConstructorArguments[0].Value is int k
            )
                return k;
        return null;
    }

    // ── Source generation ──

    static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var seen = new HashSet<string>();
        var hintNames = new HashSet<string>();
        foreach (var t in types)
        {
            if (!seen.Add(t.FullyQualifiedName))
                continue;
            // Guard: skip types with empty/null Name
            if (string.IsNullOrEmpty(t.Name))
                continue;
            // Try short name first; fall back to FQN on collision
            var shortHintName = $"{t.Name}_MsgPackSerializer.g.cs";
            string hintName;
            if (hintNames.Add(shortHintName))
            {
                hintName = shortHintName;
            }
            else
            {
                // SafeName handles "global::" removal and dot→underscore conversion
                var safeFq = PicoSerDe.Gen.GenInfrastructure.SafeName(t.FullyQualifiedName ?? "");
                hintName = $"{safeFq}_MsgPackSerializer.g.cs";
                hintNames.Add(hintName);
            }
            spc.AddSource(hintName, SourceText.From(Gen(t), Encoding.UTF8));
        }
    }

    static string Gen(TypeInfo type)
    {
        int c = 0;
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using System.Collections.Generic; using PicoSerDe.Core; using PicoMsgPack;");
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            s.Append("using ");
            s.Append(type.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        var sorted = type.Properties.OrderBy(p => p.IntKey ?? 0).ToImmutableArray();

        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("MsgPackSerializer : ISerializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value) {");
        s.Append("        var mw = new MsgPackWriter(writer); mw.WriteStartObject(");
        s.Append(sorted.Length);
        s.AppendLine(");");
        foreach (var p in sorted)
        {
            s.Append("        mw.WriteInt32(");
            s.Append(p.IntKey ?? 0);
            s.AppendLine(");");
            WriteSer(s, p, $"value.{p.Name}", "        ", ref c);
        }
        s.AppendLine("        mw.WriteEndObject(); } }");
        s.AppendLine();

        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("MsgPackDeserializer : IDeserializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(type.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;
        Dictionary<string, int>? ctorMap = null;
        if (hasCtor)
        {
            ctorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
                ctorMap[type.CtorParams[ci].Name] = ci;
        }
        if (!hasCtor)
        {
            s.AppendLine("        var reader = new MsgPackReader(data);");
            var mpReq = type.Properties.Where(p => p.IsRequired).ToArray();
            if (mpReq.Length > 0)
            {
                s.Append("        var obj = new ");
                s.Append(type.Name);
                s.AppendLine(" {");
                foreach (var rp in mpReq)
                {
                    s.Append("            ");
                    s.Append(rp.Name);
                    s.Append(" = ");
                    switch (rp.TypeKind)
                    {
                        case "string":
                            s.Append("null!");
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
        else
        {
            s.AppendLine("        var reader = new MsgPackReader(data);");
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
            {
                var cp = type.CtorParams[ci];
                var tn = PicoSerDe.Gen.TypeKindResolver.MapTypeName(cp.TypeKind, null!);
                s.Append("        ");
                s.Append(tn);
                s.Append(" __cp_");
                s.Append(ci);
                s.AppendLine(cp.TypeKind == "string" ? " = null!;" : " = default;");
            }
        }
        s.AppendLine(
            "        reader.Read(); bool __isMap = reader.TokenType == TokenType.ObjectStart; int __pos = 0;"
        );
        s.AppendLine("        while (reader.Read()) {");
        s.AppendLine(
            "            if (reader.TokenType == TokenType.ObjectEnd || reader.TokenType == TokenType.ArrayEnd) break;"
        );
        s.AppendLine("            int __k;");
        s.AppendLine("            if (__isMap) {");
        s.AppendLine("                reader.TryGetInt32(out __k); reader.Read();");
        s.AppendLine("            } else {");
        s.AppendLine("                __k = __pos;");
        s.AppendLine("            }");
        s.AppendLine("            switch (__k) {");
        foreach (var p in sorted)
        {
            s.Append("                case ");
            s.Append(p.IntKey ?? 0);
            s.AppendLine(":");
            WriteDeser(s, p, "obj", "                ", ref c, ctorMap: ctorMap);
            s.AppendLine("                    break;");
        }
        s.AppendLine("                default: if (__isMap) reader.TrySkip(); break; }");
        s.AppendLine("            __pos++;");
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
            s.AppendLine("        return obj;");
        s.AppendLine("    } }");
        s.AppendLine();

        // Streaming deserializer (skipped for constructor types)
        if (hasCtor)
        { /* skip */
        }
        else
        {
            s.Append("file static class ");
            s.Append(type.Name);
            s.AppendLine("MsgPackStreaming {");
            s.AppendLine(
                "    internal static ReadStatus DeserializeStreaming(ref MsgPackReader reader, out "
                    + type.Name
                    + "? result) {"
            );
            s.AppendLine("        result = default;");
            var mpSrcReq = type.Properties.Where(p => p.IsRequired).ToArray();
            if (mpSrcReq.Length > 0)
            {
                s.Append("        var obj = new ");
                s.Append(type.Name);
                s.AppendLine(" {");
                foreach (var rp in mpSrcReq)
                {
                    s.Append("            ");
                    s.Append(rp.Name);
                    s.Append(" = ");
                    switch (rp.TypeKind)
                    {
                        case "string":
                            s.Append("null!");
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
            s.AppendLine(
                "        if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
            );
            s.AppendLine(
                "        bool __isMap = reader.TokenType == TokenType.ObjectStart; int __pos = 0;"
            );
            s.AppendLine("        while (true) {");
            s.AppendLine(
                "            if (!reader.Read()) return reader.NeedsMoreData ? ReadStatus.NeedMoreData : ReadStatus.EndOfInput;"
            );
            s.AppendLine(
                "            if (reader.TokenType == TokenType.ObjectEnd || reader.TokenType == TokenType.ArrayEnd) break;"
            );
            s.AppendLine("            int __k;");
            s.AppendLine("            if (__isMap) {");
            s.AppendLine("                reader.TryGetInt32(out __k); reader.Read();");
            s.AppendLine("            } else {");
            s.AppendLine("                __k = __pos;");
            s.AppendLine("            }");
            s.AppendLine("            switch (__k) {");
            foreach (var p in sorted)
            {
                s.Append("                case ");
                s.Append(p.IntKey ?? 0);
                s.AppendLine(":");
                WriteDeser(s, p, "obj", "                ", ref c);
                s.AppendLine("                    break;");
            }
            s.AppendLine("                default: if (__isMap) reader.TrySkip(); break; }");
            s.AppendLine("            __pos++;");
            s.AppendLine("        }");
            s.AppendLine("        result = obj;");
            s.AppendLine("        return ReadStatus.Success;");
            s.AppendLine("    }");
            s.AppendLine("}");
        } // end skip-streaming else
        s.AppendLine();

        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("__Reg {");
        s.AppendLine("    [ModuleInitializer] internal static void Register() {");
        s.Append("        MsgPackSerializer.Register<");
        s.Append(type.Name);
        s.Append(">(new ");
        s.Append(type.Name);
        s.Append("MsgPackSerializer(), new ");
        s.Append(type.Name);
        s.AppendLine("MsgPackDeserializer());");
        if (hasCtor)
            s.AppendLine("        // Streaming skipped for constructor type");
        else
        {
            s.Append("        MsgPackSerializer.RegisterStreaming<");
            s.Append(type.Name);
            s.Append(">(");
            s.Append(type.Name);
            s.AppendLine("MsgPackStreaming.DeserializeStreaming);");
        }
        s.AppendLine("    } }");
        return s.ToString();
    }

    static void WriteSer(StringBuilder s, PropertyInfo p, string a, string ind, ref int c)
    {
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(ind);
            s.Append("var __cnv");
            s.Append(c);
            s.Append(" = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(ind);
            s.Append("var __bw");
            s.Append(c);
            s.Append(" = new ArrayBufferWriter<byte>();");
            s.AppendLine();
            s.Append(ind);
            s.Append("__cnv");
            s.Append(c);
            s.Append(".Write(__bw");
            s.Append(c);
            s.Append(", ");
            s.Append(a);
            s.AppendLine(");");
            s.Append(ind);
            s.Append("mw.WriteString(__bw");
            s.Append(c);
            s.Append(".WrittenSpan);");
            s.AppendLine();
            c++;
            return;
        }
        bool needsNullCheck = p.TypeKind == "string" || p.IsNullableReference;
        if (p.IsNullable && !p.IsNullableReference)
        {
            s.Append(ind);
            s.Append("if (");
            s.Append(a);
            s.AppendLine(".HasValue) {");
            ind += "    ";
            a += ".Value";
        }
        else if (needsNullCheck)
        {
            s.Append(ind);
            s.Append("if (");
            s.Append(a);
            s.AppendLine(" != null) {");
            ind += "    ";
        }
        switch (p.TypeKind)
        {
            case "string":
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine("));");
                break;
            case "int32":
                s.Append(ind);
                s.Append("mw.WriteInt32(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "int64":
                s.Append(ind);
                s.Append("mw.WriteInt64(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "float32":
                s.Append(ind);
                s.Append("mw.WriteFloat64((double)(");
                s.Append(a);
                s.AppendLine("));");
                break;
            case "float64":
                s.Append(ind);
                s.Append("mw.WriteFloat64(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "boolean":
                s.Append(ind);
                s.Append("mw.WriteBoolean(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "datetime":
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine(".ToString(\"O\")));");
                break;
            case "timespan":
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine(".ToString()));");
                break;
            case "dateonly":
            case "guid":
            case "enum":
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine(".ToString()));");
                break;
            case "timeonly":
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine(
                    ".ToString(\"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture)));"
                );
                break;
            case "list":
            case "array":
                s.Append(ind);
                s.Append("mw.WriteStartArray(");
                s.Append(p.TypeKind == "array" ? $"{a}.Length" : $"{a}.Count");
                s.AppendLine(");");
                s.Append(ind);
                s.Append("foreach (var __i in ");
                s.Append(a);
                s.AppendLine(") {");
                WriteSerElem(s, p, "__i", ind + "    ", ref c);
                s.Append(ind);
                s.AppendLine("}");
                s.Append(ind);
                s.AppendLine("mw.WriteEndArray();");
                break;
            case "bytes":
                s.Append(ind);
                s.Append("mw.WriteBytes(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "extension":
                s.Append(ind);
                s.Append("mw.WriteExtension(");
                s.Append(p.ExtensionTag ?? 0);
                s.Append(", ");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "dict":
                s.Append(ind);
                s.Append("mw.WriteStartObject(");
                s.Append(a);
                s.AppendLine(".Count);");
                s.Append(ind);
                s.Append("foreach (var __kv in ");
                s.Append(a);
                s.AppendLine(") {");
                s.Append(ind);
                s.Append("    mw.WriteString(Encoding.UTF8.GetBytes(__kv.Key));");
                WriteSerElem(s, p, "__kv.Value", ind + "    ", ref c);
                s.Append(ind);
                s.AppendLine("}");
                s.Append(ind);
                s.AppendLine("mw.WriteEndObject();");
                break;
            case "object":
                if (p.NestedProperties.Length == 0)
                {
                    s.Append(ind);
                    s.AppendLine("mw.WriteNull();");
                    break;
                }
                s.Append(ind);
                s.Append("if (");
                s.Append(a);
                s.AppendLine(" == null) mw.WriteNull(); else {");
                var ns = p.NestedProperties.OrderBy(n => n.IntKey ?? 0).ToImmutableArray();
                s.Append(ind);
                s.Append("    mw.WriteStartObject(");
                s.Append(ns.Length);
                s.AppendLine(");");
                foreach (var n in ns)
                {
                    s.Append(ind);
                    s.Append("    mw.WriteInt32(");
                    s.Append(n.IntKey ?? 0);
                    s.AppendLine(");");
                    WriteSer(s, n, $"{a}.{n.Name}", ind + "    ", ref c);
                }
                s.Append(ind);
                s.AppendLine("    mw.WriteEndObject(); }");
                break;
            default:
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine(".ToString()));");
                break;
        }
        if (p.IsNullable || p.TypeKind == "string")
        {
            ind = ind.Substring(4);
            s.Append(ind);
            s.AppendLine("} else mw.WriteNull();");
        }
    }

    static void WriteSerElem(StringBuilder s, PropertyInfo p, string a, string ind, ref int c)
    {
        switch (p.ElementTypeKind)
        {
            case "string":
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine("));");
                break;
            case "int32":
                s.Append(ind);
                s.Append("mw.WriteInt32(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "int64":
                s.Append(ind);
                s.Append("mw.WriteInt64(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "float32":
                s.Append(ind);
                s.Append("mw.WriteFloat64((double)(");
                s.Append(a);
                s.AppendLine("));");
                break;
            case "float64":
                s.Append(ind);
                s.Append("mw.WriteFloat64(");
                s.Append(a);
                s.AppendLine(");");
                break;
            case "boolean":
                s.Append(ind);
                s.Append("mw.WriteBoolean(");
                s.Append(a);
                s.AppendLine(");");
                break;
            default:
                s.Append(ind);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(");
                s.Append(a);
                s.AppendLine(".ToString()));");
                break;
        }
    }

    static void WriteDeser(
        StringBuilder s,
        PropertyInfo p,
        string target,
        string ind,
        ref int c,
        bool ctorAssign = false,
        IReadOnlyDictionary<string, int>? ctorMap = null
    )
    {
        // If property is in ctorMap, redirect to __cp_N
        if (!ctorAssign && ctorMap is not null && ctorMap.TryGetValue(p.Name, out var __ci))
        {
            WriteDeser(s, p, $"__cp_{__ci}", ind, ref c, ctorAssign: true);
            return;
        }
        var t = ctorAssign ? target : $"{target}.{p.Name}";
        if (p.ConverterTypeFullName is not null)
        {
            s.Append(ind);
            s.Append("var __cnv");
            s.Append(c);
            s.Append(" = new ");
            s.Append(p.ConverterTypeFullName);
            s.AppendLine("();");
            s.Append(ind);
            s.Append(t);
            s.Append(" = __cnv");
            s.Append(c++);
            s.AppendLine(".Read(ref reader);");
            return;
        }
        switch (p.TypeKind)
        {
            case "string":
                s.Append(ind);
                s.Append("if (reader.TokenType == TokenType.Null) ");
                s.Append(t);
                s.AppendLine(" = null; else ");
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("if (reader.TokenType == TokenType.Null) { int? __nv = null; ");
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = __nv; } else {");
                }
                s.Append(ind);
                s.Append("reader.TryGetInt32(out var __v");
                s.Append(c);
                s.AppendLine(");");
                s.Append(ind);
                s.Append(t);
                s.Append(" = __v");
                s.Append(c++);
                s.AppendLine(";");
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("}");
                }
                break;
            case "int64":
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("if (reader.TokenType == TokenType.Null) { long? __nv = null; ");
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = __nv; } else {");
                }
                s.Append(ind);
                s.Append("reader.TryGetInt64(out var __v");
                s.Append(c);
                s.AppendLine(");");
                s.Append(ind);
                s.Append(t);
                s.Append(" = __v");
                s.Append(c++);
                s.AppendLine(";");
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("}");
                }
                break;
            case "float32":
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("if (reader.TokenType == TokenType.Null) { float? __nv = null; ");
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = __nv; } else {");
                }
                s.Append(ind);
                s.Append("reader.TryGetFloat64(out var __v");
                s.Append(c);
                s.AppendLine(");");
                s.Append(ind);
                s.Append(t);
                s.Append(" = (float)__v");
                s.Append(c++);
                s.AppendLine(";");
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("}");
                }
                break;
            case "float64":
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("if (reader.TokenType == TokenType.Null) { double? __nv = null; ");
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = __nv; } else {");
                }
                s.Append(ind);
                s.Append("reader.TryGetFloat64(out var __v");
                s.Append(c);
                s.AppendLine(");");
                s.Append(ind);
                s.Append(t);
                s.Append(" = __v");
                s.Append(c++);
                s.AppendLine(";");
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("}");
                }
                break;
            case "boolean":
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("if (reader.TokenType == TokenType.Null) { bool? __nv = null; ");
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = __nv; } else {");
                }
                s.Append(ind);
                s.Append("reader.TryGetBool(out var __v");
                s.Append(c);
                s.AppendLine(");");
                s.Append(ind);
                s.Append(t);
                s.Append(" = __v");
                s.Append(c++);
                s.AppendLine(";");
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.AppendLine("}");
                }
                break;
            case "datetime":
                s.Append(ind);
                s.AppendLine(
                    "var __dtRaw = Encoding.UTF8.GetString(reader.GetStringRaw()); DateTime.TryParse(__dtRaw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                );
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __dt;");
                break;
            case "timespan":
                s.Append(ind);
                s.AppendLine(
                    "TimeSpan.TryParse(Encoding.UTF8.GetString(reader.GetStringRaw()), out var __ts);"
                );
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __ts;");
                break;
            case "decimal":
                s.Append(ind);
                s.AppendLine(
                    "var __decRaw = Encoding.UTF8.GetString(reader.GetStringRaw()); decimal.TryParse(__decRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decv);"
                );
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __decv;");
                break;
            case "dateonly":
                s.Append(ind);
                s.AppendLine(
                    "var __doRaw = Encoding.UTF8.GetString(reader.GetStringRaw()); DateOnly.TryParse(__doRaw, out var __dov);"
                );
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __dov;");
                break;
            case "timeonly":
                s.Append(ind);
                s.AppendLine(
                    "var __toRaw = Encoding.UTF8.GetString(reader.GetStringRaw()); System.TimeOnly.TryParseExact(__toRaw, \"HH:mm:ss.fffffff\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __tov);"
                );
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __tov;");
                break;
            case "guid":
                s.Append(ind);
                s.AppendLine("Guid.TryParse(reader.GetStringRaw(), out var __g);");
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __g;");
                break;
            case "enum":
                s.Append(ind);
                s.Append("Enum.TryParse<");
                s.Append(p.TypeFullName);
                s.AppendLine(">(Encoding.UTF8.GetString(reader.GetStringRaw()), out var __e);");
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __e;");
                break;
            case "bytes":
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = reader.GetStringRaw().ToArray();");
                break;
            case "extension":
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = reader.GetStringRaw().ToArray();");
                break;
            case "list":
                s.Append(ind);
                s.Append(t);
                s.Append(" ??= new List<");
                s.Append(p.ElementTypeName);
                s.AppendLine(">(16);");
                s.Append(ind);
                s.AppendLine("if (reader.TokenType == TokenType.ArrayStart) {");
                s.Append(ind);
                s.AppendLine(
                    "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd) {"
                );
                ReadDeserElem(s, p, t, ".Add", ind + "        ", ref c);
                s.Append(ind);
                s.AppendLine("    } }");
                break;
            case "array":
                s.Append(ind);
                s.Append("var __l = new List<");
                s.Append(p.ElementTypeName);
                s.AppendLine(">(16);");
                s.Append(ind);
                s.AppendLine("if (reader.TokenType == TokenType.ArrayStart) {");
                s.Append(ind);
                s.AppendLine(
                    "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd) {"
                );
                ReadDeserElem(s, p, "__l", ".Add", ind + "        ", ref c);
                s.Append(ind);
                s.AppendLine("    } }");
                s.Append(ind);
                s.Append(t);
                s.AppendLine(" = __l.ToArray();");
                break;
            case "dict":
                s.Append(ind);
                s.Append(t);
                s.Append(" ??= new Dictionary<string, ");
                s.Append(p.ElementTypeName);
                s.AppendLine(">(16);");
                s.Append(ind);
                s.AppendLine("if (reader.TokenType == TokenType.ObjectStart) {");
                s.Append(ind);
                s.AppendLine(
                    "    while (reader.Read() && reader.TokenType == TokenType.PropertyName) {"
                );
                s.Append(ind);
                s.AppendLine(
                    "        var __dk = Encoding.UTF8.GetString(reader.GetStringRaw()); reader.Read();"
                );
                ReadDeserElem(s, p, $"{t}[__dk]", " =", ind + "        ", ref c);
                s.Append(ind);
                s.AppendLine("    } }");
                break;
            case "object":
                if (p.NestedProperties.Length == 0)
                {
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = default!;");
                    break;
                }
                s.Append(ind);
                s.AppendLine("if (reader.TokenType == TokenType.Null) {");
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.Append("    ");
                    s.Append(t);
                    s.AppendLine(" = null;");
                }
                s.Append(ind);
                s.AppendLine("} else {");
                s.Append(ind);
                s.Append("    var __o = new ");
                s.Append(p.TypeFullName);
                s.AppendLine("();");
                s.Append(ind);
                s.AppendLine("    int __np = 0; while (reader.Read()) {");
                s.Append(ind);
                s.AppendLine(
                    "        if (reader.TokenType == TokenType.ObjectEnd || reader.TokenType == TokenType.ArrayEnd) break;"
                );
                s.Append(ind);
                s.AppendLine(
                    "        int __nk; if (__isMap) { reader.TryGetInt32(out __nk); reader.Read(); } else { __nk = __np; }"
                );
                s.Append(ind);
                s.AppendLine("        switch (__nk) {");
                var ns = p.NestedProperties.OrderBy(n => n.IntKey ?? 0).ToImmutableArray();
                foreach (var n in ns)
                {
                    s.Append(ind);
                    s.Append("            case ");
                    s.Append(n.IntKey ?? 0);
                    s.AppendLine(":");
                    WriteDeser(s, n, "__o", ind + "                ", ref c);
                    s.Append(ind);
                    s.AppendLine("                break;");
                }
                s.Append(ind);
                s.AppendLine("            default: if (__isMap) reader.TrySkip(); break; }");
                s.Append(ind);
                s.AppendLine("        __np++;");
                s.Append(ind);
                s.AppendLine("    }");
                s.Append(ind);
                s.Append("    ");
                s.Append(t);
                s.AppendLine(" = __o; }");
                break;
        }
    }

    static void ReadDeserElem(
        StringBuilder s,
        PropertyInfo p,
        string target,
        string op,
        string ind,
        ref int c
    )
    {
        switch (p.ElementTypeKind)
        {
            case "string":
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
            case "int32":
                s.Append(ind);
                s.AppendLine("reader.TryGetInt32(out var __ev);");
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(__ev);");
                break;
            case "int64":
                s.Append(ind);
                s.AppendLine("reader.TryGetInt64(out var __ev);");
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(__ev);");
                break;
            case "float32":
                s.Append(ind);
                s.AppendLine("reader.TryGetFloat64(out var __ev);");
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("((float)__ev);");
                break;
            case "float64":
                s.Append(ind);
                s.AppendLine("reader.TryGetFloat64(out var __ev);");
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(__ev);");
                break;
            case "boolean":
                s.Append(ind);
                s.AppendLine("reader.TryGetBool(out var __ev);");
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(__ev);");
                break;
            case "decimal":
                s.Append(ind);
                s.AppendLine("var __decRaw = Encoding.UTF8.GetString(reader.GetStringRaw());");
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine(
                    "(decimal.Parse(__decRaw, System.Globalization.CultureInfo.InvariantCulture));"
                );
                break;
            default:
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(default!);");
                break;
        }
    }
}
