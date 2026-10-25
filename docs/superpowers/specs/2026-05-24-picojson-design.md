# PicoJetson Design Spec

**Status**: Draft
**Date**: 2026-05-24
**Topic**: PicoJetson — JSON serialization for the PicoHex ecosystem

---

## 1. Motivation

PicoJetson is the first format implementation in the PicoHex serialization ecosystem. It builds on `PicoSerDe.Abs` (`TokenType`, `ISerializer<T>`, `IDeserializer<T>`) and provides a high-performance, AOT-first JSON serializer with zero-reflection code generation.

## 2. Design Goals

1. **AOT-first** — zero reflection, zero runtime code generation, 100% Source Generator driven
2. **Extreme performance** — ref struct JsonReader/JsonWriter, zero-copy value access, SIMD-ready UTF-8 parsing
3. **Strict RFC 8259** — no comments, no trailing commas, no single quotes by default
4. **No attribute required** — property name maps directly to JSON key by default
5. **Minimal API surface** — fewer methods = fewer branches = faster hot path
6. **Compact by default** — indented output opt-in for readability

---

## 3. Module Structure

```
PicoSerDe.slnx
├── PicoSerDe.Abs/                       ← net10.0, shared interfaces + TokenType
│   └── src/PicoSerDe.Abs/
│       └── PicoSerDe.Abs.csproj
│
└── PicoJetson/                            ← net10.0, JSON implementation
    ├── src/
    │   ├── PicoJetson/
    │   │   ├── PicoJetson.csproj           (net10.0, refs PicoSerDe.Abs)
    │   │   ├── JsonReader.cs
    │   │   ├── JsonWriter.cs
    │   │   └── JsonSerializer.cs
    │   └── PicoJetson.Gen/
    │       ├── PicoJetson.Gen.csproj       (netstandard2.0, Roslyn Analyzer)
    │       └── JsonSerializerGenerator.cs
    ├── tests/
    │   └── PicoJetson.Tests/
    ├── benchmarks/
    └── samples/
```

---

## 4. JsonReader

Forward-only, stack-only ref struct that parses UTF-8 JSON bytes into `TokenType` tokens.

### 4.1 API

```csharp
public ref struct JsonReader
{
    // Constructors
    public JsonReader(ReadOnlySpan<byte> data);
    public JsonReader(ReadOnlySequence<byte> data);

    // Position
    public TokenType TokenType { get; }
    public int Depth { get; }
    public long BytesConsumed { get; }
    public ReadOnlySpan<byte> ValueSpan { get; }  // raw bytes of current token

    // Navigation
    public bool Read();       // true = token available, false = EOF, malformed JSON throws
    public void Skip();       // skip current subtree, throws on error
    public bool TrySkip();    // skip current subtree, returns false on error

    // Zero-copy value access
    public ReadOnlySpan<byte> GetStringRaw();  // decoded UTF-8 (unescape resolved)

    // TryGet* — return false on overflow/type mismatch
    public bool TryGetInt32(out int value);
    public bool TryGetInt64(out long value);
    public bool TryGetFloat64(out double value);
    public bool TryGetBool(out bool value);
}
```

### 4.2 Internal Design

- **Pure UTF-8 state machine** — `Read()` contains a single large loop: skip whitespace → classify next byte → emit token → update depth
- **String fast path**: scan for `\` (SIMD-accelerable). No escape → `GetStringRaw()` points directly to source buffer. Has escape → stackalloc decode.
- **Depth tracking**: internal stack of container types, max depth 256 (configurable). `{` `[` push, `}` `]` pop + validate match.
- **Number parsing**: single-pass validation + parsing into `ValueSpan`. `TryGetInt32()` etc. parse from `ValueSpan` on demand.
- **Error handling**: malformed JSON throws `JsonException` (or `FormatException`). No error codes.
- **No options**: v1 strict RFC 8259 only. No comment handling, no trailing commas, no single quotes.

### 4.3 JSON → TokenType Mapping

| UTF-8 Input | TokenType |
|---|---|
| `{` | `ObjectStart` |
| `}` | `ObjectEnd` |
| `[` | `ArrayStart` |
| `]` | `ArrayEnd` |
| `"key":` | `PropertyName` |
| `"value"` | `String` |
| `42` | `Int32` (or `Int64` if overflow) |
| `3.14` / `1e10` | `Float64` |
| `true` / `false` | `Bool` |
| `null` | `Null` |

---

## 5. JsonWriter

Forward-only ref struct that writes UTF-8 JSON to an `IBufferWriter<byte>`.

### 5.1 API

```csharp
public ref struct JsonWriter
{
    public JsonWriter(IBufferWriter<byte> buffer, bool indented = false);

    // Structure
    public void WriteStartObject();
    public void WriteEndObject();
    public void WriteStartArray();
    public void WriteEndArray();
    public void WritePropertyName(ReadOnlySpan<byte> utf8Name);

    // Values
    public void WriteString(ReadOnlySpan<byte> utf8Value);
    public void WriteNumber(int value);
    public void WriteNumber(long value);
    public void WriteNumber(double value);
    public void WriteBoolean(bool value);
    public void WriteNull();

    // Control
    public void Flush();
    public long BytesWritten { get; }
}
```

### 5.2 Internal Design

- **Compact (default)**: no whitespace between tokens, minimal output size
- **Indented**: newline + spaces per depth level. Uses `indented: true` constructor flag.
- **No structural validation**: Source Generator guarantees correctness. Manual users accept risk.
- **Buffer strategy**: delegates to `IBufferWriter<byte>` — caller controls allocation (ArrayPool, fixed buffer, PipeWriter)
- **String writing**: writes raw UTF-8 bytes directly, no escape encoding needed (input is clean decoded bytes from ISerializer)

---

## 6. JsonSerializer — Static API

Static facade that triggers Source Generator code generation.

```csharp
public static class JsonSerializer
{
    // Serialize
    public static byte[] SerializeToUtf8Bytes<T>(T value);
    public static string Serialize<T>(T value);
    public static void Serialize<T>(IBufferWriter<byte> writer, T value);
    public static void Serialize<T>(Stream stream, T value);

    // Deserialize
    public static T? Deserialize<T>(ReadOnlySpan<byte> data);
    public static T? Deserialize<T>(Stream stream);
    public static ValueTask<T?> DeserializeAsync<T>(Stream stream, CancellationToken ct = default);
}
```

Implementation delegates to Source-Generator-produced `ISerializer<T>` / `IDeserializer<T>` implementations. Async methods use PicoSerDe.Abs extensions internally.

---

## 7. Source Generator (PicoJetson.Gen)

### 7.1 Trigger

Scans all `JsonSerializer.Serialize<T>(...)` and `JsonSerializer.Deserialize<T>(...)` call sites in the compilation. For each unique `T`, generates a serialization struct.

### 7.2 Generated Code

For a type `Person { string Name; int Age; }`:

```csharp
// Generated: Person.PicoJetson.g.cs

file readonly struct PersonJsonSerializer : ISerializer<Person>
{
    public void Serialize(IBufferWriter<byte> writer, Person value)
    {
        var jw = new JsonWriter(writer);
        jw.WriteStartObject();

        jw.WritePropertyName("Name"u8);
        jw.WriteString(Encoding.UTF8.GetBytes(value.Name));

        jw.WritePropertyName("Age"u8);
        jw.WriteNumber(value.Age);

        jw.WriteEndObject();
    }
}

file readonly struct PersonJsonDeserializer : IDeserializer<Person>
{
    public Person Deserialize(ReadOnlySpan<byte> data)
    {
        var reader = new JsonReader(data);
        var obj = new Person();
        reader.Read(); // ObjectStart

        while (reader.Read() && reader.TokenType == TokenType.PropertyName)
        {
            var prop = reader.GetStringRaw();
            reader.Read();

            if (MemoryExtensions.SequenceEqual(prop, "Name"u8))
                obj.Name = Encoding.UTF8.GetString(reader.GetStringRaw());
            else if (MemoryExtensions.SequenceEqual(prop, "Age"u8))
            {
                reader.TryGetInt32(out var age);
                obj.Age = age;
            }
            else
                reader.TrySkip();
        }
        return obj;
    }
}
```

### 7.3 Generation Rules

- **Property name**: uses `[JsonPropertyName("key")]` if present, otherwise uses property name as-is
- **Unknown properties**: skipped via `TrySkip()`, no error
- **Nested objects**: recursive generation — if `Person` contains `Address Address`, generates `AddressJsonSerializer` / `AddressJsonDeserializer` inline
- **Collections**: v1 supports `List<T>`, `T[]`, `Dictionary<string, T>`
- **Built-in types**: `string`, `int`, `long`, `double`, `bool`, `DateTime` (ISO 8601 string), `Guid` (string) handled directly

### 7.4 Usage Pattern

```csharp
// User writes:
var bytes = JsonSerializer.SerializeToUtf8Bytes(person);
var p = JsonSerializer.Deserialize<Person>(bytes);

// SG sees Serialize<Person> and Deserialize<Person> → generates implementations
// JsonSerializer.SerializeToUtf8Bytes internally does:
//   new PersonJsonSerializer().Serialize(writer, value)
```

---

## 8. JsonPropertyName Attribute

```csharp
namespace PicoJetson;

[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonPropertyNameAttribute : Attribute
{
    public string Name { get; }
    public JsonPropertyNameAttribute(string name) => Name = name;
}
```

Usage:

```csharp
public class Person
{
    [JsonPropertyName("full_name")]
    public string Name { get; set; }
}
```

SG emits `"full_name"` instead of `"Name"` in both serialize and deserialize paths.

---

## 9. Design Decisions

1. **No Reader/Writer base classes** — each format has zero-abstraction-cost ref structs
2. **No `partial class` required** — SG triggers on `JsonSerializer.Serialize/Deserialize<T>` calls
3. **Separate `PicoJetson.Gen` targeting netstandard2.0** — Roslyn analyzer requirement. Runtime library stays net10.0 for ref struct / Span / SIMD.
4. **`ReadOnlySpan<byte>` + `ReadOnlySequence<byte>` constructors** — memory and streaming paths both supported v1
5. **`Skip()` + `TrySkip()`** — both provided. Deserializer uses `TrySkip()` for unknown fields (no exception on malformed skipped content — intentional tradeoff for performance).
6. **Compact by default** — `indented: true` opt-in for readability.

---

## 10. Open Questions

1. **Property casing**: default is exact match. Should we offer `PropertyNamingPolicy = CamelCase` later?
2. **Constructor deserialization**: v1 uses `new T()` + property setters. Should we support `[JsonConstructor]` for immutable types?
3. **Collection element types**: `List<T>` where T is complex — recursive generation. What about `IEnumerable<T>` / `IReadOnlyList<T>`?
4. **`DateTime` format**: ISO 8601 string by default. Configurable?

---

## 11. Implementation Status (v1)

### Completed

| Feature | Status |
|---------|--------|
| JsonReader — ref struct UTF-8 pull parser | ✅ |
| JsonReader — `ReadOnlySequence<byte>` streaming support via SequenceReader | ✅ |
| JsonReader — full RFC 8259 unicode escapes (`\uXXXX` + surrogate pairs) | ✅ |
| JsonReader — byte-offset error messages | ✅ |
| JsonReader — zero-copy string fast path (no `\` → points to source buffer) | ✅ |
| JsonWriter — ref struct IBufferWriter push writer | ✅ |
| JsonWriter — NaN/Infinity throws ArgumentException | ✅ |
| JsonSerializer — static facade + `Register<T>` API | ✅ |
| JsonSerializer — `ConcurrentDictionary` for thread safety | ✅ |
| JsonPropertyNameAttribute | ✅ |
| JsonIgnoreAttribute | ✅ |
| JsonConverterAttribute + `IJsonConverter<T>` with `Read(ref JsonReader)` | ✅ |
| Source Generator — `IIncrementalGenerator`, `ModuleInitializer` registration | ✅ |
| Source Generator — `TypeKindResolver` with Roslyn symbol-based type detection | ✅ |
| Source Generator — descriptive variable names, nesting-level tracking | ✅ |
| Source Generator — `ImmutableArray<PropertyInfo>` for incremental pipeline correctness | ✅ |
| Source Generator — case-insensitive property name matching | ✅ |
| Source Generator — Dict non-string key deserialization (int, long, guid, enum) | ✅ |
| Source Generator — `JsonConverter` support in generated code | ✅ |
| CI/CD — Tier 2 pipelines (ci + release + AOT) on 5-platform matrix | ✅ |
| Benchmarks — PicoBench comparisons vs System.Text.Json (JIT + AOT) | ✅ |
| NuGet packaging — SourceLink, snupkg, `Directory.Build.props` baseline | ✅ |

### P0 — Remaining for v1

| # | Task | Doc Ref | Detail |
|---|------|---------|--------|
| 1 | **Depth defense** | token-layer §5.1 | `MaxDepth=256` configurable, throw on exceed. Currently `_depth` increments unchecked. |
| 2 | **Stackalloc short escapes** | token-layer §5.3 | Strings ≤256 bytes with `\` should use `stackalloc` not `ArrayPool.Rent`. Fast path (no `\`) already zero-copy. |

### P2 — Deferred to v2

| # | Task | Doc Ref |
|---|------|---------|
| 3 | SIMD-accelerated `\` scan | token-layer §5.3 |
| 4 | CamelCase naming policy | §10 Q1 |
| 5 | `[JsonConstructor]` for immutable types | §10 Q2 |
| 6 | `IEnumerable<T>` / `IReadOnlyList<T>` support | §10 Q3 |
| 7 | DateTime format configurability | §10 Q4 |
| 8 | Collection deserialization SIMD/int fast-path | perf benchmark gap |

### Excluded (cross-format token layer only)

These TokenReader/TokenWriter APIs are defined in `token-layer-design.md` but belong to the abstract cross-format layer — not required for JSON implementation:

- `TryGetInt8/16/UInt*/Float16/Float32` — JSON only produces Int32/Float64
- `GetBytesRaw()` / `WriteBytes()` — Bytes token for binary formats
- `GetExtensionTag/Raw()` / `WriteExtension()` — Extension for MessagePack
- `ArrayLength` hint — JSON arrays have no length prefix
- `GetPropertyNameRaw()` — covered by `GetStringRaw()` when TokenType=PropertyName

---

## 12. v2 Roadmap

### Design Review

- **No Reader/Writer base classes** (§9.1 explicitly rejected). v2 stays ref struct only.
- **No dual-mode branch elimination** as independent task. The `_isSequence` branch in `Read()` is correct H4+H5 implementation. Specialized read paths (e.g., integer array fast-path) naturally subsume this when relevant.

### Tasks (all independent, no architectural dependencies)

**Order**: SIMD first (provides performance substrate for collection optimization), then performance tasks, then feature tasks, then engineering.

| # | Task | Category | Complexity | Doc Ref |
|---|------|----------|------------|---------|
| 4 | **SIMD acceleration** | Performance | High | token-layer §5.3 |
| 3 | **Collection deserialization** | Performance | Med | benchmark 0.77x gap |
| 5 | **Dict key optimization** | Performance | Low | — |
| 9 | **DateTime format configurable** | Feature | Low | §10 Q4 |
| 6 | **CamelCase naming policy** | Feature | Low | §10 Q1 |
| 7 | **`[JsonConstructor]` for immutables** | Feature | High | §10 Q2 |
| 8 | **More collection types** | Feature | Med | §10 Q3 |
| 10 | **CI benchmark** | Engineering | Low | — |
| 11 | **Fuzzing tests** | Engineering | Med | — |

**Weak dep**: (3) Collection opt can leverage (4) SIMD number scanning — do (4) before (3).

---

## 13. Collection Deserialization Performance — Fix Plan

**Status**: AOT benchmark 0.37x vs S.T.J (5183ns vs 1895ns, GC 260 vs 154). Root cause: per-element overhead in `TryReadNextInt32Span` — 6 bounds checks + separate digit scanning + `Utf8Parser.TryParse` per element × 100 elements.

### Tier 1 — Reader-level (hours)

| # | Fix | Current | Target | Est. |
|---|-----|---------|--------|------|
| **A1** | Fuse `Utf8Parser` into digit loop | Two-pass: scan digits + `Utf8Parser.TryParse` | Single-pass inline `int.Parse` style | -0.3μs |
| **A2** | Reduce bounds checks | 6× `_position >= _data.Length` per element | Merge adjacent checks; use `ref` pointer | -0.3μs |
| **A3** | Eliminate `_valueSpan` slice | `_valueSpan = data[start..position]` + parse | Parse directly from `data[start..]` | -0.1μs |
| **A4** | List initial capacity | `new List<int>()` (0→4→...→128) | `new List<int>(16)` saves 3 resizes | GC: -30 |

### Tier 2 — SG-level (days)

| # | Fix | Est. |
|---|-----|------|
| **B1** | Compile-time unroll for small arrays | SG generates inline `ParseInt` + `Add` blocks without loop overhead | -1μs, GC: -60 |

### Tier 3 — Architecture (weeks)

| # | Fix |
|---|-----|
| **C1** | Fused Reader parse — `Read()` produces parsed value directly, eliminates tokenize/parse separation |

### Previously Attempted & Ruled Out

- **A2 (original)**: Eliminate `Read()` loop — already implemented. `TryReadNextInt32` loop skips per-element `Read()`.
- **B2 (original)**: Skip float detection — already handled. `TryReadNextInt32Span` has no float checks.
- **B4 (original)**: Collection reuse — implemented. `obj.Scores ??= new List<int>()` eliminates orphaned objects.
- **Stackalloc decode**: Blocked by C# CS8352. `Span<byte>` from stackalloc cannot escape to ref struct field.
- **SIMD whitespace**: Implemented. `SkipWhitespaceSpan` uses Vector128. Scalar fast-path avoids wasted SIMD for compact JSON.
- **Span property matching**: Implemented. `__PicoJetsonHelp.Eq` replaces `Encoding.UTF8.GetString` + `string.Equals`.

### Pre-check Before Implementation

- A1-A3 are local to `TryReadNextInt32Span` in `JsonReader.cs`. No SG changes.
- A4 is a one-line change in `JsonSerializerGenerator.cs` `case "list"`.
- B1 requires detecting array size at compile time (metadata only, not JSON content).
- C1 requires full Reader API redesign.
