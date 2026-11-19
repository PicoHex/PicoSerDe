namespace PicoToml.Gen;

using Microsoft.CodeAnalysis.CSharp;
using AnonFieldInfo = PicoSerDe.Gen.AnonFieldInfo;
using AnonPipelineConfig = PicoSerDe.Gen.AnonPipelineConfig;
using Arity = PicoSerDe.Gen.AttributeArity;
using AttributeLookup = PicoSerDe.Gen.AttributeLookup;
using DefaultLiteralRow = PicoSerDe.Gen.DefaultLiteralRow;
using DefaultLiterals = PicoSerDe.Gen.DefaultLiterals;
using GenPipeline = PicoSerDe.Gen.GenPipeline;
using NullGuardEmit = PicoSerDe.Gen.NullGuardEmit;
using PropertyInfo = PicoSerDe.Gen.PropertyInfo;
using TypeInfo = PicoSerDe.Gen.TypeInfo;

[Generator(LanguageNames.CSharp)]
public sealed class TomlSerializerGenerator : IIncrementalGenerator
{
    private static readonly PicoSerDe.Gen.FormatConfig Config = new(
        "TomlSerializer",
        "PicoToml",
        "toml",
        "TomlConstructorAttribute"
    );

    private static readonly DiagnosticDescriptor AnonRequiresCSharp12 = new(
        id: "PICOTOML003",
        title: "Anonymous types require C# 12+",
        messageFormat: "Anonymous type serialization requires C# 12 or later",
        category: "PicoToml.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor AnonRequiresUnsafe = new(
        id: "PICOTOML004",
        title: "Requires AllowUnsafeBlocks",
        messageFormat: "Anonymous type serialization requires AllowUnsafeBlocks to be enabled",
        category: "PicoToml.Gen",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    private static readonly PicoSerDe.Gen.AttributeHelpers Attrs = new(
        HasTomlCamelCase,
        GetTomlKey,
        HasTomlIgnore,
        GetTomlConverter,
        GetTomlDateTimeFormat,
        OverrideKindWithStringOnConverter: true
    );

    private static readonly PicoSerDe.Gen.AnonFormatConfig AnonCfg = new(
        HasNullLiteral: false,
        EmbedsKeyInValue: true,
        ObjectStartMethod: null,
        ObjectEndMethod: null,
        ObjectStartNeedsCount: false,
        HasIndentedMaxDepth: false,
        KeyIsEncodedString: false,
        HasNamingPolicy: false,
        HasOptionsParam: false,
        FacadeTakesOptions: true,
        HasIndented: true,
        NestedSectionMethod: "WriteTable"
    );

    /// <summary>
    /// This format's anonymous-type pipeline inputs: its two configs, its two diagnostics, its emitter.
    /// </summary>
    private static readonly AnonPipelineConfig AnonPipe = new(
        Config,
        Attrs,
        AnonCfg,
        AnonRequiresCSharp12,
        AnonRequiresUnsafe,
        EmitTomlValue
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipelines A–F: the shape is shared (GenPipeline, review C2); every literal below is this
        // format's own — its syntax transform, its format attribute, its two diagnostics.
        var usageDriven = GenPipeline.UsageDriven(context, Transform);
        var attrDriven = GenPipeline.AttributeDriven(context, Config, Attrs);
        var shorthandAttr = GenPipeline.ShorthandAttributeDriven(context, Config, Attrs);
        var formatAttr = GenPipeline.FormatAttributeDriven(
            context,
            "PicoToml.PicoTomlSerializableAttribute",
            Config,
            Attrs
        );
        var poly = GenPipeline.PolymorphicDriven(context, Config, Attrs);
        var asmName = GenPipeline.AssemblyName(context);

        // Pipeline-Anon: anonymous type serialization via interceptors
        GenPipeline.RegisterAnonOutput(context, AnonPipe, asmName);

        // Merge all pipelines into one output
        var all = GenPipeline.Merge(usageDriven, attrDriven, formatAttr, shorthandAttr, poly);
        GenPipeline.RegisterTypesOutput(context, all, Config.FormatTag, GenerateAll);
    }

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        bool hasCtor = false;
        if (
            ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol method
            && method.TypeArguments.Length == 1
        )
        {
            // ── Top-level List<T> (e.g. List<int>, List<SomeDto>) ──
            if (
                method.TypeArguments[0] is INamedTypeSymbol ntsList
                && PicoSerDe.Gen.GenInfrastructure.IsGenericList(ntsList)
            )
                return PicoSerDe.Gen.GenInfrastructure.TransformTopLevelList(
                    ntsList,
                    Config,
                    Attrs
                );

            if (method.TypeArguments[0] is not INamedTypeSymbol namedType)
                return null;
            if (namedType.IsAnonymousType)
                return null; // handled by anonDriven pipeline

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
            // Record primary constructor auto-detection: asked of the ONE shared implementation
            // (GenInfrastructure.HasPrimaryConstructor) that the attribute pipeline asks too, so a
            // record can never be a record for a call site and a plain type for an attribute.
            if (PicoSerDe.Gen.GenInfrastructure.HasPrimaryConstructor(namedType))
                hasCtor = true;
            // [TomlConstructor]. `null` is the "no namespace filter" argument, not an
            // oversight: the hand-written scan this replaces compared the attribute NAME
            // only (pinned by the N2 equivalence corpus).
            if (!hasCtor)
            {
                foreach (var ctor in namedType.Constructors)
                {
                    if (ctor.DeclaredAccessibility != Accessibility.Public)
                        continue;
                    if (AttributeLookup.Has(ctor, null, "TomlConstructorAttribute"))
                    {
                        hasCtor = true;
                        break;
                    }
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

    private static bool HasTomlCamelCase(ITypeSymbol type) =>
        AttributeLookup.Has(type, "PicoToml", "TomlCamelCaseAttribute");

    private static string? GetTomlKey(IPropertySymbol p) =>
        AttributeLookup.GetString(p, "PicoToml", "TomlKeyAttribute", Arity.One);

    private static bool HasTomlIgnore(IPropertySymbol p) =>
        AttributeLookup.Has(p, "PicoToml", "TomlIgnoreAttribute");

    // TOML is the only format whose converter attribute also has a GENERIC form
    // (`[TomlConverter<T>]`, the converter as the class's type argument): the shared lookup takes
    // that as its one flag instead of the body being copied back out here.
    private static string? GetTomlConverter(IPropertySymbol p) =>
        AttributeLookup.GetTypeName(
            p,
            "PicoToml",
            "TomlConverterAttribute",
            Arity.AtLeastOne,
            genericTypeArgumentFallback: true
        );

    private static string? GetTomlDateTimeFormat(IPropertySymbol p) =>
        AttributeLookup.GetString(p, "PicoToml", "TomlDateTimeFormatAttribute", Arity.AtLeastOne);

    // ── Source generation ──

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
        var validTypes = types
            .Where(t =>
                !string.IsNullOrEmpty(t.FullyQualifiedName) && !string.IsNullOrEmpty(t.Name)
            )
            .ToArray();
        foreach (var t in validTypes)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedTypes(t, nestedTypes);

        // Also collect element types from top-level arrays/lists (e.g. SimplePoco for List<SimplePoco>)
        foreach (var t in types)
        {
            if (
                t.ArrayElementKind is "object"
                && !string.IsNullOrEmpty(t.ArrayElementName)
                && !t.ArrayElementNestedProps.IsDefaultOrEmpty
            )
            {
                // Raw FQN keeps this key identical to CollectNestedTypes' so a
                // type used both as a DTO property and as a top-level array
                // element shares one helper.
                var elemFqn = t.ArrayElementName!;
                if (!nestedTypes.ContainsKey(elemFqn))
                    nestedTypes[elemFqn] = t.ArrayElementNestedProps;
            }
        }

        // Recursive references: seed the cycle targets' helpers from their real
        // (merged) property sets, and surface per-format skips as PICOSERDE003.
        PicoSerDe.Gen.GenInfrastructure.SeedRecursiveTargets(validTypes, nestedTypes);
        PicoSerDe.Gen.GenInfrastructure.ReportSkippedRecursiveMembers(
            spc,
            validTypes,
            Config.FormatTag
        );
        PicoSerDe.Gen.GenInfrastructure.ReportUnsupportedMembers(spc, validTypes, Config.FormatTag);
        // PICOSERDE001: a member whose type is unresolvable for this format is
        // recorded during extraction (it cannot be reported there — a transform must
        // stay pure), so the drop becomes visible here, once per member (D-6).
        PicoSerDe.Gen.GenInfrastructure.ReportCollectedDiagnostics(
            spc,
            PicoSerDe.Gen.GenInfrastructure.CollectUnsupportedTypeMemberDiagnostics(
                validTypes,
                Config.FormatTag
            )
        );

        // Collect nested Dictionary types
        var nestedDictTypes = new Dictionary<string, PropertyInfo>();
        foreach (var t in validTypes)
            PicoSerDe.Gen.GenInfrastructure.CollectNestedDictTypes(t, nestedDictTypes);

        var hintNames = new HashSet<string>();
        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var safeName = PicoSerDe.Gen.GenInfrastructure.UniqueName(fullName);
            var hintName = $"{safeName}_TomlInner.g.cs";
            if (!hintNames.Add(hintName))
                continue;
            // The same question the JSON generator asks of the same list: is this nested type a
            // polymorphic BASE? Then its helper has to dispatch on the discriminator instead of
            // emitting the base's own members — which dropped the tag and every derived member,
            // silently, while JSON carried both.
            var polyTarget = validTypes.FirstOrDefault(t =>
                t.FullyQualifiedName == fullName && !t.DerivedTypes.IsDefaultOrEmpty
            );
            spc.AddSource(
                hintName,
                SourceText.From(
                    polyTarget.FullyQualifiedName is not null
                        ? GenPolyInner(fullName, safeName, polyTarget)
                        : GenInner(fullName, safeName, props),
                    Encoding.UTF8
                )
            );
        }

        // Generate inner helpers for nested Dictionary types
        foreach (var kv in nestedDictTypes)
        {
            var fullName = kv.Key;
            var dictProp = kv.Value;
            var safeName = PicoSerDe.Gen.GenInfrastructure.UniqueName(fullName);
            var hintName = $"{safeName}_TomlDictInner.g.cs";
            if (hintNames.Add(hintName))
                spc.AddSource(
                    hintName,
                    SourceText.From(GenDictInner(fullName, safeName, dictProp), Encoding.UTF8)
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
            var safeFq = PicoSerDe.Gen.GenInfrastructure.UniqueName(t.FullyQualifiedName ?? "");
            var hintName = $"{safeFq}_TomlSerializer.g.cs";
            string code;
            if (t.IsRefLikeType)
                code = GenRefStruct(t);
            else if (t.IsTopLevelList || t.ArrayElementKind is not null)
                code = GenList(t);
            else if (!t.DerivedTypes.IsDefaultOrEmpty)
                code = GenPoly(t, typeMap);
            else
                code = Gen(t);
            if (!hintNames.Add(hintName))
            {
                PicoSerDe.Gen.GenInfrastructure.ReportHintNameCollision(
                    spc,
                    hintName,
                    t.FullyQualifiedName
                );
                continue;
            }
            spc.AddSource(hintName, SourceText.From(code, Encoding.UTF8));
        }
    }

    static string GenInner(string fqn, string shortName, ImmutableArray<PropertyInfo> props)
    {
        var clean = fqn.Replace("global::", "");
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
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
        // Same ordering rule as the top level: everything that is not a table
        // section first, then the sections. Sorted by key inside each pass so the
        // output stays deterministic.
        // RegisterCustom<T> first: a customized object member is a key/value line, and a
        // key/value line may not follow a `[table]` header (TOML scoping).
        EmitCustomObjectMemberWrites(s, props, "value", "        ");
        foreach (var p in props.OrderBy(x => EmitOrderKey(x)).ThenBy(x => x.JsonName))
            EmitSerializeProp(s, p, "value", "        ");
        s.AppendLine("    }");

        s.Append("    internal static ");
        s.Append(clean);
        s.AppendLine(" Deserialize(ref TomlReader r) {");
        s.Append("        var o = new ");
        s.Append(clean);
        s.AppendLine("();");
        // TOML is section-based and never emits ObjectEnd. Dotted-key lines re-emit
        // ObjectStart with the *same* TablePath, so an inner helper continues across
        // PropertyName and same-section ObjectStart tokens, and stops at the first token of
        // another section (or EOF) - otherwise it swallows every following [section]. The
        // name is copied out of the reader: a ReadOnlySpan<byte> must not outlive the
        // Read() calls in the loop below (the reader re-points TablePath on every token).
        s.AppendLine("        var __section = r.TablePath.ToArray();");
        s.AppendLine("        r.Read();");
        s.AppendLine("        while (true) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        var sorted = props.OrderBy(x => x.JsonName).ToImmutableArray();
        EmitPropertyDispatch(s, sorted, "k", "o", "                ", "                    ", null);
        s.AppendLine("                if (!r.Read()) break;");
        s.AppendLine("                continue;");
        s.AppendLine("            }");
        // A CHILD section of this one (`[Parent.Key]`) is how a nested table - and therefore
        // every recursion - is written. Dispatch it by the ONE segment below this section and
        // let that member's helper consume its own subtree; the loop must NOT advance
        // afterwards, because the helper already left the reader on the next header.
        s.AppendLine("            if (r.TokenType == TokenType.ObjectStart) {");
        s.AppendLine("                if (TextHelpers.Eq(r.TablePath, __section)) {");
        s.AppendLine(
            "                    if (!r.Read()) break; // dotted key re-opening this table"
        );
        s.AppendLine("                    continue;");
        s.AppendLine("                }");
        s.AppendLine("                var __childPos = r.BytesConsumed;");
        s.AppendLine("                if (r.TablePath.Length > __section.Length + 1");
        s.AppendLine("                    && r.TablePath[__section.Length] == (byte)'.'");
        s.AppendLine(
            "                    && MemoryExtensions.SequenceEqual(r.TablePath.Slice(0, __section.Length), __section)) {"
        );
        s.AppendLine("                    var __child = r.TablePath.Slice(__section.Length + 1);");
        var childTables = props
            .Where(IsNestedTableMember)
            .OrderBy(x => x.JsonName)
            .ToImmutableArray();
        EmitPropertyDispatch(
            s,
            childTables,
            "__child",
            "o",
            "                    ",
            "                        ",
            null
        );
        // Progress guard (the D-9 lesson): a child header that matches no member must leave
        // this helper, never spin on the same token. The caller advances it.
        s.AppendLine(
            "                    if (r.TokenType == TokenType.ObjectStart && r.BytesConsumed == __childPos) break;"
        );
        s.AppendLine("                    continue;");
        s.AppendLine("                }");
        s.AppendLine("            }");
        s.AppendLine("            break;");
        s.AppendLine("        }");
        s.AppendLine("        return o;");
        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    static string GenDictInner(string fqn, string shortName, PropertyInfo dp)
    {
        var clean = fqn.Replace("global::", "");
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using PicoSerDe.Core; using PicoToml;");
        var lastDot = clean.LastIndexOf('.');
        if (!clean.Contains('<') && lastDot > 0)
        {
            s.Append("namespace ");
            s.Append(clean.Substring(0, lastDot));
            s.AppendLine(";");
        }
        s.AppendLine();
        s.Append("internal static class ");
        s.Append(shortName);
        s.AppendLine("TomlDictInner {");

        // Serialize: write key-value pairs inline (caller handles WriteStartInlineTable/WriteEndInlineTable)
        s.Append("    internal static void SerializeContent(TomlWriter tw, ");
        s.Append(clean);
        s.AppendLine(" value) {");
        s.AppendLine("        foreach (var __kvp in value) {");
        switch (dp.ElementTypeKind)
        {
            case "string":
                s.AppendLine("            tw.WriteKeyValue(__kvp.Key, __kvp.Value!);");
                break;
            case "int32":
            case "int64":
            case "int16":
            case "uint16":
            case "sbyte":
            case "byte":
            case "uint32":
            case "uint64":
            case "float32":
            case "float64":
                s.AppendLine("            tw.WriteKeyValue(__kvp.Key, __kvp.Value);");
                break;
            case "boolean":
                s.AppendLine("            tw.WriteKeyValue(__kvp.Key, __kvp.Value);");
                break;
            case "any":
                s.AppendLine(
                    "            if (__kvp.Value != null) tw.WriteKeyValue(__kvp.Key, __kvp.Value.ToString()!);"
                );
                break;
            default:
                s.AppendLine("            tw.WriteKeyValue(__kvp.Key, __kvp.Value.ToString());");
                break;
        }
        s.AppendLine("        }");
        s.AppendLine("    }");

        // Deserialize
        s.Append("    internal static ");
        s.Append(clean);
        s.AppendLine(" Deserialize(ref TomlReader r) {");
        s.Append("        var o = new ");
        s.Append(clean);
        s.AppendLine("();");
        s.AppendLine("        r.Read(); // skip ObjectStart from inline table");
        s.AppendLine("        while (r.Read() && r.TokenType == TokenType.PropertyName) {");
        EmitTomlDictKeyRead(s, dp, "            ");
        switch (dp.ElementTypeKind)
        {
            case "string":
                s.AppendLine("            o[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
                break;
            case "int32":
                s.AppendLine("            r.TryGetInt32(out var __dv); o[__dk] = __dv;");
                break;
            case "int64":
                s.AppendLine("            r.TryGetInt64(out var __dv); o[__dk] = __dv;");
                break;
            case "int16":
                s.AppendLine(
                    "            r.TryGetInt32(out var __dv); o[__dk] = checked((short)__dv);"
                );
                break;
            case "uint16":
                s.AppendLine(
                    "            r.TryGetInt32(out var __dv); o[__dk] = checked((ushort)__dv);"
                );
                break;
            case "sbyte":
                s.AppendLine(
                    "            r.TryGetInt32(out var __dv); o[__dk] = checked((sbyte)__dv);"
                );
                break;
            case "byte":
                s.AppendLine(
                    "            r.TryGetInt32(out var __dv); o[__dk] = checked((byte)__dv);"
                );
                break;
            case "uint32":
                s.AppendLine(
                    "            r.TryGetInt64(out var __dv); o[__dk] = checked((uint)__dv);"
                );
                break;
            case "uint64":
                s.AppendLine("            r.TryGetUInt64(out var __dv); o[__dk] = __dv;");
                break;
            case "float32":
                s.AppendLine("            r.TryGetFloat64(out var __dv); o[__dk] = (float)__dv;");
                break;
            case "float64":
                s.AppendLine("            r.TryGetFloat64(out var __dv); o[__dk] = __dv;");
                break;
            case "boolean":
                s.AppendLine("            r.TryGetBool(out var __dv); o[__dk] = __dv;");
                break;
            case "datetime":
                s.AppendLine("            var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.AppendLine(
                    "            System.DateTime.TryParse(__raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dv);"
                );
                s.AppendLine("            o[__dk] = __dv;");
                break;
            case "guid":
                s.AppendLine("            var __raw = Encoding.UTF8.GetString(r.ValueSpan);");
                s.AppendLine(
                    "            System.Guid.TryParse(__raw, out var __dv); o[__dk] = __dv;"
                );
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlInner",
                    dp.ElementTypeName!
                );
                s.Append("            o[__dk] = ");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref r);");
                break;
            }
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlDictInner",
                    dp.ElementTypeName!
                );
                s.Append("            o[__dk] = ");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref r);");
                break;
            }
            default:
                s.AppendLine("            o[__dk] = Encoding.UTF8.GetString(r.ValueSpan);");
                break;
        }
        s.AppendLine("        }");
        s.AppendLine("        return o;");
        s.AppendLine("    }");
        s.AppendLine("}");
        return s.ToString();
    }

    static void EmitTomlDictKeyRead(StringBuilder s, PropertyInfo dp, string indent)
    {
        switch (dp.KeyTypeKind)
        {
            case "string":
                s.Append(indent);
                s.AppendLine("var __dk = Encoding.UTF8.GetString(r.KeySpan);");
                break;
            case "int32":
                s.Append(indent);
                s.AppendLine("int.TryParse(r.KeySpan, out var __dk);");
                break;
            case "int64":
                s.Append(indent);
                s.AppendLine("long.TryParse(r.KeySpan, out var __dk);");
                break;
            case "int16":
                s.Append(indent);
                s.AppendLine("short.TryParse(r.KeySpan, out var __dk);");
                break;
            case "uint16":
                s.Append(indent);
                s.AppendLine("ushort.TryParse(r.KeySpan, out var __dk);");
                break;
            case "sbyte":
                s.Append(indent);
                s.AppendLine("sbyte.TryParse(r.KeySpan, out var __dk);");
                break;
            case "byte":
                s.Append(indent);
                s.AppendLine("byte.TryParse(r.KeySpan, out var __dk);");
                break;
            case "uint32":
                s.Append(indent);
                s.AppendLine("uint.TryParse(r.KeySpan, out var __dk);");
                break;
            case "uint64":
                s.Append(indent);
                s.AppendLine("ulong.TryParse(r.KeySpan, out var __dk);");
                break;
            default:
                s.Append(indent);
                s.AppendLine("var __dk = Encoding.UTF8.GetString(r.KeySpan);");
                break;
        }
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

        s.Append("file readonly struct ");
        s.Append(t.Name);
        s.Append("_TomlSer : ISerializer<");
        s.Append(t.Name);
        s.AppendLine(">, global::PicoSerDe.Core.IOptionsSerializer<");
        s.Append(t.Name);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(t.Name);
        s.AppendLine(" v) => Serialize(w, v, null);");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(t.Name);
        s.AppendLine(" v, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoToml.TomlOptions?)options;");
        s.AppendLine("        var tw = new TomlWriter(w, indented: __opts?.Indented ?? false);");
        EmitCustomObjectMemberWrites(s, t.Properties, "v", "        ");
        foreach (var p in t.Properties.OrderBy(x => EmitOrderKey(x)))
            EmitSerializeProp(s, p, "v", "        ");
        s.AppendLine("    } }");

        s.Append("file readonly struct ");
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
                var tn = cp.TypeFullName;
                s.Append("        ");
                s.Append(tn);
                s.Append(" __cp_");
                s.Append(ci);
                s.Append(" = ");
                s.Append(DefaultLiterals.For(cp.TypeKind, DefaultLiteralRow.CtorTempNull));
                s.AppendLine(";");
            }
        }
        else
        {
            var tomlReq = t.Properties.Where(p => p.IsRequired).ToArray();
            if (tomlReq.Length > 0)
            {
                s.Append("        var o = new ");
                s.Append(t.Name);
                s.AppendLine(" {");
                foreach (var rp in tomlReq)
                {
                    DefaultLiterals.EmitRequiredMemberLine(s, rp, "            ");
                }
                s.Append("        };");
            }
            else
            {
                s.Append("        var o = new ");
                s.Append(t.Name);
                s.AppendLine("();");
            }
        }
        // Arrays of tables are collected into a list first (arrays have no Add).
        var tomlArrayObjProps = t
            .Properties.Where(x => x.TypeKind == "array" && IsArrayOfTablesMember(x))
            .ToArray();
        int TomlCtorIndex(string name) =>
            ctorMap is not null && ctorMap.TryGetValue(name, out var idx) ? idx : -1;
        foreach (var ap in tomlArrayObjProps)
        {
            s.Append("        System.Collections.Generic.List<");
            s.Append(ap.ElementTypeName ?? "object");
            s.Append(">? __arr_");
            s.Append(ap.Name);
            s.AppendLine(" = null;");
        }
        s.AppendLine("        r.Read();");
        s.AppendLine("        while (true) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        // Scalar types, flat lists AND dicts handled via PropertyName dispatch.
        // Only complex-object lists (ArrayStart/[[key]]) and nested objects (ObjectStart/[key]) excluded.
        var scalarProps = t
            .Properties.Where(x => x.TypeKind != "object" && !IsArrayOfTablesMember(x))
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
        var listObjProps = t.Properties.Where(x => IsArrayOfTablesMember(x)).ToImmutableArray();
        // ObjectStart branch (dicts and nested objects)
        if (objProps.Length > 0 || dictProps.Length > 0)
        {
            s.AppendLine("            if (r.TokenType == TokenType.ObjectStart) {");
            s.AppendLine("                var __tblPos = r.BytesConsumed;");
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
            // This branch must always PROGRESS - stopping at EOF alone is not enough.
            //
            // (a) A matched table leaves the NEXT table header current and undispatched, so
            //     a blind `Read()` here would swallow that header and silently drop the
            //     following section's keys - hence no unconditional advance.
            // (b) A header matching NO arm (e.g. `[tool.ruff]`, `[build-system]` on a model
            //     without those sections) issues no `Read()` at all: the token stays
            //     `ObjectStart` at the same offset and this `continue` re-dispatches it for
            //     ever. Real TOML files are full of such headers.
            //
            // So: stop when the reader is exhausted (a bare `[V]` at EOF, D-9), and
            // otherwise advance exactly once when this branch consumed nothing (b).
            s.AppendLine("                if (r.TokenType == TokenType.None) break;");
            s.AppendLine(
                "                if (r.TokenType == TokenType.ObjectStart && r.BytesConsumed == __tblPos && !r.Read()) break;"
            );
            s.AppendLine("                continue;");
            s.AppendLine("            }");
        }
        // ArrayStart branch ([[key]] array of tables for List<ComplexObject>)
        if (listObjProps.Length > 0)
        {
            s.AppendLine("            if (r.TokenType == TokenType.ArrayStart) {");
            s.AppendLine("                var __arrPos = r.BytesConsumed;");
            // The ARM match is same-level as well: a hand-written `[[Parent.Items]]` names the
            // member by its LAST dotted segment (`TextHelpers.EqLastSegment`). Matching the whole
            // path against the bare member name skipped the entire section instead of mis-reading
            // one element (C-1). `[table]` headers below keep whole-path matching on purpose: a
            // nested table is dispatched by prefix inside its own helper, and relaxing those would
            // let a real-world `[tool.ruff]` populate a member named `ruff`.
            // A POLYMORPHIC element carries its own discriminator, so the element type's helper
            // reads it whole. The inline loop below is the BASE's-members bug at element position:
            // it wrote `Id = 4` and nothing else.
            bool __polyElems = false;
            foreach (var ap in listObjProps.Where(x => x.ElementIsPolyBase))
            {
                s.Append("                ");
                s.Append(__polyElems ? "else " : "");
                s.Append("if (TextHelpers.EqLastSegment(r.TablePath, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName));
                s.AppendLine("\"u8)) {");
                EmitPolyArrayTableRead(s, ap, "o", "                    ", false);
                s.AppendLine("                }");
                __polyElems = true;
            }
            for (int ai = 0; ai < listObjProps.Length; ai++)
            {
                var ap = listObjProps[ai];
                if (ap.ElementIsPolyBase)
                    continue;
                s.Append("                ");
                s.Append(ai == 0 && !__polyElems ? "if" : "else if");
                s.Append(" (TextHelpers.EqLastSegment(r.TablePath, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName));
                s.AppendLine("\"u8)) {");
                var elemTypeName = ap.ElementTypeName ?? "object";
                s.Append("                    var __item = new ");
                s.Append(elemTypeName);
                s.AppendLine("();");
                s.AppendLine("                    while (true) {");
                s.AppendLine("                        var __itPos = r.BytesConsumed;");
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
                if (ap.TypeKind == "array")
                {
                    // Arrays materialize from the collected elements.
                    s.AppendLine(
                        "                        __arr_"
                            + ap.Name
                            + " ??= new System.Collections.Generic.List<"
                            + (ap.ElementTypeName ?? "object")
                            + ">();"
                    );
                    s.AppendLine("                        __arr_" + ap.Name + ".Add(__item);");
                }
                else
                {
                    s.AppendLine(
                        "                        o."
                            + ap.Name
                            + " ??= new System.Collections.Generic.List<"
                            + (ap.ElementTypeName ?? "object")
                            + ">();"
                    );
                    s.AppendLine("                        o." + ap.Name + ".Add(__item);");
                }
                s.AppendLine(
                    "                        if (r.TokenType != TokenType.ArrayStart) break;"
                );
                // D-9d: the NEXT `[[…]]` header is another element of THIS list only when
                // it names this table. A foreign one (`[[Unknown]]`) used to append an EMPTY
                // element — a value the document never had — because the loop reused
                // every `ArrayStart` token. Stop instead and let the enclosing dispatch
                // advance past the header it does not own.
                // The name is compared AT THE SAME LEVEL — the header's LAST dotted segment
                // (`TextHelpers.EqLastSegment`). A hand-written `[[Parent.Items]]` carries the whole
                // path while an arm owns ONE member, and whole-path equality matched no header at all:
                // every element of a standard nested array-of-tables was silently dropped (C-1).
                s.AppendLine(
                    "                        if (!TextHelpers.EqLastSegment(r.TablePath, \""
                        + PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName)
                        + "\"u8)) break;"
                );
                // Local no-progress guard (I-3): the loop only used to stop because the
                // reader reports a different token at EOF. If that ever regresses, this
                // breaks instead of appending empty elements for ever (an OOM, not a hang).
                s.AppendLine("                        if (r.BytesConsumed == __itPos) break;");
                s.AppendLine(
                    "                        __item = new "
                        + (ap.ElementTypeName ?? "object")
                        + "();"
                );
                s.AppendLine("                    }");
                s.AppendLine("                }");
            }
            // Same progress contract as the ObjectStart branch above: a `[[…]]` header
            // naming no known list, or an element loop that consumed nothing, must not
            // leave `ArrayStart` current for the next pass to re-dispatch for ever.
            s.AppendLine("                if (r.TokenType == TokenType.None) break;");
            s.AppendLine(
                "                if (r.TokenType == TokenType.ArrayStart && r.BytesConsumed == __arrPos && !r.Read()) break;"
            );
            s.AppendLine("                continue;");
            s.AppendLine("            }");
        }
        s.AppendLine("            if (!r.Read()) break;");
        s.AppendLine("        }");
        foreach (var ap in tomlArrayObjProps)
        {
            s.Append("        if (__arr_");
            s.Append(ap.Name);
            s.Append(" is not null) ");
            // Constructor parameters receive the array directly.
            var apCtor = TomlCtorIndex(ap.Name);
            if (apCtor >= 0)
            {
                s.Append("__cp_");
                s.Append(apCtor);
            }
            else
            {
                s.Append("o.");
                s.Append(ap.Name);
            }
            s.Append(" = __arr_");
            s.Append(ap.Name);
            s.AppendLine(".ToArray();");
        }
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

        // Streaming: property-level resume (ctor types fall back to buffering).
        // Types with arrays of tables are skipped: the resume strategy mutates
        // the collection in place and arrays have no Add/Count. Callers get the
        // explicit "no streaming deserializer registered" error instead of
        // non-compiling code; sync deserialization handles the array.
        var tomlHasArrayOfTables = t.Properties.Any(x =>
            x.TypeKind == "array" && x.ElementTypeKind == "object" && x.NestedProperties.Length > 0
        );
        if (!hasCtor && !tomlHasArrayOfTables)
            EmitTomlStreaming(s, t, ctorMap);
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
        if (hasCtor || tomlHasArrayOfTables)
            s.AppendLine(
                "            // Streaming skipped for this type (ctor / arrays of tables)"
            );
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

    /// <summary>
    /// Emits the streaming deserializer: the synchronous read loop with a
    /// snapshot/rewind guard per member, so an incomplete chunk resumes at the
    /// member start. Previously parsed members flow through <c>partial</c>.
    /// </summary>
    private static void EmitTomlStreaming(
        StringBuilder s,
        TypeInfo t,
        Dictionary<string, int>? ctorMap
    )
    {
        var req = t.Properties.Where(p => p.IsRequired).ToArray();
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("_TomlStreaming {");
        s.Append("    internal static ReadStatus DeserializeStreaming(ref TomlReader r, ");
        s.Append(t.Name);
        s.AppendLine("? partial, out ");
        s.Append(t.Name);
        s.AppendLine("? result) {");
        s.Append("        var o = partial ?? new ");
        s.Append(t.Name);
        if (req.Length > 0)
        {
            s.Append(" { ");
            foreach (var rp in req)
            {
                s.Append(rp.Name);
                s.Append(" = ");
                s.Append(DefaultLiterals.For(rp.TypeKind, DefaultLiteralRow.RequiredMember));
                s.Append(", ");
            }
            s.Append("}");
        }
        else
        {
            s.Append("()");
        }
        s.AppendLine(";");
        s.AppendLine("        result = o;");
        s.AppendLine("        if (!r.IsResumed) {");
        s.AppendLine(
            "            r.Mark(); if (!r.Read()) { if (r.NeedsMoreData) { r.RewindToMark(); return ReadStatus.NeedMoreData; } return ReadStatus.Success; }"
        );
        s.AppendLine("        }");
        s.AppendLine("        while (true) {");
        s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
        s.AppendLine("                var k = r.KeySpan;");
        var simpleProps = t
            .Properties.Where(x => x.TypeKind != "object" && !IsArrayOfTablesMember(x))
            .ToImmutableArray();
        EmitPropertyDispatch(
            s,
            simpleProps,
            "k",
            "o",
            "                ",
            "                    ",
            ctorMap
        );
        s.AppendLine(
            "                if (r.NeedsMoreData) { r.RewindToMark(); return ReadStatus.NeedMoreData; }"
        );
        s.AppendLine(
            "                r.Mark(); if (!r.Read()) { if (r.NeedsMoreData) { r.RewindToMark(); result = o; return ReadStatus.NeedMoreData; } result = o; return ReadStatus.Success; }"
        );
        s.AppendLine("                continue;");
        s.AppendLine("            }");
        var objProps = t.Properties.Where(x => x.TypeKind == "object").ToImmutableArray();
        var dictProps = t.Properties.Where(x => x.TypeKind == "dict").ToImmutableArray();
        if (objProps.Length > 0 || dictProps.Length > 0)
        {
            s.AppendLine("            if (r.TokenType == TokenType.ObjectStart) {");
            s.AppendLine("                var __tblPos = r.BytesConsumed;");
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
                s.Append(i == 0 && objProps.Length == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.Eq(tbl, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dictProps[i].JsonName));
                s.AppendLine("\"u8)) {");
                EmitDictRead(s, dictProps[i], "o", "                    ");
                s.AppendLine("                }");
            }
            s.AppendLine(
                "                if (r.NeedsMoreData) { r.RewindToMark(); return ReadStatus.NeedMoreData; }"
            );
            // Same progress guard as the buffered emitter: a header that matches no arm
            // reads nothing, so advance once instead of re-dispatching it for ever.
            s.AppendLine(
                "                if (r.BytesConsumed == __tblPos) { r.Mark(); if (!r.Read()) { if (r.NeedsMoreData) { r.RewindToMark(); result = o; return ReadStatus.NeedMoreData; } result = o; return ReadStatus.Success; } }"
            );
            s.AppendLine("                continue;");
            s.AppendLine("            }");
        }
        var listObjProps = t.Properties.Where(x => IsArrayOfTablesMember(x)).ToImmutableArray();
        if (listObjProps.Length > 0)
        {
            s.AppendLine("            if (r.TokenType == TokenType.ArrayStart) {");
            s.AppendLine("                var __arrPos = r.BytesConsumed;");
            // The ARM match is same-level as well: a hand-written `[[Parent.Items]]` names the
            // member by its LAST dotted segment (`TextHelpers.EqLastSegment`). Matching the whole
            // path against the bare member name skipped the entire section instead of mis-reading
            // one element (C-1). `[table]` headers below keep whole-path matching on purpose: a
            // nested table is dispatched by prefix inside its own helper, and relaxing those would
            // let a real-world `[tool.ruff]` populate a member named `ruff`.
            for (int ai = 0; ai < listObjProps.Length; ai++)
            {
                var ap = listObjProps[ai];
                var elemTypeName = ap.ElementTypeName ?? "object";
                s.Append("                ");
                s.Append(ai == 0 ? "if" : "else if");
                s.Append(" (TextHelpers.EqLastSegment(r.TablePath, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName));
                s.AppendLine("\"u8)) {");
                s.Append("                    int __cnt = o.");
                s.Append(ap.Name);
                s.AppendLine("?.Count ?? 0;");
                s.Append("                    var __item = new ");
                s.Append(elemTypeName);
                s.AppendLine("();");
                s.AppendLine("                    while (true) {");
                s.AppendLine("                        var __itPos = r.BytesConsumed;");
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
                s.AppendLine("                        if (r.NeedsMoreData) {");
                s.Append("                            if (o.");
                s.Append(ap.Name);
                s.Append(" != null && o.");
                s.Append(ap.Name);
                s.Append(".Count > __cnt) o.");
                s.Append(ap.Name);
                s.Append(".RemoveRange(__cnt, o.");
                s.Append(ap.Name);
                s.AppendLine(".Count - __cnt);");
                s.AppendLine("                            r.RewindToMark();");
                s.AppendLine("                            return ReadStatus.NeedMoreData;");
                s.AppendLine("                        }");
                s.Append("                        o.");
                s.Append(ap.Name);
                s.Append(" ??= new System.Collections.Generic.List<");
                s.Append(elemTypeName);
                s.AppendLine(">();");
                s.Append("                        o.");
                s.Append(ap.Name);
                s.AppendLine(".Add(__item);");
                s.AppendLine(
                    "                        if (r.TokenType != TokenType.ArrayStart) break;"
                );
                // D-9d: the NEXT `[[…]]` header is another element of THIS list only when
                // it names this table. A foreign one (`[[Unknown]]`) used to append an EMPTY
                // element — a value the document never had — because the loop reused
                // every `ArrayStart` token. Stop instead and let the enclosing dispatch
                // advance past the header it does not own.
                // The name is compared AT THE SAME LEVEL — the header's LAST dotted segment
                // (`TextHelpers.EqLastSegment`). A hand-written `[[Parent.Items]]` carries the whole
                // path while an arm owns ONE member, and whole-path equality matched no header at all:
                // every element of a standard nested array-of-tables was silently dropped (C-1).
                s.AppendLine(
                    "                        if (!TextHelpers.EqLastSegment(r.TablePath, \""
                        + PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName)
                        + "\"u8)) break;"
                );
                // Local no-progress guard (I-3), same reason as the buffered emitter.
                s.AppendLine("                        if (r.BytesConsumed == __itPos) break;");
                s.Append("                        __item = new ");
                s.Append(elemTypeName);
                s.AppendLine("();");
                s.AppendLine("                    }");
                s.AppendLine("                }");
            }
            // Same progress guard as the buffered emitter's `[[…]]` branch.
            s.AppendLine(
                "                if (r.BytesConsumed == __arrPos) { r.Mark(); if (!r.Read()) { if (r.NeedsMoreData) { r.RewindToMark(); result = o; return ReadStatus.NeedMoreData; } result = o; return ReadStatus.Success; } }"
            );
            s.AppendLine("                continue;");
            s.AppendLine("            }");
        }
        s.AppendLine(
            "            r.Mark(); if (!r.Read()) { if (r.NeedsMoreData) { r.RewindToMark(); result = o; return ReadStatus.NeedMoreData; } result = o; return ReadStatus.Success; }"
        );
        s.AppendLine("        }");
        s.AppendLine("    }");
        s.AppendLine("}");
    }

    /// <summary>Ref struct serializer — static class + delegate registration.</summary>
    private static string GenRefStruct(TypeInfo t)
    {
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine(
            "using System; using System.Buffers; using System.Text; using System.Runtime.CompilerServices;"
        );
        s.AppendLine("using PicoSerDe.Core; using PicoToml;");
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
        s.AppendLine("TomlSer {");
        s.Append("    public static void Serialize(IBufferWriter<byte> w, ");
        s.Append(t.Name);
        s.AppendLine(" v, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoToml.TomlOptions?)options;");
        s.AppendLine("        var tw = new TomlWriter(w, indented: __opts?.Indented ?? false);");
        foreach (var p in t.Properties)
        {
            s.Append("        tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\", ");
            s.Append($"v.{p.Name}");
            s.AppendLine(");");
        }
        s.AppendLine("    } }");
        s.AppendLine();

        // Registration
        var typeRef = string.IsNullOrEmpty(t.Namespace) ? t.Name : $"{t.Namespace}.{t.Name}";
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        global::PicoToml.TomlSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            ");
        s.Append(t.Name);
        s.AppendLine("TomlSer.Serialize);");
        s.AppendLine("    } }");
        return s.ToString();
    }

    /// <summary>Generates serializer/deserializer for a top-level List&lt;T&gt; using existing element helpers.</summary>
    private static string GenList(TypeInfo t)
    {
        var s = new StringBuilder();
        var listFqn = t.FullyQualifiedName;
        var ek = t.ArrayElementKind!;
        var et = t.ArrayElementName!;

        // The ONE array a root `List<T>` owns, shared by the writer and the reader (I-1):
        // the read side accepts a header whose LAST dotted segment is this name and skips
        // every other header, so both sides name the same array by construction.
        const string rootTableName = "data";

        // Synthetic PropertyInfo — drives EmitSerializeListElement / EmitDeserializeListElementTemp
        var elemP = new PropertyInfo(
            Name: "__elem",
            JsonName: "__elem",
            TypeKind: ek,
            TypeFullName: et,
            IsNullable: false,
            ElementTypeKind: ek,
            ElementTypeName: et,
            KeyTypeKind: null,
            KeyTypeName: null,
            NestedProperties: t.ArrayElementNestedProps.IsDefaultOrEmpty
                ? ImmutableArray<PropertyInfo>.Empty
                : t.ArrayElementNestedProps,
            ConverterTypeFullName: null
        );

        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using System.Runtime.CompilerServices;");
        s.AppendLine("using System.Collections.Generic; using PicoSerDe.Core; using PicoToml;");
        s.AppendLine();

        // ── Serializer ──
        s.Append("file readonly struct ");
        s.Append(t.Name);
        s.Append("_TomlSer : ISerializer<");
        s.Append(listFqn);
        s.AppendLine(">, global::PicoSerDe.Core.IOptionsSerializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(listFqn);
        s.AppendLine(" v) => Serialize(w, v, null);");
        s.Append("    public void Serialize(IBufferWriter<byte> w, ");
        s.Append(listFqn);
        s.AppendLine(" v, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoToml.TomlOptions?)options;");
        s.AppendLine("        var tw = new TomlWriter(w, indented: __opts?.Indented ?? false);");
        if (ek == "object" && IsObjectTableElement(elemP))
        {
            // Array of tables: [[data]] per element
            s.AppendLine("        foreach (var __item in v) {");
            s.AppendLine("            tw.WriteArrayTable(\"" + rootTableName + "\"u8);");
            EmitSerializeListElement(s, elemP, "            ");
            s.AppendLine("        }");
        }
        else
        {
            // Inline array: data = [1, 2, 3]
            s.AppendLine("        tw.WriteStartArray(\"data\"u8);");
            s.AppendLine("        foreach (var __item in v) {");
            EmitSerializeListElement(s, elemP, "            ");
            s.AppendLine("        }");
            s.AppendLine("        tw.WriteEndArray();");
        }
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Deserializer (reuses EmitDeserializeListElementTemp) ──
        s.Append("file readonly struct ");
        s.Append(t.Name);
        s.Append("_TomlDes : IDeserializer<");
        s.Append(listFqn);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(listFqn);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.Append("        var r = new TomlReader(data);");
        s.AppendLine("        r.Read();");
        s.Append("        var __tmpList = new List<");
        s.Append(et);
        s.AppendLine(">(16);");
        if (ek == "object" && IsObjectTableElement(elemP))
        {
            // Array of tables: one `[[data]]` per element (the write side writes exactly that name).
            //
            // D-9d at the ROOT — the `[[…]]` element loop of the outermost container. The
            // member-level emitters (buffered / streaming / list-temporary / polymorphic) express
            // D-9d with a `break`, and that is correct THERE because an object-level dispatch loop
            // re-enters the element loop for the next header. This one IS the outermost loop:
            // `Deserialize` returns right after it, so a `break` does not "let the owner handle
            // it", it ends the document. Measured (fix round 1, C-1):
            //   [[data]] Name="a" / [[Unknown]] / [[data]] Name="b"
            //     break-on-foreign  -> [a]      (b silently gone: no element, no exception, no
            //                                    diagnostic — the shape I-1/I-7 forbid)
            //     skip-and-continue -> [a, b]   (what TOML's own reader says:)
            //   tomllib.loads('[[data]]\na = 1\n[[Unknown]]\n[[data]]\nb = 2\n')
            //     -> {'data': [{'a': 1}, {'b': 2}], 'Unknown': [{}]}
            // So a header this list does not own is SKIPPED — header AND body — and the scan
            // continues. The body matters: leaving its `key = value` rows current would let the
            // next element's `__k` loop swallow them.
            //
            // WHICH array does the root list own (I-1)? The name the WRITER emits, `data`,
            // compared at the same level as every member-level arm (the header's LAST dotted
            // segment, so a hand-written `[[Parent.data]]` still reads). "Whichever header came
            // first" is not good enough: `[[Unknown]] a [[data]] b` would then make `Unknown` the
            // list's own name and drop `b` — the same silent loss in another shape — and a lone
            // `[[Unknown]]` would open one element where the member-level equivalent reads 0.
            // Price, recorded in spec §4 D-9d: a document whose array is named anything else reads
            // as 0 elements.
            s.AppendLine("        while (r.TokenType != TokenType.None)");
            s.AppendLine("        {");
            s.AppendLine(
                "            if (r.TokenType == TokenType.ArrayStart && TextHelpers.EqLastSegment(r.TablePath, \""
                    + rootTableName
                    + "\"u8))"
            );
            s.AppendLine("            {");
            // I-3 no-progress guard: the root loop has no outer loop to escape to, so a reader
            // that stopped advancing would append empty elements for ever (an OOM, not a hang).
            s.AppendLine("                var __itPos = r.BytesConsumed;");
            s.Append("                var __item = new ");
            s.Append(et);
            s.AppendLine("();");
            s.AppendLine(
                "                while (r.Read() && r.TokenType == TokenType.PropertyName)"
            );
            s.AppendLine("                {");
            s.AppendLine("                    var __k = r.KeySpan;");
            EmitPropertyDispatch(
                s,
                elemP.NestedProperties,
                "__k",
                "__item",
                "                    ",
                "                        "
            );
            s.AppendLine("                }");
            s.AppendLine("                __tmpList.Add(__item);");
            s.AppendLine("                if (r.BytesConsumed == __itPos) break;");
            s.AppendLine("                continue;");
            s.AppendLine("            }");
            s.AppendLine(
                "            // Foreign header (or a stray token before the first header):"
            );
            s.AppendLine(
                "            // skip it and its body, then keep looking for this list's array."
            );
            s.AppendLine("            var __skipPos = r.BytesConsumed;");
            s.AppendLine(
                "            while (r.Read() && !(r.TokenType == TokenType.ArrayStart && TextHelpers.EqLastSegment(r.TablePath, \""
                    + rootTableName
                    + "\"u8))) { }"
            );
            s.AppendLine("            if (r.TokenType == TokenType.None) break;");
            s.AppendLine("            if (r.BytesConsumed == __skipPos) break;");
            s.AppendLine("        }");
        }
        else
        {
            // Inline array: data = [1, 2, 3]
            s.AppendLine(
                "        if (r.TokenType == TokenType.PropertyName) r.Read(); // skip key"
            );
            s.AppendLine("        if (r.TokenType == TokenType.ArrayStart) {");
            s.AppendLine("            while (r.Read() && r.TokenType != TokenType.ArrayEnd) {");
            EmitDeserializeListElementTemp(s, elemP, "                ");
            s.AppendLine("            }");
            s.AppendLine("        }");
        }
        s.AppendLine("        return __tmpList;");
        s.AppendLine("    } }");
        s.AppendLine();

        // ── Registration ──
        s.Append("file static class ");
        s.Append(t.Name);
        s.AppendLine("_Reg {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void R() {");
        s.Append("        global::PicoToml.TomlSerializer.Register<");
        s.Append(listFqn);
        s.Append(">(new ");
        s.Append(t.Name);
        s.Append("_TomlSer(), new ");
        s.Append(t.Name);
        s.AppendLine("_TomlDes());");
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
                EmitDeserializePropOrAssign(s, props[i], target, bodyIndent, ctorMap);
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

    /// <summary>
    /// TOML ordering key for a member: <c>0</c> for values that stay on the current
    /// table (scalars, arrays) and <c>1</c> for members that open a new section
    /// (<c>[Name]</c> for a nested object or dictionary, <c>[[Name]]</c> for an
    /// object list). A section header ends the current table, so every section has
    /// to be written after the plain members — otherwise the members declared
    /// after it are emitted into that section and lost (C-11).
    /// </summary>
    private static int EmitOrderKey(PropertyInfo p) =>
        p.TypeKind switch
        {
            // A recursive member opens a section like any other nested table: EmitOrderKey
            // has to agree with the emit branch (IsNestedTableMember), or the header is
            // written before the sibling values and those land INSIDE the sub-table.
            "object" => IsNestedTableMember(p) ? 1 : 0,
            "dict" => 1,
            "list" or "array" => IsArrayOfTablesMember(p) ? 1 : 0,
            _ => 0,
        };

    /// <summary>
    /// The runtime half of <c>RegisterCustom</c> for TOML OBJECT members.
    /// <para>
    /// TOML is section-based: a nested object is a <c>[Table]</c> header and every key/value
    /// line written after one belongs to that table. A custom-serialized member is ONE
    /// key/value line instead, so it has to be written in the "keys first" region — before
    /// any table header — or a single <c>[Other]</c> sibling would swallow it. That is why
    /// this pass runs before the ordered member loop rather than inside the member branch,
    /// and why the branch itself is guarded with <c>!HasCustomSerializer</c> (see
    /// <see cref="EmitSerializeProp"/>): exactly one of the two writes, in the right region.
    /// </para>
    /// <para>
    /// Nothing is written when the type has no registration, so the unregistered document is
    /// byte-identical to what this generator wrote before RegisterCustom existed.
    /// </para>
    /// </summary>
    private static void EmitCustomObjectMemberWrites(
        StringBuilder s,
        IEnumerable<PropertyInfo> props,
        string target,
        string indent
    )
    {
        foreach (var p in props.Where(x => x.TypeKind == "object").OrderBy(x => x.JsonName))
        {
            // A cycle-truncated member (IsRecursiveRef) keeps its type's helper: its own
            // type name is the registration key the user wrote.
            var ct = (p.TypeFullName ?? "object").TrimEnd('?');
            var acc = target + "." + p.Name;
            var buf = "__rcb" + p.Name;
            s.Append(indent);
            s.Append("if (");
            // TOML has no null literal: a null member is OMITTED, so the custom write is
            // skipped too (the guarded table branch skips it for the same reason).
            if (p.IsNullableReference)
            {
                s.Append(acc);
                s.Append(" != null && ");
            }
            s.Append("global::PicoToml.TomlSerializer.HasCustomSerializer<");
            s.Append(ct);
            s.AppendLine(">())");
            s.Append(indent);
            s.AppendLine("{");
            s.Append(indent);
            s.Append("    var ");
            s.Append(buf);
            s.AppendLine(" = new System.Buffers.ArrayBufferWriter<byte>();");
            s.Append(indent);
            s.Append("    global::PicoToml.TomlSerializer.SerializeCustom<");
            s.Append(ct);
            s.Append(">(");
            s.Append(buf);
            s.Append(", ");
            s.Append(acc);
            s.AppendLine(");");
            s.Append(indent);
            s.Append("    tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, System.Text.Encoding.UTF8.GetString(");
            s.Append(buf);
            s.AppendLine(".WrittenSpan));");
            s.Append(indent);
            s.AppendLine("}");
        }
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

        if (PicoSerDe.Gen.CollectionKinds.IsSequence(p.TypeKind))
        {
            // Extended collection kinds (S7) share this branch: their wire form is the same
            // array, and the only difference is how the reader builds them back (see
            // EmitCollectionFromList below). Asking CollectionKinds.IsSequence instead of spelling
            // `is "list" or "array"` is what keeps a new kind from being dropped here.
            // TOML has no null literal — a null collection can only be omitted,
            // regardless of DefaultIgnoreCondition.
            bool collGuard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                s,
                p,
                $"{target}.{p.Name}",
                indent
            );
            // RegisterCustom<T> at the MEMBER-LIST position: when every element's type has a
            // registered serializer the list is ONE inline array carrying that text
            // (`Items = ["c1", "c2"]`), not the array of tables (`[[Items]]`) the built-in
            // path emits. Both arms are runtime-decided: registration is a runtime fact.
            //
            // Only an OBJECT element has a registration to honour (the same rule the JSON
            // generator applies to its `case "object"` arm): a nested collection
            // (`List<int[]>`) or a scalar has no member-position custom form to replace.
            var listAccessor = p.IsNullableReference ? $"{target}.{p.Name}!" : $"{target}.{p.Name}";
            bool customElements = p.ElementTypeKind is "object";
            var b = new StringBuilder();
            if (customElements)
            {
                s.Append(indent);
                s.Append("if (global::PicoToml.TomlSerializer.HasCustomSerializer<");
                s.Append((p.ElementTypeName ?? "object").TrimEnd('?'));
                s.AppendLine(">())");
                s.Append(indent);
                s.AppendLine("{");
                s.Append(indent);
                s.Append("    tw.WriteStartArray(\"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                s.AppendLine("\"u8);");
                s.Append(indent);
                s.Append("    foreach (var __item in ");
                s.Append(
                    PicoSerDe.Gen.CollectionKinds.SequenceItemsExpression(p.TypeKind, listAccessor)
                );
                s.AppendLine(")");
                s.Append(indent);
                s.AppendLine("    {");
                EmitSerializeListElement(s, p, indent + "        ");
                s.Append(indent);
                s.AppendLine("    }");
                s.Append(indent);
                s.AppendLine("    tw.WriteEndArray();");
                s.Append(indent);
                s.AppendLine("}");
                s.Append(indent);
                s.AppendLine("else");
                s.Append(indent);
                s.AppendLine("{");
            }
            // Array of tables ([[key]]) for List<ComplexObject>
            if (IsArrayOfTablesMember(p))
            {
                var elemTypeName = p.ElementTypeName ?? "object";
                var innerSn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlInner",
                    elemTypeName
                );
                b.Append(indent);
                b.Append("foreach (var __item in ");
                b.Append(listAccessor);
                b.AppendLine(")");
                b.Append(indent);
                b.AppendLine("{");
                b.Append(indent);
                b.Append("    tw.WriteArrayTable(\"");
                b.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                b.AppendLine("\"u8);");
                b.Append(indent);
                b.Append("    ");
                b.Append(innerSn);
                b.Append(".Serialize(tw, __item);");
                b.AppendLine();
                b.Append(indent);
                b.AppendLine("}");
            }
            else
            {
                b.Append(indent);
                b.Append("tw.WriteStartArray(\"");
                b.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
                b.AppendLine("\"u8);");
                b.Append(indent);
                b.Append("foreach (var __item in ");
                b.Append(
                    PicoSerDe.Gen.CollectionKinds.SequenceItemsExpression(
                        p.TypeKind,
                        target + "." + p.Name + (p.IsNullableReference ? "!" : "")
                    )
                );
                b.AppendLine(")");
                b.Append(indent);
                b.AppendLine("{");
                if (IsNestedCollectionMember(p))
                    EmitSerializeNestedArrayLevel(b, p, "__item", indent + "    ", 1);
                else
                    EmitSerializeListElement(b, p, indent + "    ");
                b.Append(indent);
                b.AppendLine("}");
                b.Append(indent);
                b.AppendLine("tw.WriteEndArray();");
            }
            s.Append(b);
            if (customElements)
            {
                s.Append(indent);
                s.AppendLine("}");
            }
            if (collGuard)
            {
                s.Append(indent);
                s.AppendLine("}");
            }
        }
        else if (p.TypeKind is "dict")
        {
            // TOML has no null literal — a null dictionary can only be omitted.
            bool dictGuard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                s,
                p,
                $"{target}.{p.Name}",
                indent
            );
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
            if (p.ElementTypeKind is "dict" || p.ElementTypeKind is "object")
            {
                // Nested dict/object: write as inline table via dict inner helper
                // The inner helper takes (TomlWriter, string key, Dict value)
                s.Append(indent);
                s.Append("    tw.WriteStartInlineTable(__kvp.Key);");
                s.AppendLine();
                if (p.ElementTypeKind is "dict")
                {
                    var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "TomlDictInner",
                        p.ElementTypeName!
                    );
                    s.Append(indent);
                    s.Append("    ");
                    s.Append(sn);
                    s.AppendLine(".SerializeContent(tw, __kvp.Value);");
                }
                else
                {
                    // RegisterCustom<T> at the DICT-VALUE position: the entry becomes one
                    // `key = "text"` row carrying what the registered serializer wrote,
                    // instead of the inline table ({ V = 1 }) the built-in path emits.
                    var ct = p.ElementTypeName!.TrimEnd('?');
                    var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "TomlInner",
                        p.ElementTypeName!
                    );
                    s.Append(indent);
                    s.AppendLine(
                        "    if (global::PicoToml.TomlSerializer.HasCustomSerializer<" + ct + ">())"
                    );
                    s.Append(indent);
                    s.AppendLine("    {");
                    s.Append(indent);
                    s.AppendLine(
                        "        var __rcbDict = new System.Buffers.ArrayBufferWriter<byte>();"
                    );
                    s.Append(indent);
                    s.Append("        global::PicoToml.TomlSerializer.SerializeCustom<");
                    s.Append(ct);
                    s.AppendLine(">(__rcbDict, __kvp.Value!);");
                    s.Append(indent);
                    s.AppendLine(
                        "        tw.WriteKeyValue(__kvp.Key, System.Text.Encoding.UTF8.GetString(__rcbDict.WrittenSpan));"
                    );
                    s.Append(indent);
                    s.AppendLine("    }");
                    s.Append(indent);
                    s.AppendLine("    else");
                    s.Append(indent);
                    s.AppendLine("    {");
                    s.Append(indent);
                    s.Append("        ");
                    s.Append(sn);
                    s.AppendLine(".Serialize(tw, __kvp.Value);");
                    s.Append(indent);
                    s.AppendLine("    }");
                }
                s.Append(indent);
                s.AppendLine("    tw.WriteEndInlineTable();");
            }
            else if (
                PicoSerDe.Gen.CollectionKinds.IsSequence(p.ElementTypeKind)
                && p.NestedProperties.Length > 0
            )
            {
                // S10: the dict VALUE is a collection. TOML writes it as an inline array
                // under the entry's own key (`k = [1, 2]`), and a nested chain reuses the
                // same per-level emitter the member arm uses - one level per call - so
                // `Dictionary<string, List<List<int>>>` needs no second implementation.
                // Before this branch the value fell through to the scalar accessor and was
                // written as the collection's ToString().
                var valAcc = PicoSerDe.Gen.CollectionKinds.IsNullableSequence(p.ElementTypeKind)
                    ? "__kvp.Value!"
                    : "__kvp.Value";
                s.Append(indent);
                s.AppendLine("    tw.WriteStartArray(Encoding.UTF8.GetBytes(__kvp.Key));");
                s.Append(indent);
                s.Append("    foreach (var __item in ");
                s.Append(
                    PicoSerDe.Gen.CollectionKinds.SequenceItemsExpression(p.ElementTypeKind, valAcc)
                );
                s.AppendLine(")");
                s.Append(indent);
                s.AppendLine("    {");
                EmitSerializeNestedArrayLevel(
                    s,
                    p.NestedProperties[0],
                    "__item",
                    indent + "    ",
                    1
                );
                s.Append(indent);
                s.AppendLine("    }");
                s.Append(indent);
                s.AppendLine("    tw.WriteEndArray();");
            }
            else
            {
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
                    "__kvp.Value!"
                );
                s.AppendLine(");");
            }
            s.Append(indent);
            s.AppendLine("}");
            if (dictGuard)
            {
                s.Append(indent);
                s.AppendLine("}");
            }
        }
        else if (IsNestedTableMember(p))
        {
            // A member carried as a nested table: an object with its own members, or a
            // cycle-truncated one (IsRecursiveRef) whose members live in the TARGET type's
            // helper. R2 Task 5 opened the second case with S11a/S11b x toml.
            //
            // The header is written through WriteNestedTable, NOT WriteTable: a cycle closes
            // by nesting a table in a table, so the header must carry its ancestor path
            // (`[Child]` then `[Child.Child]`, `[B]` then `[B.A]`). Every pair is closed with
            // EndTable so the next sibling stays at this level — an unbalanced pair turns
            // `[W]` into `[V.W]` and loses the document.
            // RegisterCustom<T>: a registered custom serializer wins over the nested table.
            // Its key/value line was already written by EmitCustomObjectMemberWrites (in the
            // "keys first" region, where a key/value line may live); this branch is the
            // built-in half and must stay silent then, or every customized member would be
            // written twice. The body is built in its own builder so the guard does not have
            // to re-indent it.
            var b = new StringBuilder();
            bool nullGuard = PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(
                b,
                p,
                $"{target}.{p.Name}",
                indent
            );
            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("TomlInner", p.TypeFullName!);
            var innerPad = nullGuard ? indent + "    " : indent;
            b.Append(innerPad);
            b.Append("tw.WriteNestedTable(\"");
            b.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            b.AppendLine("\"u8);");
            b.Append(innerPad);
            b.Append(sn);
            b.Append(".Serialize(tw, ");
            b.Append(target);
            b.Append('.');
            b.Append(p.Name);
            b.AppendLine(");");
            b.Append(innerPad);
            b.AppendLine("tw.EndTable();");
            if (nullGuard)
            {
                b.Append(indent);
                b.AppendLine("}");
            }
            s.Append(indent);
            s.Append("if (!global::PicoToml.TomlSerializer.HasCustomSerializer<");
            s.Append(p.TypeFullName!.TrimEnd('?'));
            s.AppendLine(">())");
            s.Append(indent);
            s.AppendLine("{");
            s.Append(b);
            s.Append(indent);
            s.AppendLine("}");
        }
        else if (p.IsNullable && !p.IsNullableReference)
        {
            // TOML has no null literal — null scalars are always omitted,
            // regardless of DefaultIgnoreCondition.
            PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(s, p, $"{target}.{p.Name}", indent);
            s.Append(indent);
            s.Append("    tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, ");
            EmitValueAccessor(s, p, $"{target}.{p.Name}!.Value");
            s.AppendLine(");");
            s.Append(indent);
            s.AppendLine("}");
        }
        else if (p.IsNullable && p.IsNullableReference)
        {
            // TOML has no null literal — null scalars are always omitted.
            PicoSerDe.Gen.GenInfrastructure.EmitNullGuardOpen(s, p, $"{target}.{p.Name}", indent);
            s.Append(indent);
            s.Append("    tw.WriteKeyValue(\"");
            s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName));
            s.Append("\"u8, ");
            EmitValueAccessor(s, p, $"{target}.{p.Name}!");
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
        if (op.IsRecursiveRef && op.NestedProperties.IsEmpty)
        {
            // A cycle-truncated member has no member list of its own — the target type's
            // helper owns them. Reading it is what closes the cycle, and it is why the
            // helper's own loop has to accept CHILD table headers (see GenInner).
            var rsn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("TomlInner", op.TypeFullName!);
            s.Append(pad);
            s.Append(tgt);
            s.Append('.');
            s.Append(op.Name);
            s.Append(" = ");
            s.Append(rsn);
            s.AppendLine(".Deserialize(ref r);");
            return;
        }
        // NOTE (R2 Task 5): the old "nested tables deeper than one level" guard is GONE — it
        // refused a chain that now reads correctly, because each helper dispatches its OWN
        // child tables (see GenInner) and every header carries its ancestors. What still has
        // no read path is an object COLLECTION inside a table (an array of tables is written
        // unqualified), and that one keeps throwing: a refused read is a report, an empty
        // collection would be silent loss (R2).
        if (PicoSerDe.Gen.GenInfrastructure.HasNestedObjectCollection(op))
        {
            s.Append(pad);
            s.AppendLine(
                "throw new System.NotSupportedException(\"TOML cannot read an object collection from inside a nested table yet.\");"
            );
            return;
        }
        if (op.NestedProperties.Length > 0 || IsEmptyObjectShape(op))
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

    /// <summary>
    /// Emits the read of ONE dictionary value that is itself a collection (S10): the bracket
    /// pair written by the dict arm of <see cref="EmitSerializeProp"/> is read into a temp
    /// list of the value's ELEMENT type - the extractor's descriptor chain
    /// (<c>NestedProperties[0]</c>) describes that element - and the shared
    /// <see cref="PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList"/> turns it into the
    /// declared kind, exactly like the member arm does.
    /// <para>
    /// One copy, two callers: the section-based read (<see cref="EmitDictRead"/>, what our own
    /// writer produces: <c>[Map]</c> + <c>k = [...]</c>) and the inline-table read
    /// (<c>Map = { k = [...] }</c>, what an external TOML file may contain). TOML used to have
    /// two hand-written dict value switches that disagreed; a third copy of the seven kinds
    /// is not how this family should grow.
    /// </para>
    /// <para>
    /// Both callers hand over a reader standing on the entry's PROPERTY NAME (the scalar arms
    /// read the pre-parsed <c>ValueSpan</c> from there), so this level takes the one
    /// <c>Read()</c> that enters the bracket pair — same shape as the member-level list arm.
    /// </para>
    /// </summary>
    private static void EmitDictValueCollectionRead(
        StringBuilder s,
        PropertyInfo dp,
        string valueTarget,
        string pad
    )
    {
        var elemNode = dp.NestedProperties[0];
        var elemType = elemNode.ElementTypeNameAnnotated ?? elemNode.ElementTypeName ?? "object";
        s.Append(pad);
        s.Append("var __dl = new System.Collections.Generic.List<");
        s.Append(elemType);
        s.AppendLine(">(16);");
        s.Append(pad);
        s.AppendLine("if (r.Read() && r.TokenType == TokenType.ArrayStart)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.AppendLine("    while (r.Read() && r.TokenType != TokenType.ArrayEnd)");
        s.Append(pad);
        s.AppendLine("    {");
        if (PicoSerDe.Gen.CollectionKinds.IsSequence(elemNode.ElementTypeKind))
            EmitDeserializeNestedArrayLevel(s, dp, "__dl", pad + "        ", 1);
        else
            EmitDeserializeListElementTemp(s, elemNode, pad + "        ", "__dl");
        s.Append(pad);
        s.AppendLine("    }");
        s.Append(pad);
        s.AppendLine("}");
        PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
            s,
            dp.ElementTypeKind!,
            "__dl",
            valueTarget,
            elemType,
            pad
        );
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
        s.Append(dp.ElementTypeNameAnnotated ?? dp.ElementTypeName ?? "int");
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
                s.AppendLine(
                    "    if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
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
                s.AppendLine(
                    "    if (!r.TryGetInt64(out var __dv)) throw new System.FormatException($\"Expected a 64-bit integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "int16":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine(
                    "    if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = checked((short)__dv);");
                break;
            case "uint16":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine(
                    "    if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = checked((ushort)__dv);");
                break;
            case "sbyte":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine(
                    "    if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = checked((sbyte)__dv);");
                break;
            case "byte":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine(
                    "    if (!r.TryGetInt32(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = checked((byte)__dv);");
                break;
            case "uint32":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine(
                    "    if (!r.TryGetInt64(out var __dv)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                );
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = checked((uint)__dv);");
                break;
            case "uint64":
                s.AppendLine();
                s.Append(pad);
                s.AppendLine(
                    "    if (!r.TryGetUInt64(out var __dv)) throw new System.FormatException($\"Expected a 64-bit unsigned integer at offset {r.BytesConsumed}\");"
                );
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
                s.AppendLine(
                    "    if (!r.TryGetFloat64(out var __dv)) throw new System.FormatException($\"Expected a floating-point number at offset {r.BytesConsumed}\");"
                );
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
                s.AppendLine(
                    "    if (!r.TryGetFloat64(out var __dv)) throw new System.FormatException($\"Expected a floating-point number at offset {r.BytesConsumed}\");"
                );
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
                s.AppendLine(
                    "    if (!r.TryGetBool(out var __dv)) throw new System.FormatException($\"Expected a boolean at offset {r.BytesConsumed}\");"
                );
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
                s.Append(
                    PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(dp.ElementTypeName ?? "object")
                );
                s.AppendLine(">(__raw, out var __dv);");
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.AppendLine("[__dk] = __dv;");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlDictInner",
                    dp.ElementTypeName!
                );
                s.AppendLine();
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.Append("[__dk] = ");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref r);");
                break;
            }
            // One sequence arm, asked of the table: CollectionKinds.IsSequence (nine kinds, one row each).
            case var __kind when PicoSerDe.Gen.CollectionKinds.IsSequence(__kind):
                s.AppendLine();
                EmitDictValueCollectionRead(s, dp, tgt + "." + dp.Name + "[__dk]", pad + "    ");
                break;
            case "object":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlInner",
                    dp.ElementTypeName!
                );
                s.AppendLine();
                s.Append(pad);
                s.Append("    ");
                s.Append(tgt);
                s.Append('.');
                s.Append(dp.Name);
                s.Append("[__dk] = ");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref r);");
                break;
            }
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
                // C-14: TOML numbers are locale-free, and the reader parses
                // invariantly — so write invariantly too.
                s.Append(accessor);
                s.Append(".ToString(\"c\", System.Globalization.CultureInfo.InvariantCulture)");
                break;
            case "decimal":
                // C-14: de-DE used to emit "1,5" (not valid TOML).
                s.Append(accessor);
                s.Append(".ToString(System.Globalization.CultureInfo.InvariantCulture)");
                break;
            case "guid":
            case "enum":
                s.Append(accessor);
                s.Append(".ToString()");
                break;
            case "any":
                s.Append(accessor);
                s.Append("?.ToString() ?? \"\"");
                break;
            default:
                s.Append(accessor);
                break;
        }
    }

    private static void EmitSerializeListElement(
        StringBuilder s,
        PropertyInfo p,
        string indent,
        string itemVar = "__item"
    )
    {
        // TOML has no null type: null scalar elements are skipped (YAML does the
        // same), so a nullable string element needs a guard before the writer.
        if (p.ElementIsNullableReference && p.ElementTypeKind is "string")
        {
            s.Append(indent);
            s.Append("if (");
            s.Append(itemVar);
            s.AppendLine(" != null)");
            s.Append(indent);
            s.Append("    tw.WriteArrayValue(");
            s.Append(itemVar);
            s.AppendLine(");");
            return;
        }
        switch (p.ElementTypeKind)
        {
            case "string":
                s.Append(indent);
                s.Append("tw.WriteArrayValue(");
                s.Append(itemVar);
                s.AppendLine(");");
                break;
            case "int32":
            case "int64":
            case "int16":
            case "uint16":
            case "sbyte":
            case "byte":
            case "uint32":
            case "uint64":
            case "float32":
            case "float64":
                s.Append(indent);
                s.Append("tw.WriteArrayValue(");
                s.Append(itemVar);
                s.AppendLine(");");
                break;
            case "boolean":
                s.Append(indent);
                s.Append("tw.WriteArrayValue(");
                s.Append(itemVar);
                s.AppendLine(");");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlDictInner",
                    p.ElementTypeName!
                );
                s.Append(indent);
                s.Append(sn);
                s.Append(".SerializeContent(tw, ");
                s.Append(itemVar);
                s.AppendLine(");");
                break;
            }
            case "object":
            {
                // RegisterCustom<T> at the ELEMENT position: one array value per element,
                // carrying the registered serializer's text, instead of the element's own
                // table shape ([[key]] for an object list, an inline table inside a dict).
                var ct = p.ElementTypeName!.TrimEnd('?');
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlInner",
                    p.ElementTypeName!
                );
                s.Append(indent);
                s.AppendLine(
                    "if (global::PicoToml.TomlSerializer.HasCustomSerializer<" + ct + ">())"
                );
                s.Append(indent);
                s.AppendLine("{");
                s.Append(indent);
                s.AppendLine("    var __rcbElem = new System.Buffers.ArrayBufferWriter<byte>();");
                s.Append(indent);
                s.Append("    global::PicoToml.TomlSerializer.SerializeCustom<");
                s.Append(ct);
                s.Append(">(__rcbElem, ");
                s.Append(itemVar);
                s.AppendLine(");");
                s.Append(indent);
                s.Append(
                    "    tw.WriteArrayValue(System.Text.Encoding.UTF8.GetString(__rcbElem.WrittenSpan));"
                );
                s.AppendLine();
                s.Append(indent);
                s.AppendLine("}");
                s.Append(indent);
                s.AppendLine("else");
                s.Append(indent);
                s.Append("    ");
                s.Append(sn);
                s.Append(".Serialize(tw, ");
                s.Append(itemVar);
                s.AppendLine(");");
                break;
            }
            default:
                s.Append(indent);
                s.Append("tw.WriteArrayValue(");
                s.Append(itemVar);
                s.AppendLine(".ToString());");
                break;
        }
    }

    /// <summary>
    /// True for a collection member whose elements are themselves collections
    /// (<c>List&lt;List&lt;int&gt;&gt;</c>, <c>List&lt;int[]&gt;</c>,
    /// <c>List&lt;List&lt;List&lt;int&gt;&gt;&gt;</c>). TOML encodes those as an inline
    /// array of inline arrays (F6); object/dict leaves never get here because the
    /// shared extractor drops them with PICOSERDE004.
    /// </summary>
    /// <summary>
    /// True when this member is written as a nested TOML table: an object with its own
    /// members, or a cycle-truncated member (<see cref="PropertyInfo.IsRecursiveRef"/>,
    /// whose members live in the target type's helper). R2 Task 5 opened the second case —
    /// before it, a recursive member reached no emit branch at all and TOML wrote nothing
    /// for it while the table said the format can hold a cycle.
    /// </summary>
    private static bool IsNestedTableMember(PropertyInfo p) =>
        p.TypeKind == "object"
        && (p.NestedProperties.Length > 0 || p.IsRecursiveRef || IsEmptyObjectShape(p));

    /// <summary>
    /// An object whose serialized shape is EMPTY — nothing was extracted under its table header
    /// and there is no cycle target to delegate to (BU-4).
    /// <para>
    /// N-5 asked whether the predicate is too wide. It asks "did extraction SEE any member?", not
    /// "does the type declare zero members?", and the two differ: a type whose members are all
    /// dropped (only <c>KeyValuePair</c>-shaped / unresolvable members) or all
    /// <c>[PicoIgnore]</c>d also comes back with no nested properties. Measured, that is not a
    /// misfire, and the difference is deliberate:
    /// </para>
    /// <list type="bullet">
    /// <item>the dropped member keeps its own loud PICOSERDE001 (⏸) naming it, so the empty table
    ///       is never a SILENT loss — json has written <c>{}</c> for exactly the same model since
    ///       long before BU-4, so tightening this predicate would make TOML the odd one out;</item>
    /// <item>the alternative branch is the SCALAR one, which writes the object itself as the value
    ///       (<c>tw.WriteKeyValue(key, v.Mark)</c>) — a compile error in the consumer's build
    ///       instead of a document. That is the bug this predicate closed;</item>
    /// <item>an all-<c>[PicoIgnore]</c> type is not lossy at all: not serializing those members was
    ///       the user's instruction, so <c>{}</c> IS the shape.</item>
    /// </list>
    /// <para>
    /// The question "does the type declare zero members?" belongs to the RESOLVER, where it decides
    /// the empty-object shape (<c>TypeKindResolver.DeclaresPublicInstanceData</c>, and since N-1 the
    /// same gates decide the diagnostic verdict through <c>EmptyObjectBlockOf</c>). This predicate
    /// answers the EMITTER's question — what did we get to write? — and it is the right one here.
    /// </para>
    /// <para>
    /// TOML has the form: a table header with no keys, <c>[Mark]</c>, which is what an empty table
    /// is (measured: <c>tomllib.loads('[Mark]')</c> → <c>{'Mark': {}}</c>).
    /// </para>
    /// </summary>
    private static bool IsEmptyObjectShape(PropertyInfo p) =>
        p.NestedProperties.Length == 0 && !p.IsRecursiveRef;

    /// <summary>
    /// Does this member carry object ELEMENTS as their own table — an object list written as an
    /// array of tables (<c>[[Marks]]</c>)?
    /// <para>
    /// The answer used to be "the element has members" (<c>NestedProperties.Length &gt; 0</c>),
    /// which is the same question only while an empty element is unrepresentable: for a
    /// ZERO-member element the length is 0, the member fell through to the INLINE-ARRAY emitter,
    /// and each element went through the scalar accessor (a compile error). <c>[[Marks]]</c> with
    /// nothing under it is exactly one empty table (measured:
    /// <c>tomllib.loads('[[Marks]]')</c> → <c>{'Marks': [{}]}</c>), so the array-of-tables path
    /// is the right one for it too.
    /// </para>
    /// </summary>
    private static bool IsArrayOfTablesMember(PropertyInfo p) =>
        (p.TypeKind is "list" or "array")
        && p.ElementTypeKind == "object"
        && IsObjectTableElement(p);

    /// <summary>
    /// The element-position half of <see cref="IsArrayOfTablesMember"/>: every object element is
    /// its own table EXCEPT a cycle-truncated one, whose members live in the target type's helper
    /// (<see cref="PropertyInfo.IsRecursiveRef"/>).
    /// </summary>
    private static bool IsObjectTableElement(PropertyInfo element) =>
        element.NestedProperties.Length > 0
        || (element.NestedProperties.Length == 0 && !element.IsRecursiveRef);

    private static bool IsNestedCollectionMember(PropertyInfo p) =>
        PicoSerDe.Gen.CollectionKinds.IsSequence(p.TypeKind)
        && PicoSerDe.Gen.CollectionKinds.IsSequence(p.ElementTypeKind)
        && p.NestedProperties.Length > 0;

    /// <summary>
    /// Emits the body of the <c>foreach (var __itemN in ...)</c> that runs per
    /// element of one nesting level: either another bracketed level
    /// (<c>tw.WriteStartArray("")</c> … <c>tw.WriteEndArray()</c>) or the scalar
    /// leaf write, which reuses the flat list element emitter.
    /// </summary>
    private static void EmitSerializeNestedArrayLevel(
        StringBuilder s,
        PropertyInfo node,
        string itemVar,
        string indent,
        int depth
    )
    {
        if (
            PicoSerDe.Gen.CollectionKinds.IsSequence(node.ElementTypeKind)
            && node.NestedProperties.Length > 0
        )
        {
            var innerVar =
                "__item" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
            s.Append(indent);
            s.AppendLine("tw.WriteStartArray(\"\"u8);");
            s.Append(indent);
            s.Append("foreach (var ");
            s.Append(innerVar);
            s.Append(" in ");
            s.Append(
                PicoSerDe.Gen.CollectionKinds.SequenceItemsExpression(node.ElementTypeKind, itemVar)
            );
            s.AppendLine(")");
            s.Append(indent);
            s.AppendLine("{");
            EmitSerializeNestedArrayLevel(
                s,
                node.NestedProperties[0],
                innerVar,
                indent + "    ",
                depth + 1
            );
            s.Append(indent);
            s.AppendLine("}");
            s.Append(indent);
            s.AppendLine("tw.WriteEndArray();");
            return;
        }
        if (node.ElementTypeKind == "object" && node.NestedProperties.Length > 0)
        {
            // The object leaf of a nested chain is an INLINE TABLE (`V = [[{ Name = "a" }]]`,
            // spec §3 S4). WriteStartInlineTable always writes `key = { `, and an array
            // element has no key, so the value form is what this position needs — the same
            // per-member emitters then write the `name = value` pairs, comma separated, inside
            // the braces.
            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                "TomlInner",
                node.ElementTypeName!
            );
            s.Append(indent);
            s.AppendLine("tw.WriteStartInlineTableValue();");
            s.Append(indent);
            s.Append(sn);
            s.Append(".Serialize(tw, ");
            s.Append(itemVar);
            s.AppendLine(");");
            s.Append(indent);
            s.AppendLine("tw.WriteEndInlineTable();");
            return;
        }
        EmitSerializeListElement(s, node, indent, itemVar);
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

        if (PicoSerDe.Gen.CollectionKinds.IsSequence(p.TypeKind))
        {
            // Extended collection kinds (S7) are read into the same temp list and then
            // converted into the DECLARED type by EmitCollectionFromList — the one shared
            // copy of the seven conversions (json used to own the only one).
            s.Append(pad);
            s.Append("var __tmpList = new System.Collections.Generic.List<");
            s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object");
            s.AppendLine(">(16);");
            // Array of tables ([[key]]) for List<ComplexObject>
            if (IsArrayOfTablesMember(p))
            {
                // Loop: each ArrayStart from [[key]] is one item; read its properties inline.
                var elemTypeName = p.ElementTypeName ?? "object";
                s.Append(pad);
                s.AppendLine("while (r.Read())");
                s.Append(pad);
                s.AppendLine("{");
                s.Append(pad);
                s.AppendLine("    if (r.TokenType != TokenType.ArrayStart) break;");
                // D-9d: the NEXT `[[…]]` header is another element of THIS list only when
                // it names this table. A foreign one (`[[Unknown]]`) used to append an EMPTY
                // element — a value the document never had — because the loop reused
                // every `ArrayStart` token. Stop instead and let the enclosing dispatch
                // advance past the header it does not own.
                // The name is compared AT THE SAME LEVEL — the header's LAST dotted segment
                // (`TextHelpers.EqLastSegment`). A hand-written `[[Parent.Items]]` carries the whole
                // path while an arm owns ONE member, and whole-path equality matched no header at all:
                // every element of a standard nested array-of-tables was silently dropped (C-1).
                s.AppendLine(
                    "    if (!TextHelpers.EqLastSegment(r.TablePath, \""
                        + PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(p.JsonName)
                        + "\"u8)) break;"
                );
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
                if (IsNestedCollectionMember(p))
                    EmitDeserializeNestedArrayLevel(s, p, "__tmpList", pad + "        ", 1);
                else
                    EmitDeserializeListElementTemp(s, p, pad + "        ");
                s.Append(pad);
                s.AppendLine("    }");
                s.Append(pad);
                s.AppendLine("}");
            }
            PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
                s,
                p.TypeKind!,
                "__tmpList",
                ctorAssign ? tgt : tgt + "." + p.Name,
                p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "object",
                pad
            );
        }
        else
        {
            // C-30: a null literal must not reach the scalar parsers. Dict and
            // object members keep their existing empty-value behavior.
            var __scalar = p.TypeKind is not ("dict" or "object");
            if (__scalar)
                NullGuardEmit.EmitMemberNullGuard(s, p, EmitAssign, pad, "r");
            switch (p.TypeKind)
            {
                case "string":
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
                case "int32":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = __v;");
                    break;
                case "int64":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt64(out var __v)) throw new System.FormatException($\"Expected a 64-bit integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __v;");
                    break;
                case "int16":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = checked((short)__v);");
                    break;
                case "uint16":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = checked((ushort)__v);");
                    break;
                case "sbyte":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = checked((sbyte)__v);");
                    break;
                case "byte":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt32(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = checked((byte)__v);");
                    break;
                case "uint32":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetInt64(out var __v)) throw new System.FormatException($\"Expected an integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = checked((uint)__v);");
                    break;
                case "uint64":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetUInt64(out var __v)) throw new System.FormatException($\"Expected a 64-bit unsigned integer at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    EmitAssign();
                    s.AppendLine(" = __v;");
                    break;
                case "float32":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetFloat64(out var __v)) throw new System.FormatException($\"Expected a floating-point number at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = (float)__v;");
                    break;
                case "float64":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetFloat64(out var __v)) throw new System.FormatException($\"Expected a floating-point number at offset {r.BytesConsumed}\");"
                    );
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = __v;");
                    break;
                case "boolean":
                    s.Append(pad);
                    s.AppendLine(
                        "if (!r.TryGetBool(out var __v)) throw new System.FormatException($\"Expected a boolean at offset {r.BytesConsumed}\");"
                    );
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
                        s.Append("if (!System.DateTime.TryParseExact(__raw, \"");
                        s.Append(p.DateTimeFormat);
                        s.Append(
                            "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt)) throw new System.FormatException($\"Invalid DateTime at offset {r.BytesConsumed}\");"
                        );
                    }
                    else
                        s.AppendLine(
                            "if (!System.DateTime.TryParse(__raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt)) throw new System.FormatException($\"Invalid DateTime at offset {r.BytesConsumed}\");"
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
                    s.Append(PicoSerDe.Gen.GenInfrastructure.MapEnumReadType(p.TypeFullName));
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
                    s.Append(p.ElementTypeNameAnnotated ?? p.ElementTypeName ?? "string");
                    s.AppendLine(">();");
                    s.Append(pad);
                    s.AppendLine("r.Read(); // skip ObjectStart from inline table");
                    s.Append(pad);
                    s.AppendLine("while (r.Read() && r.TokenType == TokenType.PropertyName) {");
                    s.Append(pad);
                    s.Append("    var __dk = Encoding.UTF8.GetString(r.KeySpan);");
                    switch (p.ElementTypeKind)
                    {
                        // One sequence arm, asked of the table: CollectionKinds.IsSequence (nine kinds, one row each).
                        case var __kind when PicoSerDe.Gen.CollectionKinds.IsSequence(__kind):
                            // Same S10 value position as the section arm below: an inline
                            // table can hold the same `k = [1, 2]` entries, so it goes
                            // through the same emitter instead of storing the collection's
                            // text.
                            s.AppendLine();
                            EmitDictValueCollectionRead(
                                s,
                                p,
                                tgt + "." + p.Name + "[__dk]",
                                pad + "    "
                            );
                            break;
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
                        case "int16":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetInt32(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = checked((short)__dv);");
                            break;
                        case "uint16":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetInt32(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = checked((ushort)__dv);");
                            break;
                        case "sbyte":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetInt32(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = checked((sbyte)__dv);");
                            break;
                        case "byte":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetInt32(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = checked((byte)__dv);");
                            break;
                        case "uint32":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetInt64(out var __dv);");
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.AppendLine("[__dk] = checked((uint)__dv);");
                            break;
                        case "uint64":
                            s.AppendLine();
                            s.Append(pad);
                            s.AppendLine("    r.TryGetUInt64(out var __dv);");
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
                        case "dict":
                        {
                            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                                "TomlDictInner",
                                p.ElementTypeName!
                            );
                            s.AppendLine();
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.Append("[__dk] = ");
                            s.Append(sn);
                            s.AppendLine(".Deserialize(ref r);");
                            break;
                        }
                        case "object":
                        {
                            var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                                "TomlInner",
                                p.ElementTypeName!
                            );
                            s.AppendLine();
                            s.Append(pad);
                            s.Append("    ");
                            s.Append(tgt);
                            s.Append('.');
                            s.Append(p.Name);
                            s.Append("[__dk] = ");
                            s.Append(sn);
                            s.AppendLine(".Deserialize(ref r);");
                            break;
                        }
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
                case "object":
                {
                    if (PicoSerDe.Gen.GenInfrastructure.HasNestedObjectCollection(p))
                    {
                        s.Append(pad);
                        s.AppendLine(
                            "throw new System.NotSupportedException(\"TOML cannot read an object collection from inside a nested table yet.\");"
                        );
                        break;
                    }
                    var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "TomlInner",
                        p.TypeFullName!
                    );
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.Append(" = ");
                    s.Append(sn);
                    s.AppendLine(".Deserialize(ref r);");
                    break;
                }
                default:
                    s.Append(pad);
                    s.Append(tgt);
                    s.Append('.');
                    s.Append(p.Name);
                    s.AppendLine(" = Encoding.UTF8.GetString(r.ValueSpan);");
                    break;
            }
            if (__scalar)
            {
                s.Append(pad);
                s.AppendLine("}");
            }
        }
    }

    private static void EmitDeserializeListElementTemp(
        StringBuilder s,
        PropertyInfo p,
        string pad,
        string listVar = "__tmpList"
    )
    {
        switch (p.ElementTypeKind)
        {
            case "string":
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
            case "int32":
                s.Append(pad);
                s.AppendLine("r.TryGetInt32(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(__ev);");
                break;
            case "int64":
                s.Append(pad);
                s.AppendLine("r.TryGetInt64(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(__ev);");
                break;
            case "int16":
                s.Append(pad);
                s.AppendLine("r.TryGetInt32(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(checked((short)__ev));");
                break;
            case "uint16":
                s.Append(pad);
                s.AppendLine("r.TryGetInt32(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(checked((ushort)__ev));");
                break;
            case "sbyte":
                s.Append(pad);
                s.AppendLine("r.TryGetInt32(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(checked((sbyte)__ev));");
                break;
            case "byte":
                s.Append(pad);
                s.AppendLine("r.TryGetInt32(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(checked((byte)__ev));");
                break;
            case "uint32":
                s.Append(pad);
                s.AppendLine("r.TryGetInt64(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(checked((uint)__ev));");
                break;
            case "uint64":
                s.Append(pad);
                s.AppendLine("r.TryGetUInt64(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(__ev);");
                break;
            case "float32":
                s.Append(pad);
                s.AppendLine("r.TryGetFloat64(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add((float)__ev);");
                break;
            case "float64":
                s.Append(pad);
                s.AppendLine("r.TryGetFloat64(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(__ev);");
                break;
            case "boolean":
                s.Append(pad);
                s.AppendLine("r.TryGetBool(out var __ev);");
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(__ev);");
                break;
            case "dict":
            {
                var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "TomlDictInner",
                    p.ElementTypeName!
                );
                s.Append(pad);
                s.Append(listVar);
                s.Append(".Add(");
                s.Append(sn);
                s.AppendLine(".Deserialize(ref r));");
                break;
            }
            default:
                s.Append(pad);
                s.Append(listVar);
                s.AppendLine(".Add(Encoding.UTF8.GetString(r.ValueSpan));");
                break;
        }
    }

    /// <summary>
    /// Reads ONE inline table that is an ELEMENT of a nested array
    /// (<c>V = [[{ Name = "a", N = 1 }]]</c>, spec §3 S4): the reader is positioned on
    /// the <c>{</c> token, the properties are read with the same per-member emitters a
    /// section uses, and the <c>}</c> ends the element.
    /// <para>
    /// The element type is a scalar-only object by construction: the extractor drops a leaf
    /// whose members would need a <c>[table]</c> header or a nested value
    /// (<c>GenInfrastructure.IsInlineTableShaped</c>), so no branch here has to place a table
    /// inside an array.
    /// </para>
    /// </summary>
    private static void EmitDeserializeInlineTableElement(
        StringBuilder s,
        PropertyInfo node,
        string listVar,
        string pad
    )
    {
        var elemTypeName = node.ElementTypeName ?? "object";
        s.Append(pad);
        s.AppendLine("if (r.TokenType == TokenType.ObjectStart)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.Append("    var __inline = new ");
        s.Append(elemTypeName);
        s.AppendLine("();");
        s.Append(pad);
        s.AppendLine("    while (r.Read() && r.TokenType == TokenType.PropertyName)");
        s.Append(pad);
        s.AppendLine("    {");
        s.Append(pad);
        s.AppendLine("        var __ik = r.KeySpan;");
        EmitPropertyDispatch(
            s,
            node.NestedProperties,
            "__ik",
            "__inline",
            pad + "        ",
            pad + "            "
        );
        s.Append(pad);
        s.AppendLine("    }");
        s.Append(pad);
        s.Append("    ");
        s.Append(listVar);
        s.AppendLine(".Add(__inline);");
        s.Append(pad);
        s.AppendLine("}");
    }

    /// <summary>
    /// Emits the body that reads ONE element of the collection described by
    /// <paramref name="node"/> into <paramref name="listVar"/>. For a nested level the
    /// reader is already positioned on the opening <c>[</c> of the inner inline array
    /// (the caller's <c>r.Read()</c> advanced onto it), so the level reads its own
    /// bracket pair into a fresh accumulator and adds that accumulator to the
    /// enclosing list — <c>ToArray()</c> when the level is a real array.
    /// </summary>
    private static void EmitDeserializeNestedArrayLevel(
        StringBuilder s,
        PropertyInfo node,
        string listVar,
        string pad,
        int depth
    )
    {
        if (node.ElementTypeKind == "object" && node.NestedProperties.Length > 0)
        {
            EmitDeserializeInlineTableElement(s, node, listVar, pad);
            return;
        }
        if (
            !PicoSerDe.Gen.CollectionKinds.IsSequence(node.ElementTypeKind)
            || node.NestedProperties.Length == 0
        )
        {
            EmitDeserializeListElementTemp(s, node, pad, listVar);
            return;
        }
        var innerVar =
            "__tmpList" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        s.Append(pad);
        s.AppendLine("if (r.TokenType == TokenType.ArrayStart)");
        s.Append(pad);
        s.AppendLine("{");
        s.Append(pad);
        s.Append("    var ");
        s.Append(innerVar);
        // The accumulator holds the ELEMENT type of the level being entered,
        // which is described by the next node in the chain (node describes this
        // level, so node.ElementTypeName* is still the enclosing element type).
        s.Append(" = new System.Collections.Generic.List<");
        s.Append(
            node.NestedProperties[0].ElementTypeNameAnnotated
                ?? node.NestedProperties[0].ElementTypeName
                ?? "object"
        );
        s.AppendLine(">(16);");
        s.Append(pad);
        s.AppendLine("    while (r.Read() && r.TokenType != TokenType.ArrayEnd)");
        s.Append(pad);
        s.AppendLine("    {");
        EmitDeserializeNestedArrayLevel(
            s,
            node.NestedProperties[0],
            innerVar,
            pad + "        ",
            depth + 1
        );
        s.Append(pad);
        s.AppendLine("    }");
        // The level's items are in innerVar now. When this level IS an extended
        // collection, the DECLARED element type is that collection — convert before adding.
        var converted = innerVar;
        if (PicoSerDe.Gen.CollectionKinds.IsExtendedCollection(node.ElementTypeKind))
        {
            var convVar =
                "__cnv" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
            s.Append(pad);
            s.Append("    ");
            s.Append(node.ElementTypeNameAnnotated ?? node.ElementTypeName ?? "object");
            s.Append(' ');
            s.Append(convVar);
            s.AppendLine(" = default!;");
            PicoSerDe.Gen.GenInfrastructure.EmitCollectionFromList(
                s,
                node.ElementTypeKind!,
                innerVar,
                convVar,
                node.NestedProperties[0].ElementTypeNameAnnotated
                    ?? node.NestedProperties[0].ElementTypeName
                    ?? "object",
                pad + "    "
            );
            converted = convVar;
        }
        s.Append(pad);
        s.Append("    ");
        s.Append(listVar);
        s.Append(".Add(");
        s.Append(converted);
        if (node.ElementTypeKind == "array")
            s.Append(".ToArray()");
        s.AppendLine(");");
        s.Append(pad);
        s.AppendLine("}");
    }

    static string GenPoly(TypeInfo type, Dictionary<string, TypeInfo> typeMap)
    {
        var dpn = PicoSerDe.Gen.GenInfrastructure.DiscriminatorName(type);
        // Concrete bases get a synthesized discriminator so a base-typed
        // instance serializes as a valid value (and round-trips).
        var __baseDisc = PicoSerDe.Gen.GenInfrastructure.BaseDiscriminator(type);
        // The ONE definition of the case list, shared with the other generators: the MEMBER-position
        // dispatch below has to agree with the ROOT one about which cases exist, or a value written
        // by one is unreadable by the other.
        var __cases = PicoSerDe.Gen.GenInfrastructure.PolyCases(type);
        // The classes BOTH positions call. They cannot be `file`-scoped: the member-position helper
        // lives in another generated file, and the name must be unique per TYPE, not per short name.
        var __polyPrefix = PicoSerDe.Gen.GenInfrastructure.PolyClassPrefix(type);

        var dpnEsc = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dpn);
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using System.Runtime.CompilerServices;");
        s.AppendLine("using PicoSerDe.Core; using PicoToml;");
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            s.Append("using ");
            s.Append(type.Namespace);
            s.AppendLine(";");
        }
        s.AppendLine();

        // ── Serializer (root document) ──
        // Its tag+switch is NOT spelled here: a polymorphic base is also reached as a MEMBER of
        // another document (`[Member]`, `[[Items]]`), and a `file` struct taking a whole document
        // could not be called from that other generated file — which is exactly how a member was
        // written as the BASE's own members, with the tag and every derived member silently gone.
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("TomlSerializer : ISerializer<");
        s.Append(type.Name);
        s.AppendLine(">, global::PicoSerDe.Core.IOptionsSerializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value) => Serialize(writer, value, null);");
        s.Append("    public void Serialize(IBufferWriter<byte> writer, ");
        s.Append(type.Name);
        s.AppendLine(" value, global::PicoSerDe.Core.SerOptions? options) {");
        s.AppendLine("        var __opts = (global::PicoToml.TomlOptions?)options;");
        s.AppendLine(
            "        var tw = new TomlWriter(writer, indented: __opts?.Indented ?? false);"
        );
        s.Append("        ");
        s.Append(__polyPrefix);
        s.AppendLine("TomlPolySer.SerializeMembers(tw, value);");
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();

        // ── the dispatch both positions share ──
        EmitPolySer(s, type, dpnEsc, __baseDisc, __cases, typeMap, __polyPrefix);
        EmitPolyDes(s, type, dpnEsc, __cases, typeMap, __polyPrefix);

        // ── Deserializer (root document) ──
        s.Append("file readonly struct ");
        s.Append(type.Name);
        s.Append("TomlDeserializer : IDeserializer<");
        s.Append(type.Name);
        s.AppendLine("> {");
        s.Append("    public ");
        s.Append(type.Name);
        s.AppendLine(" Deserialize(ReadOnlySpan<byte> data) {");
        s.AppendLine("        var r = new TomlReader(data);");
        s.AppendLine("        r.Read();");
        s.Append("        return ");
        s.Append(__polyPrefix);
        s.AppendLine("TomlPolyDes.DeserializeRoot(ref r);");
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();

        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"{type.Namespace}.{type.Name}";
        s.Append("file static class ");
        s.Append(type.Name);
        s.AppendLine("SerDeRegistration {");
        s.AppendLine("    [ModuleInitializer]");
        s.AppendLine("    internal static void Register() {");
        s.Append("        global::PicoToml.TomlSerializer.Register<");
        s.Append(typeRef);
        s.AppendLine(">(");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("TomlSerializer(),");
        s.Append("            new ");
        s.Append(type.Name);
        s.AppendLine("TomlDeserializer());");
        s.AppendLine("    }");
        s.AppendLine("}");

        return s.ToString();
    }

    /// <summary>
    /// The one place a polymorphic type's WRITE dispatch is spelled: the discriminator row, then the
    /// runtime-type switch. Emitted as a cross-file class (not inline in the root serializer) because
    /// the MEMBER position calls it from another generated file; that is the whole shape this fix is
    /// about — the root serializer used to be the ONLY writer, and a member had nowhere to go.
    /// </summary>
    static void EmitPolySer(
        StringBuilder s,
        TypeInfo type,
        string dpnEsc,
        string? __baseDisc,
        ImmutableArray<PicoSerDe.Gen.DerivedTypeInfo> __cases,
        Dictionary<string, TypeInfo> typeMap,
        string __polyPrefix
    )
    {
        s.Append("internal static class ");
        s.Append(__polyPrefix);
        s.AppendLine("TomlPolySer {");
        s.Append("    internal static void SerializeMembers(TomlWriter tw, ");
        s.Append(type.Name);
        s.AppendLine(" value) {");
        s.Append("        tw.WriteKeyValue(\"");
        s.Append(dpnEsc);
        s.Append("\", (value switch { ");
        foreach (var dt in __cases)
        {
            var ds = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            var desc = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dt.TypeDiscriminator);
            s.Append(ds);
            s.Append(" => \"");
            s.Append(desc);
            s.Append("\", ");
        }
        s.AppendLine(
            __baseDisc is null
                ? "_ => \"\" }).AsSpan());"
                : "_ => \""
                    + PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(__baseDisc)
                    + "\" }).AsSpan());"
        );
        s.AppendLine("        switch (value)");
        s.AppendLine("        {");
        foreach (var dt in __cases)
        {
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out var dti))
                continue;
            var ds = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            s.Append("            case ");
            s.Append(ds);
            s.AppendLine(" __v:");
            EmitCustomObjectMemberWrites(s, dti.Properties, "__v", "                ");
            foreach (var prop in dti.Properties.OrderBy(x => EmitOrderKey(x)))
            {
                // The shared member emitter covers scalars, nested objects/dicts ('[Section]') and
                // object collections ('[[key]]') — the poly branch previously emitted scalars only.
                EmitSerializeProp(s, prop, "__v", "                ");
            }
            s.AppendLine("                break;");
        }
        s.AppendLine("        }");
        s.AppendLine("    }");
        s.AppendLine("}");
        s.AppendLine();
    }

    /// <summary>
    /// The one place a polymorphic type's READ dispatch is spelled, in the two forms the two reader
    /// positions enter it with:
    /// <list type="bullet">
    ///   <item><c>DeserializeRoot</c> — a whole document: the current token IS the discriminator row
    ///         (the implicit root section).</item>
    ///   <item><c>DeserializeSection</c> — a MEMBER or an ELEMENT: the current token is the opening
    ///         token of the value's own section (<c>[Member]</c> / <c>[[Items]]</c>) and the
    ///         discriminator is that section's first row, which is where the writer put it.</item>
    /// </list>
    /// Both end in ONE per-case reader, which carries the ctor map, the child-table dispatch and the
    /// object-collection loop exactly once. A copy per position is what would drift.
    /// <para>
    /// The per-case reader is <b>prefix-relative</b>: a section entry owns only its own children
    /// (<c>[Member.X]</c>) and stops at anything else, while the root entry has an EMPTY prefix and
    /// therefore treats every header as its own. Without that, the member position read the
    /// <c>[[Items]]</c> section that follows into the member and handed back its element (measured:
    /// <c>Lives=7</c> where the document says <c>9</c>).
    /// </para>
    /// </summary>
    static void EmitPolyDes(
        StringBuilder s,
        TypeInfo type,
        string dpnEsc,
        ImmutableArray<PicoSerDe.Gen.DerivedTypeInfo> __cases,
        Dictionary<string, TypeInfo> typeMap,
        string __polyPrefix
    )
    {
        s.Append("internal static class ");
        s.Append(__polyPrefix);
        s.AppendLine("TomlPolyDes {");

        // ── root entry: the discriminator row is the current token ──
        s.Append("    internal static ");
        s.Append(type.Name);
        s.AppendLine(" DeserializeRoot(ref TomlReader r) {");
        s.Append("        if (!MemoryExtensions.SequenceEqual(r.KeySpan, \"");
        s.Append(dpnEsc);
        s.AppendLine("\"u8))");
        s.Append("            throw new FormatException(\"Expected discriminator '");
        s.Append(dpnEsc);
        s.AppendLine("'\");");
        s.AppendLine("        return __Dispatch(ref r, System.Array.Empty<byte>());");
        s.AppendLine("    }");
        s.AppendLine();

        // ── member/element entry: the section's opening token is current ──
        s.Append("    internal static ");
        s.Append(type.Name);
        s.AppendLine(" DeserializeSection(ref TomlReader r) {");
        // The section name is copied out of the reader: a ReadOnlySpan<byte> must not outlive the
        // Read() calls below (the reader re-points TablePath on every token).
        s.AppendLine("        var __section = r.TablePath.ToArray();");
        s.AppendLine("        if (!r.Read() || r.TokenType != TokenType.PropertyName)");
        s.Append("            throw new FormatException(\"Expected discriminator '");
        s.Append(dpnEsc);
        s.AppendLine("'\");");
        s.Append("        if (!MemoryExtensions.SequenceEqual(r.KeySpan, \"");
        s.Append(dpnEsc);
        s.AppendLine("\"u8))");
        s.Append("            throw new FormatException(\"Expected discriminator '");
        s.Append(dpnEsc);
        s.AppendLine("'\");");
        s.AppendLine("        return __Dispatch(ref r, __section);");
        s.AppendLine("    }");
        s.AppendLine();

        // ── the dispatch itself ──
        s.Append("    static ");
        s.Append(type.Name);
        s.AppendLine(" __Dispatch(ref TomlReader r, byte[] __prefix) {");
        // The value span cannot outlive the per-case reader's Read() calls.
        s.AppendLine("        var __discVal = r.ValueSpan.ToArray();");
        foreach (var dt in __cases)
        {
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out _))
                continue;
            var desc = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(dt.TypeDiscriminator);
            s.Append("        if (MemoryExtensions.SequenceEqual(__discVal, \"");
            s.Append(desc);
            s.AppendLine("\"u8))");
            s.Append("            return Read");
            s.Append(PicoSerDe.Gen.GenInfrastructure.SafeName(dt.FullyQualifiedName));
            s.AppendLine("(ref r, __prefix);");
        }
        s.AppendLine(
            "        throw new FormatException($\"Unknown discriminator: {Encoding.UTF8.GetString(__discVal)}\");"
        );
        s.AppendLine("    }");

        // ── one reader per case: ctor map, child tables, object collections ──
        foreach (var dt in __cases)
        {
            if (!typeMap.TryGetValue(dt.FullyQualifiedName, out var dti))
                continue;
            var ds = PicoSerDe.Gen.GenInfrastructure.ShortName(dt.FullyQualifiedName);
            var caseName = PicoSerDe.Gen.GenInfrastructure.SafeName(dt.FullyQualifiedName);
            s.AppendLine();
            s.Append("    static ");
            s.Append(type.Name);
            s.Append(" Read");
            s.Append(caseName);
            s.AppendLine("(ref TomlReader r, byte[] __prefix) {");

            var hasCtor = !dti.CtorParams.IsDefaultOrEmpty && dti.CtorParams.Length > 0;
            if (hasCtor)
            {
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                {
                    var cp = dti.CtorParams[ci];
                    s.Append("        ");
                    s.Append(cp.TypeFullNameAnnotated ?? cp.TypeFullName);
                    s.Append(" __cp_");
                    s.Append(ci);
                    s.Append(" = ");
                    s.Append(DefaultLiterals.For(cp.TypeKind, DefaultLiteralRow.CtorTempNull));
                    s.AppendLine(";");
                }
            }
            else
            {
                s.Append("        var obj = new ");
                s.Append(ds);
                s.AppendLine("();");
            }

            s.AppendLine("        r.Read();");
            s.AppendLine("        while (true) {");
            s.AppendLine("            if (r.TokenType == TokenType.PropertyName) {");
            s.AppendLine("                var __k = r.KeySpan;");
            var __polyCtorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!dti.CtorParams.IsDefaultOrEmpty)
                for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                    if (!string.IsNullOrEmpty(dti.CtorParams[ci].Name))
                        __polyCtorMap[dti.CtorParams[ci].Name!] = ci;
            var __polyScalars = dti
                .Properties.Where(x =>
                    !PicoSerDe.Gen.GenInfrastructure.IsComplexMember(x) && !IsArrayOfTablesMember(x)
                )
                .ToImmutableArray();
            EmitPropertyDispatch(
                s,
                __polyScalars,
                "__k",
                "obj",
                "                ",
                "                    ",
                __polyCtorMap
            );
            // The discriminator row lands here too (no case member is named `$type`): it falls into
            // the "unknown key" arm and is skipped, which is exactly right.
            s.AppendLine("                if (!r.Read()) break;");
            s.AppendLine("                continue;");
            s.AppendLine("            }");
            // Child tables of THIS value. Which headers those are depends on where the reader was
            // entered: the root entry owns everything, a section entry owns only `<prefix>.<child>`
            // and hands every other header back to its caller.
            s.AppendLine("            if (r.TokenType == TokenType.ObjectStart) {");
            s.AppendLine("                var __tblPos = r.BytesConsumed;");
            s.AppendLine("                if (__prefix.Length != 0) {");
            s.AppendLine("                    if (TextHelpers.Eq(r.TablePath, __prefix)) {");
            s.AppendLine(
                "                        if (!r.Read()) break; // dotted key re-opening this table"
            );
            s.AppendLine("                        continue;");
            s.AppendLine("                    }");
            s.AppendLine("                    if (!(__prefix.Length + 1 < r.TablePath.Length");
            s.AppendLine("                        && r.TablePath[__prefix.Length] == (byte)'.'");
            s.AppendLine(
                "                        && MemoryExtensions.SequenceEqual(r.TablePath.Slice(0, __prefix.Length), __prefix)))"
            );
            s.AppendLine(
                "                        break; // a header outside this value: it ends here"
            );
            s.AppendLine("                }");
            s.AppendLine(
                "                var __tbl = __prefix.Length == 0 ? r.TablePath : r.TablePath.Slice(__prefix.Length + 1);"
            );
            var __polyComplex = dti
                .Properties.Where(PicoSerDe.Gen.GenInfrastructure.IsComplexMember)
                .ToImmutableArray();
            var __cmFirst = true;
            foreach (var prop in __polyComplex)
            {
                var __cmIdx = -1;
                if (hasCtor)
                {
                    for (int ci = 0; ci < dti.CtorParams.Length; ci++)
                        if (
                            string.Equals(
                                dti.CtorParams[ci].Name,
                                prop.Name,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            __cmIdx = ci;
                            break;
                        }
                }
                // Ctor parameters are only supported for object members
                // (dict members are read into a settable property).
                if (hasCtor && __cmIdx < 0)
                    continue;
                if (hasCtor && prop.TypeKind != "object")
                    continue;
                var __cmKw = PicoSerDe.Gen.GenInfrastructure.ChainKeyword(ref __cmFirst);
                s.Append("                ");
                s.Append(__cmKw);
                s.Append(" (TextHelpers.Eq(__tbl, \"");
                s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(prop.JsonName));
                s.AppendLine("\"u8)) {");
                if (prop.TypeKind == "dict")
                {
                    // Shared dict read (handles scalar/object/nested values).
                    EmitDictRead(s, prop, "obj", "                    ");
                }
                else if (__cmIdx >= 0)
                {
                    var __cmHelper = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                        "TomlInner",
                        prop.TypeFullName!
                    );
                    s.Append("                    ");
                    s.Append($"__cp_{__cmIdx}");
                    s.Append(" = ");
                    s.Append(__cmHelper);
                    s.AppendLine(".Deserialize(ref r);");
                }
                else
                {
                    EmitNestedObjectRead(s, prop, "obj", "                    ");
                }
                s.AppendLine("                }");
            }
            // An unmatched header reads nothing, so this `continue` would re-dispatch the same
            // ObjectStart for ever; progress is the contract (C-1).
            s.AppendLine("                if (r.TokenType == TokenType.None) break;");
            s.AppendLine(
                "                if (r.TokenType == TokenType.ObjectStart && r.BytesConsumed == __tblPos && !r.Read()) break;"
            );
            s.AppendLine("                continue;");
            s.AppendLine("            }");
            // `[[…]]` sections: another element of a list this value OWNS, or — when the path is
            // this value's own section — the NEXT element of the list this value is an element of,
            // which ends the value. Nothing else can appear at this level.
            s.AppendLine("            if (r.TokenType == TokenType.ArrayStart) {");
            s.AppendLine(
                "                if (__prefix.Length != 0 && TextHelpers.Eq(r.TablePath, __prefix)) break;"
            );
            var __polyListObjs = hasCtor
                ? ImmutableArray<PropertyInfo>.Empty
                : dti.Properties.Where(x => IsArrayOfTablesMember(x)).ToImmutableArray();
            if (__polyListObjs.Length > 0)
            {
                s.AppendLine("                var __arrPos = r.BytesConsumed;");
                s.AppendLine("                {");
                var __alFirst = true;
                // The ARM match is same-level (the header's LAST dotted segment): a hand-written
                // `[[Parent.Items]]` names the member by its last segment (C-1), while the
                // same-section break above already handled the element-of-this-list case.
                foreach (var ap in __polyListObjs)
                {
                    var __alKw = PicoSerDe.Gen.GenInfrastructure.ChainKeyword(ref __alFirst);
                    s.Append("                    ");
                    s.Append(__alKw);
                    s.Append(" (TextHelpers.EqLastSegment(r.TablePath, \"");
                    s.Append(PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName));
                    s.AppendLine("\"u8)) {");
                    if (ap.ElementIsPolyBase)
                    {
                        // A polymorphic ELEMENT: it carries its own discriminator, so the element
                        // type's helper reads it whole. Inlining the base's members is the same bug
                        // the member position had.
                        EmitPolyArrayTableRead(s, ap, "obj", "                        ", true);
                    }
                    else
                    {
                        var __alElemType = ap.ElementTypeName ?? "object";
                        s.Append("                        var __item = new ");
                        s.Append(__alElemType);
                        s.AppendLine("();");
                        s.AppendLine("                        while (true) {");
                        s.AppendLine("                            var __itPos = r.BytesConsumed;");
                        s.AppendLine(
                            "                            while (r.Read() && r.TokenType == TokenType.PropertyName) {"
                        );
                        s.AppendLine("                                var __ik = r.KeySpan;");
                        EmitPropertyDispatch(
                            s,
                            ap.NestedProperties,
                            "__ik",
                            "__item",
                            "                                ",
                            "                                    "
                        );
                        s.AppendLine("                            }");
                        s.Append("                        obj.");
                        s.Append(ap.Name);
                        s.Append(" ??= new System.Collections.Generic.List<");
                        s.Append(__alElemType);
                        s.AppendLine(">();");
                        s.Append("                        obj.");
                        s.Append(ap.Name);
                        s.AppendLine(".Add(__item);");
                        s.AppendLine(
                            "                        if (r.TokenType != TokenType.ArrayStart) break;"
                        );
                        // D-9d: the NEXT `[[…]]` header is another element of THIS list only when
                        // it names this table; a foreign one must not append an EMPTY element.
                        s.AppendLine(
                            "                        if (!TextHelpers.EqLastSegment(r.TablePath, \""
                                + PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName)
                                + "\"u8)) break;"
                        );
                        s.AppendLine(
                            "                        if (r.BytesConsumed == __itPos) break;"
                        );
                        s.Append("                        __item = new ");
                        s.Append(__alElemType);
                        s.AppendLine("();");
                        s.AppendLine("                        }");
                    }
                    s.AppendLine("                    }");
                }
                s.AppendLine(
                    "                    if (r.TokenType == TokenType.ArrayStart && r.BytesConsumed == __arrPos && !r.Read()) break;"
                );
                s.AppendLine("                }");
                s.AppendLine("                if (r.TokenType == TokenType.None) break;");
                s.AppendLine("                continue;");
            }
            s.AppendLine("            }");
            // Any other token (EOF, or a header owned by somebody else) ends this value.
            s.AppendLine("            break;");
            s.AppendLine("        }");
            if (hasCtor)
            {
                s.Append("        return new ");
                s.Append(ds);
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
            {
                s.AppendLine("        return obj;");
            }
            s.AppendLine("    }");
        }

        s.AppendLine("}");
        s.AppendLine();
    }

    /// <summary>
    /// The member/element-position helper of a POLYMORPHIC base: it does not emit the base's own
    /// members at all, it forwards to the type's discriminator dispatch
    /// (<c>&lt;prefix&gt;TomlPolySer/Des</c>). Without it a member declared as the base type was
    /// written as the BASE's members only — the discriminator and every derived member silently
    /// gone, while JSON carried both — and, for an abstract base, the generated `new
    /// &lt;abstract&gt;()` did not even compile (CS0144).
    /// </summary>
    static string GenPolyInner(string fqn, string shortName, TypeInfo polyType)
    {
        var clean = fqn.Replace("global::", "");
        var s = new StringBuilder();
        s.AppendLine("// <auto-generated/>");
        s.AppendLine("#nullable enable");
        s.AppendLine("using System; using System.Buffers; using System.Text;");
        s.AppendLine("using PicoSerDe.Core; using PicoToml;");
        var lastDot = clean.LastIndexOf('.');
        if (!clean.Contains('<') && lastDot > 0)
        {
            s.Append("namespace ");
            s.Append(clean.Substring(0, lastDot));
            s.AppendLine(";");
        }
        s.AppendLine();
        var poly = PicoSerDe.Gen.GenInfrastructure.PolyClassPrefix(polyType);
        s.Append("internal static class ");
        s.Append(shortName);
        s.AppendLine("TomlInner {");
        s.Append("    internal static void Serialize(TomlWriter tw, ");
        s.Append(clean);
        s.AppendLine(" value)");
        s.Append("        => ");
        s.Append(poly);
        s.AppendLine("TomlPolySer.SerializeMembers(tw, value);");
        s.AppendLine();
        s.Append("    internal static ");
        s.Append(clean);
        s.AppendLine(" Deserialize(ref TomlReader r)");
        s.Append("        => ");
        s.Append(poly);
        s.AppendLine("TomlPolyDes.DeserializeSection(ref r);");
        s.AppendLine("}");
        return s.ToString();
    }

    /// <summary>
    /// Emits the READ of a <c>[[key]]</c> array-of-tables whose ELEMENT type is a polymorphic base:
    /// each element carries its own discriminator, so the element type's helper reads it whole
    /// instead of an inline loop over the BASE's members (which is what dropped the tag and every
    /// derived member and handed back base instances).
    /// <para>
    /// Contract, and why the loop has this shape: on entry the reader is ON the element's
    /// <c>[[key]]</c> header, the helper consumes the whole element and stops ON THE NEXT element's
    /// header (that is what ends a value in a section format) — so the loop must re-dispatch that
    /// token rather than read past it.
    /// </para>
    /// <paramref name="insideCase"/> says whether the collection is written as <c>obj.Name</c> (a
    /// poly case reader, where <c>obj</c> is the instance being built) or as the root reader's
    /// <c>__arr_Name</c>/<c>o.Name</c> pair.
    /// </summary>
    private static void EmitPolyArrayTableRead(
        StringBuilder s,
        PropertyInfo ap,
        string target,
        string pad,
        bool insideCase
    )
    {
        var elemType = ap.ElementTypeNameAnnotated ?? ap.ElementTypeName ?? "object";
        var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName(
            "TomlInner",
            ap.ElementTypeName ?? "object"
        );
        var nameEsc = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(ap.JsonName);
        var listExpr =
            ap.TypeKind == "array" && !insideCase ? "__arr_" + ap.Name : target + "." + ap.Name;
        s.Append(pad);
        s.Append(listExpr);
        s.Append(" ??= new System.Collections.Generic.List<");
        s.Append(elemType);
        s.AppendLine(">();");
        s.Append(pad);
        s.AppendLine("while (true) {");
        s.Append(pad);
        s.AppendLine("    var __itPos = r.BytesConsumed;");
        s.Append(pad);
        s.Append("    ");
        s.Append(listExpr);
        s.Append(".Add(");
        s.Append(sn);
        s.AppendLine(".Deserialize(ref r));");
        s.Append(pad);
        s.AppendLine("    if (r.TokenType != TokenType.ArrayStart) break;");
        s.Append(pad);
        s.Append("    if (!TextHelpers.EqLastSegment(r.TablePath, \"");
        s.Append(nameEsc);
        s.AppendLine("\"u8)) break;");
        s.Append(pad);
        s.AppendLine("    if (r.BytesConsumed == __itPos) break;");
        s.Append(pad);
        s.AppendLine("}");
    }

    private static string EmitTomlValue(AnonFieldInfo f, string vv, string wv)
    {
        var kn = PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(f.JsonName);
        return f.TypeKind switch
        {
            "string" => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv});",
            "int32"
            or "int64"
            or "int16"
            or "uint16"
            or "sbyte"
            or "byte"
            or "uint32"
            or "uint64" => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv});",
            "float32" or "float64" => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv});",
            "boolean" => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv});",
            _ => $"{wv}.WriteKeyValue(\"{kn}\"u8, {vv}.ToString());",
        };
    }

    static void WriteTomlValue(StringBuilder s, PropertyInfo p, string acc)
    {
        if (p.TypeKind == "string")
            s.Append(acc);
        else
            s.Append(acc);
    }

    static void ReadTomlValue(StringBuilder s, PropertyInfo p)
    {
        if (p.TypeKind == "string")
            s.Append("Encoding.UTF8.GetString(reader.ValueSpan)");
        else if (p.TypeKind == "int32")
            s.Append("int.TryParse(reader.ValueSpan, out var __v) ? __v : 0");
        else if (p.TypeKind == "int64")
            s.Append("long.TryParse(reader.ValueSpan, out var __v) ? __v : 0");
        else if (p.TypeKind == "float64" || p.TypeKind == "float32")
            s.Append("double.TryParse(reader.ValueSpan, out var __v) ? __v : 0");
        else if (p.TypeKind == "boolean")
            s.Append("bool.TryParse(reader.ValueSpan, out var __v) ? __v : false");
        else
            s.Append("default");
    }
}
