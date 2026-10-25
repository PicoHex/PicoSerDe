# Anonymous Type Serialization — Remaining Features Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Complete anonymous type serialization: nested types, collections, DefaultIgnoreCondition, MaxDepth.

**Architecture:** All changes in `shared/Gen/AnonTypeHandler.cs` per-field emit. No new files, no API changes.

---

### Task 1: Nested Anonymous Type Helpers

**Files:** `shared/Gen/AnonTypeHandler.cs`

- [ ] **Step 1: Add `GenerateNestedHelper` method**

Emit a `__AnonInner_{UniqueId}` class with `Serialize` method for nested anonymous types:

```csharp
internal static string GenerateNestedHelper(AnonTypeInfo info, FormatConfig config, AnonFormatConfig afc, string fmtTag, string ot, Func<AnonFieldInfo, string, string, string> emitField)
{
    string wt = config.FormatTag switch { }; // same as GenerateInterceptorClass
    string wv = wt == "JsonWriter" ? "jw" : "__w";
    
    var sb = new StringBuilder();
    sb.AppendLine($"file static class __AnonInner_{info.UniqueId}");
    sb.AppendLine("{");
    sb.AppendLine($"    internal static void Serialize(ref {wt} {wv}, object obj)");
    sb.AppendLine("    {");
    foreach (var f in info.Fields)
    {
        sb.AppendLine($"        var __v = __AnonReaders_{fmtTag}_{info.UniqueId}.Read_{f.TypeKind}(obj, {f.RuntimeOffset});");
        // Key + value emit (same pattern as outer interceptor, without [InterceptsLocation])
        EmitAnonFieldKey(sb, wv, f, afc, ot, "        ");
        if (TypeLayout[f.TypeKind].IsRef) { /* null guard */ }
        sb.AppendLine($"        {emitField(f, "__v", wv)}");
    }
    sb.AppendLine("    }");
    sb.AppendLine("}");
    return sb.ToString();
}
```

- [ ] **Step 2: Wire into `GenerateInterceptorClass`**

Add nested branch to per-field emit, before existing `isRef/else` logic:

```csharp
if (f.NestedAnonType is { } nested)
{
    sb.AppendLine($"                var __child = __AnonReaders_{fmtTag}_{info.UniqueId}.Read_object(obj, {f.RuntimeOffset});");
    sb.AppendLine("                if (__child != null) {");
    if (!afc.EmbedsKeyInValue)
        EmitAnonFieldKey(sb, wv, f, afc, ot, "                    ");
    sb.AppendLine($"                    __AnonInner_{nested.UniqueId}.Serialize(ref {wv}, __child);");
    sb.AppendLine("                } else { /* null: skip or WriteNull per format */ }");
    continue;
}
```

- [ ] **Step 3: Emit nested helpers alongside interceptors**

In `GenerateInterceptorClass`, after generating the main interceptor, also emit nested helpers by traversing `info.Fields` to collect unique nested types:

```csharp
// Collect unique nested AnonTypeInfo from all fields recursively
var nestedSet = new HashSet<string>();
void CollectNested(AnonFieldInfo f) {
    if (f.NestedAnonType is { } n && nestedSet.Add(n.UniqueId))
        foreach (var nf in n.Fields)
            if (nf.NestedAnonType is { } nn) CollectNested(nf);
}
foreach (var f in info.Fields) CollectNested(f);
// Emit helpers for each unique nested type
foreach (var uid in nestedSet)
    sb.Append(GenerateNestedHelper(/*resolve by uid*/, config, afc, fmtTag, ot, emitField));
```

- [ ] **Step 4: Build and verify nested anonymous types work**

Manual test:
```csharp
var json = JsonSerializer.Serialize(new { Outer = "root", Inner = new { X = 1, Y = 2 } });
// Expected: {"Outer":"root","Inner":{"X":1,"Y":2}}
```

- [ ] **Step 5: Commit**

```bash
git add shared/Gen/AnonTypeHandler.cs
git commit -m "feat: nested anonymous type serialization helpers"
```

---

### Task 2: Collection/Dict Fields

**Files:** `shared/Gen/AnonTypeHandler.cs`

- [ ] **Step 1: Add collection/dict branch to per-field emit**

After nested branch, before existing `isRef/else`:

```csharp
if (f.TypeKind is "list" or "array" or "dict")
{
    sb.AppendLine($"                var __col = __AnonReaders_{fmtTag}_{info.UniqueId}.Read_object(obj, {f.RuntimeOffset});");
    sb.AppendLine("                if (__col != null) {");
    if (!afc.EmbedsKeyInValue)
        EmitAnonFieldKey(sb, wv, f, afc, ot, "                    ");
    sb.AppendLine($"                    var __typed = ({f.TypeFullName})__col;");
    sb.AppendLine($"                    {CollectionEmitHelper(f, config)}.Serialize(ref {wv}, __typed);");
    sb.AppendLine("                } else { /* null handling */ }");
    continue;
}
```

`CollectionEmitHelper(f, config)` resolves to existing inner helper class name (e.g., `__ListInt32JsonInner`).

- [ ] **Step 2: Handle `Dictionary<string, object?>` case**

```csharp
if (f.TypeKind == "dict" && f.ElementTypeKind == "any")
    sb.AppendLine($"                    __PicoAnyDictHelper.SerializeValue(ref {wv}, __typed);");
else if (f.TypeKind == "dict")
    sb.AppendLine($"                    {CollectionEmitHelper(f, config)}.Serialize(ref {wv}, __typed);");
```

- [ ] **Step 3: Verify with tests**

```csharp
var json = JsonSerializer.Serialize(new { Items = new[] { 1, 2, 3 } });
var ini = IniSerializer.Serialize(new { Dict = new Dictionary<string, object?> { ["k"] = "v" } });
```

- [ ] **Step 4: Commit**

```bash
git add shared/Gen/AnonTypeHandler.cs
git commit -m "feat: collection/dict field support in anonymous type serialization"
```

---

### Task 3: DefaultIgnoreCondition

**Files:** `shared/Gen/AnonTypeHandler.cs`

- [ ] **Step 1: Merge `DefaultIgnoreCondition` into per-field emit for JSON ref-type fields**

In the per-field emit, where `isRef && afc.HasNullLiteral` is handled, add a sub-branch for JSON:

```csharp
if (isRef && afc.HasNullLiteral)
{
    if (afc.HasOptionsParam)
    {
        // JSON: runtime check for DefaultIgnoreCondition
        sb.AppendLine($"                if (__v != null || {ot}.Current?.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingNull) {{");
        EmitAnonFieldKey(sb, wv, f, afc, ot, indent);
        sb.AppendLine($"                    {emitField(f, "__v", wv)}");
        sb.AppendLine("                }");
    }
    else
    {
        // MsgPack: existing WriteNull path
        ...
    }
}
```

- [ ] **Step 2: Add test**

```csharp
var json = JsonSerializer.Serialize(
    new { Name = (string?)null, Value = 42 },
    new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
await Assert.That(json).DoesNotContain("Name");  // key skipped
```

- [ ] **Step 3: Commit**

```bash
git add shared/Gen/AnonTypeHandler.cs
git commit -m "feat: DefaultIgnoreCondition support for anonymous types"
```

---

### Task 4: MaxDepth Enforcement

**Files:** `shared/Gen/AnonTypeHandler.cs`

- [ ] **Step 1: Add local function wrapper when nesting exists**

In `GenerateInterceptorClass`, when `info.Fields.Any(f => f.NestedAnonType != null)`:

```csharp
bool hasNesting = info.Fields.Any(f => f.NestedAnonType != null);
if (hasNesting)
{
    sb.AppendLine($"        int __maxDepth = {ot}.Current?.MaxDepth ?? 63;");
    sb.AppendLine("        void __SerializeObj(object __o, int __depth) {");
    sb.AppendLine("            if (__depth >= __maxDepth) throw new FormatException(\"MaxDepth exceeded\");");
    // flat per-field emit here, nested calls pass __depth + 1
    sb.AppendLine("        }");
    sb.AppendLine("        __SerializeObj(obj, 0);");
}
else
{
    // existing flat per-field emit (no depth tracking)
}
```

- [ ] **Step 2: Add test for depth enforcement (expect FormatException at depth 2)**

```csharp
// Create deeply nested anonymous type (depth > MaxDepth)
// Assert FormatException thrown
```

- [ ] **Step 3: Commit**

```bash
git add shared/Gen/AnonTypeHandler.cs
git commit -m "feat: MaxDepth enforcement for nested anonymous types"
```

---

### Task 5: Update Tests & Docs

- [ ] **Step 1: Remove `[Skip("Phase 4")]` from nested anonymous test**, add full nested/collection/dict tests
- [ ] **Step 2: Update skill file** — remove "Nested anonymous types" and "DefaultIgnoreCondition" from known limitations
- [ ] **Step 3: Run full test suite** `dotnet test --filter "AnonType"`
- [ ] **Step 4: Commit**

```bash
git add PicoJetson/tests/PicoJetson.Integration.Tests/AnonTypeSerializationTests.cs
git commit -m "test: enable nested/collection anon type tests, update known limitations"
```
