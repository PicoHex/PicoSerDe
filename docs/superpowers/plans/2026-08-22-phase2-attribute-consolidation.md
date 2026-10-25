# Phase 2 — Attribute Family Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One shape per attribute concept across the 5 formats, shared implementations in PicoSerDe.Core where semantics are identical, no wire-behavior changes.

**Architecture:** Core gains small base attribute classes (`PicoCamelCaseAttribute`, `PicoConstructorAttribute`, `PicoDateTimeFormatAttribute`, `PicoConverterAttribute`; `PicoIgnoreAttribute` and `PicoSerializableAttribute` already exist — the former gets unsealed). The 25 per-format attributes with identical semantics become sealed subclasses (public API names unchanged, `AttributeUsage` inherited). Key attributes normalize to a `Key` property (`IniKeyAttribute.Name` → obsolete alias). Format-specific attributes (IniComment, IniSection, MsgPackExtensionTag, YamlTag, JsonPropertyName) stay standalone. Source generators detect attributes by name — no generator changes.

**Tech Stack:** .NET 10, C# 14, TUnit.

**Spec:** `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` (§5, §8, §9)

## Global Constraints

- Public API names unchanged: `[JsonIgnore]`, `[IniKey]`, ... remain valid, source-compatible (spec §3).
- No semantic changes: wire output identical before/after (spec §5). The 1110-test suite is the equivalence gate.
- `[Obsolete]` aliases with messages; no silent removal (spec §8).
- SGs detect attributes by class name — verify no generator changes needed (they are not: `HasJsonIgnore` checks `JsonIgnoreAttribute`, subclasses report their own name).

---

### Task 1: Core attribute bases

**Files:**
- Modify: `PicoSerDe.Core/src/PicoIgnoreAttribute.cs` — remove `sealed`
- Create: `PicoSerDe.Core/src/PicoCamelCaseAttribute.cs`, `PicoSerDe.Core/src/PicoConstructorAttribute.cs`, `PicoSerDe.Core/src/PicoDateTimeFormatAttribute.cs`, `PicoSerDe.Core/src/PicoConverterAttribute.cs`
- Test: create `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SharedAttributeTests.cs`

**Interfaces:**
- Produces:
  - `public class PicoCamelCaseAttribute : Attribute` — `[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]`
  - `public class PicoConstructorAttribute : Attribute` — `[AttributeUsage(AttributeTargets.Constructor)]`
  - `public class PicoDateTimeFormatAttribute : Attribute` — `[AttributeUsage(AttributeTargets.Property)]`, `public string Format { get; }`, ctor `(string format)`
  - `public class PicoConverterAttribute : Attribute` — `[AttributeUsage(AttributeTargets.Property)]`, `public Type ConverterType { get; }`, ctor `(Type converterType)`

- [ ] **Step 1: Write failing tests**

```csharp
[Test]
public async Task PicoDateTimeFormat_ExposesFormat()
{
    var a = new PicoDateTimeFormatAttribute("yyyy-MM-dd");
    await Assert.That(a.Format).IsEqualTo("yyyy-MM-dd");
}

[Test]
public async Task PicoConverter_ExposesConverterType()
{
    var a = new PicoConverterAttribute(typeof(string));
    await Assert.That(a.ConverterType).IsEqualTo(typeof(string));
}

[Test]
public async Task PicoIgnore_IsSubclassable()
{
    var a = new TestIgnoreAttribute { Condition = PicoIgnoreCondition.WhenWritingNull };
    await Assert.That(a.Condition).IsEqualTo(PicoIgnoreCondition.WhenWritingNull);
}

public sealed class TestIgnoreAttribute : PicoIgnoreAttribute { }
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — types undefined / `PicoIgnoreAttribute` sealed.

- [ ] **Step 3: Implement**

Create the 4 base classes per Interfaces (copy the shape of `PicoDateTimeFormatAttribute`/`PicoConverterAttribute` from the existing per-format classes — see `IniDateTimeFormatAttribute.cs` / `IniConverterAttribute.cs` for the established shape). Unseal `PicoIgnoreAttribute`.

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests/PicoSerDe.Core.Tests.csproj -c Release`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoSerDe.Core/src PicoSerDe.Core/tests/PicoSerDe.Core.Tests
git commit -m "feat(core): add shared attribute bases (camelcase/ctor/datetime/converter); unseal PicoIgnore"
```

---

### Task 2: Ignore family → PicoIgnoreAttribute subclasses

**Files:**
- Modify (rewrite body to a 1-line subclass): `PicoIni/src/PicoIni/IniIgnoreAttribute.cs`, `PicoJetson/src/PicoJetson/JsonIgnoreAttribute.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackIgnoreAttribute.cs`, `PicoToml/src/PicoToml/TomlIgnoreAttribute.cs`, `PicoYaml/src/PicoYaml/YamlIgnoreAttribute.cs`
- Test: create `PicoJetson/tests/PicoJetson.Unit.Tests/IgnoreAttributeCompatibilityTests.cs` (pattern applies to all 5 — one representative test file per module is enough for 4 of them, full coverage in JSON)

**Interfaces:**
- Consumes: `PicoIgnoreAttribute` (unsealed, Task 1).
- Produces: e.g. `public sealed class JsonIgnoreAttribute : PicoIgnoreAttribute { }` (same for all 5).

- [ ] **Step 1: Write failing/verification tests**

```csharp
[Test]
public async Task JsonIgnore_StillUnconditionallyIgnores()
{
    var json = JsonSerializer.Serialize(new DocWithIgnored { Kept = 1, Dropped = 2 });
    await Assert.That(json).DoesNotContain("Dropped");
    await Assert.That(json).Contains("Kept");
}

[Test]
public async Task PicoIgnoreCondition_And_JsonIgnore_Coexist()
{
    var json = JsonSerializer.Serialize(new MixedDoc { A = null, B = 1 });
    // A: [PicoIgnore(Condition = WhenWritingNull)] omitted when null
    // B: [JsonIgnore] always omitted
    await Assert.That(json).DoesNotContain("B");
}
```
(`DocWithIgnored`/`MixedDoc` POCOs defined in the test file with the attributes applied.)

- [ ] **Step 2: Run tests to verify they pass already (equivalence baseline)**

Expected: PASS — this task must not change behavior; the tests above guard the equivalence.

- [ ] **Step 3: Implement**

Replace each of the 5 files' body with:
```csharp
namespace PicoIni;   // (module namespace)

/// <summary>Excludes a property from INI serialization and deserialization.</summary>
public sealed class IniIgnoreAttribute : PicoIgnoreAttribute { }
```
(Keep each file's existing doc comment and namespace.)

- [ ] **Step 4: Run all 5 module suites + the new tests**

Run: `dotnet test PicoSerDe.slnx -c Release`
Expected: PASS (1110+ suite green — this is the wire-equivalence gate).

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/IniIgnoreAttribute.cs PicoJetson/src/PicoJetson/JsonIgnoreAttribute.cs PicoMsgPack/src/PicoMsgPack/MsgPackIgnoreAttribute.cs PicoToml/src/PicoToml/TomlIgnoreAttribute.cs PicoYaml/src/PicoYaml/YamlIgnoreAttribute.cs PicoJetson/tests/PicoJetson.Unit.Tests
git commit -m "refactor(attrs): ignore attributes inherit shared PicoIgnoreAttribute"
```

---

### Task 3: Serializable family → PicoSerializableAttribute subclasses

**Files:**
- Modify: `PicoIni/src/PicoIni/PicoIniSerializableAttribute.cs`, `PicoJetson/src/PicoJetson/PicoJsonSerializableAttribute.cs`, `PicoMsgPack/src/PicoMsgPack/PicoMsgPackSerializableAttribute.cs`, `PicoToml/src/PicoToml/PicoTomlSerializableAttribute.cs`, `PicoYaml/src/PicoYaml/PicoYamlSerializableAttribute.cs`
- Test: add one e2e test to `PicoJetson/tests/PicoJetson.Unit.Tests/` (create `SerializableAttributeCompatibilityTests.cs`)

**Interfaces:**
- Consumes: `PicoSerializableAttribute` (already unsealed, has `()` and `(Type)` ctors + `IncludeFields`).
- Produces: `public sealed class PicoIniSerializableAttribute : PicoSerializableAttribute { public PicoIniSerializableAttribute() { } public PicoIniSerializableAttribute(Type type) : base(type) { } }` (×5; constructors are not inherited — must re-declare both).

- [ ] **Step 1: Write verification test**

```csharp
[Test]
public async Task PicoJsonSerializable_StillGeneratesSerializer()
{
    var json = JsonSerializer.Serialize(new AttributedDoc { X = 1 });
    await Assert.That(json).Contains("X");
}
[Test]
public async Task PicoJsonSerializable_TypeArg_StillGenerates()
{
    var json = JsonSerializer.Serialize(new ExternalDoc { Y = 2 });
    await Assert.That(json).Contains("Y");
}
```
(`AttributedDoc` is `[PicoJsonSerializable]`; `ExternalDoc` is referenced via `[PicoJsonSerializable(typeof(ExternalDoc))]` on a holder class in the test file.)

- [ ] **Step 2: Run to verify baseline passes**

Expected: PASS.

- [ ] **Step 3: Implement**

Rewrite the 5 files as subclasses with both ctors forwarded (see Interfaces).

- [ ] **Step 4: Run full suite**

Run: `dotnet test PicoSerDe.slnx -c Release`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/PicoIniSerializableAttribute.cs PicoJetson/src/PicoJetson/PicoJsonSerializableAttribute.cs PicoMsgPack/src/PicoMsgPack/PicoMsgPackSerializableAttribute.cs PicoToml/src/PicoToml/PicoTomlSerializableAttribute.cs PicoYaml/src/PicoYaml/PicoYamlSerializableAttribute.cs PicoJetson/tests/PicoJetson.Unit.Tests
git commit -m "refactor(attrs): serializable attributes inherit shared PicoSerializableAttribute"
```

---

### Task 4: CamelCase family → PicoCamelCaseAttribute subclasses

**Files:**
- Modify: `PicoIni/src/PicoIni/IniCamelCaseAttribute.cs`, `PicoJetson/src/PicoJetson/JsonCamelCaseAttribute.cs`, `PicoToml/src/PicoToml/TomlCamelCaseAttribute.cs`, `PicoYaml/src/PicoYaml/YamlCamelCaseAttribute.cs` (MsgPack has none — verified)
- Test: `PicoJetson/tests/PicoJetson.Unit.Tests/` — create `CamelCaseAttributeCompatibilityTests.cs`

**Interfaces:**
- Produces: `public sealed class IniCamelCaseAttribute : PicoCamelCaseAttribute { }` (×4)

- [ ] **Step 1: Write verification test**

```csharp
[Test]
public async Task JsonCamelCase_StillApplies()
{
    var json = JsonSerializer.Serialize(new CamelDoc { FullName = "x" });
    await Assert.That(json).Contains("fullName");
}
```

- [ ] **Step 2: Run to verify baseline passes**

Expected: PASS.

- [ ] **Step 3: Implement**

Rewrite the 4 files as subclasses. Verify the existing `AttributeUsage` on each matches the base's (`Class | Struct`) — if a format's current usage is narrower, keep that format's usage by re-declaring `[AttributeUsage(...)]` on the subclass.

- [ ] **Step 4: Run full suite**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/IniCamelCaseAttribute.cs PicoJetson/src/PicoJetson/JsonCamelCaseAttribute.cs PicoToml/src/PicoToml/TomlCamelCaseAttribute.cs PicoYaml/src/PicoYaml/YamlCamelCaseAttribute.cs PicoJetson/tests/PicoJetson.Unit.Tests
git commit -m "refactor(attrs): camelcase attributes inherit shared PicoCamelCaseAttribute"
```

---

### Task 5: Constructor + DateTimeFormat families → shared bases

**Files:**
- Modify (Constructor ×5): `PicoIni/src/PicoIni/IniConstructorAttribute.cs`, `PicoJetson/src/PicoJetson/JsonConstructorAttribute.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackConstructorAttribute.cs`, `PicoToml/src/PicoToml/TomlConstructorAttribute.cs`, `PicoYaml/src/PicoYaml/YamlConstructorAttribute.cs`
- Modify (DateTimeFormat ×4): `PicoIni/src/PicoIni/IniDateTimeFormatAttribute.cs`, `PicoJetson/src/PicoJetson/DateTimeFormatAttribute.cs` (note: no Json prefix), `PicoToml/src/PicoToml/TomlDateTimeFormatAttribute.cs`, `PicoYaml/src/PicoYaml/YamlDateTimeFormatAttribute.cs`
- Test: `PicoJetson/tests/PicoJetson.Unit.Tests/` — create `ConstructorAndDateFormatAttributeCompatibilityTests.cs`

**Interfaces:**
- Produces: `public sealed class JsonConstructorAttribute : PicoConstructorAttribute { }` (×5); `public sealed class DateTimeFormatAttribute : PicoDateTimeFormatAttribute { public DateTimeFormatAttribute(string format) : base(format) { } }` (×4)

- [ ] **Step 1: Write verification tests**

```csharp
[Test]
public async Task JsonConstructor_StillUsed()
{
    var doc = JsonSerializer.Deserialize<CtorDoc>("{ \"a\": 1, \"b\": 2 }"u8);
    await Assert.That(doc!.Sum).IsEqualTo(3);   // Sum set by the [JsonConstructor] ctor
}

[Test]
public async Task DateTimeFormat_StillApplied()
{
    var json = JsonSerializer.Serialize(new DateDoc { When = new DateTime(2026, 1, 2, 3, 4, 5) });
    await Assert.That(json).Contains("2026-01-02");
}
```
(`CtorDoc` has a `[JsonConstructor]` ctor computing `Sum`; `DateDoc` uses `[DateTimeFormat("yyyy-MM-dd")]`.)

- [ ] **Step 2: Run to verify baseline passes**

Expected: PASS.

- [ ] **Step 3: Implement**

Constructor family: verify each per-format class is a plain marker (no members) before rewriting — check `JsonConstructorAttribute.cs` first; if any has members, keep those members on the subclass. Rewrite as subclasses of `PicoConstructorAttribute`.

DateTimeFormat family: rewrite as subclasses with ctor forwarding.

- [ ] **Step 4: Run full suite**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/IniConstructorAttribute.cs PicoIni/src/PicoIni/IniDateTimeFormatAttribute.cs PicoJetson/src/PicoJetson/JsonConstructorAttribute.cs PicoJetson/src/PicoJetson/DateTimeFormatAttribute.cs PicoMsgPack/src/PicoMsgPack/MsgPackConstructorAttribute.cs PicoToml/src/PicoToml/TomlConstructorAttribute.cs PicoToml/src/PicoToml/TomlDateTimeFormatAttribute.cs PicoYaml/src/PicoYaml/YamlConstructorAttribute.cs PicoYaml/src/PicoYaml/YamlDateTimeFormatAttribute.cs PicoJetson/tests/PicoJetson.Unit.Tests
git commit -m "refactor(attrs): constructor and datetime-format attributes inherit shared bases"
```

---

### Task 6: Converter family → PicoConverterAttribute subclasses

**Files:**
- Modify: `PicoIni/src/PicoIni/IniConverterAttribute.cs`, `PicoJetson/src/PicoJetson/JsonConverterAttribute.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackConverterAttribute.cs`, `PicoToml/src/PicoToml/TomlConverterAttribute.cs`, `PicoYaml/src/PicoYaml/YamlConverterAttribute.cs`
- Test: `PicoJetson/tests/PicoJetson.Unit.Tests/` — create `ConverterAttributeCompatibilityTests.cs`

**Interfaces:**
- Produces: `public sealed class JsonConverterAttribute : PicoConverterAttribute { public JsonConverterAttribute(Type converterType) : base(converterType) { } }` (×5)

- [ ] **Step 1: Write verification test**

```csharp
[Test]
public async Task JsonConverter_StillInvoked()
{
    var json = JsonSerializer.Serialize(new ConvDoc { V = new CustomValue(7) });
    await Assert.That(json).Contains("seven");
}
```
(`CustomValue` with a converter class `CustomValueConverter` registered via `[JsonConverter(typeof(CustomValueConverter))]` — copy the pattern from an existing converter test in the JSON test project if present.)

- [ ] **Step 2: Run to verify baseline passes**

Expected: PASS.

- [ ] **Step 3: Implement**

Rewrite the 5 files as subclasses with ctor forwarding. Verify each per-format class exposes `ConverterType` with the same shape first (all 5 verified to take `Type converterType` — Task 5 note applies if any differs).

- [ ] **Step 4: Run full suite**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/IniConverterAttribute.cs PicoJetson/src/PicoJetson/JsonConverterAttribute.cs PicoMsgPack/src/PicoMsgPack/MsgPackConverterAttribute.cs PicoToml/src/PicoToml/TomlConverterAttribute.cs PicoYaml/src/PicoYaml/YamlConverterAttribute.cs PicoJetson/tests/PicoJetson.Unit.Tests
git commit -m "refactor(attrs): converter attributes inherit shared PicoConverterAttribute"
```

---

### Task 7: IniKey `Name` → `Key` normalization

**Files:**
- Modify: `PicoIni/src/PicoIni/IniKeyAttribute.cs`
- Test: `PicoIni/tests/PicoIni.Tests/` — create `IniKeyAttributeTests.cs`

**Interfaces:**
- Produces: `IniKeyAttribute(string name)` keeping `Name` (obsolete) + new `public string Key { get; }`; SG unchanged (the INI generator reads `GetCustomName` from `IniKeyAttribute` — verify it reads `Name`; if so, switch it to `Key` in the same task: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs` `GetIniKey` helper).

- [ ] **Step 1: Write failing test**

```csharp
[Test]
public async Task IniKey_ExposesKey()
{
    var a = new IniKeyAttribute("k");
    await Assert.That(a.Key).IsEqualTo("k");
}

[Test]
public async Task IniKey_NameStillWorksWithObsoleteWarning()
{
    var a = new IniKeyAttribute("k");
#pragma warning disable CS0618
    await Assert.That(a.Name).IsEqualTo("k");
#pragma warning restore CS0618
}

[Test]
public async Task IniKey_OverridesWireKey()
{
    var ini = IniSerializer.Serialize(new KeyDoc { Value = 1 });
    await Assert.That(ini).Contains("k=1");   // [IniKey("k")] on Value
}
```

- [ ] **Step 2: Run tests to verify the first two fail**

Expected: `Key` property FAIL (undefined); the wire-key test passes or fails depending on the generator's current read of `Name` (keep it passing).

- [ ] **Step 3: Implement**

```csharp
public sealed class IniKeyAttribute : Attribute
{
    /// <summary>The key name to use in the INI file.</summary>
    public string Key { get; }

    [Obsolete("Use Key instead.")]
    public string Name => Key;

    public IniKeyAttribute(string name) => Key = name;
}
```
Update the INI generator's key-name read to `Key` if it referenced `Name`.

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoIni/tests/PicoIni.Tests/PicoIni.Tests.csproj -c Release`
Expected: PASS (including the existing `[IniKey]` suite).

- [ ] **Step 5: Commit**

```bash
git add PicoIni/src/PicoIni/IniKeyAttribute.cs PicoIni/src/PicoIni.Gen PicoIni/tests/PicoIni.Tests
git commit -m "refactor(attrs): normalize IniKey to Key property with obsolete Name alias"
```

---

### Task 8: Full verification + README

**Files:**
- Modify: `README.md` (root) — attribute matrix note (per-format attributes now inherit shared Core bases; `IniKey.Key`)

- [ ] **Step 1: Full clean rebuild + full suite**

Run: `dotnet build PicoSerDe.slnx -c Release` + `dotnet test PicoSerDe.slnx -c Release`
Expected: build succeeds; all tests pass.

- [ ] **Step 2: Verify no per-format attribute duplicates semantics**

Run: `grep -rn "sealed class .*Attribute : Attribute" PicoIni/src/PicoIni PicoJetson/src/PicoJetson PicoMsgPack/src/PicoMsgPack PicoToml/src/PicoToml PicoYaml/src/PicoYaml`
Expected: only format-specific attributes (IniComment, IniSection, MsgPackExtensionTag, YamlTag, JsonPropertyName) and the Key attributes remain direct `: Attribute` subclasses.

- [ ] **Step 3: Update README**

Document: shared attribute bases in Core; `IniKeyAttribute.Key` (Name obsolete).

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: document shared attribute bases and IniKey.Key normalization"
```
