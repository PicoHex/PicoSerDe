# v2 TDD Implementation Plan — PicoSerDe

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement 6 v2 architectural features using TDD: JSON `[JsonConstructor]`, TOML dotted keys, YAML block scalar (complete), YAML tag serialization, nested `List<List<T>>`, and MsgPack ext SG support.

**Architecture:** Each feature requires Reader/SG layer changes. The shared `ExtractNestedProperties` must go recursive. Token buffering needed for TOML dotted keys. Writer API extensions needed for YAML tag support.

**Tech Stack:** C# 13 / .NET 10, TUnit test framework, Microsoft Testing Platform, Incremental Source Generators

---

## Scope & Dependency Graph

```
Task 1 (JSON [JsonConstructor]) ── independent
Task 2 (TOML dotted keys)        ── independent
Task 3 (YAML block scalar full)  ── independent
Task 4 (YAML tag serialization)  ── independent
Task 5 (Nested List<List<T>>)    ── dependent on shared GenInfrastructure
Task 6 (MsgPack ext SG)          ── independent
```

---

### Task 1: JSON `[JsonConstructor]` — Parameterized Deserialization

**Files:**
- Create/Modify tests: `PicoJetson/tests/PicoJetson.Tests/JsonConstructorTests.cs`
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`

**Goal:** SG detects `[JsonConstructor]` on immutable types/records, emits temp variables for each constructor parameter, populates them from JSON properties, and calls the constructor.

**Design:**
1. In `TransformType`, detect `[JsonConstructor]` attribute on constructors
2. Map constructor parameter names to property names (case-insensitive)
3. In `EmitDeserializer`, generate:
   - Temp variable declarations for each constructor parameter
   - Property→temp assignment (same property matching logic)
   - `new T(p1, p2, ...)` after parsing all properties
   - `return` the constructed object

- [ ] **Step 1: Write failing tests for readonly record deserialization**

```csharp
// PicoJetson/tests/PicoJetson.Tests/JsonConstructorTests.cs
namespace PicoJetson.Tests;

public class JsonConstructorTests
{
    [Test]
    public async Task Deserialize_RecordWithConstructor_ReturnsObject()
    {
        var json = """{"name":"Alice","age":30}"""u8;
        var result = JsonSerializer.Deserialize<PersonRecord>(json);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Alice");
        await Assert.That(result!.Age).IsEqualTo(30);
    }

    [Test]
    public async Task Deserialize_RecordWithConstructor_DifferentOrder_ReturnsObject()
    {
        var json = """{"age":25,"name":"Bob"}"""u8;
        var result = JsonSerializer.Deserialize<PersonRecord>(json);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Bob");
        await Assert.That(result!.Age).IsEqualTo(25);
    }
}

public record PersonRecord([property: JsonConstructor] string Name, int Age);
```

- [ ] **Step 2: Verify tests fail with "No ISerializer/IJsonSerializer registered"**

Run: `dotnet test PicoJetson/tests/PicoJetson.Tests --filter "JsonConstructorTests"`

- [ ] **Step 3: Add constructor detection to TransformType**

In `JsonSerializerGenerator.cs`, add a helper:

```csharp
private static IMethodSymbol? GetJsonConstructor(INamedTypeSymbol type)
{
    foreach (var ctor in type.Constructors)
    {
        if (ctor.DeclaredAccessibility != Accessibility.Public) continue;
        foreach (var attr in ctor.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "JsonConstructorAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJetson")
                return ctor;
        }
    }
    return null;
}
```

Store constructor info in a new optional field on `TypeInfo`.

- [ ] **Step 4: Modify `EmitDeserializer` to emit constructor calling pattern**

When a `[JsonConstructor]` is detected, generate:

```
var reader = new JsonReader(data);
// temp vars for constructor params
string __p0 = default!; int __p1 = 0;
while (reader.Read() && reader.TokenType == TokenType.PropertyName) {
    var propNameSpan = reader.GetStringRaw();
    reader.Read();
    if (TextHelpers.Eq(propNameSpan, "name"u8))
        __p0 = Encoding.UTF8.GetString(reader.GetStringRaw());
    else if (TextHelpers.Eq(propNameSpan, "age"u8)) {
        reader.TryGetInt32(out var __v);
        __p1 = __v;
    }
    else reader.TrySkip();
}
return new PersonRecord(__p0, __p1);
```

- [ ] **Step 5: Run tests to verify they pass**

- [ ] **Step 6: Commit**

```bash
git add ... && git commit -m "feat(json): add [JsonConstructor] support for immutable types"
```

---

### Task 2: TOML Dotted Keys `a.b.c = value`

**Files:**
- Create/Modify tests: `PicoToml/tests/PicoToml.Tests/TomlDottedKeysTests.cs`
- Modify: `PicoToml/src/PicoToml/TomlReader.cs`
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs` (may need table header generation)

**Goal:** When Reader encounters `a.b.c = value`, it emits a sequence of tokens: `ObjectStart("a")` → `ObjectStart("b")` → `PropertyName("c")` + value. Requires an inline 4-slot token buffer.

**Design:**
1. Add `TokenBuffer4` inline struct with 4 slots
2. After reading a key and detecting `.` in it, split and buffer the multi-token sequence
3. `Read()` drains buffer before reading new data
4. Track depth appropriately

- [ ] **Step 1: Write failing test for dotted key deserialization**

```csharp
// PicoToml/tests/PicoToml.Tests/TomlDottedKeysTests.cs
namespace PicoToml.Tests;

using PicoToml;

public class TomlDottedKeysTests
{
    [Test]
    public async Task Deserialize_DottedKey_SingleLevel_NestedObject()
    {
        var toml = """
        server.host = "localhost"
        server.port = 8080
        """u8;
        var result = TomlSerializer.Deserialize<ServerConfig>(toml);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Server.Host).IsEqualTo("localhost");
        await Assert.That(result!.Server.Port).IsEqualTo(8080);
    }

    [Test]
    public async Task Serialize_DottedKey_Roundtrips()
    {
        var config = new ServerConfig { Server = new ServerInfo { Host = "localhost", Port = 8080 } };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(config);
        var result = TomlSerializer.Deserialize<ServerConfig>(bytes);
        await Assert.That(result!.Server.Host).IsEqualTo("localhost");
        await Assert.That(result!.Server.Port).IsEqualTo(8080);
    }
}

public class ServerConfig
{
    public ServerInfo Server { get; set; } = new();
}

public class ServerInfo
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}
```

- [ ] **Step 2: Verify tests fail**

Run: `dotnet test PicoToml/tests/PicoToml.Tests --filter "TomlDottedKeysTests"`

- [ ] **Step 3: Add `TokenBuffer4` struct and split logic to `TomlReader`**

```csharp
// In TomlReader.cs, add:
private struct TokenBuffer4
{
    internal TokenType _t0, _t1, _t2, _t3;
    internal ReadOnlySpan<byte> _v0, _v1, _v2, _v3;
    internal int _count;
    internal int _index;
}

private TokenBuffer4 _tokenBuf;
```

After reading `keySpan` and detecting `.`, call `SplitDottedKey()` which populates `_tokenBuf`. Then modify `Read()` to drain buffer first:

```csharp
public bool Read() {
    if (_tokenBuf._index < _tokenBuf._count) {
        // emit next buffered token
        _tokenBuf._index++;
        return true;
    }
    // normal read logic...
}
```

- [ ] **Step 4: Implement `TrySplitDottedKey` method**

Scan `_keySpan` for `.` bytes (outside of quoted sections). Split into segments. For `a.b.c`:
- Emit `ObjectStart` with key "a"
- Emit `ObjectStart` with key "b"  
- Emit `PropertyName` with key "c" (carry value normally)

- [ ] **Step 5: Run tests to verify they pass**

- [ ] **Step 6: Commit**

---

### Task 3: YAML Block Scalar Complete

**Files:**
- Create/Modify tests: `PicoYaml/tests/PicoYaml.Tests/YamlBlockScalarTests.cs`
- Modify: `PicoYaml/src/PicoYaml/YamlReader.cs`

**Goal:** Extend the minimal `|`/`>` implementation with: indent stripping (leading spaces relative to base), `|+`/`|-` chomping (keep/drop trailing newlines), `>` folded mode (replace single newlines with spaces, double newlines with single newline).

**Design:**
1. Parse chomping indicator: `|+`, `|-`, `>+`, `>-`
2. Track `_baseIndent` for the block
3. Strip `_baseIndent` spaces from each line start
4. For folded `>`, accumulate lines with space replacement logic

- [ ] **Step 1: Write failing tests**

```csharp
// PicoYaml/tests/PicoYaml.Tests/YamlBlockScalarTests.cs
namespace PicoYaml.Tests;

public class YamlBlockScalarTests
{
    [Test]
    public async Task Deserialize_LiteralBlockScalar_StripIndent_ReturnsTrimmedLines()
    {
        var yaml = """
        key: |
          line one
          line two
        next: value
        """u8;
        var reader = new YamlReader(yaml);
        reader.Read(); // ObjectStart
        reader.Read(); // PropertyName "key"
        reader.ReadValue(); // Read the value
        var val = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(val).IsEqualTo("line one\nline two\n");
    }

    [Test]
    public async Task Deserialize_LiteralBlockScalar_KeepChomping_PreservesTrailingNewlines()
    {
        var yaml = """
        key: |+
          line one
          line two
        next: value
        """u8;
        var reader = new YamlReader(yaml);
        reader.Read(); // ObjectStart
        reader.Read(); // PropertyName "key"
        reader.ReadValue();
        var val = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(val).EndsWith("\n");
    }

    [Test]
    public async Task Deserialize_LiteralBlockScalar_StripChomping_StripsTrailingNewlines()
    {
        var yaml = """
        key: |-
          line one
          line two
        next: value
        """u8;
        var reader = new YamlReader(yaml);
        reader.Read(); // ObjectStart
        reader.Read(); // PropertyName "key"
        reader.ReadValue();
        var val = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(val).IsEqualTo("line one\nline two");
    }

    [Test]
    public async Task Deserialize_FoldedBlockScalar_ConvertsNewlinesToSpaces()
    {
        var yaml = """
        key: >
          line one
          line two
        next: value
        """u8;
        var reader = new YamlReader(yaml);
        reader.Read(); // ObjectStart
        reader.Read(); // PropertyName "key"
        reader.ReadValue();
        var val = Encoding.UTF8.GetString(reader.GetStringRaw());
        await Assert.That(val).IsEqualTo("line one line two\n");
    }
}
```

- [ ] **Step 2: Verify tests fail** (current minimal impl doesn't handle chomping/folded/strip)

- [ ] **Step 3: Refactor `ReadBlockScalarSpan` to handle chomping and folded mode**

```csharp
private bool ReadBlockScalarSpan(/* existing params */)
{
    // Parse chomping: + | - | (none = clip)
    // Parse indicator: | (literal) or > (folded)
    // Track baseIndent
    // Read lines, stripping baseIndent
    // Apply chomping rules
    // Apply folding if needed
}
```

- [ ] **Step 4: Run tests, iterate until all pass**

- [ ] **Step 5: Commit**

---

### Task 4: YAML Tag Serialization `!type`

**Files:**
- Create tests: `PicoYaml/tests/PicoYaml.Tests/YamlTagTests.cs`
- Create attribute: `PicoYaml/src/PicoYaml/YamlTagAttribute.cs`
- Modify: `PicoYaml/src/PicoYaml/YamlWriter.cs`
- Modify: `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`

**Goal:** Writer can emit `!type` tags. SG generates tag emission for types with `[YamlTag]` attribute.

**Design:**
1. Add `YamlTagAttribute` (target: Class, Struct)
2. Add `WriteTag(string tag)` to `YamlWriter`
3. Add `[YamlTag("!mytag")]` to attribute helpers in SG
4. In serialize path, emit `!type` before property value or mapping start

- [ ] **Step 1: Write failing tests**

```csharp
// PicoYaml/tests/PicoYaml.Tests/YamlTagTests.cs
namespace PicoYaml.Tests;

using PicoYaml;

[YamlTag("!person")]
public class TaggedPerson
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class YamlTagTests
{
    [Test]
    public async Task Serialize_TaggedType_EmitsTag()
    {
        var person = new TaggedPerson { Name = "Alice", Age = 30 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(person);
        var str = Encoding.UTF8.GetString(bytes);
        await Assert.That(str).Contains("!person");
        await Assert.That(str).Contains("Name: Alice");
    }

    [Test]
    public async Task Serialize_TaggedType_Roundtrips()
    {
        var person = new TaggedPerson { Name = "Bob", Age = 25 };
        var bytes = YamlSerializer.SerializeToUtf8Bytes(person);
        var result = YamlSerializer.Deserialize<TaggedPerson>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Bob");
        await Assert.That(result!.Age).IsEqualTo(25);
    }
}
```

- [ ] **Step 2: Verify tests fail**

- [ ] **Step 3: Create `YamlTagAttribute`**

```csharp
// PicoYaml/src/PicoYaml/YamlTagAttribute.cs
namespace PicoYaml;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class YamlTagAttribute : Attribute
{
    public string Tag { get; }
    public YamlTagAttribute(string tag) => Tag = tag;
}
```

- [ ] **Step 4: Add `WriteTag` to `YamlWriter`**

```csharp
public void WriteTag(string tag)
{
    WriteRaw(Encoding.UTF8.GetBytes(tag));
    WriteByte((byte)' ');
}
```

- [ ] **Step 5: Add tag detection to SG attribute helpers**

In `YamlSerializerGenerator.cs`, add `GetYamlTag(ITypeSymbol)` that reads `[YamlTag]` attribute.

- [ ] **Step 6: In serializer emit path, output tag before property writes**

Add tag emission to the serializer generated code when a type-level tag is found.

- [ ] **Step 7: Run tests, iterate**

- [ ] **Step 8: Commit**

---

### Task 5: Nested Collections `List<List<T>>`

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs` (ExtractNestedProperties recursive)
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`
- Modify: `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`
- Create tests: `PicoJetson/tests/PicoJetson.Tests/NestedListTests.cs`

**Goal:** When a type has `List<List<int>>` or `List<Dictionary<string, int>>`, the SG generates correct nested loops for both serialize and deserialize.

**Design:**
1. Make `ExtractNestedProperties` recursive: when element type kind is `"list"` or `"dict"`, recurse and store nested structure
2. Add `NestedListKind`/`NestedDictKind` to `PropertyInfo` (or encode as recursive `ElementTypeKind` + `NestedProperties`)
3. In each SG's serialization emit: nested foreach loops for nested collections
4. In each SG's deserialization emit: nested while loops for nested collections

- [ ] **Step 1: Write failing test for JSON `List<List<int>>`**

```csharp
// PicoJetson/tests/PicoJetson.Tests/NestedListTests.cs
namespace PicoJetson.Tests;

public class NestedListTests
{
    [Test]
    public async Task SerializeDeserialize_NestedListOfInt_Roundtrips()
    {
        var model = new MatrixModel
        {
            Rows = new List<List<int>>
            {
                new() { 1, 2, 3 },
                new() { 4, 5, 6 },
            }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<MatrixModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Rows).HasCount().EqualTo(2);
        await Assert.That(result.Rows[0]).IsEqualTo(new List<int> { 1, 2, 3 });
        await Assert.That(result.Rows[1]).IsEqualTo(new List<int> { 4, 5, 6 });
    }
}

public class MatrixModel
{
    public List<List<int>> Rows { get; set; } = new();
}
```

- [ ] **Step 2: Verify test fails** (current code doesn't handle nested lists in SG)

- [ ] **Step 3: Make `ExtractNestedProperties` recursive**

In `GenInfrastructure.cs`, when processing element type of a list/dict property and its kind is `"list"` or `"dict"`, store the nested structure via a new `NestedElementTypeKind` field on `PropertyInfo`.

- [ ] **Step 4: Update JSON SG `EmitSerializeProperty` for nested collections**

For `list` with nested element types, emit inner `foreach` loop with inner `WriteStartArray`/`WriteEndArray`.

- [ ] **Step 5: Update JSON SG `EmitDeserializeValue` for nested collections**

For nested lists, emit inner while loop with inner array start/end detection.

- [ ] **Step 6: Run JSON tests, iterate until pass**

- [ ] **Step 7: Apply same pattern to TOML, YAML, INI SGs**

- [ ] **Step 8: Run all tests, verify nothing broke**

- [ ] **Step 9: Commit**

---

### Task 6: MsgPack Extension Type SG

**Files:**
- Create tests: `PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackExtensionTests.cs`
- Modify: `shared/Gen/TypeKindResolver.cs`
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`

**Goal:** When a type has `byte[]` annotated with extension semantics (or a dedicated attribute), the SG generates `WriteExtension`/`ReadExtension` calls.

**Design:**
1. Add `"extension"` type kind to `TypeKindResolver` (or reuse `"bytes"` with a format-specific path)
2. MsgPack Writer already has `WriteExtension(byte tag, ReadOnlySpan<byte> data)` at line 208
3. MsgPack Reader already parses Extension tokens (`TokenType.Extension`, reads `_tag` and `_valueSpan`)
4. In SG: add case for `"extension"` type kind → emit `mw.WriteExtension(tag, value)` / `reader.GetExtension()`

- [ ] **Step 1: Write failing test for ext type roundtrip**

```csharp
// PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackExtensionTests.cs
namespace PicoMsgPack.Tests;

using PicoMsgPack;

public class MsgPackExtensionTests
{
    [Test]
    public async Task SerializeDeserialize_ExtensionType_Roundtrips()
    {
        var model = new ExtModel { Data = new byte[] { 1, 2, 3, 4 }, Tag = 42 };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var result = MsgPackSerializer.Deserialize<ExtModel>(bytes);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Data).IsEqualTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(result.Tag).IsEqualTo(42);
    }
}

public class ExtModel
{
    [MsgPackKey(0)] public byte[] Data { get; set; } = Array.Empty<byte>();
    [MsgPackExtTag(42)]
    [MsgPackKey(1)] public byte Tag { get; set; }
}
```

Wait — the current SG maps `byte[]` → `"bytes"` type kind in MsgPack. We need to decide: should we add a `[MsgPackExtTag]` attribute or just treat `byte[]` properties in MsgPack format as extension?

The simplest approach: if a property in MsgPack format has `[MsgPackExtensionTag(byte)]`, emit extension read/write. Otherwise `byte[]` maps to regular bytes format.

- [ ] **Step 2: Verify tests fail**

- [ ] **Step 3: Add `"extension"` kind to `TypeKindResolver` when format is `"msgpack"` and a special attribute is present**

Simpler: add `OverrideKindWithExtension` flag to `AttributeHelpers` that converts `"bytes"` → `"extension"` for MsgPack format.

- [ ] **Step 4: Add extension emit to MsgPack SG**

In `WriteSer`, add `case "extension"`:
```csharp
s.Append("mw.WriteExtension(");
s.Append(p.IntKey ?? 0);
s.Append(", ");
s.Append(a);
s.AppendLine(");");
```

In `WriteDeser`, add `case "extension"`:
```csharp
s.Append("var __raw = reader.GetStringRaw(); ");
s.Append("reader._tag"); // need to expose tag
```

- [ ] **Step 5: Run tests, iterate**

- [ ] **Step 6: Commit**

---

## Cross-Task Ordering

Execute tasks in order: 1 → 2 → 3 → 4 → 5 → 6

Task 5 (nested collections) is the riskiest — touches all 5 SGs. Do it after the format-specific tasks are stable.
