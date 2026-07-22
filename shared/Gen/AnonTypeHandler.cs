namespace PicoSerDe.Gen;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

internal readonly record struct AnonFieldInfo(
    string Name, string JsonName, string TypeKind,
    int RuntimeOffset, bool IsReferenceType,
    AnonTypeInfo? NestedAnonType,
    string? ElementTypeKind, string? ElementTypeName);

internal sealed class AnonTypeInfo : IEquatable<AnonTypeInfo>
{
    public string UniqueId { get; }
    public ImmutableArray<AnonFieldInfo> Fields { get; }
    public string FilePath { get; }
    public int Line { get; }
    public int Column { get; }
    public string SerializeMethodName { get; }

    public AnonTypeInfo(
        string uniqueId, ImmutableArray<AnonFieldInfo> fields,
        string filePath, int line, int column, string serializeMethodName)
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
        if (other is null) return false;
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
        ["string"]=(8,8,true), ["int32"]=(4,4,false), ["int64"]=(8,8,false),
        ["float32"]=(4,4,false), ["float64"]=(8,8,false), ["boolean"]=(1,1,false),
        ["decimal"]=(16,8,false), ["datetime"]=(8,8,false), ["dateonly"]=(4,4,false),
        ["timeonly"]=(8,8,false), ["timespan"]=(8,8,false), ["guid"]=(16,4,false),
        ["enum"]=(4,4,false),
        ["object"]=(8,8,true), ["dict"]=(8,8,true), ["list"]=(8,8,true),
        ["array"]=(8,8,true), ["any"]=(8,8,true), ["bytes"]=(8,8,true),
    };

    internal static ImmutableArray<AnonFieldInfo> ComputeFieldLayout(
        List<AnonFieldInfo> fields)
    {
        var refFields = fields.Where(f => f.IsReferenceType).ToList();
        var valFields = fields.Where(f => !f.IsReferenceType)
            .OrderByDescending(f => TypeLayout[f.TypeKind].Align)
            .ThenBy(f => fields.IndexOf(f))
            .ToList();
        var ordered = new List<AnonFieldInfo>(refFields); ordered.AddRange(valFields);
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
        INamedTypeSymbol anonType, GeneratorSyntaxContext ctx,
        FormatConfig config, AttributeHelpers attrs)
    {
        if (!anonType.IsAnonymousType) return null;
        var fields = new List<AnonFieldInfo>();
        foreach (var member in anonType.GetMembers())
        {
            if (member is not IPropertySymbol p || p.IsStatic || p.IsIndexer || p.GetMethod is null) continue;
            var (tk, _, _) = TypeKindResolver.Resolve(p.Type, config.FormatTag);
            if (tk is null) continue;
            bool isRef = TypeLayout.TryGetValue(tk, out var l) && l.IsRef;
            AnonTypeInfo? nested = null;
            if (p.Type is INamedTypeSymbol pn && pn.IsAnonymousType)
                nested = BuildAnonTypeInfo(pn, ctx, config, attrs);
            string jn = attrs.GetCustomName?.Invoke(p)
                ?? (attrs.HasCamelCase(anonType) ? GenInfrastructure.ToCamelCase(p.Name) : p.Name);
            fields.Add(new AnonFieldInfo(p.Name, jn, tk, 0, isRef, nested, null, null));
        }
        var ordered = ComputeFieldLayout(fields);
        var methodLoc = ctx.Node is InvocationExpressionSyntax inv
            ? (inv.Expression is MemberAccessExpressionSyntax ma
                ? ma.Name.GetLocation()
                : inv.Expression is MemberBindingExpressionSyntax mb
                    ? mb.Name.GetLocation()
                    : ctx.Node.GetLocation())
            : ctx.Node.GetLocation();
        var ls = methodLoc.GetLineSpan();
        var methodName = ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol m
            ? m.Name switch { "SerializeToUtf8Bytes" => "Utf8", "Serialize" when m.Parameters.Length >= 3 => "Writer", _ => "Ser" }
            : "Ser";
        var uid = ComputeUid(ordered);
        return new AnonTypeInfo(uid, ordered, ls.Path, ls.StartLinePosition.Line+1, ls.StartLinePosition.Character+1, methodName);
    }

    internal static string GenerateReaderClass(AnonTypeInfo info, string fmtTag)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>\n#nullable enable\nusing System;\nusing System.Runtime.CompilerServices;\n");
        sb.AppendLine($"file unsafe static class __AnonReaders_{fmtTag}_{info.UniqueId}\n{{");
        foreach (var kind in info.Fields.Select(f => f.TypeKind).Distinct())
        {
            var (_, _, isRef) = TypeLayout[kind];
            var ct = KindToCSharp(kind);
            sb.AppendLine($"    internal static {ct} Read_{kind}(object obj, int offset)\n    {{");
            sb.AppendLine("        void* pRef = Unsafe.AsPointer(ref obj);");
            sb.AppendLine("        IntPtr objPtr = *(IntPtr*)pRef;");
            sb.AppendLine("        byte* data = (byte*)objPtr + IntPtr.Size + offset;");
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

    private static string KindToCSharp(string k) => k switch {
        "string"=>"string","int32"=>"int","int64"=>"long","float32"=>"float","float64"=>"double",
        "boolean"=>"bool","decimal"=>"decimal","datetime"=>"System.DateTime","dateonly"=>"System.DateOnly",
        "timeonly"=>"System.TimeOnly","timespan"=>"System.TimeSpan","guid"=>"System.Guid",
        "enum"=>"int", _=>"object"};

    internal static void GenerateInterceptorClass(
        SourceProductionContext spc, AnonTypeInfo info, FormatConfig config,
        AnonFormatConfig afc,
        Func<AnonFieldInfo, string, string, string> emitField)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>\n#nullable enable\n#pragma warning disable CS9270");
        sb.AppendLine("using System; using System.Buffers; using System.Runtime.CompilerServices; using System.Text;");
        sb.AppendLine("using PicoSerDe.Core;");
        sb.AppendLine($"using {config.Namespace};\n");
        var prefix = GenInfrastructure.AssemblyPrefix ?? "__PicoSerDe";
        sb.AppendLine($"namespace {prefix};\n");
        var fmtTag = config.FormatTag;
        sb.Append(GenerateReaderClass(info, fmtTag));
        sb.AppendLine();

        string wt = config.FormatTag switch { "json"=>"JsonWriter","msgpack"=>"MsgPackWriter","ini"=>"IniWriter","toml"=>"TomlWriter","yaml"=>"YamlWriter", _=>"JsonWriter" };
        string ot = config.FormatTag switch { "json"=>"JsonOptions","msgpack"=>"MsgPackOptions","ini"=>"IniOptions","toml"=>"TomlOptions","yaml"=>"YamlOptions", _=>"JsonOptions" };
        string wv = "jw";

        var csid = $"CS{info.Line}_{info.Column}";
        var attr = $"[InterceptsLocation(@\"{Esc(info.FilePath)}\", {info.Line}, {info.Column})]";

        sb.AppendLine($"internal static class __AnonInterceptor_{fmtTag}_{info.UniqueId}_{csid}\n{{");

        string ret = info.SerializeMethodName switch { "Utf8" => "byte[]", "Writer" => "void", _ => "string" };
        string mn = info.SerializeMethodName switch { "Utf8" => $"__Intercept_Utf8_{csid}", "Writer" => $"__Intercept_Writer_{csid}", _ => $"__Intercept_Ser_{csid}" };
        string ep = info.SerializeMethodName == "Writer" ? "IBufferWriter<byte> writer, " : "";
        string optionsParam = afc.HasOptionsParam ? $", {ot}? options = null" : "";

        sb.AppendLine($"    {attr}");
        sb.AppendLine($"    internal static {ret} {mn}<T>({ep}T value{optionsParam})");
        sb.AppendLine("    {");
        sb.AppendLine("        object obj = (object)value!;");
        if (afc.HasOptionsParam)
        {
            sb.AppendLine($"        var prev = {ot}.Current;");
            sb.AppendLine($"        {ot}.Current = options;");
            sb.AppendLine("        try {");
        }

        // Format-specific writer construction
        if (info.SerializeMethodName == "Writer")
            sb.AppendLine($"        var {wv} = new {wt}(writer);");
        else
        {
            sb.AppendLine("        var __buf = SerializerExtensions.RentWriter();");
            if (afc.HasIndentedMaxDepth)
                sb.AppendLine($"        var {wv} = new {wt}(__buf, indented: {ot}.Current?.Indented ?? false, maxDepth: {ot}.Current?.MaxDepth ?? 63);");
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

        foreach (var f in info.Fields)
        {
            sb.AppendLine("            {");
            sb.AppendLine($"                var __v = __AnonReaders_{fmtTag}_{info.UniqueId}.Read_{f.TypeKind}(obj, {f.RuntimeOffset});");
            bool isRef = TypeLayout[f.TypeKind].IsRef;
            bool skipNull = !afc.HasNullLiteral;
            if (isRef && skipNull)
            {
                sb.AppendLine("                if (__v != null) {");
                if (!afc.EmbedsKeyInValue)
                    EmitAnonFieldKey(sb, wv, f, afc, ot, "                    ");
                sb.AppendLine($"                    {emitField(f, "__v", wv)}");
                sb.AppendLine("                }");
            }
            else if (isRef)
            {
                if (!afc.EmbedsKeyInValue)
                    EmitAnonFieldKey(sb, wv, f, afc, ot, "                ");
                sb.AppendLine("                if (__v != null) {");
                sb.AppendLine($"                    {emitField(f, "__v", wv)}");
                sb.AppendLine($"                }} else {{ {wv}.WriteNull(); }}");
            }
            else
            {
                if (!afc.EmbedsKeyInValue)
                    EmitAnonFieldKey(sb, wv, f, afc, ot, "                ");
                sb.AppendLine($"                {emitField(f, "__v", wv)}");
            }
            sb.AppendLine("            }");
        }

        // Format-specific write end
        if (afc.ObjectEndMethod is not null)
            sb.AppendLine($"        {wv}.{afc.ObjectEndMethod}();");

        if (info.SerializeMethodName == "Utf8")
            sb.AppendLine("            return __buf.WrittenSpan.ToArray();");
        else if (info.SerializeMethodName != "Writer")
            sb.AppendLine("            return Encoding.UTF8.GetString(__buf.WrittenSpan);");

        if (afc.HasOptionsParam)
        {
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine($"            {ot}.Current = prev;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource($"__AnonInterceptor_{fmtTag}_{info.UniqueId}_{csid}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string ComputeUid(ImmutableArray<AnonFieldInfo> fields)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var f in fields)
            {
                foreach (char c in f.Name) { hash ^= c; hash *= 16777619; }
                hash ^= 0x7C;
                foreach (char c in f.TypeKind) { hash ^= c; hash *= 16777619; }
                hash ^= 0x3B;
            }
            return "A" + (hash & 0x7FFFFFFF).ToString("X8");
        }
    }

    private static string Esc(string s) => s.Replace("\\","\\\\").Replace("\"","\\\"");

    private static void EmitFieldKey(StringBuilder sb, string wv, string name, bool keyIsEncodedString, string indent)
    {
        if (keyIsEncodedString)
            sb.AppendLine($"{indent}{wv}.WriteString(Encoding.UTF8.GetBytes(\"{Esc(name)}\"));");
        else
            sb.AppendLine($"{indent}{wv}.WritePropertyName(\"{Esc(name)}\"u8);");
    }

    private static void EmitAnonFieldKey(StringBuilder sb, string wv, AnonFieldInfo f, AnonFormatConfig afc, string ot, string indent)
    {
        var camel = GenInfrastructure.ToCamelCase(f.Name);
        if (afc.HasNamingPolicy && camel != f.JsonName)
        {
            sb.AppendLine($"{indent}var __n = {ot}.Current?.PropertyNamingPolicy == PicoJetson.JsonNamingPolicy.CamelCase ? \"{camel}\"u8 : \"{Esc(f.JsonName)}\"u8;");
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
