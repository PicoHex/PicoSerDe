namespace PicoMsgPack.Gen;

using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class MsgPackSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "MsgPackSerializer",
        "PicoMsgPack",
        "msgpack",
        "MsgPackConstructorAttribute"
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

    static bool IsCandidate(SyntaxNode n) => PicoSerDe.Gen.GenInfrastructure.IsCandidate(n);

    static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        bool hasCtor = false;
        INamedTypeSymbol? namedType = null;
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol m
            && m.TypeArguments.Length == 1
        )
        {
            // ── Top-level List<T> (e.g. List<int>, List<SomeDto>) ──
            if (
                m.TypeArguments[0] is INamedTypeSymbol ntsList
                && PicoSerDe.Gen.GenInfrastructure.IsGenericList(ntsList)
            )
                return PicoSerDe.Gen.GenInfrastructure.TransformTopLevelList(
                    ntsList,
                    Config,
                    Attrs
                );

            if (m.TypeArguments[0] is not INamedTypeSymbol nt)
                return null;
            if (nt.IsAnonymousType)
                return null; // handled by anonDriven pipeline
            namedType = nt;
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
        // Collect nested Dictionary types for inner helper generation
        var nestedDictTypes = new Dictionary<string, PropertyInfo>();
        foreach (var t in types)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedDictTypes(t, nestedDictTypes);

        // Collect nested object types (from DTO properties + top-level array/list elements)
        var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
        foreach (var t in types)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedTypes(t, nestedTypes);
        foreach (var t in types)
        {
            if (
                t.ArrayElementKind is "object"
                && !string.IsNullOrEmpty(t.ArrayElementName)
                && !t.ArrayElementNestedProps.IsDefaultOrEmpty
            )
            {
                var elemFqn = t.ArrayElementName!.Replace("global::", "");
                if (!nestedTypes.ContainsKey(elemFqn))
                    nestedTypes[elemFqn] = t.ArrayElementNestedProps;
            }
        }

        var hintNames = new HashSet<string>();
        foreach (var kv in nestedDictTypes)
        {
            var fullName = kv.Key;
            var dictProp = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var sn = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanName);
            var hintName = $"{sn}_MsgPackDictInner.g.cs";
            if (hintNames.Add(hintName))
                spc.AddSource(
                    hintName,
                    SourceText.From(GenDictInner(cleanName, sn, dictProp), Encoding.UTF8)
                );
        }

        // Generate inner helpers for nested object types
        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var sn = PicoSerDe.Gen.GenInfrastructure.SafeName(fullName);
            var hintName = $"{sn}_MsgPackInner.g.cs";
            if (hintNames.Add(hintName))
                spc.AddSource(
                    hintName,
                    SourceText.From(GenInner(fullName, sn, props), Encoding.UTF8)
                );
        }

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
                typeMap[t.FullyQualifiedName] = t;
        }

        foreach (var kv in typeMap)
        {
            var t = kv.Value;
            if (string.IsNullOrEmpty(t.Name))
                continue;
            var safeFq = PicoSerDe.Gen.GenInfrastructure.SafeName(t.FullyQualifiedName ?? "");
            var hintName = $"{safeFq}_MsgPackSerializer.g.cs";
            string code;
            if (t.IsRefLikeType)
                code = GenRefStruct(t);
            else if (t.IsTopLevelList || t.ArrayElementKind is not null)
                code = GenList(t);
            else if (!t.DerivedTypes.IsDefaultOrEmpty)
                code = GenPoly(t, typeMap);
            else
                code = Gen(t);
            spc.AddSource(hintName, SourceText.From(code, Encoding.UTF8));
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
        s.AppendLine("        var mw = new MsgPackWriter(writer);");
        var topSkips = EmitObjectHeaderWithSkips(
            s,
            sorted,
            p => $"value.{p.Name}",
            "        ",
            ref c
        );
        foreach (var p in sorted)
        {
            var bodyInd = EmitSkipGuardOpen(s, topSkips, p, "        ", out var sv);
            s.Append(bodyInd);
            s.Append("mw.WriteInt32(");
            s.Append(p.IntKey ?? 0);
            s.AppendLine(");");
            WriteSer(s, p, $"value.{p.Name}", bodyInd, ref c);
            EmitSkipGuardClose(s, sv, "        ");
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
        else
        {
            s.AppendLine("        var reader = new MsgPackReader(data);");
            for (int ci = 0; ci < type.CtorParams.Length; ci++)
            {
                var cp = type.CtorParams[ci];
                var tn = cp.TypeFullName;
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

    /// <summary>Inner helper for nested Dictionary types.</summary>
    static string GenDictInner(string cleanName, string shortName, PropertyInfo dp)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using PicoSerDe.Core; using PicoMsgPack;");
        // Only emit namespace for non-generic type names
        if (!cleanName.Contains('<'))
        {
            var lastDot = cleanName.LastIndexOf('.');
            if (lastDot > 0)
            {
                s.Append("namespace ");
                s.Append(cleanName.Substring(0, lastDot));
                s.AppendLine(";");
            }
        }
        s.AppendLine();
        s.Append("internal static class ");
        s.Append(shortName);
        s.AppendLine("MsgPackDictInner {");

        // Serialize
        s.Append("    internal static void Serialize(ref MsgPackWriter mw, ");
        s.Append(cleanName);
        s.AppendLine(" value) {");
        s.AppendLine("        mw.WriteStartObject(value.Count);");
        s.AppendLine("        foreach (var __dikvp in value) {");
        s.AppendLine("            mw.WriteString(Encoding.UTF8.GetBytes(__dikvp.Key));");
        switch (dp.ElementTypeKind)
        {
            case "string":
                s.AppendLine("            mw.WriteString(Encoding.UTF8.GetBytes(__dikvp.Value));");
                break;
            case "int32":
                s.AppendLine("            mw.WriteInt32(__dikvp.Value);");
                break;
            case "int64":
                s.AppendLine("            mw.WriteInt64(__dikvp.Value);");
                break;
            case "float32":
                s.AppendLine("            mw.WriteFloat64((double)__dikvp.Value);");
                break;
            case "float64":
                s.AppendLine("            mw.WriteFloat64(__dikvp.Value);");
                break;
            case "boolean":
                s.AppendLine("            mw.WriteBoolean(__dikvp.Value);");
                break;
            case "object":
                // MsgPack uses inline serialization for objects, not inner helpers.
                // Fallback: skip complex object values in nested dict serialization.
                s.AppendLine("            // object values not supported in MsgPack dict nesting");
                break;
            case "any":
                EmitMsgPackAnySerialize(s, "__dikvp.Value", "            ");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "MsgPackDictInner",
                    dp.ElementTypeName!
                );
                s.Append("            ");
                s.Append(sn);
                s.AppendLine(".Serialize(ref mw, __dikvp.Value);");
                break;
            }
            default:
                s.AppendLine(
                    "            mw.WriteString(Encoding.UTF8.GetBytes(__dikvp.Value.ToString()));"
                );
                break;
        }
        s.AppendLine("        }");
        s.AppendLine("        mw.WriteEndObject();");
        s.AppendLine("    }");

        // Deserialize
        s.Append("    internal static ");
        s.Append(cleanName);
        s.AppendLine(" Deserialize(ref MsgPackReader reader) {");
        s.Append("        var obj = new ");
        s.Append(cleanName);
        s.AppendLine("();");
        s.AppendLine("        if (reader.TokenType == TokenType.ObjectStart) {");
        s.AppendLine(
            "            while (reader.Read() && reader.TokenType == TokenType.PropertyName) {"
        );
        s.AppendLine(
            "                var __dk = Encoding.UTF8.GetString(reader.GetStringRaw()); reader.Read();"
        );
        switch (dp.ElementTypeKind)
        {
            case "string":
                s.AppendLine(
                    "                obj[__dk] = Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                break;
            case "int32":
                s.AppendLine("                reader.TryGetInt32(out var __dv); obj[__dk] = __dv;");
                break;
            case "int64":
                s.AppendLine("                reader.TryGetInt64(out var __dv); obj[__dk] = __dv;");
                break;
            case "float32":
                s.AppendLine(
                    "                reader.TryGetFloat64(out var __dv); obj[__dk] = (float)__dv;"
                );
                break;
            case "float64":
                s.AppendLine(
                    "                reader.TryGetFloat64(out var __dv); obj[__dk] = __dv;"
                );
                break;
            case "boolean":
                s.AppendLine("                reader.TryGetBool(out var __dv); obj[__dk] = __dv;");
                break;
            case "any":
                EmitMsgPackAnyDeserialize(s, "obj[__dk]", "                ");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "MsgPackDictInner",
                    dp.ElementTypeName!
                );
                s.Append("                obj[__dk] = ");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref reader);");
                break;
            }
            default:
                s.AppendLine(
                    "                obj[__dk] = Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                break;
        }
        s.AppendLine("            }");
        s.AppendLine("        }");
        s.AppendLine("        return obj;");
        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    /// <summary>Generates inner helper for nested object types (used by top-level List&lt;SomeDto&gt;).</summary>
    static string GenInner(string fqn, string shortName, ImmutableArray<PropertyInfo> props)
    {
        int c = 0;
        var clean = fqn.Replace("global::", "");
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using System.Collections.Generic; using PicoSerDe.Core; using PicoMsgPack;");
        var lastDot = clean.LastIndexOf('.');
        if (lastDot > 0)
        {
            s.Append("namespace ");
            s.Append(clean.Substring(0, lastDot));
            s.AppendLine(";");
            s.AppendLine();
        }
        s.Append("internal static class ");
        s.Append(shortName);
        s.AppendLine("MsgPackInner");
        s.AppendLine("{");

        var sorted = props.OrderBy(p => p.IntKey ?? 0).ToImmutableArray();

        // Serialize
        s.Append("    internal static void Serialize(ref MsgPackWriter mw, ");
        s.Append(clean);
        s.AppendLine(" v)");
        s.AppendLine("    {");
        var innerSkips = EmitObjectHeaderWithSkips(
            s,
            sorted,
            p => $"v.{p.Name}",
            "        ",
            ref c
        );
        foreach (var p in sorted)
        {
            var bodyInd = EmitSkipGuardOpen(s, innerSkips, p, "        ", out var sv);
            s.Append(bodyInd);
            s.Append("mw.WriteInt32(");
            s.Append(p.IntKey ?? 0);
            s.AppendLine(");");
            WriteSer(s, p, $"v.{p.Name}", bodyInd, ref c);
            EmitSkipGuardClose(s, sv, "        ");
        }
        s.AppendLine("        mw.WriteEndObject();");
        s.AppendLine("    }");
        s.AppendLine();

        // Deserialize
        s.Append("    internal static ");
        s.Append(clean);
        s.AppendLine(" Deserialize(ref MsgPackReader reader)");
        s.AppendLine("    {");
        s.Append("        var obj = new ");
        s.Append(clean);
        s.AppendLine("();");
        s.AppendLine("        bool __isMap = reader.TokenType == TokenType.ObjectStart;");
        s.AppendLine("        int __pos = 0;");
        s.AppendLine("        while (reader.Read())");
        s.AppendLine("        {");
        s.AppendLine(
            "            if (reader.TokenType == TokenType.ObjectEnd || reader.TokenType == TokenType.ArrayEnd) break;"
        );
        s.AppendLine("            int __k;");
        s.AppendLine("            if (__isMap)");
        s.AppendLine("            {");
        s.AppendLine("                reader.TryGetInt32(out __k); reader.Read();");
        s.AppendLine("            }");
        s.AppendLine("            else");
        s.AppendLine("            {");
        s.AppendLine("                __k = __pos;");
        s.AppendLine("            }");
        s.AppendLine("            switch (__k)");
        s.AppendLine("            {");
        foreach (var p in sorted)
        {
            s.Append("                case ");
            s.Append(p.IntKey ?? 0);
            s.AppendLine(":");
            WriteDeser(s, p, "obj", "                    ", ref c);
            s.AppendLine("                    break;");
        }
        s.AppendLine("                default: if (__isMap) reader.TrySkip(); break;");
        s.AppendLine("            }");
        s.AppendLine("            __pos++;");
        s.AppendLine("        }");
        s.AppendLine("        return obj;");
        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    // ── Any-value helpers for Dictionary<string, object?> ──

    static void EmitMsgPackAnySerialize(StringBuilder s, string valueExpr, string indent)
    {
        s.Append(indent);
        s.Append("if (");
        s.Append(valueExpr);
        s.AppendLine(" == null) mw.WriteNull();");
        s.Append(indent);
        s.Append("else if (");
        s.Append(valueExpr);
        s.AppendLine(" is string __s) mw.WriteString(Encoding.UTF8.GetBytes(__s));");
        s.Append(indent);
        s.Append("else if (");
        s.Append(valueExpr);
        s.AppendLine(" is long __l) mw.WriteInt64(__l);");
        s.Append(indent);
        s.Append("else if (");
        s.Append(valueExpr);
        s.AppendLine(" is int __i) mw.WriteInt32(__i);");
        s.Append(indent);
        s.Append("else if (");
        s.Append(valueExpr);
        s.AppendLine(" is uint __ui) mw.WriteInt64((long)__ui);");
        s.Append(indent);
        s.Append("else if (");
        s.Append(valueExpr);
        s.AppendLine(" is double __d) mw.WriteFloat64(__d);");
        s.Append(indent);
        s.Append("else if (");
        s.Append(valueExpr);
        s.AppendLine(" is float __f) mw.WriteFloat64((double)__f);");
        s.Append(indent);
        s.Append("else if (");
        s.Append(valueExpr);
        s.AppendLine(" is bool __b) mw.WriteBoolean(__b);");
        s.Append(indent);
        s.Append("else mw.WriteString(Encoding.UTF8.GetBytes(");
        s.Append(valueExpr);
        s.AppendLine(".ToString()!));");
    }

    static void EmitMsgPackAnyDeserialize(StringBuilder s, string assignTarget, string indent)
    {
        s.Append(indent);
        s.AppendLine("if (reader.TokenType == TokenType.Null) {");
        s.Append(indent);
        s.Append("    ");
        s.Append(assignTarget);
        s.AppendLine(" = null;");
        s.Append(indent);
        s.AppendLine("} else if (reader.TokenType == TokenType.String) {");
        s.Append(indent);
        s.Append("    ");
        s.Append(assignTarget);
        s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
        s.Append(indent);
        s.AppendLine("} else if (reader.TokenType == TokenType.Int32) {");
        s.Append(indent);
        s.AppendLine("    reader.TryGetInt32(out var __i);");
        s.Append(indent);
        s.Append("    ");
        s.Append(assignTarget);
        s.AppendLine(" = (long)__i;");
        s.Append(indent);
        s.AppendLine("} else if (reader.TokenType == TokenType.Int64) {");
        s.Append(indent);
        s.AppendLine("    reader.TryGetInt64(out var __l);");
        s.Append(indent);
        s.Append("    ");
        s.Append(assignTarget);
        s.AppendLine(" = __l;");
        s.Append(indent);
        s.AppendLine(
            "} else if (reader.TokenType == TokenType.Float64 || reader.TokenType == TokenType.Float32) {"
        );
        s.Append(indent);
        s.AppendLine("    reader.TryGetFloat64(out var __d);");
        s.Append(indent);
        s.Append("    ");
        s.Append(assignTarget);
        s.AppendLine(" = __d;");
        s.Append(indent);
        s.AppendLine("} else if (reader.TokenType == TokenType.Bool) {");
        s.Append(indent);
        s.AppendLine("    reader.TryGetBool(out var __b);");
        s.Append(indent);
        s.Append("    ");
        s.Append(assignTarget);
        s.AppendLine(" = __b;");
        s.Append(indent);
        s.AppendLine("} else {");
        s.Append(indent);
        s.Append("    ");
        s.Append(assignTarget);
        s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
        s.Append(indent);
        s.AppendLine("}");
    }

    /// <summary>Ref struct serializer — static class + delegate registration.</summary>
    static string GenRefStruct(TypeInfo t)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoMsgPack;");
        if (!string.IsNullOrEmpty(t.Namespace))
        {
            s.Append("using ");
            s.Append(t.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        // Serializer
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("MsgPackSer {");
        s.Append("    public static void Serialize(IBufferWriter<byte> writer, ");
        s.Append(t.Name);
        s.AppendLine(" v) {");
        s.Append("        var mw = new MsgPackWriter(writer); mw.WriteStartObject(");
        s.Append(t.Properties.Length);
        s.AppendLine(");");
        foreach (var p in t.Properties)
        {
            s.Append("        mw.WriteInt32(");
            s.Append($"v.{p.Name}");
            s.AppendLine(");");
        }
        s.AppendLine("        mw.WriteEndObject(); } }");
        s.AppendLine();

        // Registration
        var typeRef = string.IsNullOrEmpty(t.Namespace) ? t.Name : $"{t.Namespace}.{t.Name}";
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        MsgPackSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            ");
        s.Append(t.Name);
        s.AppendLine("MsgPackSer.Serialize);");
        s.AppendLine("    } }");
        return s.ToString();
    }

    /// <summary>Generates serializer/deserializer for a top-level List&lt;T&gt;.</summary>
    static string GenList(TypeInfo type)
    {
        int c = 0;
        var s = new StringBuilder();
        var elemKind = type.ArrayElementKind!;
        var elemTypeName = type.ArrayElementName!;
        var listFqn = type.FullyQualifiedName;

        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using System.Collections.Generic; using PicoSerDe.Core; using PicoMsgPack;");
        s.AppendLine();

        // ── Serializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("MsgPackSerializer : ISerializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(listFqn);
        s.AppendLine(" value) {");
        s.AppendLine("        var mw = new MsgPackWriter(writer);");
        s.AppendLine("        mw.WriteStartArray(value.Count);");
        s.AppendLine("        foreach (var __item in value) {");
        // Create a synthetic PropertyInfo for element serialization
        var elemP = new PropertyInfo(
            Name: "__item",
            JsonName: "__item",
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
        WriteSerElem(s, elemP, "__item", "            ", ref c);
        s.AppendLine("        }");
        s.AppendLine("        mw.WriteEndArray();");
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Deserializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("MsgPackDeserializer : IDeserializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(listFqn);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.AppendLine("        var reader = new MsgPackReader(data);");
        s.AppendLine("        reader.Read();");
        s.Append("        var __list = new List<");
        s.Append(elemTypeName);
        s.AppendLine(">(16);");
        s.AppendLine("        if (reader.TokenType == TokenType.ArrayStart) {");
        s.AppendLine(
            "            while (reader.Read() && reader.TokenType != TokenType.ArrayEnd) {"
        );
        ReadDeserElem(s, elemP, "__list", ".Add", "                ", ref c);
        s.AppendLine("            } }");
        s.AppendLine("        return __list;");
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Registration ──
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("MsgPackReg {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        global::PicoMsgPack.MsgPackSerializer.Register<");
        s.Append(listFqn);
        s.Append(">(new ");
        s.Append(type.Name);
        s.Append("MsgPackSerializer(), new ");
        s.Append(type.Name);
        s.AppendLine("MsgPackDeserializer());");
        s.AppendLine("    } }");

        return s.ToString();
    }

    /// <summary>
    /// Emits the map header for an object. When any property is nullable and
    /// MsgPackIgnoreCondition.WhenWritingNull is active at runtime, null members
    /// are skipped entirely, so the map count is computed dynamically to keep
    /// the header consistent with the number of written key/value pairs.
    /// Returns a map from property name to its skip-flag local, or null when
    /// no property is nullable (constant count, zero overhead).
    /// </summary>
    /// <summary>
    /// Opens the per-member skip guard when the member has a skip flag from
    /// EmitObjectHeaderWithSkips; returns the body indent (increased inside
    /// the guard). The caller must call EmitSkipGuardClose with the same base
    /// indent.
    /// </summary>
    static string EmitSkipGuardOpen(
        StringBuilder s,
        Dictionary<string, string>? skips,
        PropertyInfo prop,
        string ind,
        out string? skipVar
    )
    {
        skipVar = null;
        if (skips is null || !skips.TryGetValue(prop.Name, out skipVar))
            return ind;
        s.Append(ind);
        s.Append("if (!");
        s.Append(skipVar);
        s.AppendLine(") {");
        return ind + "    ";
    }

    static void EmitSkipGuardClose(StringBuilder s, string? skipVar, string ind)
    {
        if (skipVar is not null)
        {
            s.Append(ind);
            s.AppendLine("}");
        }
    }

    static Dictionary<string, string>? EmitObjectHeaderWithSkips(
        StringBuilder s,
        ImmutableArray<PropertyInfo> props,
        Func<PropertyInfo, string> accessor,
        string ind,
        ref int c,
        int extraCount = 0
    )
    {
        var nullable = props
            .Where(p =>
                PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(p)
                // Per-property Never exempts the member from skipping entirely
                && p.IgnoreCondition != "Never"
            )
            .ToArray();
        if (nullable.Length == 0)
        {
            s.Append(ind);
            s.Append("mw.WriteStartObject(");
            s.Append(props.Length + extraCount);
            s.AppendLine(");");
            return null;
        }
        var cnt = $"__n{c++}";
        s.Append(ind);
        s.Append("int ");
        s.Append(cnt);
        s.Append(" = ");
        s.Append(props.Length + extraCount);
        s.AppendLine(";");
        var skips = new Dictionary<string, string>();
        foreach (var p in nullable)
        {
            var sv = $"__sk{c++}";
            skips[p.Name] = sv;
            s.Append(ind);
            s.Append("bool ");
            s.Append(sv);
            // Per-property conditions skip unconditionally; otherwise the
            // global MsgPackOptions condition applies.
            switch (p.IgnoreCondition)
            {
                case "WhenWritingNull":
                    s.Append(" = ");
                    s.Append(accessor(p));
                    s.AppendLine(" == null;");
                    break;
                case "WhenWritingDefault":
                    s.Append(" = ");
                    s.Append(accessor(p));
                    if (PicoSerDe.Gen.GenInfrastructure.IsValueDefaultKind(p.TypeKind))
                        s.AppendLine(" == default;");
                    else
                        s.AppendLine(" == null;");
                    break;
                default:
                    s.Append(
                        " = PicoMsgPack.MsgPackOptions.Current?.DefaultIgnoreCondition == PicoMsgPack.MsgPackIgnoreCondition.WhenWritingNull && "
                    );
                    s.Append(accessor(p));
                    s.AppendLine(" == null;");
                    break;
            }
            s.Append(ind);
            s.Append("if (");
            s.Append(sv);
            s.Append(") ");
            s.Append(cnt);
            s.AppendLine("--;");
        }
        s.Append(ind);
        s.Append("mw.WriteStartObject(");
        s.Append(cnt);
        s.AppendLine(");");
        return skips;
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
                s.AppendLine(" == null) mw.WriteNull();");
                // RegisterCustom<T> overrides the SG inline expansion for nested values
                var __ct = (p.TypeFullName ?? "").TrimEnd('?');
                s.Append(ind);
                s.Append("else if (global::PicoMsgPack.MsgPackSerializer.HasCustomSerializer<");
                s.Append(__ct);
                s.AppendLine(">())");
                s.Append(ind);
                s.Append("    global::PicoMsgPack.MsgPackSerializer.SerializeCustom<");
                s.Append(__ct);
                s.Append(">(mw.Buffer, ");
                s.Append(a);
                s.AppendLine(");");
                s.Append(ind);
                s.AppendLine("else {");
                var ns = p.NestedProperties.OrderBy(n => n.IntKey ?? 0).ToImmutableArray();
                var nSkips = EmitObjectHeaderWithSkips(
                    s,
                    ns,
                    n => $"{a}.{n.Name}",
                    ind + "    ",
                    ref c
                );
                foreach (var n in ns)
                {
                    var bodyInd = ind + "    ";
                    string? nsv = null;
                    if (nSkips is not null && nSkips.TryGetValue(n.Name, out nsv))
                    {
                        s.Append(bodyInd);
                        s.Append("if (!");
                        s.Append(nsv);
                        s.AppendLine(") {");
                        bodyInd += "    ";
                    }
                    s.Append(bodyInd);
                    s.Append("mw.WriteInt32(");
                    s.Append(n.IntKey ?? 0);
                    s.AppendLine(");");
                    WriteSer(s, n, $"{a}.{n.Name}", bodyInd, ref c);
                    if (nsv is not null)
                    {
                        s.Append(ind);
                        s.AppendLine("    }");
                    }
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
            case "any":
                EmitMsgPackAnySerialize(s, a, ind);
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "MsgPackDictInner",
                    p.ElementTypeName!
                );
                s.Append(ind);
                s.Append(sn);
                s.Append(".Serialize(ref mw, ");
                s.Append(a);
                s.AppendLine(");");
                break;
            }
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "MsgPackInner",
                    p.ElementTypeName!
                );
                var elemCt = p.ElementTypeName!.TrimEnd('?');
                s.Append(ind);
                s.Append("if (global::PicoMsgPack.MsgPackSerializer.HasCustomSerializer<");
                s.Append(elemCt);
                s.AppendLine(">())");
                s.Append(ind);
                s.Append("    global::PicoMsgPack.MsgPackSerializer.SerializeCustom<");
                s.Append(elemCt);
                s.Append(">(mw.Buffer, ");
                s.Append(a);
                s.AppendLine(");");
                s.Append(ind);
                s.AppendLine("else");
                s.Append(ind);
                s.Append("    ");
                s.Append(sn);
                s.Append(".Serialize(ref mw, ");
                s.Append(a);
                s.AppendLine(");");
                break;
            }
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
                if (p.IsNullable)
                {
                    s.Append(ind);
                    s.Append("if (reader.TokenType == TokenType.Null) ");
                    s.Append(t);
                    s.AppendLine(" = null; else ");
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                }
                else
                {
                    s.Append(ind);
                    s.Append(t);
                    s.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                }
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
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "MsgPackInner",
                    p.ElementTypeName!
                );
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.Append("(");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref reader));");
                break;
            }
            case "any":
            {
                s.Append(ind);
                s.AppendLine("object? __av = null;");
                EmitMsgPackAnyDeserialize(s, "__av", ind);
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(__av!);");
                break;
            }
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "MsgPackDictInner",
                    p.ElementTypeName!
                );
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.Append("(");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref reader));");
                break;
            }
            default:
                s.Append(ind);
                s.Append(target);
                s.Append(op);
                s.AppendLine("(default!);");
                break;
        }
    }

    static string GenPoly(TypeInfo type, Dictionary<string, TypeInfo> typeMap)
    {
        var dpn = type.DiscriminatorPropertyName ?? "$type";
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoMsgPack;");
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
        s.Append("MsgPackSerializer : ISerializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value) {");
        s.AppendLine("        var mw = new MsgPackWriter(writer);");
        int c = 0;
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
            s.AppendLine("            {");
            // Map count covers only this runtime type's members (+1 discriminator);
            // nullable members skipped under WhenWritingNull decrement it.
            var dtProps = dti
                .Properties.Where(p => !PicoSerDe.Gen.GenInfrastructure.IsComplexMember(p))
                .ToImmutableArray();
            var skips = EmitObjectHeaderWithSkips(
                s,
                dtProps,
                p => $"__v.{p.Name}",
                "                ",
                ref c,
                extraCount: 1
            );
            s.Append("                mw.WriteString(Encoding.UTF8.GetBytes(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dpn));
            s.AppendLine("\"));");
            s.Append("                mw.WriteString(Encoding.UTF8.GetBytes(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dt.TypeDiscriminator));
            s.AppendLine("\"));");
            foreach (var prop in dtProps)
            {
                var bodyInd = EmitSkipGuardOpen(s, skips, prop, "                ", out var sv);
                s.Append(bodyInd);
                s.Append("mw.WriteString(Encoding.UTF8.GetBytes(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName));
                s.AppendLine("\"));");
                WriteSer(s, prop, $"__v.{prop.Name}", bodyInd, ref c);
                EmitSkipGuardClose(s, sv, "                ");
            }
            s.AppendLine("                break;");
            s.AppendLine("            }");
        }
        s.AppendLine("        }");
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();

        // ── Deserializer ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("MsgPackDeserializer : IDeserializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(type.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.AppendLine("        var reader = new MsgPackReader(data);");
        s.AppendLine("        reader.Read(); // map start");
        s.AppendLine("        reader.Read(); // discriminator key");
        s.AppendLine("        var __discKey = reader.GetStringRaw();");
        s.AppendLine("        reader.Read(); // discriminator value");
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
                    s.Append(cp.TypeFullName);
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

            s.AppendLine(
                "            while (reader.Read() && reader.TokenType == TokenType.PropertyName) {"
            );
            s.AppendLine("                var __k = reader.GetStringRaw();");
            s.AppendLine("                reader.Read();");
            for (int pi = 0; pi < dti.Properties.Length; pi++)
            {
                var prop = dti.Properties[pi];
                if (PicoSerDe.Gen.GenInfrastructure.IsComplexMember(prop))
                    continue;
                var kw2 = pi == 0 ? "if" : "else if";
                s.Append("                ");
                s.Append(kw2);
                s.Append(" (MemoryExtensions.SequenceEqual(__k, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName));
                s.AppendLine("\"u8)) {");
                s.Append("                    ");
                if (hasCtor)
                {
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
                        s.Append("__cp_");
                        s.Append(matchIdx);
                        s.Append(" = ");
                        EmitMpReadValue(s, prop);
                        s.AppendLine(";");
                    }
                }
                else
                {
                    s.Append("obj.");
                    s.Append(prop.Name);
                    s.Append(" = ");
                    EmitMpReadValue(s, prop);
                    s.AppendLine(";");
                }
                s.AppendLine("                }");
            }
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
        s.AppendLine("    } }");
        s.AppendLine();

        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"{type.Namespace}.{type.Name}";
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        MsgPackSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("MsgPackSerializer(),");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("MsgPackDeserializer());");
        s.AppendLine("    }");
        s.AppendLine("}");

        return s.ToString();
    }

    static void EmitMpReadValue(StringBuilder s, PropertyInfo prop)
    {
        // Parity with the main deserialization path: nil values (written for
        // nulls under the Never condition) must map back to null/default.
        bool nullable = PicoSerDe.Gen.GenInfrastructure.IsConditionallyOmittable(prop);
        switch (prop.TypeKind)
        {
            case "string":
                if (nullable)
                    s.Append(
                        "reader.TokenType == TokenType.Null ? null : Encoding.UTF8.GetString(reader.GetStringRaw())"
                    );
                else
                    s.Append("Encoding.UTF8.GetString(reader.GetStringRaw())");
                break;
            case "int32":
                if (nullable)
                    s.Append(
                        "reader.TokenType == TokenType.Null ? default(int?) : (reader.TryGetInt32(out var __iv) ? __iv : 0)"
                    );
                else
                    s.Append("reader.TryGetInt32(out var __iv) ? __iv : 0");
                break;
            case "int64":
                if (nullable)
                    s.Append(
                        "reader.TokenType == TokenType.Null ? default(long?) : (reader.TryGetInt64(out var __lv) ? __lv : 0)"
                    );
                else
                    s.Append("reader.TryGetInt64(out var __lv) ? __lv : 0");
                break;
            case "float64":
            case "float32":
                if (nullable)
                    s.Append(
                        "reader.TokenType == TokenType.Null ? default(double?) : (reader.TryGetFloat64(out var __dv) ? __dv : 0)"
                    );
                else
                    s.Append("reader.TryGetFloat64(out var __dv) ? __dv : 0");
                break;
            case "boolean":
                if (nullable)
                    s.Append(
                        "reader.TokenType == TokenType.Null ? default(bool?) : (reader.TryGetBool(out var __bv) ? __bv : false)"
                    );
                else
                    s.Append("reader.TryGetBool(out var __bv) ? __bv : false");
                break;
            default:
                s.Append("default");
                break;
        }
    }
}
