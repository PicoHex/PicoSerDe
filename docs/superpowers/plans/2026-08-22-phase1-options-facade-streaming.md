# Phase 1 — Options Redesign + Facade Consolidation + JSON Streaming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the ambient `[ThreadStatic] Current` options mechanism (audit P1 root cause), consolidate the 5 near-identical serializer facades into a shared Core implementation, and migrate JSON streaming to the state-export pattern used by the other 4 formats.

**Architecture:** Options become an explicit parameter on the top-level generated serialize/deserialize methods and are carried per-instance by `JsonReader`/`JsonWriter` (`Options` property) so nested generated code reads `jw.Options`/`reader.Options` instead of static state. `SerDelegate<T>` and a new `DeserializeDelegate<T>` gain a `SerOptions?` slot (breaking Core change, spec §8 amendment). A generic `SerializerFacade<TFormat>` in Core hosts the shared register/serialize/deserialize machinery; each format's public static class becomes a thin forwarding partial. A shared `StreamingRunner` hosts the pipe loop; JSON's O(n²) re-parse loop is deleted and replaced with state-export (reader reconstruction from `ExportState()`).

**Tech Stack:** .NET 10, C# 14 (`allows ref struct` generic constraints), 5 Roslyn source generators (netstandard2.0), TUnit, PicoBench.

**Spec:** `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` (§4, §8, §9, §10)

## Global Constraints

- Public API preservation: `XxxSerializer` public method names stay put (generated code in consumer assemblies calls them). See spec §3.
- Zero allocation on hot paths: `SerRegistry<,>` field reads, no per-call closures (struct captures only).
- `SerDelegate<T>`/`DeserializeDelegate<T>`/`StreamingFunc<TReader,T>`/`DesRegistry` changes are the spec §8 Phase-1 breaks; update the spec §8 break list in Task 8.
- Gen projects stay netstandard2.0; generated-code contract changes are internal to the SG pipeline.
- 1110 existing tests stay green per task; benchmark parity per task where the task touches measured paths.
- All `JsonOptions.Current` references must be gone from `src/` when Task 4 lands (grep-verified).
- Spec §8 amendment (add): SerDelegate signature gains `SerOptions?`; DesRegistry storage type becomes `DeserializeDelegate<T>?`; per-format `StreamingFunc<T>` delegates are replaced by Core `StreamingFunc<TReader, T>`; generated `ISerializer`/`IDeserializer` wrapper classes are removed (internal detail).

---

### Task 1: Core options delegates + registry + mechanical facade compile fixes

**Files:**
- Modify: `PicoSerDe.Core/src/SerDelegate.cs`
- Create: `PicoSerDe.Core/src/SerOptions.cs`, `PicoSerDe.Core/src/DeserializeDelegate.cs`
- Modify: `PicoSerDe.Core/src/DesRegistry.cs`
- Modify: `PicoIni/src/PicoIni/IniSerializer.cs`, `PicoJetson/src/PicoJetson/JsonSerializer.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackSerializer.cs`, `PicoToml/src/PicoToml/TomlSerializer.cs`, `PicoYaml/src/PicoYaml/YamlSerializer.cs`
- Modify: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SerDelegateTests.cs`, `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SerRegistryTests.cs`

**Interfaces:**
- Produces: `SerOptions` (empty base class); `SerDelegate<T>` = `void(IBufferWriter<byte>, T, SerOptions?)`; `DeserializeDelegate<T>` = `T? Deserialize(ReadOnlySpan<byte>, SerOptions?)`; `DesRegistry<TFormat,T>.Deserializer` : `DeserializeDelegate<T>?`

- [ ] **Step 1: Write failing Core tests**

`SerDelegateTests.cs` — update the existing delegate construction to the new signature and add:

```csharp
[Test]
public async Task SerDelegate_CarriesOptionsSlot()
{
    SerOptions? captured = null;
    SerDelegate<int> d = (writer, value, options) => captured = options;
    var opts = new SerOptions();
    d(new ArrayBufferWriter<byte>(), 42, opts);
    await Assert.That(captured).IsEqualTo(opts);
}
```

`SerRegistryTests.cs` — add:

```csharp
[Test]
public async Task DesRegistry_StoresDeserializeDelegate()
{
    var d = new DeserializeDelegate<int>((data, options) => 7);
    DesRegistry<TestFormat, int>.Deserializer = d;
    await Assert.That(DesRegistry<TestFormat, int>.Deserializer!(default, null)).IsEqualTo(7);
}
```
(Use the existing `TestFormat` marker struct from SerRegistryTests; add one if absent.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests/PicoSerDe.Core.Tests.csproj -c Release`
Expected: FAIL — `SerDelegate` has 2 params / `DeserializeDelegate` undefined.

- [ ] **Step 3: Implement Core changes**

`SerOptions.cs`:
```csharp
namespace PicoSerDe.Core;

/// <summary>Base class for all format options (JsonOptions, IniOptions, ...).</summary>
public class SerOptions { }
```

`SerDelegate.cs`:
```csharp
public delegate void SerDelegate<T>(IBufferWriter<byte> writer, T value, SerOptions? options)
    where T : allows ref struct;
```

`DeserializeDelegate.cs`:
```csharp
namespace PicoSerDe.Core;

/// <summary>Universal deserialization delegate; carries per-call options explicitly.</summary>
public delegate T? DeserializeDelegate<T>(ReadOnlySpan<byte> data, SerOptions? options)
    where T : allows ref struct;
```

`DesRegistry.cs` — change field type:
```csharp
public static DeserializeDelegate<T>? Deserializer;
```

- [ ] **Step 4: Fix the 5 facades mechanically (compile fixes only, behavior unchanged)**

In each `XxxSerializer`, wrap every `SerRegistry<TFormat, T>.Handler = (writer, value) => ...` and `CustomHandler` assignment by adding the third parameter: `(writer, value, _) => ...`. Wrap every `DesRegistry<TFormat, T>.Deserializer = ...` assignment: `DesRegistry<TFormat, T>.Deserializer = (data, _) => deserializer.Deserialize(data);`. Do not touch facade method signatures in this task.

- [ ] **Step 5: Run Core + one module test suite**

Run: `dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests/PicoSerDe.Core.Tests.csproj -c Release` then `dotnet test PicoIni/tests/PicoIni.Tests/PicoIni.Tests.csproj -c Release`
Expected: both PASS.

- [ ] **Step 6: Commit**

```bash
git add PicoSerDe.Core/src PicoSerDe.Core/tests PicoIni/src/PicoIni/IniSerializer.cs PicoJetson/src/PicoJetson/JsonSerializer.cs PicoMsgPack/src/PicoMsgPack/MsgPackSerializer.cs PicoToml/src/PicoToml/TomlSerializer.cs PicoYaml/src/PicoYaml/YamlSerializer.cs
git commit -m "refactor(core): add SerOptions and options slot to SerDelegate/DeserializeDelegate"
```

---

### Task 2: JsonReader/JsonWriter carry options per-instance

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs` (ctor ~line 70, `_options` field, Current reads at lines 161, 219, 296, 306, 315)
- Modify: `PicoJetson/src/PicoJetson/JsonWriter.cs` (ctor, Current reads at lines 82, 97)
- Test: create `PicoJetson/tests/PicoJetson.Unit.Tests/JsonReaderOptionsTests.cs` (or add to existing reader test file)

**Interfaces:**
- Produces: `JsonReader(ReadOnlySequence<byte> data, bool isFinalBlock, JsonReaderState state, JsonOptions? options = null)` (trailing param — source compatible), same for the span ctor; `public JsonOptions? Options { get; }` on both reader and writer.

- [ ] **Step 1: Write failing tests**

```csharp
[Test]
public async Task Reader_UsesCtorOptions_ForCommentHandling()
{
    var data = "{ /* c */ \"a\": 1 }"u8;
    var opts = new JsonOptions { ReadCommentHandling = JsonCommentHandling.Skip };
    var reader = new JsonReader(data, options: opts);
    await Assert.That(reader.Read()).IsTrue();   // comment skipped, no throw
}
```

```csharp
[Test]
public async Task Writer_UsesCtorOptions_ForNumberHandling()
{
    var buf = new ArrayBufferWriter<byte>();
    var opts = new JsonOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    var writer = new JsonWriter(buf, options: opts);
    writer.WriteNumber(double.NaN);
    await Assert.That(buf.WrittenSpan.SequenceEqual("NaN"u8)).IsTrue();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — comment throws / NaN throws (Current is null in tests).

- [ ] **Step 3: Implement**

Add `private readonly JsonOptions? _options;` field. Extend both ctors with trailing `JsonOptions? options = null` and store it. Expose `public JsonOptions? Options => _options;`. Replace the 5 `PicoJetson.JsonOptions.Current` reads in JsonReader (lines 161, 219, 296, 306, 315) with `_options` (the two `var opts = ...` sites become `var opts = _options;`). Replace the 2 reads in JsonWriter (82, 97) with `_options`.

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoJetson/tests/PicoJetson.Unit.Tests/PicoJetson.Unit.Tests.csproj -c Release`
Expected: PASS (existing suite green — generated code still uses `Current`, which still exists).

- [ ] **Step 5: Commit**

```bash
git add PicoJetson/src/PicoJetson/JsonReader.cs PicoJetson/src/PicoJetson/JsonWriter.cs PicoJetson/tests/PicoJetson.Unit.Tests
git commit -m "feat(picojetson): carry JsonOptions per-instance on reader/writer"
```

---

### Task 3: JSON generator — explicit options threading (generated code stops reading Current)

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs` (EmitSerializer ~1370-1430, property-name emit ~1409, ignore-condition emits ~1462-1513, deserialize emits ~750, 765, 2054, registration emits ~3785-3845, SerializeCustom call sites ~1771-1780, 1919-1923)
- Modify: `shared/Gen/AnonTypeHandler.cs` (~line 340-343: remove the `{ot}.Current = options;` write)
- Test: create `PicoJetson/tests/PicoJetson.Unit.Tests/GeneratorOptionsTests.cs`

**Interfaces:**
- Produces: generated top-level `Serialize(IBufferWriter<byte> writer, T value, global::PicoSerDe.Core.SerOptions? options)` / `Deserialize(ReadOnlySpan<byte> data, global::PicoSerDe.Core.SerOptions? options)` static methods; generated registration `JsonSerializer.Register<T>(XJsonSer.Serialize, XJsonDeserializer.Deserialize)`; nested emitted code reads `jw.Options` / `reader.Options` only.

- [ ] **Step 1: Write failing e2e tests**

```csharp
[Test]
public async Task Serialize_WithOptions_ThreadsToNestedTypes()
{
    var opts = new JsonOptions { Indented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    var result = JsonSerializer.Serialize(new Outer { Inner = new Inner { FullName = "x" } }, opts);
    await Assert.That(result).Contains("full_name");
    await Assert.That(result).Contains("\n");
}
```
(Add `Outer`/`Inner` POCOs in the test file; `Inner` as a separate `[PicoJsonSerializable]`-attributed or usage-registered type.)

```csharp
[Test]
public async Task Deserialize_WithUnmappedDisallow_Throws()
{
    var opts = new JsonOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    await Assert.That(() => JsonSerializer.Deserialize<SimpleDoc>("{ \"nope\": 1 }"u8, opts)).Throws<JsonException>();
}
```
(Use an existing POCO from the test project; assert the current behavior first — the throw type may be `FormatException`; adjust to the actual existing exception type.)

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — generated code still reads `Current` (null in tests) → naming policy/indent ignored; unmapped check skipped.

- [ ] **Step 3: Implement generator changes**

3a. **EmitSerializer** (line ~1370-1430): replace the wrapper-class emit (`XJsonSer : ISerializer<T>` + `public void Serialize(IBufferWriter<byte> writer, T value)`) with a static class emit:
```
file static class {Name}JsonSer
{
    internal static void Serialize(IBufferWriter<byte> writer, {T} value, global::PicoSerDe.Core.SerOptions? options)
    {
        var __opts = (global::PicoJetson.JsonOptions?)options;
        var jw = new JsonWriter(writer, indented: __opts?.Indented ?? false, maxDepth: __opts?.MaxDepth ?? 63, options: __opts);
```
(Keep the rest of the body identical except the replacements in 3b.)

3b. **Replace emit strings** (exact before → after, in the generator source):

| Before | After |
|---|---|
| `PicoJetson.JsonOptions.Current?.PropertyNamingPolicy?.ConvertName(` | `jw.Options?.PropertyNamingPolicy?.ConvertName(` |
| `PicoJetson.JsonOptions.Current?.DefaultIgnoreCondition` (×5, lines 1462-1513) | `jw.Options?.DefaultIgnoreCondition` |
| `PicoJetson.JsonOptions.Current?.PropertyNameCaseInsensitive` | `reader.Options?.PropertyNameCaseInsensitive` |
| `PicoJetson.JsonOptions.Current?.UnmappedMemberHandling` | `reader.Options?.UnmappedMemberHandling` |
| `new JsonReader(data, maxDepth: PicoJetson.JsonOptions.Current?.MaxDepth ?? 256)` | `new JsonReader(data, maxDepth: __opts?.MaxDepth ?? 256, options: __opts)` |
| `global::PicoJetson.JsonSerializer.SerializeCustom<` … `>(jw.Buffer, ` | append `, jw.Options` before the closing `);` of the same call |

Verify each replacement site has the needed variable in scope (`jw` in property emits inside helper methods; `reader` in deserialize emits; `__opts` in the top-level method only).

3c. **EmitDeserializer**: same treatment — wrapper class `XJsonDeserializer : IDeserializer<T>` becomes `file static class XJsonDeserializer { internal static T? Deserialize(ReadOnlySpan<byte> data, global::PicoSerDe.Core.SerOptions? options) { var __opts = (global::PicoJetson.JsonOptions?)options; ...` with the `new JsonReader(...)` line from 3b. The deserializer's return type becomes `T?` (already emits `return default;` fallbacks).

3d. **Registration emits** (~3785-3845, both the regular and ref-struct paths): change
`Register<T>(new XJsonSer(), new XJsonDeserializer())` → `Register<T>(XJsonSer.Serialize, XJsonDeserializer.Deserialize)`.
The ref-struct path `Register<T>(XJsonSer.Serialize)` stays (method group now matches the new `SerDelegate<T>`).

3e. **AnonTypeHandler.cs** (~line 340-343): remove the emitted `{ot}.Current = options;` line; locate the writer-construction emit in the same method and pass the options parameter through (`options:` argument). The `HasOptionsParam` parameter threading already exists; only the ambient write is removed.

- [ ] **Step 4: Run tests**

Run: `dotnet test PicoJetson/tests/PicoJetson.Unit.Tests/PicoJetson.Unit.Tests.csproj -c Release` and `dotnet test PicoJetson/tests/PicoJetson.Integration.Tests/PicoJetson.Integration.Tests.csproj -c Release`
Expected: PASS. Grep `grep -rn "JsonOptions.Current" PicoJetson/src` — only `JsonOptions.cs` (the field declaration, removed in Task 4) and `JsonSerializer.cs` prev/restore blocks may remain.

- [ ] **Step 5: Commit**

```bash
git add PicoJetson/src/PicoJetson.Gen shared/Gen/AnonTypeHandler.cs PicoJetson/tests/PicoJetson.Unit.Tests PicoJetson/tests/PicoJetson.Integration.Tests
git commit -m "feat(picojetson-gen): thread options explicitly through generated code"
```

---

### Task 4: Remove `[ThreadStatic] Current` from all 5 options classes

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonOptions.cs`, `PicoIni/src/PicoIni/IniOptions.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackOptions.cs`, `PicoToml/src/PicoToml/TomlOptions.cs`, `PicoYaml/src/PicoYaml/YamlOptions.cs` (all 5 gain `: SerOptions`, drop the `Current` field)
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs` (delete all `var prev = JsonOptions.Current;` / `JsonOptions.Current = options;` / `JsonOptions.Current = prev;` blocks — lines 86-96, 108-118, 134-142, 153-161, 187-238, 249-270, 283-322, 357-587; the JSONL/async-enum paths call delegates directly with `options`)

**Interfaces:**
- Consumes: Task 3 (generated code no longer reads `Current`); Task 1 (`SerOptions`).

- [ ] **Step 1: Write failing test**

```csharp
[Test]
public async Task Options_NoLongerExposeThreadStaticCurrent()
{
    var t = typeof(JsonOptions);
    await Assert.That(t.GetField("Current")).IsNull();
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — `Current` exists.

- [ ] **Step 3: Implement**

5 options classes: `public class JsonOptions : SerOptions` (etc.), delete the `[ThreadStatic] public static JsonOptions? Current;` field and its doc comment. In `JsonSerializer.cs`, delete every `prev`/`Current` block; update the bodies: `SerializeLines` calls `SerRegistry<JsonFormat, T>.Handler!(buf, v, options)`; `DeserializeLines` calls `d(line, options)`; `DeserializeAsyncEnumerableImpl` calls `deserializer(lineBytes, options)`; the `try/finally` wrappers are removed (no state to restore).

- [ ] **Step 4: Run tests + verify zero Current references**

Run: `dotnet test PicoJetson/tests/PicoJetson.Unit.Tests/PicoJetson.Unit.Tests.csproj -c Release`
Then: `grep -rn "Options.Current" PicoJetson/src PicoIni/src PicoMsgPack/src PicoToml/src PicoYaml/src shared/Gen` → **must be empty**.
Expected: PASS + empty grep.

- [ ] **Step 5: Commit**

```bash
git add PicoJetson/src/PicoJetson/JsonOptions.cs PicoJetson/src/PicoJetson/JsonSerializer.cs PicoIni/src/PicoIni/IniOptions.cs PicoMsgPack/src/PicoMsgPack/MsgPackOptions.cs PicoToml/src/PicoToml/TomlOptions.cs PicoYaml/src/PicoYaml/YamlOptions.cs
git commit -m "refactor: remove [ThreadStatic] Current from all options classes (audit P1 root cause)"
```

---

### Task 5: MsgPack/Ini/Toml/Yaml generators — options parameter plumbing

**Files:**
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs` (line 1335 Current read; top-level method signatures; `SerializeCustom` call sites if present — mirror Task 3's `mw.Options` pattern)
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs` — no change needed here (Task 3 done)
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`, `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`, `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs` — top-level generated methods gain the unused `global::PicoSerDe.Core.SerOptions? options` parameter so their method groups match `SerDelegate<T>`; `XxxDeserializer` likewise
- Modify: `PicoMsgPack/src/PicoMsgPack/MsgPackWriter.cs` — add `public MsgPackOptions? Options { get; }` (ctor trailing param) only if Task 3-style custom call sites need it (grep `SerializeCustom` in the MsgPack generator first)

**Interfaces:**
- Produces: all 5 generators emit top-level methods matching `SerDelegate<T>` / `DeserializeDelegate<T>` / `StreamingFunc<Reader, T>`; MsgPack generated code reads `((global::PicoMsgPack.MsgPackOptions?)options)` only.

- [ ] **Step 1: Write failing test**

```csharp
[Test]
public async Task MsgPack_DefaultIgnoreCondition_FromOptions()
{
    var opts = new MsgPackOptions { DefaultIgnoreCondition = MsgPackIgnoreCondition.WhenWritingNull };
    var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new MsgDoc { A = null, B = 1 }, opts);
    // B present, A omitted — decode with MsgPackReader and assert key set
}
```
(Add a nullable-prop POCO to the MsgPack test project; assert the wire key set after decoding.)

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — options not threaded (handler invoked without options or Current removed).

- [ ] **Step 3: Implement**

Per generator: top-level `Serialize`/`Deserialize` emit gains the `SerOptions? options` parameter; MsgPack's line 1335 read becomes `((global::PicoMsgPack.MsgPackOptions?)options)?.DefaultIgnoreCondition` (add `var __opts = (global::PicoMsgPack.MsgPackOptions?)options;` at method top if more reads exist). Ini/Toml/Yaml: parameter added, unused (no options reads exist — verified). Registration emits: same method-group change as Task 3 3d (the pair overload exists from Task 1 via facade; check each generator's emit shape and adapt: `Register<T>(Ser, Deser)` method groups).

- [ ] **Step 4: Run all 4 module test suites**

Run: `dotnet test PicoMsgPack/tests/PicoMsgPack.Tests/PicoMsgPack.Tests.csproj -c Release` + Ini + Toml + Yaml suites
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add PicoMsgPack/src PicoIni/src/PicoIni.Gen PicoToml/src/PicoToml.Gen PicoYaml/src/PicoYaml.Gen PicoMsgPack/tests
git commit -m "feat(gen): thread SerOptions parameter through all 5 format generators"
```

---

### Task 6: Shared facade core `SerializerFacade<TFormat>` + 5 thin partials

**Files:**
- Create: `PicoSerDe.Core/src/SerializerFacade.cs`
- Modify: `PicoIni/src/PicoIni/IniSerializer.cs`, `PicoJetson/src/PicoJetson/JsonSerializer.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackSerializer.cs`, `PicoToml/src/PicoToml/TomlSerializer.cs`, `PicoYaml/src/PicoYaml/YamlSerializer.cs` — bodies become forwarding calls
- Test: create `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SerializerFacadeTests.cs` + parity additions in each module test project (or rely on existing suites + the new Core tests)

**Interfaces:**
- Produces: `SerializerFacade<TFormat>.Register<T>(SerDelegate<T>)`, `.Register<T>(SerDelegate<T>, DeserializeDelegate<T>)`, `.Register<T>(ISerializer<T>, IDeserializer<T>)`, `.RegisterDeserializer<T>(DeserializeDelegate<T>)`, `.RegisterDeserializer<T>(IDeserializer<T>)`, `.SerializeToUtf8Bytes<T>(T, SerOptions? = null)`, `.Serialize<T>(T, SerOptions? = null)`, `.Serialize<T>(IBufferWriter<byte>, T, SerOptions? = null)`, `.Deserialize<T>(ReadOnlySpan<byte>, SerOptions? = null)` — all `where T : allows ref struct`.

- [ ] **Step 1: Write failing Core test**

```csharp
[Test]
public async Task Facade_RegistersAndSerializes()
{
    SerDelegate<int> handler = static (writer, value, _) =>
    {
        var span = writer.GetSpan(4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(4);
    };
    SerializerFacade<TestFormat>.Register(handler);
    var bytes = SerializerFacade<TestFormat>.SerializeToUtf8Bytes(42);
    await Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes)).IsEqualTo(42);
}
```
(Register with a fresh marker struct `TestFormat2` to isolate state.)

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — `SerializerFacade` undefined.

- [ ] **Step 3: Implement Core facade**

As specified in Interfaces. Implementation bodies use `SerRegistry<TFormat, T>`/`DesRegistry<TFormat, T>` field reads, `SerializerExtensions.RentWriter()`, `SerializerExtensions.ThrowNoSerializer<T>("PicoSerDe")`. The `ISerializer`/`IDeserializer` wrappers use struct-capturing lambdas `(writer, value, _) => ...` (zero-alloc).

- [ ] **Step 4: Rewrite the 5 format partials as forwarders**

Each `XxxSerializer` keeps: `ContentType` const, its format marker struct, `StreamingCache<T>` + `RegisterStreaming`/`HasStreamingDelegate` (format-typed — unchanged from today until Task 7), and forwards every shared method to `SerializerFacade<TFormat>`. `JsonSerializer` additionally keeps its options-typed overloads (converting `JsonOptions?` → `SerOptions?`), `RegisterCustom`/`HasCustomSerializer`/`SerializeCustom` (gains trailing `JsonOptions? options = null` passing into `CustomHandler`), JSONL methods and `DeserializeAsyncEnumerable` (calling delegates with `options` directly). `MsgPackSerializer` keeps its `RegisterCustom` pair the same way.

- [ ] **Step 5: Run full test suites for all 6 projects**

Run: all 9 test projects (`dotnet test PicoSerDe.slnx -c Release` or per project)
Expected: all PASS (1110+ tests).

- [ ] **Step 6: Commit**

```bash
git add PicoSerDe.Core/src/SerializerFacade.cs PicoSerDe.Core/tests/PicoSerDe.Core.Tests PicoIni/src/PicoIni/IniSerializer.cs PicoJetson/src/PicoJetson/JsonSerializer.cs PicoMsgPack/src/PicoMsgPack/MsgPackSerializer.cs PicoToml/src/PicoToml/TomlSerializer.cs PicoYaml/src/PicoYaml/YamlSerializer.cs
git commit -m "refactor(core): consolidate serializer facades into SerializerFacade<TFormat>"
```

---

### Task 7: Shared StreamingRunner + JSON state-export migration + P1 regression tests

**Files:**
- Create: `PicoSerDe.Core/src/StreamingRunner.cs` (contains `StreamingFunc<TReader, T>`, `StreamStep<TReader, TState, T>`, `StreamingRunner.RunAsync`)
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs` — `DeserializeFromStreamAsync` rewritten to state-export (delete the accumulator + re-parse loop, lines ~187-238)
- Modify: `PicoIni/src/PicoIni/IniSerializer.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackSerializer.cs`, `PicoToml/src/PicoToml/TomlSerializer.cs`, `PicoYaml/src/PicoYaml/YamlSerializer.cs` — `StreamingFunc<T>` → Core `StreamingFunc<XxxReader, T>`; `DeserializeStreamingCore` → `StreamingRunner.RunAsync` + per-format static `Step<T>`
- Test: create `PicoJetson/tests/PicoJetson.Unit.Tests/StreamingOptionsTests.cs`; add chunk-boundary tests to each module's test project

**Interfaces:**
- Consumes: `JsonReader(ReadOnlySequence<byte>, bool, JsonReaderState, JsonOptions?)` ctor; `ExportState()` returning `JsonReaderState` with `Position` field (all 5 readers verified to have these).
- Produces: `StreamingFunc<TReader, T>` (Core, `where TReader : allows ref struct`); `StreamStep<TReader, TState, T>`; `StreamingRunner.RunAsync<TReader, TState, T>(Stream, StreamingFunc<TReader,T>, SerOptions?, StreamStep<TReader,TState,T>, CancellationToken)`; per-format `RegisterStreaming<T>(StreamingFunc<XxxReader, T>)`.

- [ ] **Step 1: Write failing tests**

P1 regression (async options isolation):
```csharp
[Test]
public async Task ConcurrentStreams_KeepTheirOwnOptions()
{
    var strictOpts = new JsonOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    var lenient = new JsonOptions();
    var strictStream = new MemoryStream("{ \"nope\": 1 }"u8.ToArray());
    var lenientStream = new MemoryStream("{ \"nope\": 1 }"u8.ToArray());
    var strictTask = JsonSerializer.DeserializeFromStreamAsync<SimpleDoc>(strictStream, strictOpts);
    var lenientTask = JsonSerializer.DeserializeFromStreamAsync<SimpleDoc>(lenientStream, lenient);
    await Assert.That(() => strictTask).Throws<JsonException>();
    var ok = await lenientTask;   // must NOT throw — options did not bleed across awaits
    await Assert.That(ok).IsNotNull();
}
```
(Adjust the exception type to the actual one thrown by unmapped-disallow.)

Chunk-boundary (add to each format's test project; shown for JSON):
```csharp
[Test]
[Arguments(1)] [Arguments(2)] [Arguments(7)] [Arguments(16)] [Arguments(64)]
public async Task Streaming_ParsesAtEveryChunkBoundary(int chunkSize)
{
    var doc = new SimpleDoc { Name = "hello", Count = 42, Items = new List<int> { 1, 2, 3 } };
    var bytes = JsonSerializer.SerializeToUtf8Bytes(doc);
    var stream = new ChunkedStream(new MemoryStream(bytes), chunkSize);
    var result = await JsonSerializer.DeserializeFromStreamAsync<SimpleDoc>(stream);
    await Assert.That(result!.Count).IsEqualTo(42);
}

/// <summary>Wraps a stream and returns at most chunkSize bytes per ReadAsync —
/// deterministic chunk-boundary coverage for streaming tests.</summary>
private sealed class ChunkedStream(Stream inner, int chunkSize) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunkSize));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```
(Replicate this `ChunkedStream` helper and the same test shape in each format's test project, adapting the serializer call.)

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL — concurrent test throws on the lenient task (Current bleed) or strict task (Current lost across await); chunk tests pass/fail depending on current behavior — the JSON path re-parses so chunk tests may pass; the point is the new path must also pass.

- [ ] **Step 3: Implement Core StreamingRunner**

```csharp
namespace PicoSerDe.Core;

public delegate ReadStatus StreamingFunc<TReader, T>(ref TReader reader, out T? result)
    where TReader : allows ref struct
    where T : notnull;

public delegate ReadStatus StreamStep<TReader, TState, T>(
    ReadOnlySequence<byte> buffer, bool isFinalBlock, TState state, SerOptions? options,
    StreamingFunc<TReader, T> func, out T? result, out TState nextState, out SequencePosition advanceTo)
    where TReader : allows ref struct
    where TState : struct
    where T : notnull;

public static class StreamingRunner
{
    public static async ValueTask<T> RunAsync<TReader, TState, T>(
        Stream stream, StreamingFunc<TReader, T> func, SerOptions? options,
        StreamStep<TReader, TState, T> step, CancellationToken ct)
        where TReader : allows ref struct
        where TState : struct
        where T : notnull
    {
        var pipe = PipeReader.Create(stream);
        var state = default(TState);
        while (true)
        {
            var r = await pipe.ReadAsync(ct);
            var status = step(r.Buffer, r.IsCompleted, state, options, func,
                out var result, out state, out var advanceTo);
            if (status == ReadStatus.Success) { pipe.AdvanceTo(r.Buffer.End); return result!; }
            if (status == ReadStatus.NeedMoreData)
            {
                if (r.IsCompleted) throw new FormatException("Unexpected end of stream while parsing.");
                pipe.AdvanceTo(advanceTo, r.Buffer.End);
                continue;
            }
            throw new FormatException("Unexpected parser state.");
        }
    }
}
```

- [ ] **Step 4: Migrate JSON + convert 4 formats**

JsonSerializer: replace `DeserializeFromStreamAsync` body with:
```csharp
var func = StreamingCache<T>.Func
    ?? throw new InvalidOperationException($"No streaming deserializer registered for {typeof(T)}.");
return await StreamingRunner.RunAsync<JsonReader, JsonReaderState, T>(stream, func, options, Step<T>, ct);

private static ReadStatus Step<T>(ReadOnlySequence<byte> buffer, bool isFinalBlock, JsonReaderState state, SerOptions? options, StreamingFunc<JsonReader, T> func, out T? result, out JsonReaderState next, out SequencePosition advanceTo)
    where T : notnull
{
    var reader = new JsonReader(buffer, isFinalBlock, state, (JsonOptions?)options);
    var status = func(ref reader, out result);
    next = reader.ExportState();
    advanceTo = next.Position;
    reader.Dispose();   // return rented buffers (all 5 readers have Dispose); the
                        // result was already materialized by func before this point
    return status;
}
```
Delete the accumulator + re-parse loop (the old path's `reader.Dispose()` in `finally` is replaced by the `Dispose` above — the old Ini/MsgPack/Toml/Yaml cores did NOT dispose; the unified Step disposes for all 5, fixing the pooled-buffer leak).

For Ini/MsgPack/Toml/Yaml: replace each `DeserializeStreamingCore` with the same `StreamingRunner.RunAsync` call + per-format `Step<T>`; change `StreamingFunc<T>` to `StreamingFunc<XxxReader, T>` in `RegisterStreaming`/`StreamingCache`/`HasStreamingDelegate`; remove the old pipe loop bodies.

**Fallback (only if `allows ref struct` generic delegates fail to compile in the test host):** keep per-format `Step` methods (they are format-typed, no generic delegate), and change `StreamStep` to `Func<...>`-free: pass `step` as `StreamStep<TReader, TState, T>` — if the C# 13 feature is unavailable in a consumer TFM, keep the old per-format loops for that format and file an issue. The PicoSerDe repo itself is net10.0/C#14 — this fallback is not expected to trigger.

- [ ] **Step 5: Run tests**

Run: all 5 module suites + `dotnet test tests/PicoSerDe.Integration.Tests/PicoSerDe.Integration.Tests.csproj -c Release`
Expected: PASS — including the new chunk-boundary and P1 tests.

- [ ] **Step 6: Commit**

```bash
git add PicoSerDe.Core/src/StreamingRunner.cs PicoJetson/src/PicoJetson/JsonSerializer.cs PicoIni/src/PicoIni/IniSerializer.cs PicoMsgPack/src/PicoMsgPack/MsgPackSerializer.cs PicoToml/src/PicoToml/TomlSerializer.cs PicoYaml/src/PicoYaml/YamlSerializer.cs PicoJetson/tests tests/PicoSerDe.Integration.Tests
git commit -m "feat(core): shared StreamingRunner; migrate JSON to state-export streaming (fixes audit P1/P3)"
```

---

### Task 8: Full verification, spec amendment, docs

**Files:**
- Modify: `docs/superpowers/specs/2026-08-22-design-simplification-master-design.md` §8 — extend the Phase-1 break list with: SerDelegate signature, DesRegistry storage type, per-format `StreamingFunc<T>` → Core `StreamingFunc<TReader, T>`, generated wrapper classes removed
- Modify: `README.md` (root) — options note: per-call options are explicit; no ambient state

- [ ] **Step 1: Full clean rebuild + full test suite**

Run: `rm -rf **/bin **/obj` (or `find . -type d -name bin -o -name obj | xargs rm -rf`) then `dotnet build PicoSerDe.slnx -c Release` then `dotnet test PicoSerDe.slnx -c Release`
Expected: build succeeds; all tests pass.

- [ ] **Step 2: Benchmarks**

Run: `dotnet run -c Release --project PicoJetson/benchmarks/PicoJetson.Benchmarks/PicoJetson.Benchmarks.csproj` (and at least PicoIni.Benchmarks)
Expected: no regression vs pre-change baseline on serialize/deserialize/streaming measurements (compare with the recorded baseline from the 2026-08-16 audit if available; otherwise record current numbers as the new baseline).

- [ ] **Step 3: Grep-verify ambient state is gone**

Run: `grep -rn "ThreadStatic\|\.Current" PicoSerDe.Core/src PicoIni/src PicoJetson/src PicoMsgPack/src PicoToml/src PicoYaml/src shared/Gen`
Expected: no `ThreadStatic`; no `.Current` except `JsonOptions.Current`-free code (allow `Stream.Current`-style BCL matches — review each hit).

- [ ] **Step 4: Update spec §8 + README**

Add the break-list items from Global Constraints. README: document explicit options passing and the removed `Current` API.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-08-22-design-simplification-master-design.md README.md
git commit -m "docs: amend Phase 1 break list (delegate signatures); document explicit options"
```
