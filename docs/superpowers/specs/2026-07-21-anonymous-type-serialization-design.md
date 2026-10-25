# Anonymous Type Serialization — Design Spec

**Date**: 2026-07-21  
**Status**: Draft  
**Scope**: PicoSerDe (all 5 formats: JSON, MsgPack, INI, TOML, YAML)

---

## 1. Overview

### Problem

Anonymous types (`new { Name = "...", Value = 42 }`) cannot be serialized by PicoSerDe
because the source generator cannot reference compiler-generated type names
(`<>f__AnonymousType0`) in generated C# source code.

### Goal

Enable **zero-reflection, AOT-compatible serialization** of anonymous types with
**zero user code changes**. `JsonSerializer.Serialize(new { ... })` should just work.

### Non-Goals

- Deserialization of anonymous types (impossible — no setters, inaccessible constructors)
- C# < 12 / .NET < 8 support (produce a clear diagnostic instead)

---

## 2. Technical Approach

Three building blocks, all verified working:

### 2.1 Generic Interceptor

The SG emits a `[InterceptsLocation]` method with a generic `T : allows ref struct`
signature. The Roslyn compiler matches this to the specific `Serialize<AnonType>`
instantiation at the call site.

```csharp
// User code:
var json = JsonSerializer.Serialize(new { Name = "test", Value = 42 });
//          ^-- compiler resolves to Serialize<AnonType>(AnonType, ...)

// SG-generated interceptor:
[InterceptsLocation("Program.cs", 10, 30)]
internal static string __Intercept_Anon_1<T>(T value, JsonOptions? options = null)
    where T : allows ref struct
{
    // T = AnonType at runtime — verified: typeof(T) = <>f__AnonymousType0`2
    // Inside the body, we CANNOT access value.Name (T has no Name constraint).
    // Use Unsafe memory access instead (2.2).
}
```

**Verified**: Generic interceptor successfully intercepts `Serialize<AnonType>(AnonType)`.
`object`-parameter interceptors do NOT work (CS9144: signatures do not match).

**Constraint**: Requires C# 12+ / .NET 8+ on the consuming project.
The SG targets `netstandard2.0` and can emit interceptors regardless.

**Note on CS9270**: The `InterceptsLocationAttribute(string, int, int)` constructor is
deprecated in .NET 10 (CS9270). The SG should use `node.GetInterceptableLocation()`
when available (Roslyn 4.9+) and fall back to the deprecated API for older SDKs.
The deprecated API still works — only produces a warning which can be suppressed with
`#pragma warning disable CS9270` in the generated file.

### 2.2 Unsafe Field Reading

Since anonymous types are reference types (classes), we bypass the type system
entirely by reading fields at known memory offsets:

```csharp
static unsafe TField ReadField<TField>(object obj, int offset)
{
    // 1. Get stack address of the 'obj' parameter
    void* pRef = Unsafe.AsPointer(ref obj);
    // 2. Read the actual object pointer from that address
    IntPtr objPtr = *(IntPtr*)pRef;
    // 3. Skip MethodTable pointer (IntPtr.Size bytes), then add field offset
    byte* data = (byte*)objPtr + IntPtr.Size + offset;
    // 4. Read the field value
    return Unsafe.Read<TField>(data);
}
```

**Verified**: Works on CoreCLR. Works on NativeAOT (correct string read at offset 0).
No IL3050/IL2026 warnings in AOT publishing.

**Constraint**: Requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the
consuming project. The SG can detect this via `((CSharpCompilation)compilation).Options.AllowUnsafe`
and emit PICOJETSON004 if false.

**Reference vs Value types**: Reference type fields (string, object, dict, list, array)
store an 8-byte OBJECT POINTER, not the object itself. They are read as:
```csharp
ref IntPtr slot = ref Unsafe.AsRef<IntPtr>(data + offset);
return Unsafe.As<IntPtr, T>(ref slot); // reinterpret IntPtr as string/object ref
```
Value type fields (int, double, etc.) store the value inline:
```csharp
return Unsafe.Read<T>(data + offset); // read value directly from memory
```
The SG generates separate reader methods per TypeKind to handle this distinction.

### 2.3 CLR Field Layout Algorithm

The runtime may reorder fields in auto-layout classes for alignment. The SG must
predict the RUNTIME field order to compute correct offsets. The algorithm has been
stable across all CoreCLR/NativeAOT versions:

```
1. GC-reference fields first (string, object, class types)
   → in declaration order (as emitted by C# compiler)
2. Value-type fields after references
   → sorted by alignment requirement descending (largest first)
   → within same alignment: by declaration order
3. Each field aligned to its natural boundary
```

**Example**: `new { Name = "hello", Age = 42, Score = 99.5 }`

| Declared | Type | GC Ref? | Size | Align | Runtime Order | Offset |
|----------|------|---------|------|-------|---------------|--------|
| Name | string | ✅ | 8 | 8 | 1st | +0 |
| Score | double | — | 8 | 8 | 2nd | +8 |
| Age | int | — | 4 | 4 | 3rd | +16 |

**Verified**: NativeAOT layout matches algorithm prediction (Score before Age).

**Risk**: This is an implementation detail of the CLR. While stable for 10+ years,
ECMA-335 allows auto-layout reordering. We document the supported runtime versions
(.NET 6+).

**Type Size & Alignment Reference** (for the SG's offset calculator):

| TypeKind | C# Type | Size (x64) | Alignment | Ref/Val |
|----------|---------|------------|-----------|--------|
| `string` | `string` | 8 | 8 | Ref |
| `int32` | `int` | 4 | 4 | Val |
| `int64` | `long` | 8 | 8 | Val |
| `float32` | `float` | 4 | 4 | Val |
| `float64` | `double` | 8 | 8 | Val |
| `boolean` | `bool` | 1 | 1 | Val |
| `decimal` | `decimal` | 16 | 8 | Val |
| `datetime` | `DateTime` | 8 | 8 | Val |
| `dateonly` | `DateOnly` | 4 | 4 | Val |
| `timeonly` | `TimeOnly` | 8 | 8 | Val |
| `timespan` | `TimeSpan` | 8 | 8 | Val |
| `guid` | `Guid` | 16 | 4 | Val |
| `enum` | any enum | 4 | 4 | Val |
| `object` | any class | 8 | 8 | Ref |
| `dict` | `Dictionary<K,V>` | 8 | 8 | Ref |
| `list` | `List<T>` | 8 | 8 | Ref |
| `array` | `T[]` | 8 | 8 | Ref |
| `any` | `object?` | 8 | 8 | Ref |

Padding rule: after each field, if `offset % nextAlign != 0`, add padding bytes to reach
`nextAlign`. Final object size is padded to the maximum alignment of all fields (minimum 8
for reference types).

**Platform note**: The offsets above assume 64-bit (`IntPtr.Size = 8`). On 32-bit,
reference-type fields occupy 4 bytes. The generated interceptor code should use `IntPtr.Size`
rather than hardcoded `8` for reference-type field sizes and the MethodTable skip.
PicoJetson targets .NET 8+ where 32-bit is effectively legacy; 64-bit offsets are
used by default with a runtime `IntPtr.Size` fallback for compatibility.

---

## 3. Architecture

### 3.1 Shared Layer (`shared/Gen/AnonTypeHandler.cs`)

A new file in the shared `Gen/` directory (included by all 5 `.Gen` projects
via `<Compile Include>`) provides format-agnostic infrastructure:

```
AnonTypeHandler
├── AnonFieldInfo          // record struct: name, typeKind, runtimeOffset, isRef, nestedType
├── AnonTypeInfo           // record struct: fields[], filePath, line, column, uniqueId
├── BuildAnonTypeInfo()    // INamedTypeSymbol → AnonTypeInfo (property extraction + layout)
├── ComputeFieldLayout()   // CLR field order + offset calculation
├── GenerateReaderClass()  // emit __AnonReaders_<id> with typed reader methods
├── GenerateInterceptorClass() // emit interceptor methods + per-field serialization loop
└── GenerateNestedHelpers() // recursively handle anonymous properties with nested anon types
```

### 3.2 Format Adapter (per SG)

Each format SG adds a new pipeline stage that:

1. Calls `AnonTypeHandler.BuildAnonTypeInfo(namedType, callSite, config, attrs)`
2. Provides a callback `EmitValue(AnonFieldInfo, string varName) → string` that generates
   format-specific writer calls
3. Passes both to `AnonTypeHandler.GenerateInterceptor(info, callback)`

Example callback for JSON:
```csharp
static string EmitJsonValue(AnonFieldInfo f, string varName)
    => f.TypeKind switch
    {
        "string"   => $"jw.WriteString(Encoding.UTF8.GetBytes({varName}));",
        "int32"    => $"jw.WriteNumber({varName});",
        "float64"  => $"jw.WriteNumber({varName});",
        "boolean"  => $"jw.WriteBoolean({varName});",
        "datetime" => $"jw.WriteString(Encoding.UTF8.GetBytes({varName}.ToString(\"O\")));",
        "guid"     => $"jw.WriteString(Encoding.UTF8.GetBytes({varName}.ToString()));",
        "null"     => $"jw.WriteNull();",
        "object"   => $"{f.NestedHelperName}.Serialize(ref jw, {varName});",
        "dict"     => $"{f.NestedHelperName}.Serialize(ref jw, {varName});",
        "list" or "array" => $"EmitList_{GetElementHelper(f)}.Serialize(ref jw, {varName});",
        _          => $"jw.WriteString(Encoding.UTF8.GetBytes({varName}.ToString()));",
    };
```

### 3.3 Generated Interceptor (per call site)

For each anonymous type at each call site, the SG emits:

```csharp
// <auto-generated/>
#nullable enable
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using PicoSerDe.Core;
using PicoJetson;

namespace __PicoSerDe_<AssemblyName>
{
    // ── Unsafe Field Readers (per anonymous type layout) ──
    file unsafe static class __AnonReaders_<UniqueId>
    {
        internal static string Read_str(object obj, int offset) { /* 2.2 */ }
        internal static int Read_i32(object obj, int offset) { /* 2.2 */ }
        // ... one per distinct TypeKind used in this anonymous type
    }

    // ── Interceptors (one per call site, method names arbitrary) ──
    file static class __AnonInterceptor_<UniqueId>
    {
        // ── Serialize<T>(T, JsonOptions?) ──
        [InterceptsLocation("<FilePath>", <Line1>, <Column1>)]
        internal static string __Intercept_Ser_<CallSiteId>(T value, JsonOptions? options = null)
            where T : allows ref struct
        {
            // Example for: new { Name = "test", Value = 42 }
            // Layout: [MT 8B][string Name +0][int Value +8]
            object obj = (object)value!; // no-op cast for ref types (anon types are classes)
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var w = SerializerExtensions.RentWriter();
                var jw = new JsonWriter(w,
                    indented: JsonOptions.Current?.Indented ?? false,
                    maxDepth: JsonOptions.Current?.MaxDepth ?? 63);
                jw.WriteStartObject();
                // ── Per-field emit (format-specific, with null guards for ref types) ──
                {
                    var __v0 = __AnonReaders_<UniqueId>.Read_str(obj, /*offset*/0);
                    jw.WritePropertyName("Name"u8);
                    if (__v0 != null) jw.WriteString(Encoding.UTF8.GetBytes(__v0));
                    else jw.WriteNull();
                }
                {
                    var __v1 = __AnonReaders_<UniqueId>.Read_i32(obj, /*offset*/8);
                    jw.WritePropertyName("Value"u8);
                    jw.WriteNumber(__v1);
                }
                // ── End per-field ──
                jw.WriteEndObject();
                return Encoding.UTF8.GetString(w.WrittenSpan);
            }
            finally { JsonOptions.Current = prev; }
        }

        // ── SerializeToUtf8Bytes<T>(T, JsonOptions?) ──
        [InterceptsLocation("<FilePath>", <Line2>, <Column2>)]
        internal static byte[] __Intercept_Utf8_<CallSiteId>(T value, JsonOptions? options = null)
            where T : allows ref struct
        {
            // Same as above but returns w.WrittenSpan.ToArray() directly
            // (avoids Encoding.UTF8.GetString → re-encoding round trip)
        }

        // ── Serialize<T>(IBufferWriter<byte>, T, JsonOptions?) ──
        [InterceptsLocation("<FilePath>", <Line3>, <Column3>)]
        internal static void __Intercept_Writer_<CallSiteId>(
            IBufferWriter<byte> writer, T value, JsonOptions? options = null)
            where T : allows ref struct
        {
            // Write directly to user-provided writer — zero extra allocation
        }
    }
}
```

### 3.4 Nesting Support

Anonymous types may contain nested anonymous types:

```csharp
var anon = new { 
    Name = "root", 
    Child = new { X = 1, Y = 2 }  // nested anonymous type
};
var json = JsonSerializer.Serialize(anon);
```

The SG handles this in `BuildAnonTypeInfo()`:
1. Detect nested anonymous type property: `Child.IsAnonymousType == true`
2. Recursively build `AnonTypeInfo` for the nested type (independent readers + layout)
3. Store in `AnonFieldInfo.NestedAnonType`
4. Generate a nested serialization helper that the outer interceptor calls:
   ```csharp
   // Inside outer interceptor:
   var __child = __AnonReaders_<OuterId>.Read_obj(obj, /*offset*/24);
   if (__child != null) {
       jw.WritePropertyName("Child"u8);
       __AnonInner_<InnerId>.SerializeChild(ref jw, __child);
   } else {
       jw.WritePropertyName("Child"u8);
       jw.WriteNull();
   }
   ```

The nested `new { X = 1, Y = 2 }` is NEVER serialized independently — it only exists
as a field of the outer anonymous object. No separate interceptor is needed for it.

Collections and dictionaries in anonymous types reuse the existing infrastructure:
- `new { Items = new[] { 1, 2, 3 } }` → reads the array reference (IntPtr), then emits existing array helper
- `new { Dict = new Dictionary<string, object?>() }` → reads dict reference, uses `__PicoAnyDictHelper`

### 3.5 Deduplication

Same anonymous type shape at different call sites:

```csharp
var a = JsonSerializer.Serialize(new { X = 1 }); // call site A
var b = JsonSerializer.Serialize(new { X = 1 }); // call site B — same shape
```

- **Field readers**: Deduplicated — `UniqueId` is a structural hash of the anonymous type's
  properties (names + TypeKinds + declaration order). When a property type is itself anonymous
  (nested), its recursive structural hash is included. Same shape at different call sites
  → same `UniqueId` → single `__AnonReaders_<id>` class shared across call sites.
  Two anonymous types with the same property names but different nested structures get
  different `UniqueId` values because the nested type hashes differ.
- **Interceptors**: Per-call-site — each has a different `[InterceptsLocation]` (different
  file/line/column). Multiple interceptor methods in the same class, distinguished by
  `<CallSiteId>` suffix.

### 3.6 Error Handling

| Scenario | Diagnostic | Severity |
|----------|-----------|----------|
| C# < 12 / .NET < 8 consumer | PICOJETSON003: "Anonymous types require C# 12+" | Error |
| Missing `<AllowUnsafeBlocks>` | PICOJETSON004: "Requires AllowUnsafeBlocks" | Error |
| `ref struct` in anonymous type | PICOJETSON005: "ref struct property '{0}' not supported" | Warning |
| Unsupported property type | Existing PICOSERDE001 (shared) | Warning |

**Detection notes**:
- C# version: SG checks `((CSharpCompilation)compilation).LanguageVersion >= LanguageVersion.CSharp12`.
  If false, emit PICOJETSON003 and skip interceptor generation.
- AllowUnsafeBlocks: SG checks `((CSharpCompilation)compilation).Options.AllowUnsafe`.
  If false, emit PICOJETSON004 and skip interceptor generation.

---

## 4. Implementation Plan

All work delivered in a single release — no phased rollout.

### 4.1 Shared Infrastructure

Add `shared/Gen/AnonTypeHandler.cs` (included by all 5 `.Gen` projects):
- `AnonFieldInfo`, `AnonTypeInfo` record structs
- `BuildAnonTypeInfo()` — INamedTypeSymbol → AnonTypeInfo (property extraction + CLR layout)
- `ComputeFieldLayout()` — CLR field ordering with alignment padding
- `GenerateReaderClass()` — emit `__AnonReaders_<id>` with typed reader methods
- `GenerateInterceptorClass()` — emit interceptor methods + per-field serialization loop
- `GenerateNestedHelpers()` — emit helpers for nested anonymous type properties

### 4.2 Per-Format Pipeline

Each format SG adds a **new pipeline stage** (parallel to existing `usageDriven`/
`attrDriven` pipelines) that intercepts anonymous type calls:

```csharp
// New pipeline in JsonSerializerGenerator.Initialize():
var anonDriven = context.SyntaxProvider.CreateSyntaxProvider(
    predicate: static (node, _) => IsCandidate(node),
    transform: static (ctx, _) =>
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol m) return null;
        if (m.TypeArguments.Length != 1) return null;
        if (m.TypeArguments[0] is not INamedTypeSymbol nt || !nt.IsAnonymousType) return null;
        return AnonTypeHandler.BuildAnonTypeInfo(nt, ctx, Config, Attrs);
    }
).Where(static a => a is not null);

context.RegisterSourceOutput(anonDriven.Collect().Combine(asmName), (spc, pair) => {
    foreach (var info in pair.Left)
        AnonTypeHandler.GenerateInterceptorClass(spc, info, EmitJsonValue);
});
```

The `EmitJsonValue` callback is format-specific. Each SG also adds a guard in the existing
`Transform()` method to prevent the old pipeline from emitting PICOJETSON002:

```csharp
// Add to existing Transform() in each SG, before TransformTypeSymbol:
if (namedType.IsAnonymousType) return null; // handled by anonDriven pipeline
```

Formats: JSON / MsgPack / INI / TOML / YAML — same pattern, ~65 lines each.

### 4.3 Collection/Dict Properties

Anonymous types with collection/dict properties reuse existing SG infrastructure:
- `new { Items = new[] { 1, 2 } }` — generates array helper inline or reuses existing
- `new { Dict = new Dictionary<string, object?>() }` — uses `__PicoAnyDictHelper`
- Nested list/anonymous combinations detected during `BuildAnonTypeInfo()` traversal

### 4.4 Build Integration

1. **`.targets`** — each format's NuGet package adds the interceptor namespace:
   ```xml
   <!-- PicoJetson.targets -->
   <PropertyGroup>
     <InterceptorsNamespaces>$(InterceptorsNamespaces);__PicoSerDe_$(AssemblyName.Replace('.','_').Replace('-','_').Replace(' ','_'))</InterceptorsNamespaces>
   </PropertyGroup>
   ```
2. **Diagnostic suppression**: Generated files include `#pragma warning disable CS9270`.
3. **Documentation**: Update README + skill file with anonymous type section.

### 4.5 Non-Regression

- Existing `Serialize<T>()` behavior for named types: unchanged
- Existing `ModuleInitializer` registrations: unaffected (interceptors replace the call, not the registration)
- `SerRegistry<JsonFormat, T>.Handler`: not touched for anonymous types
- All existing tests continue to pass without modification

---

## 5. Testing Strategy

### Unit Tests (per-format)

| Test | Description |
|------|-------------|
| `AnonType_SimpleProperties` | `new { Name, Age }` → correct JSON/MsgPack/... |
| `AnonType_AllTypeKinds` | All supported primitives in one anonymous type |
| `AnonType_NestedAnonymous` | Anonymous property with anonymous value |
| `AnonType_WithList` | `new { Items = new[] { 1, 2 } }` |
| `AnonType_WithDictionary` | `new { Dict = new Dictionary<string, object?>() }` |
| `AnonType_FieldReordering` | `new { A_string, B_int, C_double }` → verify offsets |
| `AnonType_JsonOptions` | `JsonOptions.Indented` honored; `PropertyNamingPolicy` deferred (see limitations) |
| `AnonType_NullProps` | Nullable reference properties → `null` emitted correctly |
| `AnonType_Dedup` | Same shape used twice → only one reader class generated |

**Known limitations in initial release**:
- `PropertyNamingPolicy` (CamelCase etc.) not applied to anonymous type property names — names match C# declaration
- `DefaultIgnoreCondition` not applied — all non-null properties emitted
- `MaxDepth` not enforced for anonymous types

### Integration Tests

| Test | Description |
|------|-------------|
| `AnonType_AOT_Publish` | `dotnet publish /p:PublishAot=true` → zero IL warnings |
| `AnonType_RoundTrip` | Serialize anon → deserialize to Dictionary<string, JsonElement> → verify |

### Cross-Validation

- Compare anonymous type serialization output vs. named class serialization of same shape
- Compare vs. `System.Text.Json` for same inputs

---

## 6. Rollout & Migration

1. **vNext**: All 5 formats support anonymous type serialization
2. **C# < 12 consumers**: Clear diagnostic with Quick Fix to generate named class
3. **Existing code**: No breaking changes — `Serialize<T>()` behavior unchanged for named types
4. **Documentation**: Update `README.md` + skill file with anonymous type section
