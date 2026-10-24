namespace PicoSerDe.Gen;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

internal readonly record struct AnonFieldInfo(
    string Name,
    string JsonName,
    string TypeKind,
    int RuntimeOffset,
    bool IsReferenceType,
    AnonTypeInfo? NestedAnonType,
    string? ElementTypeKind,
    string? ElementTypeName,
    // Nullable<T> member (review C-26). Optional so existing positional and
    // named call sites stay source-compatible.
    bool IsNullable = false
);

internal sealed class AnonTypeInfo : IEquatable<AnonTypeInfo>
{
    public string UniqueId { get; }
    public ImmutableArray<AnonFieldInfo> Fields { get; }
    public string FilePath { get; }
    public int Line { get; }
    public int Column { get; }
    public string SerializeMethodName { get; }

    public AnonTypeInfo(
        string uniqueId,
        ImmutableArray<AnonFieldInfo> fields,
        string filePath,
        int line,
        int column,
        string serializeMethodName
    )
    {
        UniqueId = uniqueId;
        Fields = fields;
        FilePath = filePath;
        Line = line;
        Column = column;
        SerializeMethodName = serializeMethodName;
    }

    public bool Equals(AnonTypeInfo? other)
    {
        if (other is null)
            return false;
        return UniqueId == other.UniqueId
            && Line == other.Line
            && Column == other.Column
            && FilePath == other.FilePath
            && SerializeMethodName == other.SerializeMethodName
            && Fields.SequenceEqual(other.Fields);
    }

    public override bool Equals(object? obj) => obj is AnonTypeInfo other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = UniqueId?.GetHashCode() ?? 0;
            h = (h * 397) ^ Line;
            h = (h * 397) ^ Column;
            h = (h * 397) ^ (FilePath?.GetHashCode() ?? 0);
            h = (h * 397) ^ (SerializeMethodName?.GetHashCode() ?? 0);
            foreach (var f in Fields)
                h = (h * 397) ^ f.GetHashCode();
            return h;
        }
    }
}

internal static class AnonTypeHandler
{
    private static readonly Dictionary<string, (int Size, int Align, bool IsRef)> TypeLayout = new()
    {
        ["string"] = (8, 8, true),
        ["int32"] = (4, 4, false),
        ["int64"] = (8, 8, false),
        ["int16"] = (2, 2, false),
        ["uint16"] = (2, 2, false),
        ["uint32"] = (4, 4, false),
        ["uint64"] = (8, 8, false),
        ["sbyte"] = (1, 1, false),
        ["byte"] = (1, 1, false),
        ["char"] = (2, 2, false),
        ["float32"] = (4, 4, false),
        ["float64"] = (8, 8, false),
        ["boolean"] = (1, 1, false),
        ["decimal"] = (16, 8, false),
        ["datetime"] = (8, 8, false),
        ["dateonly"] = (4, 4, false),
        ["timeonly"] = (8, 8, false),
        ["timespan"] = (8, 8, false),
        ["guid"] = (16, 4, false),
        ["enum"] = (4, 4, false),
        ["object"] = (8, 8, true),
        ["dict"] = (8, 8, true),
        ["list"] = (8, 8, true),
        ["array"] = (8, 8, true),
        ["any"] = (8, 8, true),
        ["bytes"] = (8, 8, true),
        // ── Nullable<T> members (review C-26) ──
        // Measured layout (.NET 10, x64): Nullable<T> keeps hasValue in byte 0
        // and starts the value at T's alignment — Unsafe.SizeOf<Guid?>() == 20
        // with the value at offset 4, Unsafe.SizeOf<decimal?>() == 24 with the
        // value at offset 8. Each entry is (size, align); align doubles as the
        // value offset inside the nullable.
        ["nullable_int32"] = (8, 4, false),
        ["nullable_int64"] = (16, 8, false),
        ["nullable_int16"] = (4, 2, false),
        ["nullable_uint16"] = (4, 2, false),
        ["nullable_uint32"] = (8, 4, false),
        ["nullable_uint64"] = (16, 8, false),
        ["nullable_sbyte"] = (2, 1, false),
        ["nullable_byte"] = (2, 1, false),
        ["nullable_char"] = (4, 2, false),
        ["nullable_float32"] = (8, 4, false),
        ["nullable_float64"] = (16, 8, false),
        ["nullable_boolean"] = (2, 1, false),
        ["nullable_decimal"] = (24, 8, false),
        ["nullable_datetime"] = (16, 8, false),
        ["nullable_dateonly"] = (8, 4, false),
        ["nullable_timeonly"] = (16, 8, false),
        ["nullable_timespan"] = (16, 8, false),
        ["nullable_guid"] = (20, 4, false),
    };

    private static bool IsNullableKind(string kind) =>
        kind.StartsWith("nullable_", StringComparison.Ordinal);

    private static string StripNullableKind(string kind) =>
        IsNullableKind(kind) ? kind.Substring("nullable_".Length) : kind;

    /// <summary>
    /// Descending sort key for value fields. The CLR reorders the fields of a
    /// boxed anonymous type (measured on .NET 10/x64): reference fields first in
    /// declaration order, then non-nullable value fields by alignment, then
    /// nullable value fields — which sort after <em>every</em> non-nullable field
    /// and keep declaration order among themselves.
    /// </summary>
    private static int SortAlign(string kind) => IsNullableKind(kind) ? 0 : TypeLayout[kind].Align;

    internal static ImmutableArray<AnonFieldInfo> ComputeFieldLayout(List<AnonFieldInfo> fields)
    {
        var refFields = fields.Where(f => f.IsReferenceType).ToList();
        var valFields = fields
            .Where(f => !f.IsReferenceType)
            .OrderByDescending(f => SortAlign(f.TypeKind))
            .ThenBy(f => fields.IndexOf(f))
            .ToList();
        var ordered = new List<AnonFieldInfo>(refFields);
        ordered.AddRange(valFields);
        int offset = 0;
        var result = ImmutableArray.CreateBuilder<AnonFieldInfo>(ordered.Count);
        foreach (var f in ordered)
        {
            var (size, align, _) = TypeLayout[f.TypeKind];
            offset += (align - (offset % align)) % align;
            result.Add(f with { RuntimeOffset = offset });
            offset += size;
        }
        return result.MoveToImmutable();
    }

    internal static AnonTypeInfo? BuildAnonTypeInfo(
        INamedTypeSymbol anonType,
        GeneratorSyntaxContext ctx,
        FormatConfig config,
        AttributeHelpers attrs
    )
    {
        if (!anonType.IsAnonymousType)
            return null;
        var fields = new List<AnonFieldInfo>();
        foreach (var member in anonType.GetMembers())
        {
            if (member is not IPropertySymbol p || p.IsStatic || p.IsIndexer || p.GetMethod is null)
                continue;
            var (resolvedKind, isNullable, innerType) = TypeKindResolver.Resolve(
                p.Type,
                config.FormatTag
            );
            var tk = resolvedKind;
            // Nullable<T> members carry a dedicated kind so the reader/layout
            // knows the extra hasValue byte (review C-26).
            if (tk is not null && isNullable)
                tk = NullableKind(tk, innerType ?? p.Type);
            AnonTypeInfo? nested = null;
            if (p.Type is INamedTypeSymbol pn && pn.IsAnonymousType)
                nested = BuildAnonTypeInfo(pn, ctx, config, attrs);
            if (tk is null && nested is null)
                continue;
            if (tk is null)
                tk = "object";
            // Unknown kinds cannot be laid out safely — skip the whole
            // anonymous type instead of crashing the generator with a
            // KeyNotFoundException (a crash discards EVERY generated
            // serializer in the compilation).
            if (!TypeLayout.ContainsKey(tk))
                return null;
            bool isRef = TypeLayout.TryGetValue(tk, out var l) && l.IsRef;
            string jn =
                attrs.GetCustomName?.Invoke(p)
                ?? (attrs.HasCamelCase(anonType) ? GenInfrastructure.ToCamelCase(p.Name) : p.Name);
            string? elemKind = null;
            string? elemName = null;
            if (tk is "array" or "list" or "dict")
            {
                if (p.Type is IArrayTypeSymbol arr)
                {
                    var (ek, _, _) = TypeKindResolver.Resolve(arr.ElementType, config.FormatTag);
                    elemKind = ek;
                    elemName = arr.ElementType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    );
                }
                else if (
                    p.Type is INamedTypeSymbol ntsList
                    && tk == "list"
                    && ntsList.TypeArguments.Length == 1
                )
                {
                    var ta = ntsList.TypeArguments[0];
                    var (ek, _, _) = TypeKindResolver.Resolve(ta, config.FormatTag);
                    elemKind = ek;
                    elemName = ta.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
                else if (
                    p.Type is INamedTypeSymbol ntsDict
                    && tk == "dict"
                    && ntsDict.TypeArguments.Length == 2
                )
                {
                    var va = ntsDict.TypeArguments[1];
                    var (ek, _, _) = TypeKindResolver.Resolve(va, config.FormatTag);
                    elemKind = ek;
                    elemName = va.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }
            fields.Add(
                new AnonFieldInfo(p.Name, jn, tk, 0, isRef, nested, elemKind, elemName, isNullable)
            );
        }
        var ordered = ComputeFieldLayout(fields);
        var methodLoc = ctx.Node is InvocationExpressionSyntax inv
            ? (
                inv.Expression is MemberAccessExpressionSyntax ma ? ma.Name.GetLocation()
                : inv.Expression is MemberBindingExpressionSyntax mb ? mb.Name.GetLocation()
                : ctx.Node.GetLocation()
            )
            : ctx.Node.GetLocation();
        var ls = methodLoc.GetLineSpan();
        var methodName = ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol m
            ? m.Name switch
            {
                "SerializeToUtf8Bytes" => "Utf8",
                "Serialize" when m.Parameters.Length >= 3 => "Writer",
                _ => "Ser",
            }
            : "Ser";
        var uid = ComputeUid(ordered);
        return new AnonTypeInfo(
            uid,
            ordered,
            ls.Path,
            ls.StartLinePosition.Line + 1,
            ls.StartLinePosition.Character + 1,
            methodName
        );
    }

    /// <summary>
    /// Maps a <c>Nullable&lt;T&gt;</c> member to its nullable layout kind. Enums are
    /// sized by <see cref="INamedTypeSymbol.EnumUnderlyingType"/>: a
    /// <c>Nullable&lt;longBackedEnum&gt;</c> is 16 bytes/align 8, a
    /// <c>Nullable&lt;intBackedEnum&gt;</c> 8/4, and narrower underlying types keep
    /// their real width — one uniform 32-bit mapping would corrupt every
    /// following field offset.
    /// </summary>
    private static string NullableKind(string kind, ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            return enumType.EnumUnderlyingType?.SpecialType switch
            {
                SpecialType.System_Byte => "nullable_byte",
                SpecialType.System_SByte => "nullable_sbyte",
                SpecialType.System_Int16 => "nullable_int16",
                SpecialType.System_UInt16 => "nullable_uint16",
                SpecialType.System_UInt32 => "nullable_uint32",
                SpecialType.System_Int64 => "nullable_int64",
                SpecialType.System_UInt64 => "nullable_uint64",
                _ => "nullable_int32",
            };
        return "nullable_" + kind;
    }

    internal static string GenerateReaderClass(
        AnonTypeInfo info,
        string fmtTag,
        bool includePreamble = true
    )
    {
        var sb = new StringBuilder();
        if (includePreamble)
            sb.AppendLine(
                "// <auto-generated/>\n#nullable enable\nusing System;\nusing System.Runtime.CompilerServices;\n"
            );
        sb.AppendLine($"file unsafe static class __AnonReaders_{fmtTag}_{info.UniqueId}\n{{");
        foreach (var kind in info.Fields.Select(f => f.TypeKind).Distinct())
        {
            if (IsNullableKind(kind))
            {
                AppendNullableReader(sb, kind);
                continue;
            }
            var (_, _, isRef) = TypeLayout[kind];
            var ct = KindToCSharp(kind);
            sb.AppendLine($"    internal static {ct} Read_{kind}(object obj, int offset)\n    {{");
            AppendReaderPrologue(sb);
            if (isRef)
            {
                sb.AppendLine($"        ref IntPtr slot = ref Unsafe.AsRef<IntPtr>(data);");
                sb.AppendLine($"        return Unsafe.As<IntPtr, {ct}>(ref slot);");
            }
            else
                sb.AppendLine($"        return Unsafe.Read<{ct}>(data);");
            sb.AppendLine("    }\n");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendReaderPrologue(StringBuilder sb)
    {
        sb.AppendLine("        void* pRef = Unsafe.AsPointer(ref obj);");
        sb.AppendLine("        IntPtr objPtr = *(IntPtr*)pRef;");
        sb.AppendLine("        byte* data = (byte*)objPtr + IntPtr.Size + offset;");
    }

    /// <summary>
    /// Reads a <c>Nullable&lt;T&gt;</c> member. A plain
    /// <c>Unsafe.Read&lt;int&gt;</c> on a <c>Nullable&lt;int&gt;</c> field reads the
    /// hasValue byte and returns garbage (review C-26), so the reader checks
    /// hasValue at byte 0 and reads the value at T's alignment.
    /// </summary>
    private static void AppendNullableReader(StringBuilder sb, string kind)
    {
        var (_, align, _) = TypeLayout[kind];
        var valueType = KindToCSharp(StripNullableKind(kind));
        var nullableType = KindToCSharp(kind);
        sb.AppendLine(
            $"    internal static {nullableType} Read_{kind}(object obj, int offset)\n    {{"
        );
        AppendReaderPrologue(sb);
        sb.AppendLine("        if (!Unsafe.Read<bool>(data))");
        sb.AppendLine("            return null;");
        sb.AppendLine($"        return Unsafe.Read<{valueType}>(data + {align});");
        sb.AppendLine("    }\n");
    }

    private static string KindToCSharp(string k)
    {
        if (IsNullableKind(k))
            return KindToCSharp(StripNullableKind(k)) + "?";
        return k switch
        {
            "string" => "string",
            "int32" => "int",
            "int64" => "long",
            "int16" => "short",
            "uint16" => "ushort",
            "uint32" => "uint",
            "uint64" => "ulong",
            "sbyte" => "sbyte",
            "byte" => "byte",
            "char" => "char",
            "float32" => "float",
            "float64" => "double",
            "boolean" => "bool",
            "decimal" => "decimal",
            "datetime" => "System.DateTime",
            "dateonly" => "System.DateOnly",
            "timeonly" => "System.TimeOnly",
            "timespan" => "System.TimeSpan",
            "guid" => "System.Guid",
            "enum" => "int",
            _ => "object",
        };
    }

    internal static void GenerateInterceptorClass(
        SourceProductionContext spc,
        AnonTypeInfo info,
        FormatConfig config,
        AnonFormatConfig afc,
        Func<AnonFieldInfo, string, string, string> emitField
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>\n#nullable enable\n#pragma warning disable CS9270");
        sb.AppendLine(
            "using System; using System.Buffers; using System.Collections; using System.Runtime.CompilerServices; using System.Text;"
        );
        sb.AppendLine("using PicoSerDe.Core;");
        sb.AppendLine($"using {config.Namespace};\n");
        var prefix = GenInfrastructure.AssemblyPrefix ?? "__PicoSerDe";
        sb.AppendLine($"namespace {prefix};\n");
        var fmtTag = config.FormatTag;
        sb.Append(GenerateReaderClass(info, fmtTag));
        var nestedTypes = CollectNestedTypes(info.Fields);
        foreach (var nt in nestedTypes)
        {
            sb.Append(GenerateReaderClass(nt, fmtTag, includePreamble: false));
            sb.AppendLine();
        }

        string wt = config.FormatTag switch
        {
            "json" => "JsonWriter",
            "msgpack" => "MsgPackWriter",
            "ini" => "IniWriter",
            "toml" => "TomlWriter",
            "yaml" => "YamlWriter",
            _ => "JsonWriter",
        };
        string ot = config.FormatTag switch
        {
            "json" => "JsonOptions",
            "msgpack" => "MsgPackOptions",
            "ini" => "IniOptions",
            "toml" => "TomlOptions",
            "yaml" => "YamlOptions",
            _ => "JsonOptions",
        };
        string wv = "jw";

        var csid = $"CS{info.Line}_{info.Column}";
        var attr = $"[InterceptsLocation(@\"{Esc(info.FilePath)}\", {info.Line}, {info.Column})]";

        sb.AppendLine(
            $"internal static class __AnonInterceptor_{fmtTag}_{info.UniqueId}_{csid}\n{{"
        );

        string ret = info.SerializeMethodName switch
        {
            "Utf8" => "byte[]",
            "Writer" => "void",
            _ => "string",
        };
        string mn = info.SerializeMethodName switch
        {
            "Utf8" => $"__Intercept_Utf8_{csid}",
            "Writer" => $"__Intercept_Writer_{csid}",
            _ => $"__Intercept_Ser_{csid}",
        };
        string ep = info.SerializeMethodName == "Writer" ? "IBufferWriter<byte> writer, " : "";
        string optionsParam = afc.FacadeTakesOptions ? $", {ot}? options = null" : "";

        sb.AppendLine($"    {attr}");
        sb.AppendLine($"    internal static {ret} {mn}<T>({ep}T value{optionsParam})");
        sb.AppendLine("    {");
        sb.AppendLine("        object obj = (object)value!;");
        if (afc.HasOptionsParam)
        {
            // options parameter is threaded explicitly; no ambient state.
        }

        // Format-specific writer construction
        if (info.SerializeMethodName == "Writer")
            sb.AppendLine(
                afc.HasIndented
                    ? $"        var {wv} = new {wt}(writer, indented: options?.Indented ?? false);"
                    : $"        var {wv} = new {wt}(writer);"
            );
        else
        {
            sb.AppendLine("        var __buf = SerializerExtensions.RentWriter();");
            if (afc.HasIndentedMaxDepth)
                sb.AppendLine(
                    $"        var {wv} = new {wt}(__buf, indented: options?.Indented ?? false, maxDepth: options?.MaxDepth ?? 63, options: options);"
                );
            else if (afc.HasIndented)
                sb.AppendLine(
                    $"        var {wv} = new {wt}(__buf, indented: options?.Indented ?? false);"
                );
            else
                sb.AppendLine($"        var {wv} = new {wt}(__buf);");
        }

        // Format-specific write start
        if (afc.ObjectStartMethod is not null)
        {
            if (afc.ObjectStartNeedsCount)
                sb.AppendLine($"        {wv}.{afc.ObjectStartMethod}({info.Fields.Length});");
            else
                sb.AppendLine($"        {wv}.{afc.ObjectStartMethod}();");
        }

        var readers = $"__AnonReaders_{fmtTag}_{info.UniqueId}";
        foreach (var f in info.Fields)
            EmitAnonMember(
                sb,
                f,
                fmtTag,
                afc,
                ot,
                wv,
                readers,
                "            ",
                "__v",
                "__vv",
                "1",
                emitField
            );

        // Format-specific write end
        if (afc.ObjectEndMethod is not null)
            sb.AppendLine($"        {wv}.{afc.ObjectEndMethod}();");

        if (info.SerializeMethodName == "Utf8")
            sb.AppendLine("            return __buf.WrittenSpan.ToArray();");
        else if (info.SerializeMethodName != "Writer")
            sb.AppendLine("            return Encoding.UTF8.GetString(__buf.WrittenSpan);");

        // try/finally removed — options are explicit; nothing to restore.
        sb.AppendLine("    }");
        sb.AppendLine("}");

        foreach (var nt in nestedTypes)
        {
            sb.AppendLine();
            sb.Append(GenerateNestedHelper(nt, config, afc, fmtTag, ot, wt, wv, emitField));
        }

        spc.AddSource(
            $"__AnonInterceptor_{fmtTag}_{info.UniqueId}_{csid}.g.cs",
            SourceText.From(sb.ToString(), Encoding.UTF8)
        );
    }

    private static string ComputeUid(ImmutableArray<AnonFieldInfo> fields)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var f in fields)
            {
                foreach (char c in f.Name)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                hash ^= 0x7C;
                foreach (char c in f.TypeKind)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                hash ^= 0x3B;
            }
            return "A" + (hash & 0x7FFFFFFF).ToString("X8");
        }
    }

    private static bool IsCollectionKind(string kind) => kind is "list" or "array" or "dict";

    /// <summary>
    /// The anonymous interceptor can only emit collection members for writers
    /// that expose a standalone array API. TOML/INI embed the key in every
    /// scalar call and YAML only offers per-item <c>WriteSequenceItem</c> calls.
    /// </summary>
    private static bool SupportsAnonCollections(string fmtTag) => fmtTag is "json" or "msgpack";

    /// <summary>
    /// Emits one anonymous-type member. Shared by the top-level interceptor and
    /// the nested-anonymous helper so both honour the same null, collection and
    /// nullable rules (review C-02, C-26).
    /// </summary>
    private static void EmitAnonMember(
        StringBuilder sb,
        AnonFieldInfo f,
        string fmtTag,
        AnonFormatConfig afc,
        string ot,
        string wv,
        string readers,
        string indent,
        string valueVar,
        string unwrappedVar,
        string depthArg,
        Func<AnonFieldInfo, string, string, string> emitField
    )
    {
        string body = indent + "    ";
        // Review C-02: for formats with a null literal the key belongs to the
        // member, not to its non-null branch — otherwise a null collection or a
        // null nested object emits a bare `null` and corrupts the document
        // (review C-02: `new { Xs = (List<int>?)null }` produced `{null}`).
        bool keyBeforeNullCheck = afc.HasNullLiteral && !afc.EmbedsKeyInValue;

        if (IsCollectionKind(f.TypeKind) && !SupportsAnonCollections(fmtTag))
        {
            sb.AppendLine(
                $"{indent}// {f.Name}: collection members cannot be represented in {fmtTag} anonymous types — omitted"
            );
            return;
        }

        sb.AppendLine($"{indent}{{");
        sb.AppendLine(
            $"{body}var {valueVar} = {readers}.Read_{f.TypeKind}(obj, {f.RuntimeOffset});"
        );

        if (f.NestedAnonType is { } nested)
        {
            if (keyBeforeNullCheck)
                EmitAnonFieldKey(sb, wv, f, afc, ot, body);
            sb.AppendLine($"{body}if ({valueVar} != null) {{");
            if (!keyBeforeNullCheck)
                EmitNestedMemberKey(sb, wv, f, afc, ot, body + "    ");
            EmitNestedSerialize(sb, wv, nested, valueVar, afc, body + "    ", depthArg);
            AppendNullBranchEnd(sb, wv, afc.HasNullLiteral, body);
        }
        else if (IsCollectionKind(f.TypeKind))
        {
            if (keyBeforeNullCheck)
                EmitAnonFieldKey(sb, wv, f, afc, ot, body);
            sb.AppendLine($"{body}if ({valueVar} != null) {{");
            if (!keyBeforeNullCheck && !afc.EmbedsKeyInValue)
                EmitAnonFieldKey(sb, wv, f, afc, ot, body + "    ");
            EmitCollectionSerialize(sb, wv, f, fmtTag, emitField, body + "    ", valueVar);
            AppendNullBranchEnd(sb, wv, afc.HasNullLiteral, body);
        }
        else if (f.IsNullable && IsNullableKind(f.TypeKind))
        {
            // Review C-26: unwrap before handing the value to the format emitter —
            // every Emit*Value overload takes the non-nullable type, and JSON's
            // fallback arm would stringify an `int?` (5 → "5").
            var valueField = f with
            {
                TypeKind = StripNullableKind(f.TypeKind),
                IsNullable = false,
            };
            sb.AppendLine($"{body}if ({valueVar}.HasValue) {{");
            if (!afc.EmbedsKeyInValue)
                EmitAnonFieldKey(sb, wv, f, afc, ot, body + "    ");
            sb.AppendLine($"{body}    var {unwrappedVar} = {valueVar}.Value;");
            sb.AppendLine($"{body}    {emitField(valueField, unwrappedVar, wv)}");
            if (afc.HasNullLiteral)
            {
                sb.AppendLine($"{body}}} else {{");
                if (!afc.EmbedsKeyInValue)
                    EmitAnonFieldKey(sb, wv, f, afc, ot, body + "    ");
                sb.AppendLine($"{body}    {wv}.WriteNull();");
            }
            sb.AppendLine($"{body}}}");
        }
        else
        {
            bool isRef = TypeLayout[f.TypeKind].IsRef;
            bool skipNull = !afc.HasNullLiteral;
            if (isRef && skipNull)
            {
                sb.AppendLine($"{body}if ({valueVar} != null) {{");
                if (!afc.EmbedsKeyInValue)
                    EmitAnonFieldKey(sb, wv, f, afc, ot, body + "    ");
                sb.AppendLine($"{body}    {emitField(f, valueVar, wv)}");
                sb.AppendLine($"{body}}}");
            }
            else if (isRef)
            {
                if (afc.HasOptionsParam)
                {
                    sb.AppendLine(
                        $"{body}if ({valueVar} != null || options?.DefaultIgnoreCondition != PicoJetson.JsonIgnoreCondition.WhenWritingNull) {{"
                    );
                    if (!afc.EmbedsKeyInValue)
                        EmitAnonFieldKey(sb, wv, f, afc, ot, body + "    ");
                    sb.AppendLine($"{body}    if ({valueVar} != null) {{");
                    sb.AppendLine($"{body}        {emitField(f, valueVar, wv)}");
                    sb.AppendLine($"{body}    }} else {{ {wv}.WriteNull(); }}");
                    sb.AppendLine($"{body}}}");
                }
                else
                {
                    if (!afc.EmbedsKeyInValue)
                        EmitAnonFieldKey(sb, wv, f, afc, ot, body);
                    sb.AppendLine($"{body}if ({valueVar} != null) {{");
                    sb.AppendLine($"{body}    {emitField(f, valueVar, wv)}");
                    sb.AppendLine($"{body}}} else {{ {wv}.WriteNull(); }}");
                }
            }
            else
            {
                if (!afc.EmbedsKeyInValue)
                    EmitAnonFieldKey(sb, wv, f, afc, ot, body);
                sb.AppendLine($"{body}{emitField(f, valueVar, wv)}");
            }
        }
        sb.AppendLine($"{indent}}}");
    }

    private static void AppendNullBranchEnd(
        StringBuilder sb,
        string wv,
        bool hasNullLiteral,
        string body
    )
    {
        if (hasNullLiteral)
        {
            sb.AppendLine($"{body}}} else {{");
            sb.AppendLine($"{body}    {wv}.WriteNull();");
        }
        sb.AppendLine($"{body}}}");
    }

    private static void EmitNestedMemberKey(
        StringBuilder sb,
        string wv,
        AnonFieldInfo f,
        AnonFormatConfig afc,
        string ot,
        string indent
    )
    {
        if (!afc.EmbedsKeyInValue)
            EmitAnonFieldKey(sb, wv, f, afc, ot, indent);
        else if (afc.NestedSectionMethod is not null)
            sb.AppendLine(
                $"{indent}{wv}.{afc.NestedSectionMethod}(\"{PicoSerDe.Gen.GenInfrastructure.EscapeCSharpString(f.JsonName)}\");"
            );
    }

    private static void EmitNestedSerialize(
        StringBuilder sb,
        string wv,
        AnonTypeInfo nested,
        string valueVar,
        AnonFormatConfig afc,
        string indent,
        string depthArg
    )
    {
        var args = afc.HasIndentedMaxDepth ? $"{valueVar}, {depthArg}" : valueVar;
        var opts = afc.HasOptionsParam ? ", options" : "";
        sb.AppendLine($"{indent}__AnonInner_{nested.UniqueId}.Serialize(ref {wv}, {args}{opts});");
    }

    private static void EmitCollectionSerialize(
        StringBuilder sb,
        string wv,
        AnonFieldInfo f,
        string fmtTag,
        Func<AnonFieldInfo, string, string, string> emitField,
        string indent,
        string valueVar
    )
    {
        // Only JSON/MsgPack reach this method (see SupportsAnonCollections), and
        // both writers expose a standalone array API.
        const string start = "WriteStartArray";
        const string end = "WriteEndArray";

        if (f.TypeKind == "dict")
        {
            sb.AppendLine($"{indent}{wv}.WriteStartObject();");
            sb.AppendLine(
                $"{indent}foreach (System.Collections.DictionaryEntry __de in (System.Collections.IDictionary){valueVar})"
            );
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    {wv}.WritePropertyName((string)__de.Key);");
            if (f.ElementTypeKind is not null && f.ElementTypeKind != "any")
            {
                var ct = KindToCSharp(f.ElementTypeKind);
                var elemField = new AnonFieldInfo(
                    "",
                    "",
                    f.ElementTypeKind,
                    0,
                    false,
                    null,
                    null,
                    null
                );
                sb.AppendLine($"{indent}    var __dv = ({ct})__de.Value;");
                sb.AppendLine($"{indent}    {emitField(elemField, "__dv", wv)}");
            }
            else
            {
                var elemField = new AnonFieldInfo("", "", "any", 0, true, null, null, null);
                sb.AppendLine($"{indent}    {emitField(elemField, "__de.Value", wv)}");
            }
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}{wv}.WriteEndObject();");
            return;
        }

        sb.AppendLine($"{indent}{wv}.{start}();");
        sb.AppendLine(
            $"{indent}foreach (var __item in (System.Collections.IEnumerable){valueVar})"
        );
        sb.AppendLine($"{indent}{{");
        if (f.ElementTypeKind is not null && f.ElementTypeKind != "any")
        {
            var ct = KindToCSharp(f.ElementTypeKind);
            var elemField = new AnonFieldInfo(
                "",
                "",
                f.ElementTypeKind,
                0,
                false,
                null,
                null,
                null
            );
            sb.AppendLine($"{indent}    var __iv = ({ct})__item;");
            sb.AppendLine($"{indent}    {emitField(elemField, "__iv", wv)}");
        }
        else
        {
            var elemField = new AnonFieldInfo("", "", "any", 0, true, null, null, null);
            sb.AppendLine($"{indent}    {emitField(elemField, "__item", wv)}");
        }
        sb.AppendLine($"{indent}}}");
        sb.AppendLine($"{indent}{wv}.{end}();");
    }

    private static List<AnonTypeInfo> CollectNestedTypes(ImmutableArray<AnonFieldInfo> fields)
    {
        var seen = new HashSet<string>();
        var list = new List<AnonTypeInfo>();
        void Walk(ImmutableArray<AnonFieldInfo> fs)
        {
            foreach (var f in fs)
            {
                if (f.NestedAnonType is { } n && seen.Add(n.UniqueId))
                {
                    list.Add(n);
                    Walk(n.Fields);
                }
            }
        }
        Walk(fields);
        return list;
    }

    private static string GenerateNestedHelper(
        AnonTypeInfo info,
        FormatConfig config,
        AnonFormatConfig afc,
        string fmtTag,
        string ot,
        string wt,
        string wv,
        Func<AnonFieldInfo, string, string, string> emitField
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"file static class __AnonInner_{info.UniqueId}");
        sb.AppendLine("{");
        sb.AppendLine(
            (
                afc.HasOptionsParam
                    ? $"    internal static void Serialize(ref {wt} {wv}, object obj, int __depth = 0, {ot}? options = null)"
                    : $"    internal static void Serialize(ref {wt} {wv}, object obj, int __depth = 0)"
            )
        );
        sb.AppendLine("    {");
        if (afc.HasIndentedMaxDepth)
        {
            sb.AppendLine($"        int __maxDepth = options?.MaxDepth ?? 63;");
            sb.AppendLine("        if (__depth >= __maxDepth)");
            sb.AppendLine("            throw new FormatException(\"MaxDepth exceeded\");");
        }
        int fi = 0;
        foreach (var f in info.Fields)
        {
            var vi = fi++;
            EmitAnonMember(
                sb,
                f,
                fmtTag,
                afc,
                ot,
                wv,
                $"__AnonReaders_{fmtTag}_{info.UniqueId}",
                "    ",
                $"__v{vi}",
                $"__vv{vi}",
                "__depth + 1",
                emitField
            );
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void EmitFieldKey(
        StringBuilder sb,
        string wv,
        string name,
        bool keyIsEncodedString,
        string indent
    )
    {
        if (keyIsEncodedString)
            sb.AppendLine($"{indent}{wv}.WriteString(Encoding.UTF8.GetBytes(\"{Esc(name)}\"));");
        else
            sb.AppendLine($"{indent}{wv}.WritePropertyName(\"{Esc(name)}\"u8);");
    }

    private static void EmitAnonFieldKey(
        StringBuilder sb,
        string wv,
        AnonFieldInfo f,
        AnonFormatConfig afc,
        string ot,
        string indent
    )
    {
        var camel = GenInfrastructure.ToCamelCase(f.Name);
        if (afc.HasNamingPolicy && camel != f.JsonName)
        {
            sb.AppendLine(
                $"{indent}var __n = options?.PropertyNamingPolicy == PicoJetson.JsonNamingPolicy.CamelCase ? \"{camel}\"u8 : \"{Esc(f.JsonName)}\"u8;"
            );
            if (afc.KeyIsEncodedString)
                sb.AppendLine($"{indent}{wv}.WriteString(Encoding.UTF8.GetBytes(__n));");
            else
                sb.AppendLine($"{indent}{wv}.WritePropertyName(__n);");
        }
        else
        {
            EmitFieldKey(sb, wv, f.JsonName, afc.KeyIsEncodedString, indent);
        }
    }
}
