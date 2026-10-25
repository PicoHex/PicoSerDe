# Polymorphic Deserialization (Type Discriminator) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add polymorphic serialization/deserialization to all five PicoSerDe formats — base type deserializes to the correct derived type via a compile-time-closed discriminator switch.

**Architecture:** Two new attributes in PicoSerDe.Core (`PicoPolymorphicAttribute`, `PicoDerivedTypeAttribute`). Each SG gets a new Pipeline E that detects `[PicoDerivedType]` and emits a `{Type}JsonPoly` helper (SerializeProps + Deserialize). Base type serializer uses `switch(value)` type pattern for write dispatch; deserializer uses zero-alloc `if/else if` chain on discriminator value. No runtime API changes.

**Tech Stack:** C# 13, Roslyn 5.3 Incremental Source Generators, TUnit, .NET 10.0 NativeAOT

**TDD:** This plan follows strict TDD. Each format: write test → watch it fail → implement → watch it pass → refactor → next.

## Global Constraints

- Zero reflection; all code generation is compile-time source gen
- Zero allocation in the hot path (deserializer discriminator match)
- Follow existing SG patterns: Pipeline A/B/C/D merge structure, `file static class` helpers, `ModuleInitializer` registration
- Follow STJ behavior: discriminator must be first field, flat namespace, write only on base-type declaration
- All five formats (JSON/INI/MsgPack/TOML/YAML) must be covered
- `[JsonConstructor]` support on derived types
- TDD: no production code without a failing test first

---

### Task 1: Add PicoSerDe.Core attributes (prerequisite)

**Files:**
- Create: `PicoSerDe.Core/src/PicoPolymorphicAttribute.cs`
- Create: `PicoSerDe.Core/src/PicoDerivedTypeAttribute.cs`

**Note:** Attributes are declarations, not production logic. No TDD cycle required (TDD skill exception for declarative code).

- [ ] **Step 1: Write PicoPolymorphicAttribute**

```csharp
// PicoSerDe.Core/src/PicoPolymorphicAttribute.cs
namespace PicoSerDe.Core;

/// <summary>
/// Configures polymorphic type discrimination for a base type.
/// When present on a type that also has <see cref="PicoDerivedTypeAttribute"/>,
/// the SG generates discriminator write/read logic for all derived types.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class PicoPolymorphicAttribute : Attribute
{
    /// <summary>Discriminator property name. Default is <c>"$type"</c>.</summary>
    public string TypeDiscriminatorPropertyName { get; set; } = "$type";
}
```

- [ ] **Step 2: Write PicoDerivedTypeAttribute**

```csharp
// PicoSerDe.Core/src/PicoDerivedTypeAttribute.cs
namespace PicoSerDe.Core;

/// <summary>
/// Declares a derived type and its discriminator value for a base type.
/// Must be placed on the base type alongside <see cref="PicoSerializableAttribute"/>.
/// Multiple allowed — one per derived type.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoDerivedTypeAttribute : Attribute
{
    public Type DerivedType { get; }
    public string TypeDiscriminator { get; }

    public PicoDerivedTypeAttribute(Type derivedType, string typeDiscriminator)
    {
        DerivedType = derivedType;
        TypeDiscriminator = typeDiscriminator;
    }
}
```

- [ ] **Step 3: Build and commit**

```bash
dotnet build PicoSerDe.Core/src/PicoSerDe.Core.csproj
git add PicoSerDe.Core/src/PicoPolymorphicAttribute.cs PicoSerDe.Core/src/PicoDerivedTypeAttribute.cs
git commit -m "feat: add PicoPolymorphicAttribute and PicoDerivedTypeAttribute"
```

---

### Task 2: Extend TypeInfo in shared GenInfrastructure (prerequisite)

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs`

**Note:** Shared infrastructure change. No TDD cycle required.

- [ ] **Step 1: Add DerivedTypeInfo record**

Insert after the `TypeInfo` record definition:

```csharp
/// <summary>Describes one derived type in a polymorphic hierarchy.</summary>
internal readonly record struct DerivedTypeInfo(
    string FullyQualifiedName,
    string TypeDiscriminator
);
```

- [ ] **Step 2: Add DiscriminatorPropertyName and DerivedTypes to TypeInfo**

```csharp
internal readonly record struct TypeInfo(
    string FullyQualifiedName,
    string Namespace,
    string Name,
    ImmutableArray<PropertyInfo> Properties,
    ImmutableArray<CtorParamInfo> CtorParams = default,
    string? TypeTag = null,
    bool IsRefLikeType = false,
    string? DiscriminatorPropertyName = null,
    ImmutableArray<DerivedTypeInfo> DerivedTypes = default
)
```

- [ ] **Step 3: Update Equals and GetHashCode**

Add `DiscriminatorPropertyName` and `DerivedTypes` to both methods (same pattern as existing fields — see plan file for full code).

- [ ] **Step 4: Add ExpandPolymorphicTypes method**

```csharp
public static ImmutableArray<TypeInfo> ExpandPolymorphicTypes(
    GeneratorAttributeSyntaxContext ctx,
    FormatConfig config,
    AttributeHelpers attrs
)
{
    if (ctx.TargetSymbol is not INamedTypeSymbol baseType)
        return ImmutableArray<TypeInfo>.Empty;

    var builder = ImmutableArray.CreateBuilder<TypeInfo>();
    var derivedList = ImmutableArray.CreateBuilder<DerivedTypeInfo>();
    var derivedTypeSymbols = new List<INamedTypeSymbol>();
    var discriminatorPropertyName = "$type";

    foreach (var attr in baseType.GetAttributes())
    {
        if (attr.AttributeClass?.Name == "PicoPolymorphicAttribute"
            && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoSerDe.Core")
        {
            foreach (var na in attr.NamedArguments)
                if (na.Key == "TypeDiscriminatorPropertyName" && na.Value.Value is string dpn)
                    discriminatorPropertyName = dpn;
        }

        if (attr.AttributeClass?.Name == "PicoDerivedTypeAttribute"
            && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoSerDe.Core"
            && attr.ConstructorArguments.Length == 2
            && attr.ConstructorArguments[0].Value is INamedTypeSymbol derivedType
            && attr.ConstructorArguments[1].Value is string discriminator)
        {
            derivedList.Add(new DerivedTypeInfo(
                derivedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                discriminator));
            derivedTypeSymbols.Add(derivedType);
        }
    }

    if (derivedList.Count == 0) return ImmutableArray<TypeInfo>.Empty;

    var baseInfo = TransformTypeSymbol(baseType, config, attrs);
    if (baseInfo.HasValue)
    {
        builder.Add(baseInfo.Value with
        {
            DiscriminatorPropertyName = discriminatorPropertyName,
            DerivedTypes = derivedList.ToImmutable()
        });
    }

    foreach (var dt in derivedTypeSymbols)
    {
        var ti = TransformTypeSymbol(dt, config, attrs);
        if (!ti.HasValue) continue;
        var ctorParams = DetectConstructor(dt, config.FormatTag, "JsonConstructorAttribute");
        if (ctorParams.HasValue)
            ti = ti.Value with { CtorParams = ctorParams.Value };
        builder.Add(ti.Value);
    }

    return builder.ToImmutable();
}
```

- [ ] **Step 5: Build and commit**

```bash
dotnet build PicoSerDe.Core/src/PicoSerDe.Core.csproj
git add shared/Gen/GenInfrastructure.cs
git commit -m "feat: add DerivedTypeInfo, TypeInfo.DerivedTypes, ExpandPolymorphicTypes"
```

---

### Task 3: RED — Write JSON polymorphic tests (expect failure)

**Files:**
- Create: `PicoJetson/tests/PicoJetson.Tests/PolymorphicTests.cs`

**Test models:**

```csharp
namespace PicoJetson.Tests;

[PicoSerializable]
[PicoDerivedType(typeof(MessageEntry), "message")]
[PicoDerivedType(typeof(CompactionEntry), "compaction")]
public abstract class SessionEntry { }

public class MessageEntry : SessionEntry
{
    public string Content { get; set; } = "";
    public int Sequence { get; set; }
}

public class CompactionEntry : SessionEntry
{
    public int From { get; set; }
    public int To { get; set; }
}

[PicoSerializable]
[PicoPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[PicoDerivedType(typeof(EmailEvent), "email")]
[PicoDerivedType(typeof(SmsEvent), "sms")]
public abstract class AppEvent { }

public class EmailEvent : AppEvent
{
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
}

public class SmsEvent : AppEvent
{
    public string Phone { get; set; } = "";
    public string Text { get; set; } = "";
}

[PicoSerializable]
[PicoDerivedType(typeof(ImmutableMessage), "immutable_msg")]
public abstract class PolyEntry { }

public class ImmutableMessage : PolyEntry
{
    public string Content { get; }
    public int Id { get; }

    [JsonConstructor]
    public ImmutableMessage(string content, int id)
    {
        Content = content;
        Id = id;
    }
}

public class PolymorphicTests
{
    [Test]
    public async Task Serialize_Polymorphic_WritesDiscriminatorFirst()
    {
        SessionEntry entry = new MessageEntry { Content = "hello", Sequence = 1 };
        var json = JsonSerializer.Serialize(entry);
        await Assert.That(json).Contains("\"$type\"");
        await Assert.That(json).Contains("\"message\"");
        var typeIdx = json.IndexOf("$type", StringComparison.Ordinal);
        var contentIdx = json.IndexOf("Content", StringComparison.Ordinal);
        await Assert.That(typeIdx).IsLessThan(contentIdx);
    }

    [Test]
    public async Task Serialize_ConcreteType_DoesNotWriteDiscriminator()
    {
        var msg = new MessageEntry { Content = "hi", Sequence = 2 };
        var json = JsonSerializer.Serialize(msg);
        await Assert.That(json).DoesNotContain("$type");
    }

    [Test]
    public async Task Serialize_CustomDiscriminatorName_WritesCorrectly()
    {
        AppEvent ev = new EmailEvent { To = "user@test.com", Subject = "Hello" };
        var json = JsonSerializer.Serialize(ev);
        await Assert.That(json).Contains("\"kind\"");
        await Assert.That(json).Contains("\"email\"");
    }

    [Test]
    public async Task Deserialize_Polymorphic_ReturnsCorrectDerivedType()
    {
        var json = """{"$type":"message","Content":"world","Sequence":42}"""u8;
        var result = JsonSerializer.Deserialize<SessionEntry>(json);
        await Assert.That(result).IsTypeOf<MessageEntry>();
        var msg = (MessageEntry)result!;
        await Assert.That(msg.Content).IsEqualTo("world");
        await Assert.That(msg.Sequence).IsEqualTo(42);
    }

    [Test]
    public async Task Deserialize_Polymorphic_AlternateDerivedType()
    {
        var json = """{"$type":"compaction","From":10,"To":20}"""u8;
        var result = JsonSerializer.Deserialize<SessionEntry>(json);
        await Assert.That(result).IsTypeOf<CompactionEntry>();
        var ce = (CompactionEntry)result!;
        await Assert.That(ce.From).IsEqualTo(10);
        await Assert.That(ce.To).IsEqualTo(20);
    }

    [Test]
    public async Task Deserialize_CustomDiscriminatorName_Works()
    {
        var json = """{"kind":"sms","Phone":"555-1234","Text":"Hi"}"""u8;
        var result = JsonSerializer.Deserialize<AppEvent>(json);
        await Assert.That(result).IsTypeOf<SmsEvent>();
        var sms = (SmsEvent)result!;
        await Assert.That(sms.Phone).IsEqualTo("555-1234");
        await Assert.That(sms.Text).IsEqualTo("Hi");
    }

    [Test]
    public async Task Deserialize_UnknownDiscriminator_Throws()
    {
        var json = """{"$type":"unknown_type","x":1}"""u8;
        await Assert.That(() => JsonSerializer.Deserialize<SessionEntry>(json))
            .Throws<FormatException>();
    }

    [Test]
    public async Task Deserialize_DiscriminatorNotFirst_Throws()
    {
        var json = """{"Content":"hi","$type":"message"}"""u8;
        await Assert.That(() => JsonSerializer.Deserialize<SessionEntry>(json))
            .Throws<FormatException>();
    }

    [Test]
    public async Task Deserialize_PolyWithJsonConstructor_Works()
    {
        var json = """{"$type":"immutable_msg","content":"hello","id":99}"""u8;
        var result = JsonSerializer.Deserialize<PolyEntry>(json);
        await Assert.That(result).IsTypeOf<ImmutableMessage>();
        var im = (ImmutableMessage)result!;
        await Assert.That(im.Content).IsEqualTo("hello");
        await Assert.That(im.Id).IsEqualTo(99);
    }

    [Test]
    public async Task RoundTrip_Polymorphic_Identity()
    {
        SessionEntry original = new CompactionEntry { From = 5, To = 15 };
        var json = JsonSerializer.SerializeToUtf8Bytes(original);
        var result = JsonSerializer.Deserialize<SessionEntry>(json);
        await Assert.That(result).IsTypeOf<CompactionEntry>();
        var ce = (CompactionEntry)result!;
        await Assert.That(ce.From).IsEqualTo(5);
        await Assert.That(ce.To).IsEqualTo(15);
    }

    [Test]
    public async Task RoundTrip_EmptyStrings_Works()
    {
        var json = """{"$type":"message","Content":"","Sequence":0}"""u8;
        var result = JsonSerializer.Deserialize<SessionEntry>(json);
        var json2 = JsonSerializer.Serialize(result);
        await Assert.That(json2).Contains("\"$type\"");
        await Assert.That(json2).Contains("\"message\"");
    }
}
```

- [ ] **Step 1: Write the test file**

Create `PicoJetson/tests/PicoJetson.Tests/PolymorphicTests.cs` with the content above.

- [ ] **Step 2: Build and run tests — verify they FAIL**

```bash
dotnet build PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~PolymorphicTests"
```

Expected: Tests FAIL. The SG does not yet generate discriminator logic. `JsonSerializer.Serialize<SessionEntry>(...)` will call `ThrowNoSerializer` because `SessionEntry` has no registered handler, or the serializer won't write `$type`.

- [ ] **Step 3: Confirm failure reason is correct**

Verify failures are because SG is missing the poly generation logic, NOT because of typos or compile errors.

- [ ] **Step 4: Commit the failing tests**

```bash
git add PicoJetson/tests/PicoJetson.Tests/PolymorphicTests.cs
git commit -m "test: add JSON polymorphic tests (RED — SG not yet implemented)"
```

---

### Task 4: GREEN — Implement JSON SG to make tests pass

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`

**Changes:**
1. Add Pipeline E + merge
2. Add `GeneratePolyHelper` method
3. Modify `GenerateTypeCode` to branch on `!type.DerivedTypes.IsDefaultOrEmpty`
4. Add `EmitPolySerializer` (switch(value) pattern)
5. Add `EmitPolyDeserializer` (if/else if discriminator dispatch)
6. Modify `GenerateAll` to collect derived types and emit Poly helpers

- [ ] **Step 1: Add Pipeline E to Initialize**

After `shorthandAttr` pipeline definition:

```csharp
// Pipeline E: polymorphic — discover types via [PicoDerivedType]
var polyPipeline = context
    .SyntaxProvider.ForAttributeWithMetadataName(
        "PicoSerDe.Core.PicoDerivedTypeAttribute",
        predicate: static (node, _) => node is TypeDeclarationSyntax,
        transform: static (ctx, _) =>
            PicoSerDe.Gen.GenInfrastructure.ExpandPolymorphicTypes(ctx, Config, Attrs)
    )
    .SelectMany(static (types, _) => types);
```

- [ ] **Step 2: Merge Pipeline E into 'all'**

Add `.Combine(polyPipeline.Collect())` link at end of existing merge chain before `RegisterSourceOutput`.

- [ ] **Step 3: Modify GenerateAll — collect derived types and generate Poly helpers**

After `nestedDictTypes` collection and before inner helper generation:

```csharp
var derivedFqns = new HashSet<string>();
foreach (var type in types)
    foreach (var dt in type.DerivedTypes)
        if (!string.IsNullOrEmpty(dt.FullyQualifiedName))
            derivedFqns.Add(dt.FullyQualifiedName);

foreach (var fqn in derivedFqns)
{
    var derivedTypeInfo = types.FirstOrDefault(t => t.FullyQualifiedName == fqn);
    if (derivedTypeInfo.FullyQualifiedName == null) continue;
    var cleanName = fqn.Replace("global::", "");
    var safeName = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanName);
    var hintName = $"{safeName}_JsonPoly.g.cs";
    if (!hintNames.Add(hintName)) continue;
    spc.AddSource(hintName, SourceText.From(
        GeneratePolyHelper(derivedTypeInfo, safeName, cleanName), Encoding.UTF8));
}
```

- [ ] **Step 4: Add GeneratePolyHelper method**

Generates `{TypeName}JsonPoly` class with `SerializeProps(ref JsonWriter jw, T value)` and `Deserialize(ref JsonReader reader)`. Full implementation follows the pattern in Task 3 of the original plan — see `docs/superpowers/plans/2026-07-01-polymorphic-deserialization.md` for the complete method body. Key points:
- `SerializeProps` writes only property name/value pairs (no `WriteStartObject`/`WriteEndObject` — caller controls brackets)
- `Deserialize` uses `[JsonConstructor]` if `type.CtorParams` non-empty, otherwise `new T()` + property assignment
- Reader starts at first property name (past discriminator)

- [ ] **Step 5: Modify GenerateTypeCode**

Add `else if (!type.DerivedTypes.IsDefaultOrEmpty)` branch between `IsRefLikeType` and the normal `else`:

```csharp
else if (!type.DerivedTypes.IsDefaultOrEmpty)
{
    EmitPolySerializer(sb, type);
    sb.AppendLine();
    EmitPolyDeserializer(sb, type);
    sb.AppendLine();
    EmitRegistration(sb, type);
}
```

- [ ] **Step 6: Add EmitPolySerializer**

Generates `{TypeName}JsonSer` with `switch(value) { case DerivedType v: ... }`:

```csharp
private static void EmitPolySerializer(StringBuilder sb, TypeInfo type)
{
    var dpn = type.DiscriminatorPropertyName ?? "$type";
    sb.Append("    file static class ").Append(type.Name).AppendLine("JsonSer");
    sb.AppendLine("    {");
    sb.Append("        public static void Serialize(IBufferWriter<byte> writer, ")
      .Append(type.Name).AppendLine(" value)");
    sb.AppendLine("        {");
    sb.AppendLine("            var jw = new JsonWriter(writer, ...);");
    sb.AppendLine("            jw.WriteStartObject();");
    sb.Append("            jw.WritePropertyName(\"").Append(EscapeCSharpString(dpn)).AppendLine("\"u8);");
    sb.AppendLine("            switch (value)");
    sb.AppendLine("            {");
    foreach (var dt in type.DerivedTypes)
    {
        var dtShort = GenInfrastructure.ShortName(dt.FullyQualifiedName);
        var polyClass = $"{GenInfrastructure.SafeName(dt.FullyQualifiedName)}JsonPoly";
        sb.Append("                case ").Append(dtShort).AppendLine(" __v:");
        sb.Append("                    jw.WriteString(\"").Append(EscapeCSharpString(dt.TypeDiscriminator)).AppendLine("\"u8);");
        sb.Append("                    ").Append(polyClass).AppendLine(".SerializeProps(ref jw, __v);");
        sb.AppendLine("                    break;");
    }
    sb.AppendLine("            }");
    sb.AppendLine("            jw.WriteEndObject();");
    sb.AppendLine("        }");
    sb.AppendLine("    }");
}
```

- [ ] **Step 7: Add EmitPolyDeserializer**

Generates `{TypeName}JsonDeserializer : IDeserializer<T>` with discriminator dispatch:

```csharp
private static void EmitPolyDeserializer(StringBuilder sb, TypeInfo type)
{
    sb.Append("    file readonly struct ").Append(type.Name)
      .Append("JsonDeserializer : IDeserializer<").Append(type.Name).AppendLine(">");
    sb.AppendLine("    {");
    sb.Append("        public ").Append(type.Name).AppendLine(" Deserialize(ReadOnlySpan<byte> data)");
    sb.AppendLine("        {");
    sb.AppendLine("            var reader = new JsonReader(data, ...);");
    sb.AppendLine("            reader.Read(); // {");
    sb.AppendLine("            reader.Read(); // discriminator property name");
    sb.AppendLine("            reader.Read(); // discriminator value");
    sb.AppendLine("            var __disc = reader.GetStringRaw();");
    for (int i = 0; i < type.DerivedTypes.Length; i++)
    {
        var dt = type.DerivedTypes[i];
        var kw = i == 0 ? "if" : "else if";
        var polyClass = $"{GenInfrastructure.SafeName(dt.FullyQualifiedName)}JsonPoly";
        sb.Append("            ").Append(kw).Append(" (TextHelpers.Eq(__disc, \"")
          .Append(EscapeCSharpString(dt.TypeDiscriminator)).AppendLine("\"u8, false))");
        sb.Append("                return ").Append(polyClass).AppendLine(".Deserialize(ref reader);");
    }
    sb.AppendLine("            throw new FormatException($\"Unknown type discriminator: {Encoding.UTF8.GetString(__disc)}\");");
    sb.AppendLine("        }");
    sb.AppendLine("    }");
}
```

- [ ] **Step 8: Build and run tests — verify they PASS**

```bash
dotnet build PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj --filter "FullyQualifiedName~PolymorphicTests"
```

Expected: All 11 tests PASS.

- [ ] **Step 9: Run full test suite to verify no regressions**

```bash
dotnet test PicoJetson/tests/PicoJetson.Tests/PicoJetson.Tests.csproj
```

Expected: All existing tests still PASS.

- [ ] **Step 10: Commit**

```bash
git add PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs
git commit -m "feat: implement polymorphic JSON serialization/deserialization (GREEN)"
```

---

### Task 5: RED — Write INI polymorphic tests

**Files:**
- Create: `PicoIni/tests/PicoIni.Tests/PolymorphicTests.cs`

Same test structure as Task 3, but adapted for INI format. Uses `[PicoSerializable]` + `[PicoDerivedType]` on the same model types. INI test data uses INI format instead of JSON.

- [ ] **Step 1: Write INI test file** (see Task 3 for structure — replace `JsonSerializer` with `IniSerializer`, JSON strings with INI format)
- [ ] **Step 2: Build and run — verify FAIL**
- [ ] **Step 3: Commit failing tests**

---

### Task 6: GREEN — Implement INI SG

**Files:**
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`

Same changes as Task 4 but adapted for INI:
- Reader/writer: `IniReader` / `IniWriter`
- Namespace: `PicoIni`
- Options: `PicoIni.IniOptions.Current`
- Class suffix: `IniPoly`, hintName: `_IniPoly.g.cs`

- [ ] **Step 1-7: Apply same Pipeline E + GeneratePolyHelper + EmitPoly* + GenerateTypeCode + GenerateAll changes** (same pattern as Task 4, substituting INI APIs)
- [ ] **Step 8: Build and run INI tests — verify PASS**
- [ ] **Step 9: Run full INI test suite — verify no regressions**
- [ ] **Step 10: Commit**

---

### Task 7: RED — Write MsgPack polymorphic tests

**Files:**
- Create: `PicoMsgPack/tests/PicoMsgPack.Tests/PolymorphicTests.cs`

- [ ] **Step 1: Write MsgPack test file**
- [ ] **Step 2: Build and run — verify FAIL**
- [ ] **Step 3: Commit failing tests**

---

### Task 8: GREEN — Implement MsgPack SG

**Files:**
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`

MsgPack conventions: `MsgPackReader` / `MsgPackWriter`, namespace `PicoMsgPack`, options `PicoMsgPack.MsgPackOptions.Current`, suffix `MsgPackPoly`, hintName `_MsgPackPoly.g.cs`.

- [ ] **Step 1-7: Apply same pattern as Task 4/6**
- [ ] **Step 8: Build and run — verify PASS**
- [ ] **Step 9: Run full MsgPack test suite**
- [ ] **Step 10: Commit**

---

### Task 9: RED — Write TOML polymorphic tests

**Files:**
- Create: `PicoToml/tests/PicoToml.Tests/PolymorphicTests.cs`

- [ ] **Step 1: Write TOML test file**
- [ ] **Step 2: Build and run — verify FAIL**
- [ ] **Step 3: Commit failing tests**

---

### Task 10: GREEN — Implement TOML SG

**Files:**
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`

TOML conventions: `TomlReader` / `TomlWriter`, namespace `PicoToml`, options `PicoToml.TomlOptions.Current`, suffix `TomlPoly`, hintName `_TomlPoly.g.cs`.

- [ ] **Step 1-7: Apply same pattern**
- [ ] **Step 8: Build and run — verify PASS**
- [ ] **Step 9: Run full TOML test suite**
- [ ] **Step 10: Commit**

---

### Task 11: RED — Write YAML polymorphic tests

**Files:**
- Create: `PicoYaml/tests/PicoYaml.Tests/PolymorphicTests.cs`

- [ ] **Step 1: Write YAML test file**
- [ ] **Step 2: Build and run — verify FAIL**
- [ ] **Step 3: Commit failing tests**

---

### Task 12: GREEN — Implement YAML SG

**Files:**
- Modify: `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`

YAML conventions: `YamlReader` / `YamlWriter`, namespace `PicoYaml`, options `PicoYaml.YamlOptions.Current`, suffix `YamlPoly`, hintName `_YamlPoly.g.cs`.

- [ ] **Step 1-7: Apply same pattern**
- [ ] **Step 8: Build and run — verify PASS**
- [ ] **Step 9: Run full YAML test suite**
- [ ] **Step 10: Commit**

---

### Task 13: Integration — full solution build + all tests

- [ ] **Step 1: Full solution build**

```bash
dotnet build PicoSerDe.slnx
```

Expected: Build succeeded. 0 errors.

- [ ] **Step 2: Run all tests**

```bash
dotnet test PicoSerDe.slnx
```

Expected: All tests PASS across all 5 formats, including existing tests (no regressions).

- [ ] **Step 3: Commit**

```bash
git commit -m "test: verify full solution build and all tests pass with polymorphic support"
```
