# Ref Struct Serialization — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add ref struct serialization support to PicoSerDe (JSON first) via a delegate-based dispatch that bypasses the `ISerializer<T>` interface incompatibility with ref structs.

**Architecture:** Replace `Cache<T>.Serializer : ISerializer<T>?` with `SerCache<T>.Handler : SerDelegate<T>?` where `SerDelegate<T>` carries `where T : allows ref struct`. The SG detects `INamedTypeSymbol.IsRefLikeType` and generates a `file static class` with a static `Serialize` method instead of a `struct : ISerializer<T>`.

**Tech Stack:** C# 13 (`allows ref struct` on delegates), Roslyn Source Generators, net10.0

## Global Constraints

- `LangVersion`: `latest` (all projects already use this)
- `TargetFramework`: `net10.0` (runtime), `netstandard2.0` (SG)
- Serialize public API must accept ref struct `T` — `where T : allows ref struct`
- Existing `Register<T>(ISerializer<T>, IDeserializer<T>)` must remain functional
- Deserialization is NOT supported for ref structs
- All 5 formats share the pattern; JSON implemented first

---

## File Structure

```
PicoSerDe.Core/src/SerDelegate.cs          [CREATE]  — new delegate type
PicoJetson/src/PicoJetson/JsonSerializer.cs [MODIFY] — Cache split, allows ref struct, new Register overloads
PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs [MODIFY] — IsRefLikeType detection + ref struct code shape
PicoJetson/tests/PicoJetson.Tests/RefStructTests.cs     [CREATE]  — integration tests
shared/Gen/GenInfrastructure.cs            [MODIFY] — (possible helper, optional)
```

---

### Task 1: Add `SerDelegate<T>` to PicoSerDe.Core

**Files:**
- Create: `PicoSerDe.Core/src/SerDelegate.cs`
- Create: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SerDelegateTests.cs` (just compile-time smoke)

**Interfaces:**
- Produces: `public delegate void SerDelegate<T>(IBufferWriter<byte> writer, T value) where T : allows ref struct;`

- [ ] **Step 1: Write minimal compile-time smoke test**

Create `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SerDelegateTests.cs`:

```csharp
namespace PicoSerDe.Core.Tests;

public ref struct SmokeRef { public int X; }

public class SerDelegateTests
{
    // Compile-time verification only: SerDelegate<T> accepts ref struct T
    [Test]
    public async Task SerDelegate_Accepts_RefStruct_AtCompileTime()
    {
        // Compile-time verification: SerDelegate<SmokeRef> is a valid type.
        // We can declare a variable of this delegate type pointing to a ref struct.
        SerDelegate<SmokeRef> handler = static (_, _) => { };
        await Assert.That(handler).IsNotNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails (SerDelegate doesn't exist yet)**

```bash
dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests/PicoSerDe.Core.Tests.csproj --filter "SerDelegate_Accepts_RefStruct_AtCompileTime"
```

Expected: FAIL — `SerDelegate` type not found.

- [ ] **Step 3: Create `PicoSerDe.Core/src/SerDelegate.cs`**

```csharp
namespace PicoSerDe.Core;

/// <summary>
/// Universal serialization delegate that accepts any type including ref structs.
/// Used as the hot-path dispatch target for <see cref="SerCache{T}"/>.
/// </summary>
public delegate void SerDelegate<T>(IBufferWriter<byte> writer, T value)
    where T : allows ref struct;
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests/PicoSerDe.Core.Tests.csproj --filter "SerDelegate_Accepts_RefStruct_AtCompileTime"
```

Expected: PASS.

- [ ] **Step 5: Build solution to ensure no regressions**

```bash
dotnet build PicoSerDe.slnx
```

Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add PicoSerDe.Core/src/SerDelegate.cs PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SerDelegateTests.cs
git commit -m "feat: add SerDelegate<T> with allows ref struct support"
```

---

### Task 2: Refactor `JsonSerializer` cache and add `allows ref struct`

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs` (full rewrite of cache + serialization methods)
- Create: `PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs` (model types + tests for new path)

**Interfaces:**
- Consumes: `SerDelegate<T>` from Task 1
- Produces:
  - `JsonSerializer.Register<T>(SerDelegate<T> handler) where T : allows ref struct`
  - `JsonSerializer.RegisterDeserializer<T>(IDeserializer<T> deserializer)`
  - `JsonSerializer.Serialize<T>(T value, ...) where T : allows ref struct`
  - `JsonSerializer.SerializeToUtf8Bytes<T>(T value, ...) where T : allows ref struct`
  - `JsonSerializer.Serialize<T>(IBufferWriter<byte> writer, T value, ...) where T : allows ref struct`
  - `JsonSerializer.SerializeLines<T>(IEnumerable<T> values, ...) where T : allows ref struct`

- [ ] **Step 1: Write failing test — serialize a ref struct using hand-registered SerDelegate**

Create `PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs`:

```csharp
namespace PicoJetson.Tests;

// Simple ref struct for testing
public ref struct Point3D
{
    public float X, Y, Z;
}

// Test suite
public class RefStructSerializerTests
{
    [Test]
    public async Task HandRegistered_SerDelegate_Serializes_RefStruct()
    {
        // Manually register a SerDelegate<Point3D> (simulating what SG will do)
        JsonSerializer.Register<Point3D>(static (writer, value) =>
        {
            var jw = new JsonWriter(writer);
            jw.WriteStartObject();
            jw.WritePropertyName("X"u8);
            jw.WriteNumber(value.X);
            jw.WritePropertyName("Y"u8);
            jw.WriteNumber(value.Y);
            jw.WritePropertyName("Z"u8);
            jw.WriteNumber(value.Z);
            jw.WriteEndObject();
        });

        var pt = new Point3D { X = 1.5f, Y = 2.5f, Z = 3.5f };
        var json = JsonSerializer.Serialize(pt);

        await Assert.That(json).Contains("1.5");
        await Assert.That(json).Contains("2.5");
        await Assert.That(json).Contains("3.5");
    }

    [Test]
    public async Task RegularType_Still_Serializes_Via_Cache()
    {
        // The existing test models should still work — just verify Person compiles and serializes
        var person = new GeneratorTests.Person { Name = "Test", Age = 42 };
        var json = JsonSerializer.Serialize(person);
        await Assert.That(json).Contains("Test");
        await Assert.That(json).Contains("42");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "HandRegistered_SerDelegate_Serializes_RefStruct"
```

Expected: FAIL — `Register<T>(SerDelegate<T>)` doesn't exist yet, or `Serialize<T>` rejects ref struct T.

- [ ] **Step 3: Refactor `JsonSerializer.cs` — Part A: Cache split**

Replace:

```csharp
    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }
```

With:

```csharp
    // Serialization cache — allows ref struct via delegate
    private static class SerCache<T> where T : allows ref struct
    {
        internal static SerDelegate<T>? Handler;
    }

    // Deserialization cache — unchanged (no ref struct support)
    private static class Cache<T>
    {
        internal static IDeserializer<T>? Deserializer;
    }
```

- [ ] **Step 3b: Refactor `JsonSerializer.cs` — Part B: Add `Register` overloads**

Replace:

```csharp
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
    }
```

With:

```csharp
    /// <summary>Register a delegate-based serializer (SG primary path).</summary>
    public static void Register<T>(SerDelegate<T> handler)
        where T : allows ref struct
    {
        SerCache<T>.Handler = handler;
    }

    /// <summary>
    /// Register serializer + deserializer (compat path for hand-written ISerializer/IDeserializer).
    /// The ISerializer<T> is wrapped into a SerDelegate<T> internally.
    /// </summary>
    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        SerCache<T>.Handler = (writer, value) => serializer.Serialize(writer, value);
        Cache<T>.Deserializer = deserializer;
    }

    /// <summary>Register a deserializer only.</summary>
    public static void RegisterDeserializer<T>(IDeserializer<T> deserializer)
    {
        Cache<T>.Deserializer = deserializer;
    }
```

- [ ] **Step 3c: Refactor `JsonSerializer.cs` — Part C: Update `Serialize`, `SerializeToUtf8Bytes`, `Serialize(IBufferWriter)`, `SerializeLines`**

Replace all `Cache<T>.Serializer` references with `SerCache<T>.Handler` and add `where T : allows ref struct`.

`SerializeToUtf8Bytes` — old:

```csharp
    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonOptions? options = null)
    {
        if (Cache<T>.Serializer is { } s)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var writer = SerializerExtensions.RentWriter();
                s.Serialize(writer, value);
                return writer.WrittenSpan.ToArray();
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return default!;
    }
```

New:

```csharp
    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonOptions? options = null)
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var writer = SerializerExtensions.RentWriter();
                h(writer, value);
                return writer.WrittenSpan.ToArray();
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return default!;
    }
```

`Serialize` (string overload) — replace `Cache<T>.Serializer is { } s` → `SerCache<T>.Handler is { } h` and `s.SerializeToString(value)` → use the same writer-based pattern:

```csharp
    public static string Serialize<T>(T value, JsonOptions? options = null)
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                var writer = SerializerExtensions.RentWriter();
                h(writer, value);
                return Encoding.UTF8.GetString(writer.WrittenSpan);
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
        return "";
    }
```

`Serialize(IBufferWriter)` — replace `Cache<T>.Serializer is { } s` → `SerCache<T>.Handler is { } h` and `s.Serialize(writer, value)` → `h(writer, value)`:

```csharp
    public static void Serialize<T>(
        IBufferWriter<byte> writer,
        T value,
        JsonOptions? options = null
    )
        where T : allows ref struct
    {
        if (SerCache<T>.Handler is { } h)
        {
            var prev = JsonOptions.Current;
            JsonOptions.Current = options;
            try
            {
                h(writer, value);
            }
            finally
            {
                JsonOptions.Current = prev;
            }
        }
        else
            SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
    }
```

`SerializeLines` — replace `Cache<T>.Serializer` → `SerCache<T>.Handler` and `s.Serialize(buf, v)` → `h(buf, v)`, add `where T : allows ref struct` (not strictly required since `IEnumerable<ref struct>` is illegal, but kept for API consistency):

```csharp
    public static byte[] SerializeLines<T>(
        IEnumerable<T> values,
        JsonOptions? options = null
    )
        where T : allows ref struct
    {
        var prev = JsonOptions.Current;
        JsonOptions.Current = options;
        try
        {
            var buf = new ArrayBufferWriter<byte>(1024);
            foreach (var v in values)
            {
                if (SerCache<T>.Handler is { } h)
                {
                    h(buf, v);
                    buf.Write("\n"u8);
                }
                else
                {
                    SerializerExtensions.ThrowNoSerializer<T>("PicoJetson.Gen");
                }
            }
            return buf.WrittenSpan.ToArray();
        }
        finally
        {
            JsonOptions.Current = prev;
        }
    }
```

- [ ] **Step 3d: Do NOT modify `Deserialize`, `DeserializeFromStreamAsync`, `DeserializeLines`, `DeserializeAsyncEnumerable`** — these use `Cache<T>.Deserializer` and do NOT get `allows ref struct`.

- [ ] **Step 4: Run the failing test — should now pass**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "HandRegistered_SerDelegate_Serializes_RefStruct"
```

Expected: PASS.

- [ ] **Step 5: Run full test suite to ensure no regressions**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj
```

Expected: All existing tests still pass (they go through the compat `Register(ISerializer, IDeserializer)` path).

- [ ] **Step 6: Build full solution**

```bash
dotnet build PicoSerDe.slnx
```

Expected: Build succeeds. SG projects can still reference `ISerializer<T>` indirectly through their generated registration code.

- [ ] **Step 7: Commit**

```bash
git add PicoJetson/src/PicoJetson/JsonSerializer.cs PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs
git commit -m "refactor: split Cache, add allows ref struct to Serialize, add SerDelegate Register"
```

---

### Task 3: Source Generator — detect ref struct and generate new shape

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`
- Modify: `PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs` (add SG-generated test)

**Interfaces:**
- Consumes: `JsonSerializer.Register<T>(SerDelegate<T>)` from Task 2, `IsRefLikeType` from Roslyn
- Produces: SG generates `file static class TJsonSer { public static void Serialize(...) }` for ref structs, `file static class TSerDeRegistration { [ModuleInitializer] ... Register<T>(TJsonSer.Serialize) }`

- [ ] **Step 1: Write failing test — define a ref struct that the SG should generate for**

Add to `PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs`:

```csharp
// Ref struct that the SG should generate a serializer for
public ref struct Vec2
{
    public int X, Y;
}

// In test class:
[Test]
public async Task SourceGen_Generates_Serializer_For_RefStruct()
{
    // When the SG runs, it should detect Vec2 is a ref struct,
    // generate a static serializer, and register it via ModuleInitializer.
    var v = new Vec2 { X = 10, Y = 20 };
    var json = JsonSerializer.Serialize(v);

    // Should produce valid JSON with both properties
    await Assert.That(json).Contains("10");
    await Assert.That(json).Contains("20");
    await Assert.That(json).Contains("X");
    await Assert.That(json).Contains("Y");
}
```

- [ ] **Step 2: Run test to verify it FAILS (SG doesn't handle ref structs yet)**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "SourceGen_Generates_Serializer_For_RefStruct"
```

Expected: FAIL — either build error (SG tries to emit `ISerializer<Vec2>` which fails) or runtime `InvalidOperationException` (no serializer registered).

- [ ] **Step 3: Modify `TransformType` — detect `IsRefLikeType`**

In `JsonSerializerGenerator.cs`, locate the `Transform` method (around line ~128). After the line:

```csharp
if (typeArg is not INamedTypeSymbol namedType)
    return null;
```

Add:

```csharp
if (namedType.IsRefLikeType)
{
    // Ref struct: use anonymous type's own transform logic but with
    // includeReadOnlyProperties since ref struct properties may be readonly.
    // Pass through TransformTypeSymbol with a flag.
    return TransformTypeSymbol(
        namedType, Config, Attrs,
        includeReadOnlyProperties: true
    );
}
```

- [ ] **Step 3b: Modify `TransformTypeSymbol` — pass through ref struct flag**

In `GenInfrastructure.cs`, modify `TransformTypeSymbol` to accept or propagate a `bool isRefLikeType` parameter. Actually, we can keep it simpler:

Add a new field to `TypeInfo`:

```csharp
internal readonly record struct TypeInfo(
    string FullyQualifiedName,
    string Namespace,
    string Name,
    ImmutableArray<PropertyInfo> Properties,
    ImmutableArray<CtorParamInfo> CtorParams = default,
    string? TypeTag = null,
    bool IsRefLikeType = false   // <-- ADD THIS
)
```

In `TransformTypeSymbol`, set `IsRefLikeType = namedType.IsRefLikeType`.

- [ ] **Step 4: Modify `GenerateTypeCode` — branch on `IsRefLikeType`**

In `GenerateTypeCode` (around line ~449), wrap the serializer/deserializer/registration emission with a branch:

```csharp
private static string GenerateTypeCode(TypeInfo type)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Buffers;");
    sb.AppendLine("using System.Text;");
    sb.AppendLine("using PicoSerDe.Core;");
    sb.AppendLine("using PicoJetson;");

    if (!string.IsNullOrEmpty(type.Namespace))
    {
        sb.AppendLine();
        sb.Append("namespace ");
        sb.Append(type.Namespace);
        sb.AppendLine(";");
    }

    sb.AppendLine();

    if (type.IsRefLikeType)
    {
        // Ref struct: static method serializer only, no deserializer.
        // EmitSerializer already outputs the static class shape for all types.
        EmitSerializer(sb, type);
        sb.AppendLine();
        EmitRefStructRegistration(sb, type);
    }
    else
    {
        // Regular type: static method serializer + interface deserializer
        EmitSerializer(sb, type);  // <-- will be changed to static method
        sb.AppendLine();
        EmitDeserializer(sb, type);
        sb.AppendLine();
        EmitRegistration(sb, type);
        sb.AppendLine();
        if (type.Properties.Length > 0)
            EmitStreamingDeserializer(sb, type);
    }

    return sb.ToString();
}
```

- [ ] **Step 5: Modify `EmitSerializer` — switch from `ISerializer<T>` to static method**

Locate `EmitSerializer` (around line ~467). Current shape:

```csharp
sb.Append("    file readonly struct ");
sb.Append(type.Name);
sb.Append("JsonSerializer : ISerializer<");
sb.Append(type.Name);
sb.AppendLine(">");
sb.AppendLine("    {");
sb.Append("        public void Serialize(IBufferWriter<byte> writer, ");
sb.Append(type.Name);
sb.AppendLine(" value)");
```

Change to (new shape for ALL types, not just ref structs):

```csharp
sb.Append("    file static class ");
sb.Append(type.Name);
sb.AppendLine("JsonSer");
sb.AppendLine("    {");
sb.Append("        public static void Serialize(IBufferWriter<byte> writer, ");
sb.Append(type.Name);
sb.AppendLine(" value)");
```

The method body (everything after `{` down to the closing `}`) stays identical. Only the enclosing type declaration changes.

Also update the closing:
```csharp
// Old:
// sb.AppendLine("    }");
// sb.AppendLine("}");

// New: closing the static class (only one level of braces)
sb.AppendLine("    }"); // end of Serialize method
sb.AppendLine("}");     // end of static class
```

Wait — currently there are TWO closing braces: one for the method, one for the struct. With a static class wrapping a static method, the structure is the same. Verify by re-reading the current `EmitSerializer`.

- [ ] **Step 6: Modify `EmitRegistration` — different calls for regular vs ref struct**

Current registration (around line ~2142):

```csharp
private static void EmitRegistration(StringBuilder sb, TypeInfo type)
{
    var typeRef = string.IsNullOrEmpty(type.Namespace)
        ? type.Name
        : $"global::{type.Namespace}.{type.Name}";
    var hasCtor = !type.CtorParams.IsDefaultOrEmpty && type.CtorParams.Length > 0;
    sb.Append("file static class ");
    sb.Append(type.Name);
    sb.AppendLine("SerDeRegistration");
    sb.AppendLine("{");
    sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
    sb.AppendLine("    internal static void Register()");
    sb.AppendLine("    {");
    sb.Append("        global::PicoJetson.JsonSerializer.Register<");
    sb.Append(typeRef);
    sb.AppendLine(">(");
    sb.Append("            new ");
    sb.Append(type.Name);
    sb.AppendLine("JsonSerializer(),");
    sb.Append("            new ");
    sb.Append(type.Name);
    sb.AppendLine("JsonDeserializer());");
    // ... streaming registration ...
    sb.AppendLine("    }");
    sb.AppendLine("}");
}
```

Change to new shape (for regular types):

```csharp
    sb.Append("file static class ");
    sb.Append(type.Name);
    sb.AppendLine("SerDeRegistration");
    sb.AppendLine("{");
    sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
    sb.AppendLine("    internal static void Register()");
    sb.AppendLine("    {");
    sb.Append("        global::PicoJetson.JsonSerializer.Register<");
    sb.Append(typeRef);
    sb.AppendLine(">(");
    sb.Append("            ");
    sb.Append(type.Name);
    sb.AppendLine("JsonSer.Serialize);");
    sb.AppendLine("        global::PicoJetson.JsonSerializer.RegisterDeserializer<");
    sb.Append(typeRef);
    sb.AppendLine(">(");
    sb.Append("            new ");
    sb.Append(type.Name);
    sb.AppendLine("JsonDeserializer());");
    // ... streaming registration unchanged ...
    sb.AppendLine("    }");
    sb.AppendLine("}");
```

- [ ] **Step 6b: Create `EmitRefStructRegistration` for ref struct types**

```csharp
private static void EmitRefStructRegistration(StringBuilder sb, TypeInfo type)
{
    var typeRef = string.IsNullOrEmpty(type.Namespace)
        ? type.Name
        : $"global::{type.Namespace}.{type.Name}";
    sb.Append("file static class ");
    sb.Append(type.Name);
    sb.AppendLine("SerDeRegistration");
    sb.AppendLine("{");
    sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
    sb.AppendLine("    internal static void Register()");
    sb.AppendLine("    {");
    sb.Append("        global::PicoJetson.JsonSerializer.Register<");
    sb.Append(typeRef);
    sb.AppendLine(">(");
    sb.Append("            ");
    sb.Append(type.Name);
    sb.AppendLine("JsonSer.Serialize);");
    sb.AppendLine("        // No deserializer — ref structs cannot be deserialized");
    sb.AppendLine("    }");
    sb.AppendLine("}");
}
```

- [ ] **Step 7: Update inner helpers generation**

In `EmitSerializeProperty` (around the "object" case), the inner helper call:

```csharp
var sn = PicoSerDe.Gen.GenInfrastructure.InnerClassName("JsonInner", prop.TypeFullName!);
```

For ref struct inner types, the inner helper name must match the new naming. Currently the inner helper is named `FooJsonInner`. With the new shape, serializers are `FooJsonSer`. Inner helpers should stay as `FooJsonInner` (they're separate). No change needed here — inner helpers are already static classes.

But we need to verify: `GenerateInnerHelper` generates `internal static class TJsonInner`. This is fine — it doesn't implement any interface.

- [ ] **Step 8: Run the SG test — should now pass**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "SourceGen_Generates_Serializer_For_RefStruct"
```

Expected: PASS — the SG generates a static serializer for `Vec2`, `ModuleInitializer` registers it, `JsonSerializer.Serialize(vec2)` produces correct JSON.

- [ ] **Step 9: Run full test suite**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj
```

Expected: ALL tests pass — existing regular-type tests still work (SG now generates static methods for them too, but the serialization behavior is identical).

- [ ] **Step 10: Build full solution**

```bash
dotnet build PicoSerDe.slnx
```

Expected: Build succeeds, no CS9244 errors, no type mismatch errors.

- [ ] **Step 11: Commit**

```bash
git add PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs shared/Gen/GenInfrastructure.cs PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs
git commit -m "feat: SG detects IsRefLikeType and generates ref struct serializers"
```

---

### Task 4: Edge case tests + nested ref struct

**Files:**
- Modify: `PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs` (add edge case tests)

**Interfaces:**
- Consumes: SG from Task 3, all runtime APIs from Task 2

- [ ] **Step 1: Write test — nested ref struct**

```csharp
public ref struct Inner3 { public float A; }
public ref struct Outer3 { public int Id; public Inner3 Inner; }

[Test]
public async Task Nested_RefStruct_Serializes_Correctly()
{
    var o = new Outer3 { Id = 42, Inner = new Inner3 { A = 3.14f } };
    var json = JsonSerializer.Serialize(o);
    await Assert.That(json).Contains("42");
    await Assert.That(json).Contains("3.14");
    await Assert.That(json).Contains("Inner");
}
```

- [ ] **Step 2: Write test — ref struct inside a generic method**

```csharp
[Test]
public async Task RefStruct_Serializes_From_GenericMethod()
{
    var v = new Vec2 { X = 5, Y = 10 };
    var json = SerializeAny(v);
    await Assert.That(json).Contains("5");
}

// Helper with allows ref struct constraint
private static string SerializeAny<T>(T value) where T : allows ref struct
    => JsonSerializer.Serialize(value);
```

- [ ] **Step 3: Write test — compat Register(ISerializer, IDeserializer) still works**

```csharp
public class CompatModel { public string Data { get; set; } = ""; }
public struct CompatSerializer : ISerializer<CompatModel>
{
    public void Serialize(IBufferWriter<byte> w, CompatModel v)
    {
        var j = $"{{\"Data\":\"{v.Data}\"}}";
        w.Write(Encoding.UTF8.GetBytes(j));
    }
}
public struct CompatDeserializer : IDeserializer<CompatModel>
{
    public CompatModel Deserialize(ReadOnlySpan<byte> d) => new();
}

[Test]
public async Task Compat_Register_ISerializer_IDeserializer_Still_Works()
{
    JsonSerializer.Register<CompatModel>(new CompatSerializer(), new CompatDeserializer());
    var m = new CompatModel { Data = "hello" };
    var json = JsonSerializer.Serialize(m);
    await Assert.That(json).Contains("hello");
}
```

- [ ] **Step 4: Run all ref struct tests**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "RefStruct"
```

Expected: ALL PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoJetson/tests/PicoJetson.Tests/RefStructSerializerTests.cs
git commit -m "test: add nested ref struct, generic method, and compat path tests"
```

---

### Task 5: Cleanup — verify inner helper naming, run full CI-like test

**Files:**
- (Verify only, no changes expected)

- [ ] **Step 1: Verify inner helper naming consistency**

In the generated code, inner helpers are named `TJsonInner`. Serializers are named `TJsonSer`. Confirm these don't collide by grepping the generated output:

```bash
dotnet build PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj
find PicoJetson/tests/PicoJetson.Tests/obj -name "*JsonSer*" -o -name "*JsonInner*" | head -20
```

Expected: Both `JsonSer` and `JsonInner` files exist without collision.

- [ ] **Step 2: Run full test suite across all projects**

```bash
dotnet test PicoSerDe.slnx
```

Expected: All tests pass across all 5 format projects.

- [ ] **Step 3: Verify `dotnet build` is clean**

```bash
dotnet build PicoSerDe.slnx -warnaserror
```

Expected: Zero warnings, zero errors.

- [ ] **Step 4: Commit**

```bash
git commit -m "chore: final verification — all tests pass, build clean"
```
