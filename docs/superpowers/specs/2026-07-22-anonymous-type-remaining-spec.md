# Anonymous Type Serialization — Remaining Features Spec

**Date**: 2026-07-22  
**Status**: Draft  
**Scope**: PicoSerDe — anonymous type serialization completion

---

## 1. Phase 4: Nested Anonymous Types & Collections

### Problem

Anonymous types containing nested anonymous types or collections degrade to `ToString()`:
```csharp
new { Outer = "root", Inner = new { X = 1, Y = 2 } }
// → {"Outer":"root","Inner":"{ X = 1, Y = 2 }"}  ← ToString()
```

`AnonFieldInfo.NestedAnonType` already stores the complete nested type structure
recursively. Only the emit path doesn't consume it.

### Design

Per-field emit in `GenerateInterceptorClass`, after reading the field value,
check `f.NestedAnonType`. If set, generate nested serialization via a helper
method on the nested type's reader class.

#### 1.1 Nested Anonymous Type

```csharp
// SG generates nested helper for inner anonymous type:
file static class __AnonInner_{InnerId}
{
    internal static void Serialize(ref {wt} {wv}, object obj)
    {
        // same per-field loop as outer interceptor, without interceptor attribute
        // reads fields from obj at offsets computed by inner's AnonTypeInfo
    }
}
```

Per-field emit for the outer type:
```csharp
if (f.NestedAnonType is { } nested)
{
    sb.AppendLine($"                var __child = __AnonReaders_{info.UniqueId}.Read_object(obj, {f.RuntimeOffset});");
    sb.AppendLine("                if (__child != null) {");
    EmitFieldKey(sb, wv, f.JsonName, afc.KeyIsEncodedString, "                    ");
    sb.AppendLine($"                    __AnonInner_{nested.UniqueId}.Serialize(ref {wv}, __child);");
    sb.AppendLine("                } else { /* null handling per format */ }");
}
```

`__AnonInner_X.Serialize` is a static helper emitted alongside the reader class
for each unique nested anonymous type (deduplicated cross site via `UniqueId`).

#### 1.2 Collections (List<T> / T[] / Dictionary<K,V>)

For collection-typed fields, the element type information is in `AnonFieldInfo`
(`ElementTypeKind`, `ElementTypeName`). The existing SG infrastructure already
generates collection helpers for top-level `List<T>` and `T[]`. Reuse them:

```csharp
if (f.TypeKind is "list" or "array" or "dict")
{
    sb.AppendLine($"                var __col = __AnonReaders_{info.UniqueId}.Read_object(obj, {f.RuntimeOffset});");
    sb.AppendLine("                if (__col != null)");
    sb.AppendLine("                {");
    EmitFieldKey(...);
    sb.AppendLine($"                    var __typed = ({f.TypeFullName})__col;");
    sb.AppendLine($"                    {CollectionHelperName(f)}.Serialize(ref {wv}, __typed);");
    sb.AppendLine("                }");
    // else: null handling per format
}
```

If the collection element type IS itself an anonymous type → uses the nested
pattern above (§1.1).

#### 1.3 Dictionary<string, object?> (any-valued dicts)

Reuse existing `__PicoAnyDictHelper` (already generated when `object?` dicts
appear anywhere in the compilation).

### Scope

- `AnonTypeHandler.GenerateInterceptorClass` — add nested/collection/dict branches to per-field emit
- `AnonTypeHandler.GenerateNestedHelper` — new method: emit nested serialize helper
- No changes to `BuildAnonTypeInfo` (already collects nested data)
- No changes to per-SG callbacks (nested emit goes through same `EmitValue`)

---

## 2. DefaultIgnoreCondition

### Problem

`JsonOptions.DefaultIgnoreCondition = WhenWritingNull` is ignored for
anonymous types — null ref fields still emit `"Key":null`.

### Design

Single JSON-only change: merge `DefaultIgnoreCondition` into per-field emit.
For JSON + ref-type fields, the SG emits a runtime check:

```csharp
// Generated interceptor code (JSON, per-field):
// Write key+value unless (value is null AND WhenWritingNull is set)
if (__v != null || JsonOptions.Current?.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingNull)
{
    jw.WritePropertyName("Key"u8);
    jw.WriteString(...);
}
```

The SG generates this by checking `afc.HasOptionsParam && isRef` at SG time
and emitting the conditional block above instead of the existing null-handling path.

Only JSON (`JsonOptions`) has `DefaultIgnoreCondition`. Other formats use
`skipNull` flag which already covers null omission where the format lacks
null literals.

### Scope

- `AnonTypeHandler` per-field emit: add `DefaultIgnoreCondition`-aware emit path for JSON, merge with
  existing `skipNull` logic
- No changes to `AnonFormatConfig`

---

## 3. MaxDepth Enforcement

### Problem

Deeply nested anonymous types have no depth guard — could overflow stack.

### Design

JSON-only (`JsonOptions.MaxDepth`). Only active when anonymous type contains
nested anonymous fields. Single-layer anonymous types keep the current flat code.

Interceptor method defines a local function:

```csharp
int maxDepth = {ot}.Current?.MaxDepth ?? 63;

// Local function for recursion
string SerializeRecursive(object obj, int depth)
{
    if (depth >= maxDepth) throw new FormatException("MaxDepth exceeded");
    // ... per-field emit, nested calls pass depth + 1 ...
}
```

For single-layer anonymous types (no nesting), no depth tracking needed —
the interceptor body stays flat as today.

### Scope

- `AnonTypeHandler.GenerateInterceptorClass` — add local function wrapper
  when `info.Fields.Any(f => f.NestedAnonType != null)`
- No changes when anonymous type has no nested anonymous fields

---

## 4. Non-Goals

- `WhenWritingDefault` (value type defaults) — rare in anonymous types
- Custom `JsonNamingPolicy` subclasses (only built-in `CamelCase` supported)
- Nested anonymous types inside collections (`List<{...}>`) — deferred further
