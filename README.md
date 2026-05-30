# PicoSerDe

High-performance, AOT-first serialization framework for the PicoHex ecosystem. Zero-reflection source generation with `ref struct` readers/writers for zero heap allocation.

[![CI](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml/badge.svg)](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Format Support

| Format | Library | Status | AOT | Key Features |
|--------|---------|--------|:---:|--------------|
| JSON | PicoJetson | ✅ Production | ✅ | RFC 8259, SIMD-accelerated, Unicode escapes |
| MessagePack | PicoMsgPack | ✅ Production | ✅ | fixint/fixstr/fixmap, extension types, binary |
| INI | PicoIni | 🟡 Beta | ✅ | Sections, comments, quoting, case-insensitive |
| TOML | PicoToml | 🟡 Beta | ✅ | Tables, arrays, inline tables, multi-line strings |
| YAML | PicoYaml | 🟡 Beta | ✅ **only AOT YAML library** | Indentation-based, flow style, anchors |

---

## Why PicoSerDe

| | PicoSerDe | System.Text.Json | MessagePack-CSharp | YamlDotNet | Tommy |
|--|:---:|:---:|:---:|:---:|:---:|
| **AOT-compatible** | ✅ | ✅ | ❌ | ❌ | ❌ |
| **Zero reflection** | ✅ SG | ✅ SG | ❌ | ❌ | ❌ |
| **Zero heap alloc (hot path)** | ✅ | ❌ | ❌ | ❌ | ❌ |
| **Zero attributes required** | ✅ | ✅ | ❌ | ✅ | ✅ |
| **Multi-format** | ✅ 5 formats | ❌ JSON only | ❌ MsgPack only | ❌ YAML only | ❌ TOML only |
| **SIMD-accelerated** | ✅ Vector128 | ❌ | ❌ | ❌ | ❌ |
| **Unified token layer** | ✅ TokenType | ❌ | ❌ | ❌ | ❌ |
| **CI coverage** | 5 OS × 2 arch | varies | varies | varies | varies |

---

## Performance

Benchmarks run as NativeAOT self-contained executables on .NET 10, 100K iterations, win-x64.

### JSON — PicoJetson vs System.Text.Json (AOT)

| Scenario | PicoJetson | STJ | Ratio |
|----------|:-------:|:---:|:-----:|
| Simple Serialize | 0.18μs | 0.23μs | **1.29x** |
| Complex Serialize | 0.75μs | 1.19μs | **1.59x** |
| Nested Serialize | 0.45μs | 0.85μs | **1.89x** |
| Collection Serialize | 1.57μs | 3.58μs | **2.28x** |
| Complex Deserialize | 0.86μs | 1.23μs | **1.43x** |

### MessagePack — PicoMsgPack vs MessagePack-CSharp

| Scenario | PicoMsgPack | MPC | Ratio |
|----------|:----------:|:---:|:-----:|
| Complex Serialize | 0.3μs | 0.6μs | **2.16x** |
| Complex Deserialize | 0.5μs | 0.6μs | **1.24x** |
| Collection Serialize | 0.3μs | 0.5μs | **1.67x** |
| Collection Deserialize | 0.8μs | 1.3μs | **1.67x** |

### INI — PicoIni vs ini-parser (AOT)

| Scenario | PicoIni | IFP | Ratio |
|----------|:------:|:---:|:-----:|
| SerializeToString | 1.0μs | 0.1μs | 0.14x |
| Deserialize←string | 14.0μs | 0.2μs | 0.01x |

> INI/TOML/YAML performance is constrained by `Encoding.UTF8.GetString()` for string properties. These competitors use reflection-based dynamic code generation unavailable in AOT.

---

## Quick Start

### JSON

```bash
dotnet add package PicoJetson
dotnet add package PicoJetson.Gen
```

```csharp
using PicoJetson;

public record Person(string Name, int Age);

var json = JsonSerializer.Serialize(new Person("Alice", 30));
// → {"Name":"Alice","Age":30}

var person = JsonSerializer.Deserialize<Person>(Encoding.UTF8.GetBytes(json));
// → Person { Name = "Alice", Age = 30 }
```

### MessagePack

```bash
dotnet add package PicoMsgPack
dotnet add package PicoMsgPack.Gen
```

```csharp
using PicoMsgPack;

public class User
{
    [MsgPackKey(0)] public string Name { get; set; } = "";
    [MsgPackKey(1)] public int Age { get; set; }
}

var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new User { Name = "Alice", Age = 30 });
var user = MsgPackSerializer.Deserialize<User>(bytes);
```

### INI

```bash
dotnet add package PicoIni
dotnet add package PicoIni.Gen
```

```csharp
using PicoIni;

public class AppConfig { public string Title { get; set; } = ""; public ServerConfig Server { get; set; } = new(); }
public class ServerConfig { public string Host { get; set; } = ""; public int Port { get; set; } }

var config = new AppConfig { Title = "MyApp", Server = new() { Host = "localhost", Port = 8080 } };
var ini = IniSerializer.Serialize(config);
// Title = MyApp
// [Server]
// Host = localhost
// Port = 8080
```

### TOML

```bash
dotnet add package PicoToml
dotnet add package PicoToml.Gen
```

### YAML

```bash
dotnet add package PicoYaml
dotnet add package PicoYaml.Gen
```

---

## Architecture

```
┌──────────────────────────────────────────────┐
│                 User Code                     │
│   JsonSerializer.Serialize<T>(value)          │
│   MsgPackSerializer.Deserialize<T>(data)      │
└──────────────────┬───────────────────────────┘
                   │  Static Cache<T>
┌──────────────────▼───────────────────────────┐
│           PicoSerDe.Core (shared infra)       │
│  ISerializer<T> │ IDeserializer<T>           │
│  TokenType      │ SimdHelpers                 │
│  TextHelpers    │ SerializerExtensions        │
└────┬────────┬─────────┬─────────┬─────────┬──┘
     │        │         │         │         │
 PicoJetson  PicoIni  PicoMsgPack PicoToml PicoYaml
   ││       ││         ││        ││       ││
  .Gen     .Gen       .Gen      .Gen     .Gen   ← 5 source generators
```

Each format ships as two NuGet packages: a **runtime library** (net10.0) and a **source generator** (netstandard2.0). The source generator produces compile-time `ISerializer<T>` / `IDeserializer<T>` implementations as `file struct`s, registered via `[ModuleInitializer]`.

---

## Design Philosophy

### 克制 (Restraint)
- Zero attributes required. Defaults map property names to serialized keys.
- Single `TokenType` enum across all formats — learn once, use everywhere.

### 专注 (Focus)
- `ref struct` readers/writers ensure zero heap allocation on the hot path.
- SIMD-accelerated whitespace and backslash scanning (Vector128).
- `GetStringRaw()` returns `ReadOnlySpan<byte>` pointing into source buffer.

### 优雅 (Elegance)
- `Cache<T>` with static generic fields — no dictionary lookup, JIT/AOT inlineable.
- `file struct` implementations — devirtualization without sealed class overhead.
- M×N nested type deduplication in source generators.

### 高效 (Efficiency)
- ThreadStatic pooled `ArrayBufferWriter<byte>` via `RentWriter()`.
- Stackalloc-based UTF-8 encoding for strings ≤256 bytes in writers.
- Inline fast path for `int[]` deserialization in JSON reader.

---

## Low-Level Reader/Writer API

All formats expose `ref struct` readers and writers for zero-allocation streaming:

```csharp
var reader = new JsonReader(Encoding.UTF8.GetBytes(json));
while (reader.Read())
{
    switch (reader.TokenType)
    {
        case TokenType.PropertyName:
            var name = reader.GetStringRaw(); // zero-copy span
            break;
        case TokenType.String:
            var value = reader.GetStringRaw();
            break;
        case TokenType.Int32:
            reader.TryGetInt32(out var n);
            break;
    }
}
```

---

## Packages

| Package | NuGet | Description |
|---------|:-----:|-------------|
| `PicoSerDe.Core` | [![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core) | Shared contracts, utilities, SIMD helpers |
| `PicoJetson` | [![NuGet](https://img.shields.io/nuget/v/PicoJetson)](https://www.nuget.org/packages/PicoJetson) | JSON serializer |
| `PicoJetson.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoJetson.Gen)](https://www.nuget.org/packages/PicoJetson.Gen) | JSON source generator |
| `PicoMsgPack` | [![NuGet](https://img.shields.io/nuget/v/PicoMsgPack)](https://www.nuget.org/packages/PicoMsgPack) | MessagePack serializer |
| `PicoMsgPack.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoMsgPack.Gen)](https://www.nuget.org/packages/PicoMsgPack.Gen) | MessagePack source generator |
| `PicoIni` | [![NuGet](https://img.shields.io/nuget/v/PicoIni)](https://www.nuget.org/packages/PicoIni) | INI serializer |
| `PicoIni.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoIni.Gen)](https://www.nuget.org/packages/PicoIni.Gen) | INI source generator |
| `PicoToml` | [![NuGet](https://img.shields.io/nuget/v/PicoToml)](https://www.nuget.org/packages/PicoToml) | TOML serializer |
| `PicoToml.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoToml.Gen)](https://www.nuget.org/packages/PicoToml.Gen) | TOML source generator |
| `PicoYaml` | [![NuGet](https://img.shields.io/nuget/v/PicoYaml)](https://www.nuget.org/packages/PicoYaml) | YAML serializer (only AOT-compatible YAML library) |
| `PicoYaml.Gen` | [![NuGet](https://img.shields.io/nuget/v/PicoYaml.Gen)](https://www.nuget.org/packages/PicoYaml.Gen) | YAML source generator |

---

## CI/CD

5-platform matrix + AOT validation on every push:

| Target | Runner | AOT Validated |
|--------|--------|:---:|
| win-x64 | windows-latest | ✅ |
| win-arm64 | windows-11-arm | ✅ |
| linux-x64 | ubuntu-latest | ✅ |
| linux-arm64 | ubuntu-24.04-arm | ✅ |
| osx-arm64 | macos-latest | ✅ |

Release pipeline triggers on `v*` tags, packs all 11 packages in dependency order, and publishes to NuGet.org.

---

## Project Structure

```
PicoSerDe/
├── PicoSerDe.Core/         # Shared infrastructure
│   ├── src/                # ISerializer<T>, IDeserializer<T>, TokenType, SimdHelpers, TextHelpers
│   └── tests/
├── PicoJetson/               # JSON (src, gen, tests, benchmarks, samples)
├── PicoMsgPack/            # MessagePack (src, gen, tests, benchmarks, samples)
├── PicoIni/                # INI (src, gen, tests, benchmarks, samples)
├── PicoToml/               # TOML (src, gen, tests, benchmarks, samples)
├── PicoYaml/               # YAML (src, gen, tests, benchmarks, samples)
├── shared/                 # Shared utilities (TypeKindResolver, BenchModels, IsExternalInit)
└── .github/workflows/      # CI (build+test+benchmark+AOT) + Release
```

## Comparison

| | PicoSerDe | System.Text.Json | YamlDotNet | VYaml | MessagePack-CSharp | Tommy |
|--|:---:|:---:|:---:|:---:|:---:|:---:|
| Formats | 5 | 1 | 1 | 1 | 1 | 1 |
| AOT | ✅ | ✅ | ❌ | ⚠️¹ | ❌ | ❌ |
| Zero-reflection | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Zero-attrs default | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| ref struct readers | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| SIMD | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Unified token layer | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |

> ¹ VYaml has source generator support but requires `[YamlObject]` attributes on every type.

---

## License

MIT
