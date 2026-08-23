# PicoSerDe

AOT-first, reflection-free serialization framework. Five formats, one
unified API. Source-generated `ref struct` readers/writers with zero heap
allocation on the hot path — deployable under NativeAOT and trimming where
many serialization libraries cannot run.

[![CI](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml/badge.svg)](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Modules

| Format | Package | Status | AOT | Readme |
|--------|---------|--------|:---:|-------|
| JSON | PicoJetson | ✅ Production | ✅ | [→](PicoJetson/README.md) |
| MessagePack | PicoMsgPack | ✅ Production | ✅ | [→](PicoMsgPack/README.md) |
| INI | PicoIni | ✅ Production | ✅ | [→](PicoIni/README.md) |
| TOML | PicoToml | ✅ Production | ✅ | [→](PicoToml/README.md) |
| YAML | PicoYaml | ✅ Production | ✅ | [→](PicoYaml/README.md) |

> PicoYaml is the **only AOT-compatible YAML library** on .NET.

---

## Test Coverage

**1106 tests** across all 6 modules, with cross-validation against 5 competitor libraries:

| Module | Tests | Competitor | Cross-Validation |
|--------|:-----:|:-----------|:----------------:|
| PicoJetson | 473 | System.Text.Json | ✅ bidirectional, all 19 property types |
| PicoToml | 120 | Tomlyn | ✅ bidirectional, 20 property types, NestedList via `[[key]]` |
| PicoYaml | 130 | YamlDotNet | ✅ bidirectional, 19 property types, DateOnly/TimeOnly conerters |
| PicoIni | 125 | Microsoft.Extensions.Configuration.Ini | ✅ bidirectional, 16 property types |
| PicoMsgPack | 145 | MessagePack-CSharp | ✅ map/array dual-format, 14 property types |
| PicoSerDe.Core | 42 | — | — |
| Integration (cross-format) | 71 | — | Ignore-condition matrix, anon types, round-trips |

> 91 of these are strictness/robustness regression tests added in the
> strict-deserialization hardening pass: wrong-typed input, trailing data,
> missing `required` members, malformed documents, chunked streaming, and
> comment handling all fail loudly instead of silently producing defaults.

## Performance Summary

Numbers below are **PicoSerDe on NativeAOT vs competitors on JIT** — the
competitors cannot run under NativeAOT at all. In a JIT environment,
mature reflection-based parsers may still be faster; PicoSerDe's
advantage is guaranteed deployability under trimming and self-contained
publishing, not peak JIT throughput.

Benchmarks: AOT self-contained, .NET 10, 100K iterations, win-x64.

| Module | vs Competitor | Avg Speedup | Competitor AOT? |
|--------|:------------:|:-----------:|:---:|
| PicoJetson | System.Text.Json | **1.35x** | ✅ |
| PicoMsgPack | MessagePack-CSharp | **1.40x** | ❌ |
| PicoIni | ini-parser | 0.12x | ❌ |
| PicoToml | Tommy | 0.30x | ❌ |
| PicoYaml | — | — | ❌ |

> JSON/MessagePack are faster than or competitive with JIT-based
> alternatives even in AOT mode. INI/TOML/YAML prioritize correct,
> reflection-free parsing over peak throughput — their JIT competitors
> benefit from years of runtime-level optimizations (cached keys,
> direct span writes, dynamic code gen) that are incompatible with
> NativeAOT. PicoSerDe is the **only** option that runs at all in a
> fully-trimmed, self-contained NativeAOT deployment for these formats.

---

## Design

```csharp
// One API across all formats
JsonSerializer.Serialize<T>(value)      // → byte[] via PicoJetson
MsgPackSerializer.Deserialize<T>(data)  // T ← byte[] via PicoMsgPack
IniSerializer.Serialize(config)         // → string via PicoIni
```

### Attribute-Driven Registration

PicoSerDe source generators discover types through **four independent pipelines**:

1. **Usage-driven** — calling `Serialize<T>()` or `Deserialize<T>()` triggers generation for `T`
2. **Generic attribute** — `[PicoSerializable]` marks a type for all referenced format modules
3. **Format-specific attribute** — `[PicoJsonSerializable]` / `[PicoIniSerializable]` / etc. marks a type for one format
4. **Shorthand attribute** — `[GenerateSerializer(typeof(T))]` for central registration

```csharp
// All referenced formats generate serializers
[PicoSerializable]
public class UserDto { public string Name { get; set; } }

// JSON only (PicoJetson)
[PicoJsonSerializable]
public class JsonOnlyDto { public string Label { get; set; } }

// Indirect — target type from any assembly
[PicoIniSerializable(typeof(ExternalLibrary.SharedDto))]
class Config { }

// Shorthand — equivalent to PicoSerializable(typeof(T))
[GenerateSerializer(typeof(UserDto))]
[GenerateSerializer(typeof(ProductDto))]
class PicoSerDeConfig { }
```

| Attribute | Scope | Defined in |
|-----------|-------|------------|
| `[PicoSerializable]` | All formats — direct or `typeof(T)` | `PicoSerDe.Core` |
| `[GenerateSerializer]` | Shorthand for `PicoSerializable(typeof(T))` | `PicoSerDe.Core` |
| `[PicoJsonSerializable]` | JSON only | `PicoJetson` |
| `[PicoIniSerializable]` | INI only | `PicoIni` |
| `[PicoTomlSerializable]` | TOML only | `PicoToml` |
| `[PicoMsgPackSerializable]` | MsgPack only | `PicoMsgPack` |
| `[PicoYamlSerializable]` | YAML only | `PicoYaml` |

No attributes are required for basic usage — calling `Serialize<T>()` automatically triggers generation.

## Key Features

### Strict Deserialization (fail-loud, STJ-compatible semantics)

Deserialization validates input shape and types instead of silently producing
default values:

- **Wrong-typed values throw** `FormatException` — `{"age":"abc"}` into an `int`
  property throws instead of yielding `0`
- **Top-level `null` returns `null`** for reference-type targets (STJ semantics);
  value-type targets throw
- **Trailing data after the document root throws**
- **Missing `required` members throw** — C# `required` properties are enforced at
  runtime across object, nested, streaming, and polymorphic paths
- **`PicoDocument.IsValid` performs full structural validation** — mismatched
  brackets, bare values in objects, missing property values, and multiple root
  values are all rejected (token-level checks are not enough)
- **Arrays**: `null` elements are allowed for reference-type elements and throw
  for value-type elements; wrong-typed elements throw
- **Comments** (`ReadCommentHandling.Skip`): malformed comment syntax (`/x`) and
  unterminated block comments throw instead of being silently swallowed

### Polymorphic Deserialization (Type Discriminator)

Base types declare derived types at compile time. Zero reflection, AOT-safe. Since v2026.3.0.

```csharp
[PicoSerializable]
[PicoDerivedType(typeof(MessageEntry), "message")]
[PicoDerivedType(typeof(CompactionEntry), "compaction")]
abstract class SessionEntry { }

class MessageEntry : SessionEntry { public string Content { get; set; } = string.Empty; }
class CompactionEntry : SessionEntry { public int From { get; set; } }

var json = """{"$type":"message","Content":"hello"}"""u8;
var result = JsonSerializer.Deserialize<SessionEntry>(json);
// result is MessageEntry at runtime
```

| Feature | Support |
|---------|---------|
| Serialization + Deserialization | ✅ v2026.3.0 |
| Streaming (PipeReader) | ✅ v2026.3.2 |
| Base class properties | ✅ v2026.3.3 |
| `[JsonConstructor]` on derived types | ✅ |
| Record derived types | ✅ v2026.3.23 |
| Complex/collection ctor params | ✅ v2026.3.24 |
| TOML / YAML poly support | ✅ v2026.3.24 |
| INI / MsgPack poly support | ✅ v2026.4.0 |

### DOM Layer (PicoDocument / PicoElement)

Schema-less JSON inspection without `System.Text.Json`. Zero-copy.

```csharp
var doc = PicoDocument.Parse("""{"name":"Alice","age":30}"""u8.ToArray());

var name = doc.RootElement["name"].GetString();         // "Alice"
var ok   = doc.RootElement.TryGetProperty("age", out _); // true
bool valid = PicoDocument.IsValid("{}"u8);               // true

// Numeric access
long big = doc.RootElement["count"].GetInt64();
double d = doc.RootElement["score"].GetDouble();
if (doc.RootElement["age"].TryGetInt32(out int age))
    Console.WriteLine(age);
```

### C# Records

`record` and `record struct` types are fully supported. Primary constructor auto-detected — no `[JsonConstructor]` needed. `init`-only properties work correctly.

### Top-Level Arrays

`Serialize<T[]>(...)` / `Deserialize<T[]>(...)` and streaming `DeserializeFromStreamAsync<T[]>(stream)` work directly.

### Three-Layer Test Structure

PicoJetson tests are split into Unit / Integration / Functional projects with clear boundaries.

> **No non-generic `Serialize(Type, object?)` overloads.** PicoSerDe is designed for AOT-first
> usage where all types are known at compile time. `SerRegistry<TFormat, T>` static fields (PicoSerDe.Core) are shared
> across assemblies and provide faster lookup than a `ConcurrentDictionary<Type, ...>`.
> Framework wrappers should call the generic API internally — the type's serializer is
> guaranteed to be registered via `ModuleInitializer` as long as the type was discovered
> by any pipeline (usage-driven, attribute, or shorthand).

```
┌──────────────────────────────────────────────┐
│                 User Code                     │
└──────────────────┬───────────────────────────┘
                   │  Static SerRegistry<TFormat, T>
┌──────────────────▼───────────────────────────┐
│           PicoSerDe.Core                      │
│  ISerializer<T>  │  IDeserializer<T>         │
│  SerRegistry     │  DesRegistry              │
│  TokenType       │  SimdHelpers (Vector128)  │
│  TextHelpers     │  SerializerExtensions     │
└────┬────────┬─────────┬─────────┬─────────┬──┘
     │        │         │         │         │
 PicoJetson  PicoIni  PicoMsgPack PicoToml PicoYaml
   ││       ││         ││        ││       ││
  .Gen     .Gen       .Gen      .Gen     .Gen
```

- **Dual-package**: each format → runtime library (net10.0) + source generator (netstandard2.0)
- **`ref struct`** readers/writers — stack-allocated, zero heap allocation on hot path
- **Static `SerRegistry<TFormat, T>`** — per-format registries in PicoSerDe.Core; JIT/AOT inlineable, no dictionary lookups
- **`file struct`** generated implementations — devirtualization without sealed class overhead
- **Ref struct serialization** — `ref struct` types are supported as serializable types across all 5 formats. Source-generator-generated static methods + delegate dispatch bypass the `ISerializer<T>` interface constraint.
- **`JsonOptions`** — runtime configuration (indentation, naming policy, ignore conditions, etc.) passed **explicitly per call** (no ambient ThreadStatic state; options thread through reader/writer instances and SG-generated code)
- **Polymorphic deserialization** — type discriminator dispatch via `[PicoDerivedType]`; serialization + deserialization + streaming (v2026.3.0); record types (v2026.3.23); TOML/YAML poly (v2026.3.24); INI/MsgPack poly (v2026.4.0)
- **Anonymous type serialization** — `Serialize(new { A = 1, B = "x" })` with nested types, collections, `PropertyNamingPolicy.CamelCase`, `DefaultIgnoreCondition.WhenWritingNull`, and `MaxDepth` enforcement. Works across all 5 formats via C# 12 interceptors + unsafe field access (serialization only, C# 12+, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`) (v2026.4.1)
- **`PicoDocument` / `PicoElement`** — zero-copy JSON DOM for schema-less inspection (v2026.3.4)
- **C# records** — primary constructor auto-detection, `init`-only support (v2026.3.3); poly+record (v2026.3.23); complex/collection ctor params (v2026.3.24)
- **Top-level arrays** — `Serialize<T[]>()` / `Deserialize<T[]>()` with streaming (v2026.3.2)

### PicoJetson JsonOptions

```csharp
// Compact (default) — optimal for data transfer
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model);

// Human-readable
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { Indented = true });

// CamelCase naming
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

// Skip null properties
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

// Allow NaN/Infinity
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
```

Available options:

| Option | Default | Description |
|--------|:-------:|-------------|
| `Indented` | `false` | Human-readable indented output |
| `MaxDepth` | `63` | Maximum nesting depth |
| `PropertyNamingPolicy` | `null` | Naming policy: `CamelCase`, `SnakeCaseLower`, `KebabCaseLower`, `PascalCase` |
| `DefaultIgnoreCondition` | `Never` | Skip null/default properties: `WhenWritingNull`, `WhenWritingDefault` |
| `NumberHandling` | `Strict` | Allow named floats: `AllowNamedFloatingPointLiterals` |
| `PropertyNameCaseInsensitive` | `true` | Property matching is case-insensitive by default; set `false` for exact case-sensitive matching |
| `AllowTrailingCommas` | `false` | Accept trailing commas in objects/arrays |
| `ReadCommentHandling` | `Disallow` | Skip `//` and `/* */` comments |
| `UnmappedMemberHandling` | `Skip` | Throw on unknown properties: `Disallow` |

#### Null handling across formats

Every format's options class (`JsonOptions`, `YamlOptions`, `TomlOptions`, `IniOptions`, `MsgPackOptions`) exposes `DefaultIgnoreCondition`, but what "writing a null" means depends on the wire format:

| Format | Default (`Never`) | `WhenWritingNull` |
|--------|-------------------|-------------------|
| JSON | `"key":null` written | omitted |
| MsgPack | `nil` written (map count adjusts automatically) | omitted |
| TOML / INI | omitted — these formats have no null literal | omitted |
| YAML | omitted — the reader has no null-literal support yet; writing `key:` would read back as a default value and break round-trip fidelity | omitted |

The matrix applies to every emit path — top-level members, nested objects, collection elements, nullable collections, and polymorphic dispatch — and is locked by cross-format regression tests (`IgnoreConditionMatrixTests`).

Per-property control is available via the cross-format `[PicoIgnore]` attribute (PicoSerDe.Core):

```csharp
[PicoIgnore]                                                  // stripped everywhere (write + read)
public string Internal { get; set; } = "";

[PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)] // omitted only when null, regardless of global options
public string? Note { get; set; }

[PicoIgnore(Condition = PicoIgnoreCondition.Never)]           // exempt from the global DefaultIgnoreCondition
public string? Pinned { get; set; }
```

Conditions affect serialization only — deserialization still maps conditional properties. Format-specific markers (`[JsonIgnore]`, `[YamlIgnore]`, …) remain single-format unconditional ignores.

#### Custom serializers for nested types

`Register` applies at the top level only. To also override `T` wherever it appears as a **nested** value (object property, list element, dictionary value), use `RegisterCustom` — available on JSON and MessagePack:

```csharp
JsonSerializer.RegisterCustom(new MySerializer(), new MyDeserializer());
// Outer { Foo Inner } now serializes Inner with MySerializer too.
// Deserialization override applies at the top level only.
```

---

## Shared Attribute Hierarchy

Per-format attributes (`[JsonIgnore]`, `[IniKey]`, ...) inherit shared PicoSerDe.Core bases
(`PicoIgnoreAttribute`, `PicoSerializableAttribute`, `PicoCamelCaseAttribute`,
`PicoConstructorAttribute`, `PicoDateTimeFormatAttribute`, `PicoConverterAttribute`) —
one implementation per concept, format-specific public names preserved. `IniKeyAttribute.Key`
is the canonical property (`Name` is an obsolete alias).

## Packages

| Package | NuGet |
|---------|:-----:|
| `PicoSerDe.Core` | [![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core) |
| `PicoJetson` / `.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoJetson)](https://www.nuget.org/packages/PicoJetson) |
| `PicoMsgPack` / `.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoMsgPack)](https://www.nuget.org/packages/PicoMsgPack) |
| `PicoIni` / `.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoIni)](https://www.nuget.org/packages/PicoIni) |
| `PicoToml` / `.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoToml)](https://www.nuget.org/packages/PicoToml) |
| `PicoYaml` / `.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoYaml)](https://www.nuget.org/packages/PicoYaml) |

---

## CI/CD

| Target | Runner |
|--------|--------|
| win-x64 | windows-latest |
| win-arm64 | windows-latest |
| linux-x64 | ubuntu-latest |
| linux-arm64 | ubuntu-24.04-arm |
| osx-arm64 | macos-latest |

Every push: build + test (1100+ tests) + 5 benchmarks smoke + 5 AOT sample publishes.
Release: `v*` tag → packs 11 packages in dependency order → NuGet.org.
Local feed: run `./scripts/release.ps1 -Version <ver>` **before** pushing the
tag — it runs the test suite, packs all 11 packages into `artifacts/nupkg`
(declared as the `local` NuGet source in `NuGet.config`), then tags and pushes.
Sibling PicoHex repos add the same folder path after nuget.org in their
`NuGet.config` to consume the new version instantly, bypassing nuget.org's
indexing window and 30-minute HTTP cache.

AOT tiers - declare `<AotOptimizationLevel>` per project (`minimal` default /
`aggressive` for samples/benchmarks). Implementations: `minimal` in
`Directory.Build.props` (PublishAot + TrimMode=full +
IlcTrimMetadata/IlcFoldIdenticalMethodBodies/IlcDisableReflection), `aggressive`
in `Directory.Build.targets` (adds size optimization, no debug info) - it must
live there so a csproj-level declaration is visible when it evaluates.
Libraries declare `IsAotCompatible`+`IsTrimmable`; only source generators
(`PicoXxx.Gen`, netstandard2.0) never AOT.

---

## Comparison

| | PicoSerDe | S.T.Json | YamlDotNet | VYaml | MsgPack-CS | Tommy |
|--|:---:|:---:|:---:|:---:|:---:|:---:|
| Formats | **5** | 1 | 1 | 1 | 1 | 1 |
| AOT | ✅ | ✅ | ❌ | ⚠️ | ❌ | ❌ |
| Zero-reflection | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Zero annotations | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| ref struct readers | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| SIMD | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| JSON DOM | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Polymorphic | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |

---

## License

MIT
