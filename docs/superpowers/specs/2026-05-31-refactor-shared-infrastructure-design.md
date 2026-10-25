# Refactor: Shared Infrastructure Extraction + Beta Format Alignment

Date: 2026-05-31
Status: Approved

## Overview

Two-phase refactoring of PicoSerDe:

1. **Phase 1**: Extract shared source generator infrastructure from 5 duplicated SG files into `shared/Gen/`. Move runtime helpers into `PicoSerDe.Core`.
2. **Phase 2**: Apply mature patterns from PicoJetson/PicoMsgPack to PicoIni/PicoToml/PicoYaml.

All work is TDD-driven. 369 existing tests protect against regressions; new tests are added for each new capability.

---

## Phase 1: Extract Shared Infrastructure

### File Moves

```
BEFORE                                AFTER
──────────────────────────────────────────────────────
shared/SimdHelpers.cs           →    PicoSerDe.Core/src/SimdHelpers.cs  (real file, no link)
shared/TextHelpers.cs           →    PicoSerDe.Core/src/TextHelpers.cs  (already real file)
shared/TypeKindResolver.cs      →    shared/Gen/TypeKindResolver.cs
shared/IsExternalInit.cs        →    shared/Gen/IsExternalInit.cs
shared/BenchModels.cs           →    shared/BenchModels.cs  (unchanged)
                                   shared/Gen/GenInfrastructure.cs  (NEW)
```

### New File: `shared/Gen/GenInfrastructure.cs`

Extracts duplicated code from 5 source generators into format-parameterized methods:

| Method | Parameterized By | Description |
|--------|-----------------|-------------|
| `Transform(SyntaxContext, format)` | `format: string` ("json"\|"ini"\|"msgpack"\|"toml"\|"yaml") | Calls format-specific attribute helpers |
| `ExtractNestedProperties(type, attributeLookup)` | delegate for attribute detection | Recursive property extraction |
| `CollectNestedTypes(typeInfo, dict)` | — | M×N dedup for nested object types |
| `GetPropertyName(prop, attrData)` | attribute data array | Property name override via attribute |
| `HasIgnore(prop, attrData)` | attribute data array | Ignore attribute detection |
| `HasCamelCase(type, attrData)` | attribute data array | CamelCase attribute detection |
| `GetConverterType(prop, attrData)` | attribute data array | Custom converter detection |
| `GetDateTimeFormat(prop, attrData)` | attribute data array | DateTime format string |
| `ShortName(fullName)` | — | `global::Ns.Type` → `Type` |

Plus shared record types:
- `TypeInfo(FullyQualifiedName, Namespace, Name, Properties)`
- `PropertyInfo(Name, JsonName, TypeKind, TypeFullName, IsNullable, ElementTypeKind, ElementTypeName, KeyTypeKind, KeyTypeName, NestedProperties, ConverterTypeFullName, DateTimeFormat)`

### Projects Affected

| Project | Change |
|---------|--------|
| `PicoSerDe.Core.csproj` | Add `SimdHelpers.cs` as real file. Remove link to `shared/SimdHelpers.cs`. |
| 5 runtime csproj (PicoJetson, PicoMsgPack, PicoIni, PicoToml, PicoYaml) | Remove `<Compile Include="..\..\..\shared\SimdHelpers.cs">` links |
| 5 SG csproj (\*.Gen) | Update `TypeKindResolver.cs` path to `..\..\..\shared\Gen\`. Add `GenInfrastructure.cs` link. |
| 5 SG .cs files | Delete duplicated methods, call `GenInfrastructure` instead. |

### SG Output Stability

Source generator output must remain byte-for-byte identical after refactoring.
Each SG file change is verified by:
1. Run tests → all 369 pass
2. Capture generated files → diff against baseline (generated before refactoring)

### Cache<T> Pattern — NOT merged

Each format keeps its own `Cache<T>` / `Register<T>` / `ThrowNoSerializer<T>`. These are format-level dispatch points and merging would introduce unnecessary cross-format coupling.

---

## Phase 2: Align Mature Patterns

### P2-1: SIMD Whitespace Skip → IniReader

**Current**: `IniReader.ReadSpan()` scans whitespace byte-by-byte.
**Target**: Call `SimdHelpers.SkipWhitespace()` in span mode path.

**Test**: New `IniReaderSIMDTests.cs` — parse INI with heavy whitespace, verify byte-level equivalence with scalar path.

### P2-2: stackalloc Number Formatting → TomlWriter, YamlWriter

**Current**: TomlWriter has 2 stackalloc usages, YamlWriter has 2. JsonWriter has 5, IniWriter has 18.
**Target**: Apply `Span<byte> buf = stackalloc byte[N]; value.TryFormat(buf, out var w)` pattern to int/long/double/bool writes.

**Test**: Existing Writer tests protect this — no new tests needed if existing tests pass.

### P2-3: ulong Bitmask Comma Tracking → TomlWriter, YamlWriter

**Current**: JsonWriter uses `ulong _needsComma` with `1UL << _depth` to avoid per-depth allocations.
**Target**: Apply same pattern to TomlWriter and YamlWriter for nested structures (tables, arrays, mappings).

**Test**: Existing nested structure Writer tests protect this.

### P2-4: Inline RawBuffer int[] Scan → MsgPackSerializerGenerator

**Current**: Only PicoJetson SG emits inline raw-buffer scanning for `int[]` arrays.
**Target**: MsgPack SG emits same optimization — binary format makes this especially natural.

**Test**: New `MsgPackSGIntArrayTests.cs` — verify generated code directly scans buffer, verify correctness via round-trip.

### Patterns NOT Synced

| Pattern | Reason |
|---------|--------|
| MsgPack type stacks (`IntStack64`) | MessagePack-specific for map/array element tracking |
| RawBuffer inline scan for INI/YAML | INI has no standard array syntax; YAML flow vs block sequences differ too much |
| SIMD for MsgPackReader | MsgPack is binary, no whitespace to skip |

---

## Constraints

- **AOT compatibility**: All changes must maintain `IsAotCompatible=true` on runtime projects
- **API stability**: No public API changes
- **SG output stability**: Generated code must be semantically equivalent (Phase 1) or strictly better (Phase 2)
- **TDD**: Every change starts with a failing test (existing or new)

---

## Verification

After each step:
```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build
```

Full CI matrix (5 platforms × 5 architectures) on PR.
