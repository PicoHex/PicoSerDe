# Post-Review Improvements Design Spec

**Status**: Draft
**Date**: 2026-05-29
**Topic**: 6 improvements identified during deep code review of PicoSerDe

---

## 1. Motivation

A comprehensive code review of the PicoSerDe codebase (JSON, INI, TOML, YAML format implementations + PicoSerDe.Abs abstractions) identified 6 areas for improvement. This spec defines the design for each.

## 2. Improvements

### 2.1 [#1] Unify INI TokenType with Shared TokenType

**Current State**: INI defines its own `IniTokenType` enum (None, SectionStart, Key, Value, Comment, Blank). JSON/TOML/YAML share `PicoSerDe.Abs.TokenType`.

**Problem**: INI cannot participate in the cross-format pipeline. The token layer's core value is format-agnostic vocabulary — INI's isolation undermines this.

**Design Decision**: Remove `IniTokenType` entirely. `IniReader` emits `PicoSerDe.Abs.TokenType`. The mapping:

| Current IniTokenType | New TokenType | Notes |
|---|---|---|
| None | None | End of input |
| SectionStart | ObjectStart | Emit ObjectEnd for previous section before ObjectStart for new section |
| Key | PropertyName (followed by value token) | Reader.Read() returns PropertyName, then caller calls Read() again for the value |
| Value | String / Int32 / Int64 / Float64 / Bool | Parsed from value span |
| Comment | Skipped (no token) | Comments are not structural |
| Blank | Skipped (no token) | Blank lines are whitespace |

**API Changes**:
- Remove `IniReader.TokenType` (was `IniTokenType`) → now `TokenType`
- Remove `SectionName`, `Key`, `ValueSpan`, `CommentText` properties
- Add `GetStringRaw()` (like JsonReader) for PropertyName and String values
- `SectionNameEquals()` replaced by `GetStringRaw()` comparison against `"SectionName"u8`
- Reader becomes stateful: emits implicit ObjectEnd when transitioning between sections
- `IniTokenType.cs` file deleted

**Source Generator Changes**: `IniSerializerGenerator.cs` updates generated code to use `TokenType` constants instead of `IniTokenType`.

### 2.2 [#2] TOML/YAML SIMD Whitespace Skip

**Current State**: `JsonReader.SkipWhitespaceSpan` has SIMD-accelerated whitespace skipping using `Vector128`. `TomlReader` and `YamlReader` use scalar-only while loops.

**Design Decision**: Extract the SIMD logic into a shared utility method. Place in `PicoSerDe.Abs` or a `shared/SimdHelpers.cs` file included via `<Compile Include>`.

```csharp
// shared/SimdHelpers.cs (or PicoSerDe.Abs)
internal static class SimdHelpers
{
    public static int SkipWhitespace(ReadOnlySpan<byte> data, int position)
    {
        // Vector128 SIMD path + scalar fallback
        // (extracted from JsonReader.SkipWhitespaceSpan)
    }
}
```

**Impact**: Zero API change. Pure internal optimization. TomlReader and YamlReader call `SimdHelpers.SkipWhitespace` instead of scalar loops.

### 2.3 [#3] CI Benchmark Regression Step

**Current State**: CI builds benchmarks (`dotnet build ...Benchmarks.csproj`) but does not run them.

**Design Decision**: Add a CI step that runs benchmarks in a "verify they execute" mode (not comparing historical data yet). This prevents bitrot where benchmarks compile but crash at runtime.

```yaml
- run: dotnet run --project PicoJetson/benchmarks/PicoJetson.Benchmarks -c Release -- --filter '*' --iterationCount 1 --warmupCount 0
```

Run for PicoJetson, PicoIni, PicoToml, PicoYaml benchmarks. If any fail, CI fails.

Future: add historical comparison via benchmark action (e.g., benchmarkdotnet-action).

### 2.4 [#4] README and Getting Started Documentation

**Current State**: `README.md` is 10 bytes (empty/minimal). Design docs exist in `docs/superpowers/specs/`.

**Design Decision**: Write a comprehensive root-level README covering:
- Project overview (what is PicoSerDe)
- Format support matrix
- Quick start (installation, basic Serialize/Deserialize)
- API examples for each format
- Performance highlights
- Project structure diagram
- Links to design docs

### 2.5 [#5] YAML Anchor/Alias Support

**Current State**: YamlReader does not parse `&anchor` definitions or `*alias` references.

**Design Decision**: Add anchor/alias support to YamlReader (span mode and sequence mode).

**Scope**:
- `&anchor` on scalar and mapping nodes → store value in internal `Dictionary<string, StoredAnchor>`
- `*alias` → resolve from anchors dictionary, emit stored token/value
- Anchors are stored as lazy references (position + length in source buffer) to avoid copying
- No `<<: *merge` (YAML 1.1 merge key) — deferred to v2
- Error on duplicate anchor names and unresolved aliases

**Data Structure**:
```csharp
private struct StoredAnchor
{
    public TokenType TokenType;
    public ReadOnlySpan<byte> Value;  // for scalars
    public int ObjectDepth;           // for mappings (track nesting)
}
private Dictionary<string, StoredAnchor>? _anchors;
```

**Reader flow for `&anchor`**:
1. Detect `&` after node indicator (key: or -)
2. Read anchor name (until whitespace or `,`)
3. On next Read(), store current token+value as anchor
4. Continue normal token emission

**Reader flow for `*alias`**:
1. Detect `*` as value
2. Look up anchor name
3. If scalar: emit stored TokenType + Value
4. If mapping: emit ObjectStart, then replay stored properties

### 2.6 [#6] Generic Static Cache Optimization

**Current State**: Each Serializer uses `ConcurrentDictionary<Type, object>` + cast on every call.

```csharp
// Hot path: dictionary lookup + cast per call
if (_serializers.TryGetValue(typeof(T), out var s))
    ((ISerializer<T>)s).Serialize(writer, value);
```

**Design Decision**: Add a generic static cache class inside each Serializer. The `Register` method populates it. The Serialize/Deserialize methods read from it directly.

```csharp
// PicoJetson/JsonSerializer.cs
public static partial class JsonSerializer
{
    // Keep ConcurrentDictionary for dynamic registration (backward compat)
    private static readonly ConcurrentDictionary<Type, object> _serializers = new();
    private static readonly ConcurrentDictionary<Type, object> _deserializers = new();

    // Fast path: generic static cache (populated by Register and ModuleInitializer)
    private static class Cache<T>
    {
        internal static ISerializer<T>? Serializer;
        internal static IDeserializer<T>? Deserializer;
    }

    public static void Register<T>(ISerializer<T> serializer, IDeserializer<T> deserializer)
    {
        Cache<T>.Serializer = serializer;
        Cache<T>.Deserializer = deserializer;
        _serializers[typeof(T)] = serializer;  // keep for dynamic lookup
        _deserializers[typeof(T)] = deserializer;
    }

    public static void Serialize<T>(IBufferWriter<byte> writer, T value)
    {
        var s = Cache<T>.Serializer;
        if (s is not null)
            s.Serialize(writer, value);
        else
            ThrowNoSerializer<T>();
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        var d = Cache<T>.Deserializer;
        if (d is not null)
            return d.Deserialize(data);
        ThrowNoSerializer<T>();
        return default;
    }
}
```

**Impact**: Eliminates dictionary lookup + cast on hot path. JIT can inline the field access. Applied to all 4 Serializer classes (JsonSerializer, IniSerializer, TomlSerializer, YamlSerializer).

---

## 3. Implementation Order

Complexity-ascending order:
1. **#4** README (pure docs, no code risk)
2. **#3** CI benchmark step (YAML change only)
3. **#6** Generic cache (local change in 4 serializer files)
4. **#2** SIMD extraction (shared helper + 2 reader files)
5. **#1** INI TokenType unification (architectural refactor: reader + generator + tests)
6. **#5** YAML anchors (new feature: reader changes + tests)

Each improvement is independently testable and committable.

---

## 4. Non-Goals for This Spec

- YAML merge key (`<<:`) support — deferred
- Cross-format conversion pipeline — separate design
- JSON/TOML/YAML new features beyond listed items
- NuGet package documentation beyond root README
