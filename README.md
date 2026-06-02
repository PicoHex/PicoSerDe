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
| INI | PicoIni | 🟡 Beta | ✅ | [→](PicoIni/README.md) |
| TOML | PicoToml | 🟡 Beta | ✅ | [→](PicoToml/README.md) |
| YAML | PicoYaml | 🟡 Beta | ✅ | [→](PicoYaml/README.md) |

> PicoYaml is the **only AOT-compatible YAML library** on .NET.

---

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

```
┌──────────────────────────────────────────────┐
│                 User Code                     │
└──────────────────┬───────────────────────────┘
                   │  Static Cache<T>
┌──────────────────▼───────────────────────────┐
│           PicoSerDe.Core                      │
│  ISerializer<T>  │  IDeserializer<T>         │
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
- **Static `Cache<T>`** — JIT/AOT inlineable, no dictionary lookups
- **`file struct`** generated implementations — devirtualization without sealed class overhead

---

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

Every push: build + test (369 tests) + 5 benchmarks smoke + 5 AOT sample publishes.
Release: `v*` tag → packs 11 packages in dependency order → NuGet.org.

---

## Comparison

| | PicoSerDe | S.T.Json | YamlDotNet | VYaml | MsgPack-CS | Tommy |
|--|:---:|:---:|:---:|:---:|:---:|:---:|
| Formats | **5** | 1 | 1 | 1 | 1 | 1 |
| AOT | ✅ | ✅ | ❌ | ⚠️ | ❌ | ❌ |
| Zero-reflection | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Zero annotations | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| ref struct | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| SIMD | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |

---

## License

MIT
