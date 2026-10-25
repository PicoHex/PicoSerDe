# Anonymous Type Serialization — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable zero-reflection, AOT-compatible `Serialize(new { ... })` across all 5 PicoSerDe formats.

**Architecture:** Shared `AnonTypeHandler` in `shared/Gen/` provides field layout + interceptor generation. Each format SG adds a parallel pipeline with a format-specific `EmitValue(field, valueVar, writerVar)` callback.

**Tech Stack:** Roslyn 5.6.0 SG, C# 12 interceptors, `Unsafe.AsPointer` + `Unsafe.Read`, CLR field layout.

## Global Constraints

- Consuming project: C# 12+ / .NET 8+, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
- 64-bit offsets with `IntPtr.Size` fallback for reference-type sizes
- Serialization only; `PropertyNamingPolicy`/`DefaultIgnoreCondition`/`MaxDepth` deferred
- Nesting & collections: Phase 4 deferred (fallback `ToString()`)
- Writer constructor parameters vary by format; implementer to verify `Indented`/`MaxDepth` availability per options class
- INI/TOML/YAML: null ref fields skip key+value entirely (format has no null literal)

---

### Task 1: Shared Infrastructure — `AnonTypeHandler.cs`

**Files:**
- Create: `shared/Gen/AnonTypeHandler.cs`
- Modify: 5 `.Gen` .csproj files (add `<Compile Include>`)

**Produces:**
- `AnonTypeHandler.BuildAnonTypeInfo(INamedTypeSymbol, GeneratorSyntaxContext, FormatConfig, AttributeHelpers) → AnonTypeInfo?`
- `AnonTypeHandler.GenerateInterceptorClass(SourceProductionContext, AnonTypeInfo, FormatConfig, Func<AnonFieldInfo,string,string,string>) → void`
  (callback: `(field, valueVar, writerVar) → "jw.WriteNumber(__v);"`)

- [ ] **Step 1: Add `<Compile Include>` to all 5 `.Gen` .csproj**

```xml
<Compile Include="..\..\..\shared\Gen\AnonTypeHandler.cs" Link="Gen\AnonTypeHandler.cs" />
```

Projects: `PicoJetson.Gen.csproj`, `PicoMsgPack.Gen.csproj`, `PicoIni.Gen.csproj`, `PicoToml.Gen.csproj`, `PicoYaml.Gen.csproj`.

- [ ] **Step 2: Create `shared/Gen/AnonTypeHandler.cs`** — data types + type layout table

```csharp
namespace PicoSerDe.Gen;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

internal readonly record struct AnonFieldInfo(
    string Name, string JsonName, string TypeKind,
    int RuntimeOffset, bool IsReferenceType,
    AnonTypeInfo? NestedAnonType,
    string? ElementTypeKind, string? ElementTypeName);

internal readonly record struct AnonTypeInfo(
    string UniqueId, ImmutableArray<AnonFieldInfo> Fields,
    string FilePath, int Line, int Column, string SerializeMethodName);

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
        ["array"]=(8,8,true), ["any"]=(8,8,true),
    };

    // ── Methods defined in Steps 3-6 below ──
    internal static ImmutableArray<AnonFieldInfo> ComputeFieldLayout(
        ImmutableArray<AnonFieldInfo>.Builder fields) { /* Step 3 */ }
    internal static AnonTypeInfo? BuildAnonTypeInfo(
        INamedTypeSymbol, GeneratorSyntaxContext, FormatConfig, AttributeHelpers) { /* Step 4 */ }
    internal static string GenerateReaderClass(AnonTypeInfo) { /* Step 5 */ }
    internal static void GenerateInterceptorClass(
        SourceProductionContext, AnonTypeInfo, FormatConfig,
        Func<AnonFieldInfo,string,string,string>) { /* Step 6 */ }
}
```

- [ ] **Step 3: Implement `ComputeFieldLayout()`**

```csharp
internal static ImmutableArray<AnonFieldInfo> ComputeFieldLayout(
    ImmutableArray<AnonFieldInfo>.Builder fields)
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
```

- [ ] **Step 4: Implement `BuildAnonTypeInfo()`**

```csharp
internal static AnonTypeInfo? BuildAnonTypeInfo(
    INamedTypeSymbol anonType, GeneratorSyntaxContext ctx,
    FormatConfig config, AttributeHelpers attrs)
{
    if (!anonType.IsAnonymousType) return null;
    var fields = ImmutableArray.CreateBuilder<AnonFieldInfo>();
    foreach (var member in anonType.GetMembers())
    {
        if (member is not IPropertySymbol p || p.IsStatic || p.IsIndexer || p.GetMethod is null) continue;
        var (tk, _, _) = TypeKindResolver.Resolve(p.Type, config.FormatTag);
        if (tk is null) continue;
        bool isRef = TypeLayout.TryGetValue(tk, out var l) && l.IsRef;
        AnonTypeInfo? nested = null;
        if (p.Type is INamedTypeSymbol pn && pn.IsAnonymousType)
            nested = BuildAnonTypeInfo(pn, ctx, config, attrs); // Phase 4: full nested support
        string jn = attrs.GetCustomName?.Invoke(p)
            ?? (attrs.HasCamelCase(anonType) ? GenInfrastructure.ToCamelCase(p.Name) : p.Name);
        fields.Add(new AnonFieldInfo(p.Name, jn, tk, 0, isRef, nested, null, null));
    }
    var ordered = ComputeFieldLayout(fields);
    var loc = ctx.Node.GetLocation(); var ls = loc.GetLineSpan();
    var methodName = ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is IMethodSymbol m
        ? m.Name switch { "SerializeToUtf8Bytes" => "Utf8", "Serialize" when m.Parameters.Length >= 3 => "Writer", _ => "Ser" }
        : "Ser";
    var hb = new StringBuilder();
    foreach (var f in ordered) { hb.Append(f.Name); hb.Append('|'); hb.Append(f.TypeKind); hb.Append(';'); }
    var uid = "A" + Math.Abs(hb.ToString().GetHashCode()).ToString("X8");
    return new AnonTypeInfo(uid, ordered, ls.Path, ls.StartLinePosition.Line+1, ls.StartLinePosition.Character+1, methodName);
}
```

- [ ] **Step 5: Implement `GenerateReaderClass()`**

```csharp
internal static string GenerateReaderClass(AnonTypeInfo info)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>\n#nullable enable\nusing System;\nusing System.Runtime.CompilerServices;\n");
    sb.AppendLine($"file unsafe static class __AnonReaders_{info.UniqueId}\n{{");
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
```

- [ ] **Step 6: Implement `GenerateInterceptorClass()` — format-parameterized**

```csharp
internal static void GenerateInterceptorClass(
    SourceProductionContext spc, AnonTypeInfo info, FormatConfig config,
    Func<AnonFieldInfo, string, string, string> emitValue)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>\n#nullable enable\n#pragma warning disable CS9270");
    sb.AppendLine("using System; using System.Buffers; using System.Runtime.CompilerServices; using System.Text;");
    sb.AppendLine("using PicoSerDe.Core;");
    sb.AppendLine($"using {config.Namespace};\n");
    var prefix = GenInfrastructure.AssemblyPrefix ?? "__PicoSerDe";
    sb.AppendLine($"namespace {prefix};\n");
    sb.Append(GenerateReaderClass(info));
    sb.AppendLine();

    string wt = config.FormatTag switch { "json"=>"JsonWriter","msgpack"=>"MsgPackWriter","ini"=>"IniWriter","toml"=>"TomlWriter","yaml"=>"YamlWriter", _=>"JsonWriter" };
    string ot = config.FormatTag switch { "json"=>"JsonOptions","msgpack"=>"MsgPackOptions","ini"=>"IniOptions","toml"=>"TomlOptions","yaml"=>"YamlOptions", _=>"JsonOptions" };
    string wv = wt == "JsonWriter" ? "jw" : "__w";

    var csid = $"CS{info.Line}_{info.Column}";
    var attr = $"[InterceptsLocation(@\"{info.FilePath}\", {info.Line}, {info.Column})]";

    sb.AppendLine($"internal static class __AnonInterceptor_{info.UniqueId}\n{{");

    // Interceptor method
    string ret = info.SerializeMethodName switch { "Utf8" => "byte[]", "Writer" => "void", _ => "string" };
    string mn = info.SerializeMethodName switch { "Utf8" => $"__Intercept_Utf8_{csid}", "Writer" => $"__Intercept_Writer_{csid}", _ => $"__Intercept_Ser_{csid}" };
    string ep = info.SerializeMethodName == "Writer" ? "IBufferWriter<byte> writer, " : "";

    sb.AppendLine($"    {attr}");
    sb.AppendLine($"    internal static {ret} {mn}<T>({ep}T value, {ot}? options = null) where T : allows ref struct");
    sb.AppendLine("    {");
    sb.AppendLine("        object obj = (object)value!;");
    sb.AppendLine($"        var prev = {ot}.Current; {ot}.Current = options;");
    sb.AppendLine("        try {");
    if (info.SerializeMethodName == "Writer")
        sb.AppendLine($"            var {wv} = new {wt}(writer,");
    else
    {
        sb.AppendLine("            var __buf = SerializerExtensions.RentWriter();");
        sb.AppendLine($"            var {wv} = new {wt}(__buf,");
    }
    sb.AppendLine($"                indented: {ot}.Current?.Indented ?? false,");
    sb.AppendLine($"                maxDepth: {ot}.Current?.MaxDepth ?? 63);");

    // Writer start
    if (wt == "MsgPackWriter")
        sb.AppendLine($"            {wv}.WriteMapHeader({info.Fields.Length});");
    else
        sb.AppendLine($"            {wv}.WriteStartObject();");

    // Per-field emit
    foreach (var f in info.Fields)
    {
        sb.AppendLine("            {");
        sb.AppendLine($"                var __v = __AnonReaders_{info.UniqueId}.Read_{f.TypeKind}(obj, {f.RuntimeOffset});");
        bool isRef = TypeLayout[f.TypeKind].IsRef;
        // Formats without null literal (INI/TOML/YAML): skip key+value entirely when null
        bool skipNull = wt is "IniWriter" or "TomlWriter" or "YamlWriter";
        if (isRef && skipNull)
        {
            sb.AppendLine("                if (__v != null) {");
            EmitWriterKey(sb, wv, wt, f.JsonName, "                    ");
            sb.AppendLine($"                    {emitValue(f, "__v", wv)}");
            sb.AppendLine("                }");
        }
        else if (isRef)
        {
            EmitWriterKey(sb, wv, wt, f.JsonName, "                ");
            sb.AppendLine("                if (__v != null) {");
            sb.AppendLine($"                    {emitValue(f, "__v", wv)}");
            sb.AppendLine($"                }} else {{ {wv}.WriteNull(); }}");
        }
        else
        {
            EmitWriterKey(sb, wv, wt, f.JsonName, "                ");
            sb.AppendLine($"                {emitValue(f, "__v", wv)}");
        }
        sb.AppendLine("            }");
    }

    if (wt != "MsgPackWriter")
        sb.AppendLine($"            {wv}.WriteEndObject();");

    if (info.SerializeMethodName == "Utf8")
        sb.AppendLine("            return __buf.WrittenSpan.ToArray();");
    else if (info.SerializeMethodName != "Writer")
        sb.AppendLine("            return Encoding.UTF8.GetString(__buf.WrittenSpan);");

    sb.AppendLine($"        }} finally {{ {ot}.Current = prev; }}");
    sb.AppendLine("    }");
    sb.AppendLine("}");

    spc.AddSource($"__AnonInterceptor_{info.UniqueId}_{csid}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
}
private static string Esc(string s) => s.Replace("\\","\\\\").Replace("\"","\\\"");

private static void EmitWriterKey(StringBuilder sb, string wv, string wt, string name, string indent)
{
    if (wt == "MsgPackWriter")
        sb.AppendLine($"{indent}{wv}.WriteString(Encoding.UTF8.GetBytes(\"{Esc(name)}\"));");
    else if (wt is "IniWriter" or "TomlWriter" or "YamlWriter")
        sb.AppendLine($"{indent}{wv}.WriteKey(\"{Esc(name)}\");");
    else
        sb.AppendLine($"{indent}{wv}.WritePropertyName(\"{Esc(name)}\"u8);");
}
```

- [ ] **Step 7: Commit**

```bash
git add shared/Gen/AnonTypeHandler.cs PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj PicoMsgPack/src/PicoMsgPack.Gen/PicoMsgPack.Gen.csproj PicoIni/src/PicoIni.Gen/PicoIni.Gen.csproj PicoToml/src/PicoToml.Gen/PicoToml.Gen.csproj PicoYaml/src/PicoYaml.Gen/PicoYaml.Gen.csproj
git commit -m "feat: add AnonTypeHandler shared infrastructure"
```

---

### Task 2: JSON SG — Pipeline + EmitJsonValue

**Files:** `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`

- [ ] **Step 1: Add diagnostic descriptors**

```csharp
private static readonly DiagnosticDescriptor AnonRequiresCSharp12 = new(
    id: "PICOJETSON003", title: "Anonymous types require C# 12+",
    messageFormat: "Anonymous type serialization requires C# 12 or later.",
    category: "PicoJetson.Gen", defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);

private static readonly DiagnosticDescriptor AnonRequiresUnsafe = new(
    id: "PICOJETSON004", title: "Requires AllowUnsafeBlocks",
    messageFormat: "Anonymous type serialization requires <AllowUnsafeBlocks>true</AllowUnsafeBlocks>.",
    category: "PicoJetson.Gen", defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);
```

- [ ] **Step 2: Add `EmitJsonValue(field, valueVar, writerVar)` callback**

```csharp
private static string EmitJsonValue(AnonFieldInfo f, string vv, string wv)
    => f.TypeKind switch
    {
        "string"   => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}));",
        "int32" or "int64" or "float32" or "float64" or "decimal" => $"{wv}.WriteNumber({vv});",
        "boolean"  => $"{wv}.WriteBoolean({vv});",
        "datetime" => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}.ToString(\"O\")));",
        "dateonly" or "timeonly" or "timespan" or "guid" => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}.ToString()));",
        _          => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}?.ToString() ?? \"\"));",
    };
```

- [ ] **Step 3: Add `IsAnonymousType` guard in existing `Transform()`**

After `if (typeArg is not INamedTypeSymbol namedType) return null;`:

```csharp
if (namedType.IsAnonymousType)
    return null; // handled by anonDriven pipeline
```

- [ ] **Step 4: Add `anonDriven` pipeline + C#12/Unsafe checks**

In `Initialize()`, after existing pipeline declarations, before the final `context.RegisterSourceOutput`:

```csharp
// Pipeline-Anon: anonymous type serialization via interceptors
var anonDriven = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (n, _) => IsCandidate(n),
    transform: static (ctx, _) =>
    {
        var comp = (CSharpCompilation)ctx.SemanticModel.Compilation;
        if (comp.LanguageVersion < LanguageVersion.CSharp12) return null;
        if (!comp.Options.AllowUnsafe) return null;
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol m) return null;
        if (m.TypeArguments.Length != 1) return null;
        if (m.TypeArguments[0] is not INamedTypeSymbol nt || !nt.IsAnonymousType) return null;
        return AnonTypeHandler.BuildAnonTypeInfo(nt, ctx, Config, Attrs);
    }
).Where(static a => a is not null);

// Combine with asmName for namespace prefix, then generate interceptor files
var anonOut = anonDriven.Collect().Combine(asmName);
context.RegisterSourceOutput(anonOut, static (spc, pair) =>
{
    GenInfrastructure.AssemblyPrefix = $"__PicoSerDe_{pair.Right}";
    foreach (var ai in pair.Left)
    {
        if (ai is not { } info) continue;
        AnonTypeHandler.GenerateInterceptorClass(spc, info, Config,
            static (f, vv, wv) => EmitJsonValue(f, vv, wv));
    }
});
```

- [ ] **Step 5: Add diagnostic emission for failed preconditions**

Emit PICOJETSON003/004 from a compilation-level check. **Severity downgraded to Warning**
(unconditional check fires for ALL consuming projects; changing to Error would break
projects that don't use anonymous types):

```csharp
context.RegisterSourceOutput(context.CompilationProvider, static (spc, comp) =>
{
    var csComp = (CSharpCompilation)comp;
    if (csComp.LanguageVersion < LanguageVersion.CSharp12)
        spc.ReportDiagnostic(Diagnostic.Create(AnonRequiresCSharp12, null));
    else if (!csComp.Options.AllowUnsafe)
        spc.ReportDiagnostic(Diagnostic.Create(AnonRequiresUnsafe, null));
});
```

- [ ] **Step 6: Verify** — manual console app test (see Task 8)

- [ ] **Step 7: Commit**

```bash
git add PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs
git commit -m "feat: add anonymous type serialization to JSON SG"
```

---

### Task 3: MsgPack SG

**Files:** `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`

- [ ] **Step 1: Add `IsAnonymousType` guard** — after `namedType = nt;` (line ~112):

```csharp
if (namedType.IsAnonymousType) return null; // handled by anonDriven pipeline
```

- [ ] **Step 2: Add diagnostic descriptors** (use `PICOMSG` prefix matching existing SG pattern)

```csharp
private static readonly DiagnosticDescriptor AnonRequiresCSharp12 = new(
    id: "PICOMSG003", title: "Anonymous types require C# 12+",
    messageFormat: "Anonymous type serialization requires C# 12 or later.",
    category: "PicoMsgPack.Gen", defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);
private static readonly DiagnosticDescriptor AnonRequiresUnsafe = new(
    id: "PICOMSG004", title: "Requires AllowUnsafeBlocks",
    messageFormat: "Anonymous type serialization requires <AllowUnsafeBlocks>true</AllowUnsafeBlocks>.",
    category: "PicoMsgPack.Gen", defaultSeverity: DiagnosticSeverity.Warning, isEnabledByDefault: true);
```

- [ ] **Step 3: Add `EmitMsgPackValue` callback**

```csharp
private static string EmitMsgPackValue(AnonFieldInfo f, string vv, string wv)
    => f.TypeKind switch
    {
        "string"   => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}));",
        "int32"    => $"{wv}.WriteInt32({vv});",
        "int64"    => $"{wv}.WriteInt64({vv});",
        "float32"  => $"{wv}.WriteFloat((float){vv});",
        "float64"  => $"{wv}.WriteFloat64({vv});",
        "boolean"  => $"{wv}.WriteBoolean({vv});",
        _          => $"{wv}.WriteString(Encoding.UTF8.GetBytes({vv}.ToString()));",
    };
```

- [ ] **Step 4: Add `anonDriven` pipeline in `Initialize()`** — same pattern as Task 2 Step 4, using `Config` (MsgPack's FormatConfig), `Attrs` (MsgPack's AttributeHelpers), and `EmitMsgPackValue`. Must include `GenInfrastructure.AssemblyPrefix = $"__PicoSerDe_{pair.Right}";` in the RegisterSourceOutput.

- [ ] **Step 5: Add compilation-level diagnostic** — same as Task 2 Step 5, using MsgPack's descriptor IDs.

- [ ] **Step 6: Commit**

```bash
git add PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs
git commit -m "feat: add anonymous type serialization to MsgPack SG"
```

---

### Task 4: INI SG

**Files:** `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`

- [ ] **Step 1: Add `IsAnonymousType` guard** — after `if (method.TypeArguments[0] is not INamedTypeSymbol namedType) return null;` (line ~119):

```csharp
if (namedType.IsAnonymousType) return null; // handled by anonDriven pipeline
```

- [ ] **Step 2: Add diagnostic descriptors** — use `PICOINI` prefix (e.g., `PICOINI003`, `PICOINI004`).

- [ ] **Step 3: Add `EmitIniValue` callback** (null handling by shared interceptor frame)

- [ ] **Step 4: Add `anonDriven` pipeline + compilation diagnostic in `Initialize()`** — same pattern as Task 2 Steps 4-5, using `Config`, `Attrs`, INI's descriptors, and `EmitIniValue`. Must include `AssemblyPrefix` assignment.

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs
git commit -m "feat: add anonymous type serialization to INI SG"
```

---

### Task 5: TOML SG

**Files:** `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`

- [ ] **Step 1: Add `IsAnonymousType` guard** — after `if (method.TypeArguments[0] is not INamedTypeSymbol namedType) return null;` (line ~112):

```csharp
if (namedType.IsAnonymousType) return null; // handled by anonDriven pipeline
```

- [ ] **Step 2: Add diagnostic descriptors** — use `PICOTOML` prefix (`PICOTOML003`, `PICOTOML004`).

- [ ] **Step 3: Add `EmitTomlValue` callback** — TOML has no null literal.

- [ ] **Step 4: Add `anonDriven` pipeline + compilation diagnostic in `Initialize()`** — same pattern as Task 2.

- [ ] **Step 5: Commit**

```bash
git add PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs
git commit -m "feat: add anonymous type serialization to TOML SG"
```

---

### Task 6: YAML SG

**Files:** `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`

- [ ] **Step 1: Add `IsAnonymousType` guard** — after `namedType = nt;` (line ~115):

```csharp
if (namedType.IsAnonymousType) return null; // handled by anonDriven pipeline
```

- [ ] **Step 2: Add diagnostic descriptors** — use `PICOYAML` prefix (`PICOYAML003`, `PICOYAML004`).

- [ ] **Step 3: Add `EmitYamlValue` callback** — YAML has no null literal.

- [ ] **Step 4: Add `anonDriven` pipeline + compilation diagnostic in `Initialize()`** — same pattern as Task 2.

- [ ] **Step 5: Commit**

```bash
git add PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs
git commit -m "feat: add anonymous type serialization to YAML SG"
```

---

### Task 7: Build Integration — `.targets` Files

**Files:**
- Create: `PicoJetson/src/PicoJetson/build/PicoJetson.targets` (repeat for all 5)
- Modify: 5 runtime `.csproj` files

- [ ] **Step 1: Create `.targets`** (identical except format name in XML comment)

```xml
<Project>
  <PropertyGroup>
    <InterceptorsNamespaces>$(InterceptorsNamespaces);__PicoSerDe_$(AssemblyName.Replace('.','_').Replace('-','_').Replace(' ','_'))</InterceptorsNamespaces>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Update runtime `.csproj`** to pack the `.targets`

```xml
<ItemGroup Condition="'$(IsPackable)' == 'true'">
  <None Include="build\$(PackageId).targets" Pack="true" PackagePath="build/$(PackageId).targets" Visible="false" />
</ItemGroup>
```

- [ ] **Step 3: Commit**

```bash
git add PicoJetson/src/PicoJetson/build/ PicoJetson/src/PicoJetson/PicoJetson.csproj PicoMsgPack/src/PicoMsgPack/build/ PicoMsgPack/src/PicoMsgPack/PicoMsgPack.csproj PicoIni/src/PicoIni/build/ PicoIni/src/PicoIni/PicoIni.csproj PicoToml/src/PicoToml/build/ PicoToml/src/PicoToml/PicoToml.csproj PicoYaml/src/PicoYaml/build/ PicoYaml/src/PicoYaml/PicoYaml.csproj
git commit -m "feat: add .targets for anonymous type interceptor namespace auto-config"
```

---

### Task 8: JSON Integration Tests

**Files:** `PicoJetson/tests/PicoJetson.Integration.Tests/AnonTypeSerializationTests.cs`

- [ ] **Step 1: Write tests** (deferred features marked as `[Test, Skip("Phase 4")]`)

```csharp
namespace PicoJetson.Tests;

public class AnonTypeSerializationTests
{
    [Test]
    public async Task SimpleProperties()
    {
        var json = JsonSerializer.Serialize(new { Name = "Alice", Age = 30 });
        await Assert.That(json).Contains("\"Name\":\"Alice\"");
        await Assert.That(json).Contains("\"Age\":30");
    }

    [Test]
    public async Task AllPrimitiveKinds()
    {
        var json = JsonSerializer.Serialize(new { S = "h", I = 42, L = 123L, D = 2.5, B = true });
        await Assert.That(json).Contains("\"S\":\"h\"");
        await Assert.That(json).Contains("\"I\":42");
        await Assert.That(json).Contains("\"B\":true");
    }

    [Test]
    public async Task NullProperty()
    {
        var json = JsonSerializer.Serialize(new { Name = (string?)null, Value = 42 });
        await Assert.That(json).Contains("\"Name\":null");
        await Assert.That(json).Contains("\"Value\":42");
    }

    [Test]
    public async Task FieldReordering()
    {
        // double (align 8) ordered before int (align 4) at runtime
        var json = JsonSerializer.Serialize(new { Text = "t", Count = 1, Ratio = 2.5 });
        await Assert.That(json).Contains("\"Text\":\"t\"");
        await Assert.That(json).Contains("\"Ratio\":2.5");
        await Assert.That(json).Contains("\"Count\":1");
    }

    [Test, Skip("Phase 4: nested anonymous types")]
    public async Task NestedAnonymous_Deferred()
    {
        // Will be enabled when Phase 4 implements nested helpers
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test PicoJetson/tests/PicoJetson.Integration.Tests --filter "AnonType"
```

- [ ] **Step 3: Commit**

```bash
git add PicoJetson/tests/PicoJetson.Integration.Tests/AnonTypeSerializationTests.cs
git commit -m "test: add JSON anonymous type integration tests"
```

---

### Task 9: Cross-Format Tests

**Files:** Create test files for MsgPack/INI/TOML/YAML (smoke tests only)

- [ ] **Step 1: Write MsgPack smoke test**

```csharp
namespace PicoMsgPack.Tests;
public class AnonTypeSerializationTests
{
    [Test]
    public async Task SimpleProperties()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new { Name = "test", Value = 42 });
        await Assert.That(bytes.Length).IsGreaterThan(0);
    }
}
```

- [ ] **Step 2: Write INI/TOML/YAML smoke tests** (same pattern — verify non-empty output)

- [ ] **Step 3: Run tests** `dotnet test --filter "AnonType"`

- [ ] **Step 4: Commit**

```bash
git add PicoMsgPack/tests/PicoMsgPack.Integration.Tests/AnonTypeSerializationTests.cs PicoIni/tests/PicoIni.Integration.Tests/AnonTypeSerializationTests.cs PicoToml/tests/PicoToml.Integration.Tests/AnonTypeSerializationTests.cs PicoYaml/tests/PicoYaml.Integration.Tests/AnonTypeSerializationTests.cs
git commit -m "test: add cross-format anonymous type smoke tests"
```

---

### Task 10: AOT Verification + Docs

**Files:** `README.md`, skill file

- [ ] **Step 1: AOT smoke test** — `dotnet publish /p:PublishAot=true` with a project referencing PicoJetson, verify zero IL3050/IL2026

- [ ] **Step 2: Update `README.md`** — add `| Anonymous | new { A = 1, B = "x" } | ✅ (serialization only, C# 12+) |` to Supported Types table

- [ ] **Step 3: Update skill file** — replace "Anonymous Types Are Not Supported" section with:
  - C# 12+ / AllowUnsafeBlocks requirements
  - Serialization only, known limitations

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: update README and skill for anonymous type support"
```
