# PicoSerDe

**AOT-first, reflection-free serialization for .NET — five formats behind one API.** JSON,
MessagePack, TOML, YAML and INI all answer to the same `Serialize<T>` / `Deserialize<T>` shape, and
every serializer is generated at compile time as a `ref struct` reader/writer pair that allocates
nothing on the hot path. Pick it when you publish with `PublishAot` / `TrimMode=full` and your
payloads are typed POCOs — the case the other multi-format libraries in the
[comparison](#comparison) leave open.

[![CI](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml/badge.svg)](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Why PicoSerDe

Four claims, each with the check that backs it.

- **NativeAOT with `TrimMode=full` — five formats, typed POCOs, actually published and run.**
  The claim is narrower than "the parser runs": a typed round-trip through every one of the five
  formats is what the [capability table](#nativeaot-support-by-competitor-verified-not-a-performance-claim)
  records, and that table has exactly one row with ✅ in both columns. The check:
  [`Directory.Build.props:53`](Directory.Build.props) defaults `TrimMode` to `full`;
  the AOT benchmark arm opts in explicitly
  (`PicoMsgPack/benchmarks/PicoMsgPack.Benchmarks.Aot/PicoMsgPack.Benchmarks.Aot.csproj:6`) and is
  published as a native executable and run in that configuration — the protocol is in
  [`docs/perf/measurement.md`](docs/perf/measurement.md).
- **One API, five formats.** Change the format, keep the call:
  `Serialize<T>(value)` / `Deserialize<T>(ReadOnlySpan<byte>)` mean the same thing in every package,
  so a DTO is not re-annotated or re-learned per format. See [Design](#design).
- **No reflection, and no heap on the hot path.** The generator emits the reader and writer; the
  runtime types are `ref struct`s over spans
  ([`JsonReader.cs:27`](PicoJetson/src/PicoJetson/JsonReader.cs),
  [`JsonWriter.cs:3`](PicoJetson/src/PicoJetson/JsonWriter.cs)), which is why trimming cannot break
  a code path it never had to discover.
- **Faster than the incumbent in every format where an opponent exists — with one exception, stated
  in the same breath: under the runtime's default JIT, three of JSON's twelve scenarios lose.** One
  line per scenario, AOT against AOT and JIT against JIT, in one window. Fastest: YAML
  `Simple — SerializeToString` — **PicoSerDe 45.6× faster** (0.138 vs 6.293 µs; the harness prints
  that pair as 0.02×). Slowest: JSON `LargeFlat 50-fields — Deserialize` — **System.Text.Json
  1.33× faster** (2.654 vs 1.992 µs). Each format's scenarios, one line at a time, with the direction
  rule and the µs pairs: [Performance](#performance).

---

## Modules

| Format | Package | Status | AOT | Readme |
|--------|---------|--------|:---:|-------|
| JSON | PicoJetson | ✅ Production | ✅ | [→](PicoJetson/README.md) |
| MessagePack | PicoMsgPack | ✅ Production | ✅ | [→](PicoMsgPack/README.md) |
| INI | PicoIni | ✅ Production | ✅ | [→](PicoIni/README.md) |
| TOML | PicoToml | ✅ Production | ✅ | [→](PicoToml/README.md) |
| YAML | PicoYaml | ✅ Production | ✅ | [→](PicoYaml/README.md) |

> PicoYaml is the only YAML library in this comparison that maps **typed POCOs** under NativeAOT.
> YamlDotNet's DOM parser does run there; VYaml was not verified either way.

---

## Quick Start

```bash
dotnet add package PicoJetson     # PicoToml / PicoYaml / PicoIni / PicoMsgPack work the same way
```

```csharp
using System.Text;
using PicoJetson;

public record Person(string Name, int Age);

var json = JsonSerializer.Serialize(new Person("Alice", 30));
// {"Name":"Alice","Age":30}

var person = JsonSerializer.Deserialize<Person>(Encoding.UTF8.GetBytes(json));
// Person { Name = "Alice", Age = 30 }
```

No attributes, no reflection, no `JsonSerializerContext`: the serializer for `Person` is generated
at compile time from the call itself, so the same program runs under NativeAOT and trimming. Every
format has the same shape — `TomlSerializer`/`YamlSerializer`/`IniSerializer` return `string` from
`Serialize<T>` and read a `ReadOnlySpan<byte>` in `Deserialize<T>`, while `MsgPackSerializer` writes
`byte[]`. Each runtime package embeds its own generator: one `PackageReference` is the install.

---

## Performance

One row per scenario per execution mode, named the way the harness names it, saying **who is faster and
by how much**. The two µs columns are `ours vs theirs` (µs/op, the median of three rounds each), and
**the multiplier is that pair divided — larger by smaller — to one decimal**: `0.589 ÷ 0.421 = 1.399 →
1.4×`, read off the µs themselves, never by inverting the harness's two-decimal ratio and rounding
again. A row the opponent wins prints the harness's own two decimals — the same division one place
further, `2.654 ÷ 1.992 = 1.3323 → 1.33×`.

**Conversion note, stated once:** the harness prints its ratios as **ours ÷ theirs**, so its `0.55×` means we are the faster side
and its `1.33×` means we are slower; **this section quotes the reciprocal and puts the winner in words** — "PicoSerDe 1.8× faster" *is*
the harness's `0.55×` — so no reader has to invert anything, and **every row carries the µs pair its multiplier was divided from**: no
cell is empty for a published pair and no pair was invented for a row that has none. A ratio inside **~±10 %** of 1.00 is not a direction
claim: those rows are **level**, with the harness ratio shown so they cannot be read as a win. INI is the one table with no
division at all — its opponent is a caller-written DOM walk, not a library call, so only absolute cost is quoted there. Per-scenario
tables for this window, both arms' medians as printed and the kept rounds behind them: [2026-10-29 window
archive](docs/perf/measurement.md#per-scenario-tables-2026-10-29-window).

### JSON — System.Text.Json

**NativeAOT — ahead in 12 of 12** (row median 0.79× ours ÷ theirs, i.e. ≈1.3× faster):

| Scenario | PicoSerDe | Opponent | Faster by |
|---|---:|---:|---|
| Collection POCO — Deserialize | 3.193 | 5.790 | **PicoSerDe 1.8×** |
| Nested POCO — Serialize | 0.312 | 0.450 | **PicoSerDe 1.4×** |
| LargeString 1KB — Deserialize | 0.421 | 0.589 | **PicoSerDe 1.4×** |
| Simple POCO — Serialize | 0.123 | 0.166 | **PicoSerDe 1.3×** |
| Collection POCO — Serialize | 1.297 | 1.694 | **PicoSerDe 1.3×** |
| LargeString 1KB — Serialize | 0.382 | 0.486 | **PicoSerDe 1.3×** |
| Nested POCO — Deserialize | 1.003 | 1.267 | **PicoSerDe 1.3×** |
| Simple POCO — Deserialize | 0.269 | 0.307 | **PicoSerDe 1.1×** |
| Complex POCO — Serialize | 0.582 | 0.650 | level (0.90×, ±10%) |
| LargeFlat 50-fields — Serialize | 1.395 | 1.513 | level (0.92×, ±10%) |
| Complex POCO — Deserialize | 0.884 | 0.936 | level (0.94×, ±10%) |
| LargeFlat 50-fields — Deserialize | 3.356 | 3.418 | level (0.98×, ±10%) |

Both the object/member paths and the collection paths are ahead, and the largest single lead is the
collection read. The four level rows at the bottom are ahead but inside the band, so they are not
direction claims.

**JIT, runtime default — slower in 3 of 12** (row median 0.96×, itself level):

| Scenario | PicoSerDe | Opponent | Faster by |
|---|---:|---:|---|
| Nested POCO — Serialize | 0.187 | 0.366 | **PicoSerDe 2.0×** |
| Simple POCO — Serialize | 0.066 | 0.109 | **PicoSerDe 1.7×** |
| Collection POCO — Deserialize | 2.180 | 3.337 | **PicoSerDe 1.5×** |
| Collection POCO — Serialize | 0.874 | 1.294 | **PicoSerDe 1.5×** |
| Complex POCO — Serialize | 0.296 | 0.322 | level (0.92×, ±10%) |
| LargeString 1KB — Serialize | 0.188 | 0.196 | level (0.96×, ±10%) |
| LargeString 1KB — Deserialize | 0.311 | 0.322 | level (0.97×, ±10%) |
| Nested POCO — Deserialize | 0.780 | 0.787 | level (0.99×, ±10%) |
| Simple POCO — Deserialize | 0.154 | 0.154 | level (1.00×, ±10%) |
| LargeFlat 50-fields — Deserialize | 2.654 | 1.992 | **System.Text.Json 1.33×** |
| LargeFlat 50-fields — Serialize | 1.173 | 0.923 | **System.Text.Json 1.27×** |
| Complex POCO — Deserialize | 0.706 | 0.617 | **System.Text.Json 1.14×** |

The three losses sit at the bottom, and all three are the JSON object/member path — the part that scales
with property count — and all three flip once tiering is out of the picture (the whole column has no
loss then, see [JSON under JIT](#json-under-jit-and-why-the-default-is-not-neutral)).

### MessagePack — MessagePack-CSharp

**NativeAOT — ahead in 4 of 4**, every cell past the band (row median 0.72×, i.e. ≈1.4× faster). The AOT
benchmarks run their own model set — `Person` and `Metrics`, with no collection scenario, because
MessagePack-CSharp's collection formatters do not survive NativeAOT:

| Scenario | PicoSerDe | Opponent | Faster by |
|---|---:|---:|---|
| Metrics — Serialize | 0.077 | 0.129 | **PicoSerDe 1.7×** |
| Person — Serialize | 0.072 | 0.104 | **PicoSerDe 1.4×** |
| Person — Deserialize | 0.115 | 0.153 | **PicoSerDe 1.3×** |
| Metrics — Deserialize | 0.126 | 0.144 | **PicoSerDe 1.1×** |

Read side included: no row of this mode is level and none is lost.

**JIT — ahead in 7 of 8** (row median 0.78×, i.e. ≈1.3× faster):

| Scenario | PicoSerDe | Opponent | Faster by |
|---|---:|---:|---|
| Nested — Serialize | 0.103 | 0.231 | **PicoSerDe 2.2×** |
| Collection — Deserialize | 0.382 | 0.694 | **PicoSerDe 1.8×** |
| Collection — Serialize | 0.136 | 0.228 | **PicoSerDe 1.7×** |
| Simple — Deserialize | 0.076 | 0.100 | **PicoSerDe 1.3×** |
| Simple — Serialize | 0.055 | 0.069 | **PicoSerDe 1.3×** |
| Nested — Deserialize | 0.325 | 0.400 | **PicoSerDe 1.2×** |
| Complex — Serialize | 0.187 | 0.200 | level (0.93×, ±10%) |
| Complex — Deserialize | 0.335 | 0.299 | **MessagePack-CSharp 1.12×** |

The two collection rows are what the bulk-path commits bought — they used to be this page's worst
MessagePack losses — and the single loss is the complex read.

### TOML — Tomlyn 2.10.1

The one opponent that does the same job in both modes — one library call, same POCO in and out — and the
only format where the two modes are compared like for like.

**NativeAOT — ahead in 10 of 10** (0.10×–0.41×, median 0.21×):

| Scenario | PicoSerDe | Opponent | Faster by |
|---|---:|---:|---|
| Simple — Deserialize←string | 0.200 | 1.931 | **PicoSerDe 9.7×** |
| Simple — Deserialize (bytes) | 0.207 | 1.898 | **PicoSerDe 9.2×** |
| Medium — Deserialize (bytes) | 0.520 | 4.305 | **PicoSerDe 8.3×** |
| Nested — Deserialize (bytes) | 0.700 | 5.595 | **PicoSerDe 8.0×** |
| Large — Deserialize (bytes) | 3.040 | 17.906 | **PicoSerDe 5.9×** |
| Simple — Serialize (bytes) | 0.158 | 0.642 | **PicoSerDe 4.1×** |
| Simple — SerializeToString | 0.171 | 0.522 | **PicoSerDe 3.1×** |
| Medium — Serialize (bytes) | 0.466 | 1.383 | **PicoSerDe 3.0×** |
| Large — Serialize (bytes) | 2.280 | 5.666 | **PicoSerDe 2.5×** |
| Nested — Serialize (bytes) | 0.634 | 1.551 | **PicoSerDe 2.4×** |

**JIT — ahead in 10 of 10** (0.09×–0.35×, median 0.19×):

| Scenario | PicoSerDe | Opponent | Faster by |
|---|---:|---:|---|
| Simple — Deserialize←string | 0.146 | 1.661 | **PicoSerDe 11.4×** |
| Simple — Deserialize (bytes) | 0.142 | 1.540 | **PicoSerDe 10.8×** |
| Medium — Deserialize (bytes) | 0.345 | 3.344 | **PicoSerDe 9.7×** |
| Nested — Deserialize (bytes) | 0.506 | 4.632 | **PicoSerDe 9.2×** |
| Large — Deserialize (bytes) | 2.674 | 14.449 | **PicoSerDe 5.4×** |
| Simple — Serialize (bytes) | 0.095 | 0.479 | **PicoSerDe 5.0×** |
| Medium — Serialize (bytes) | 0.244 | 0.969 | **PicoSerDe 4.0×** |
| Large — Serialize (bytes) | 1.309 | 3.913 | **PicoSerDe 3.0×** |
| Nested — Serialize (bytes) | 0.341 | 0.978 | **PicoSerDe 2.9×** |
| Simple — SerializeToString | 0.115 | 0.324 | **PicoSerDe 2.8×** |

Every row in both modes is ahead, and the read side carries the wider margin — the cost of this format
is dominated by the opponent's parser, not by our writer.

### YAML — YamlDotNet 18.1.0

CoreCLR only. Under NativeAOT that layer does not work at all — typed `Deserialize<T>` throws
`MissingMethodException` and typed `Serialize` silently emits `{}` — so there is no AOT opponent and none
is invented. **JIT — ahead in 6 of 6** (0.02×–0.06×, median 0.04×):

| Scenario | PicoSerDe | Opponent | Faster by |
|---|---:|---:|---|
| Simple — SerializeToString | 0.138 | 6.293 | **PicoSerDe 45.6×** |
| Collection — SerializeToString | 1.514 | 60.867 | **PicoSerDe 40.2×** |
| Nested — SerializeToString | 0.552 | 16.288 | **PicoSerDe 29.5×** |
| Simple — Deserialize←string | 0.239 | 5.297 | **PicoSerDe 22.2×** |
| Nested — Deserialize←string | 0.913 | 15.539 | **PicoSerDe 17.0×** |
| Collection — Deserialize←string | 4.752 | 74.536 | **PicoSerDe 15.7×** |

Every row carries the division of its own pair, the top one included. That top row is the one whose kept
rounds disagree (see the spread note below): its three kept rounds read 0.135 / 0.138 / 0.159 µs on our
arm — a 1.18× swing — against 6.108 / 6.377 / 6.293 µs on YamlDotNet's, which is why its multiple is
quoted with that note attached rather than read down to a second decimal. The margin is wide on both the
write and the read side.

### INI — ini-parser 2.5.3

**No multiple is given here, and none is derived from the two numbers below.** ini-parser exposes no
typed API in any execution mode, and a caller-written DOM walk is not the same job as one library call,
so this format is absolute cost on a `NON-EQUIVALENT` table:

- smallest write row — **0.084 µs**, against 7.597 µs for the DOM walk
- largest read row — **2.250 µs**, against 22.018 µs for the DOM walk

### JSON under JIT, and why the default is not neutral

The published JIT column is measured the way a user runs it — tiered compilation with dynamic PGO on —
and that default is not neutral between two libraries: tiering is a re-compilation pipeline, and each
side converts it into throughput differently. So the same twelve scenarios were measured twice more in
the same window, one runtime knob at a time: **0.94×** median with `DOTNET_TieredPGO=0` (still 3 lost,
0.59×–1.23×) and **0.58×** median with `DOTNET_TieredCompilation=0` — **no losing scenario at all**
(0.32×–0.74×). Measured per arm rather than inferred, turning dynamic PGO off costs System.Text.Json a
median 1.14× (0.91×–1.36×) and costs us 1.13× (0.89×–1.40×), near enough symmetric to explain
essentially none of the gap; turning tiered compilation off costs System.Text.Json 1.97× (1.25×–2.29×)
and us 1.14× (0.90×–1.33×), and that asymmetry is the whole 0.58× → 0.96× on the row median. The three
losses read the same way: `LargeFlat 50-fields — Deserialize` 1.33× by default, 1.23× with PGO off,
0.73× with tiering off; `LargeFlat 50-fields — Serialize` 1.27× / 0.56×; `Complex POCO — Deserialize`
1.14× / 0.74×. So the published JIT lines are what a reader actually gets, and this paragraph says how
much of them is the tiering pipeline rather than the code.

Where a row's own three rounds disagree the spread is left in the record: `Simple — SerializeToString`
(YAML) could not use its second and third kept rounds because the YamlDotNet arm was refused unstable in
both, which is why its 45.6× is quoted with this note attached and not down to a second decimal.

**How a number gets here.** A **round** is one whole process; each round prints, per arm, the median
µs/op over **5 independent runs × ≥100k iterations each**, and every figure is the **median of three
rounds** taken inside **one** window — 2026-10-29, 17:49–18:24 local, AMD Ryzen 7 6800H, Windows 11,
.NET SDK 10.0.401, runtime 10.0.12 — behind an idle-machine gate that refuses any arm missing its
drift, spread or dispersion criteria. Each multiple above comes from those two medians, and **AOT and
JIT are never merged into one figure, one column or one multiple.** A µs written as `a → b` for our own
arm is a cross-window delta and carries the later window's ~16 % discount, which is why the multiple,
not the µs, is what this page publishes.

How each number is produced — the window ledger, the per-scenario detail, the JIT companions and the
publishing protocol: [`docs/perf/measurement.md`](docs/perf/measurement.md). What each round of
changes moved, and by how much: [`docs/perf/changelog.md`](docs/perf/changelog.md). The 2026-10-27
session's own record: [`docs/perf/measurement-2026-10-27.md`](docs/perf/measurement-2026-10-27.md).

### NativeAOT support, by competitor (verified, not a performance claim)

These are capability facts, not performance claims. The two columns are deliberately separate, because
"the library runs ahead of time" and "the library maps typed POCOs ahead of time" are different
questions and only the second one is what a serializer user cares about.

| Library | Runs under NativeAOT | Typed POCO round-trip under AOT |
|---------|:--------------------:|:-------------------------------:|
| System.Text.Json | ✅ with a source-generated `JsonSerializerContext` | ✅ |
| MessagePack-CSharp | ⚠️ only statically resolvable types — e.g. `string`, `int`, `bool`, `double`, `DateTime`; **no enum, no generic collection** | ⚠️ enums and generic collections throw `MissingMethodException` — it builds formatters with `MakeGenericType` + `Activator.CreateInstance` |
| ini-parser | ✅ DOM parsing works | ❌ reflection-based, no AOT mapper |
| Tommy | ✅ DOM parsing works | ❌ no typed mapper (only the DOM API) |
| YamlDotNet | ✅ DOM works | ❌ typed `Deserialize<T>` throws `MissingMethodException`; typed `Serialize` **silently emits `{}`** |
| **PicoSerDe** | ✅ | ✅ all five formats |

**PicoSerDe is the only AOT solution above that round-trips typed POCOs across all five formats** (JSON,
TOML, YAML, INI, MessagePack). That is a capability statement, not a throughput statement: it says what
runs at all, not what runs fastest.

> The [`Comparison`](#comparison) table further down scores `AOT` as the second column above
> — typed serialization works ahead of time — which is why ini-parser, Tommy, YamlDotNet and
> MessagePack-CSharp score ❌/⚠️ there even though their parsers themselves do run under AOT.
>
> That table's VYaml `⚠️` is a documentation claim (`[YamlObject]` + source generation),
> **not** something this repo verified — no VYaml publish/run was attempted here.

## Design

```csharp
// One API across all formats. The four text formats return string;
// MsgPack is binary and returns byte[].
JsonSerializer.Serialize<T>(value)                 // → string  (PicoJetson)
TomlSerializer.Serialize<T>(value)                 // → string  (PicoToml)
YamlSerializer.Serialize<T>(value)                 // → string  (PicoYaml)
IniSerializer.Serialize<T>(value)                  // → string  (PicoIni)
MsgPackSerializer.SerializeToUtf8Bytes<T>(value)   // → byte[]  (PicoMsgPack)
```

> `SerializeToUtf8Bytes` is the `byte[]` entry point (JSON has one too); the plain `Serialize` overloads of the text formats return `string`.

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

### Strict Deserialization (fail-loud, STJ-compatible semantics)

Deserialization validates input shape and types instead of silently producing default values:

- **Wrong-typed values throw** `FormatException` — `{"age":"abc"}` into an `int` property yields a
  throw, not `0`
- **Invalid string escapes throw** — RFC 8259 allows only `\" \\ \/ \b \f \n \r \t \uXXXX`; `\q` is
  rejected, while `\b` and `\f` decode correctly
- **Leading commas throw** — `,5` and `[,1]` are invalid JSON and are rejected
- **Trailing data after the document root throws**
- **Top-level `null` returns `null`** for reference-type targets (STJ semantics); value-type targets
  throw
- **Missing `required` members throw** — C# `required` properties are enforced at runtime across
  object, nested, streaming and polymorphic paths
- **`PicoDocument.IsValid` validates structure, not just tokens** — mismatched brackets, bare values
  in objects, missing property values and multiple root values are all rejected
- **Arrays**: `null` elements are allowed for reference-type elements and throw for value-type
  elements; wrong-typed elements throw
- **Comments** (`ReadCommentHandling.Skip`): malformed comment syntax (`/x`) and unterminated block
  comments throw instead of being swallowed
- **Streaming** (`DeserializeFromStreamAsync<T>`) shares the semantics — `UnmappedMemberHandling.Disallow`
  and top-level `null` behave exactly as on the direct path (both paths use one generated dispatch
  chain). Struct DTOs and propertyless types have no streaming delegate and throw
  `InvalidOperationException`
- **MsgPack rejects zero-length payloads and trailing bytes** with `FormatException`
- **INI parses list values strictly per element kind** — malformed numbers, booleans, dates and so on
  throw `FormatException` (never a silent `0`/`false`), and parsing/formatting is culture-invariant
- **INI enum values parse strictly** — unparseable enum text throws `FormatException` naming the
  member and offset instead of falling back to the default (`0`) member
- **TOML/YAML/INI unquoted empty values are `null`, not `""`** — `Key =` (TOML/INI) and `Key:` (YAML)
  assign `null` to nullable members; a quoted `""` is still the empty string
- **`float` keeps its own precision** — `JsonWriter.WriteNumber(float)` emits the shortest
  round-trippable single-precision text instead of widening to `double`

### Null Semantics

Null is not portable across the five formats: "write a null" and "read a null" are spelled out per format, and three states are kept apart.

| State | On the wire | Behaviour |
|-------|-------------|-----------|
| **null** | JSON `"key":null`; MsgPack `nil` (`0xC0`, map/array header adjusted) | written, and round-trips — the only two formats with a null *value* on the wire |
| **empty** | `""`, `[]`, `{}` | always a value, never a null — a quoted scalar is never null, so `"null"` is four characters |
| **missing** | the member is absent | not an error — the target keeps its default, the property initializer's value when one exists |

**Who supports null.** JSON and MsgPack write an explicit null and read it back. TOML, YAML and INI have no null value on the write side,
so the member is omitted and **a null does not round-trip** through them. On read all three accept an unquoted empty value as the null
literal (TOML/INI `Key =`, YAML `Key:`), plus `~` (YAML) and the unquoted word `null` in any case (all three): that assigns `null` to a
nullable target and throws `FormatException` for a non-nullable value type, never a silent `0` / `false` / `Guid.Empty`. The non-nullable
`string` column is the generators' NRT-lenient contract — TOML/YAML/INI assign `null!` to an unannotated `string`, while JSON/MsgPack
require a value token and reject the null.

Scope: scalar members only (string, numeric, temporal, enum, Guid and their nullable forms); collection, object and dictionary branches keep
their own empty-value semantics, and nullable *elements* belong to [Format limits](#format-limits-what-each-wire-format-cannot-express).
Per-format write/read rules: [PicoToml](PicoToml/README.md#null-handling), [PicoYaml](PicoYaml/README.md#null-handling),
[PicoIni](PicoIni/README.md#null-handling), [PicoJetson](PicoJetson/README.md#nullability-fidelity), [PicoMsgPack](PicoMsgPack/README.md#limits).
`WhenWritingNull` omits the member in every format — see [PicoJetson JsonOptions](#picojetson-jsonoptions).

### Polymorphic Deserialization (Type Discriminator)

Base types declare derived types at compile time. Zero reflection, AOT-safe.

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
| Serialization + Deserialization | ✅ |
| Streaming (PipeReader) | ✅ |
| Base class properties | ✅ |
| `[JsonConstructor]` on derived types | ✅ |
| Record derived types | ✅ |
| Complex/collection ctor params | ✅ |
| TOML / YAML poly support | ✅ |
| INI / MsgPack poly support | ✅ |

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

### Small shapes: records, top-level arrays, nullable elements

- **Records** — `record` / `record struct` primary constructors are auto-detected, so no `[JsonConstructor]` or `[PicoConstructor]` is needed,
  and `init`-only properties work; this holds in every format (see [Arrays and Dictionaries](#arrays-and-dictionaries)).
- **Top-level arrays** — `Serialize<T[]>(...)` / `Deserialize<T[]>(...)` and streaming `DeserializeFromStreamAsync<T[]>(stream)` work directly.
- **Nullable elements** — `List<int?>`, `List<string?>`, `List<TObject?>`, `Dictionary<string, TObject?>` and nested lists of objects round-trip
  with their null elements in JSON and MsgPack; YAML writes the null literal for a nullable leaf, TOML and INI drop it with `PICOSERDE004` —
  the [Format limits](#format-limits-what-each-wire-format-cannot-express) matrix and its notes are the authority.

### Arrays and Dictionaries

Records (`record`/`record struct` primary constructors) work in every format — JSON, MsgPack, TOML,
YAML and INI — without a format-specific constructor attribute. Field-level object arrays
(`TObject[]`, `List<TObject[]>`, `TObject[][]`) round-trip in JSON, MsgPack, TOML and YAML (INI drops
object collections because its format is flat — see
[Format limits](#format-limits-what-each-wire-format-cannot-express) for which drops surface as a
build warning). An object sequence *inside* a nested object round-trips in YAML too (block sequences;
see [PicoYaml → Limits](PicoYaml/README.md#limits)). TOML keeps its documented loud "nested tables
deeper than one level" error, and TOML arrays of tables have no chunk-resume strategy, so their
streaming delegate is not registered — `DeserializeFromStreamAsync` then buffers the stream and uses
the synchronous path (correct, just not incremental). Dictionaries accept scalar, object and
nested-dictionary values; a **collection** dict value (`Dictionary<string, List<T>>`) is dropped with
`PICOSERDE004` instead of generating broken code.

### Format limits: what each wire format cannot express

Not every C# shape has a wire representation in every format. The matrix below is the boundary of
the five generated serializers, and every ❌ is enforced at **generation time**, never at runtime: a
member the format cannot express is left out of the generated serializer and the build reports
`PICOSERDE004` — `Member 'X.Y' cannot be represented by this format — it will be ignored` — while a
self-referencing member reports `PICOSERDE003` — `Member 'X.Y' is recursive/self-referencing and
cannot be represented by this format — it will be ignored`. The rest of the document is written and
read as usual, so the drop shows up in the build log instead of surfacing later as a mysteriously
empty property.

| Shape (C#) | JSON | MsgPack | TOML | YAML | INI |
|---|---|:---:|---|---|---|
| `List<Dto>` — list of objects | ✅ | ✅ | ✅ `[[Table]]` | ✅ block sequence of mappings | ❌ see ¹ |
| `List<List<int>>` — nested primitive list | ✅ | ✅ | ✅ `G = [[1, 2], [3]]` | ✅ nested block sequence | ❌ `PICOSERDE004` |
| `List<List<Dto>>` — list of lists of objects | ✅ | ✅ | ✅ `G = [[{ Name = "a" }]]` | ✅ nested block sequence | ❌ `PICOSERDE004` |
| `List<List<int?>>` — nullable leaf in a nested list | ✅ | ✅ | ❌ `PICOSERDE004` | ✅ `- null` | ❌ `PICOSERDE004` |
| Recursive member (`Node? Child`) | ✅ | ✅ | ✅ nested tables (`[Child]`) ³ | ✅ nested mappings ³ | ❌ `PICOSERDE003` ³ |

✅ means the generated code both writes and reads the shape, and each ✅ shape is exercised by a
round-trip test in the suite. Both diagnostics are `Warning`-severity and created with
`Location.None`, so `#pragma warning disable` cannot silence them — suppress by id in `NoWarn` only if
you accept the data loss.

**How to confirm, in one command** (the default build prints them):

```bash
dotnet build 2>&1 | grep PICOSERDE
# PowerShell: dotnet build 2>&1 | Select-String PICOSERDE
```

¹ **INI object lists.** INI has no array-of-tables syntax, so a `List<SomeDto>` **member** cannot be
represented and is dropped as `PICOSERDE004` like every other unrepresentable shape. A **top-level**
`List<SomeDto>` does round-trip: each item becomes a numbered section (`[0]`, `[1]`, …). For an object
list *inside* a DTO use TOML, YAML or JSON.

² **YAML nullable leaves.** YAML can express a null (`null`, `Null`, `NULL` or `~`), so a nullable
leaf is written as the null literal — `List<int?>`, `List<List<int?>>`, `List<string?>`, `List<Dto?>`
and `Dictionary<string, int?>` all round-trip, with "null" (a value), "empty" (an empty collection) and
"missing" (no member at all) kept apart. TOML has no null literal at all, which is why the same shapes
are dropped there; the generator diagnostic, not this table, is the authority for your build.

³ **Recursion.** The row above is the self-referencing *member* (`Node? Child`): it round-trips in
every format except INI, which drops it with `PICOSERDE003` while the type's other members still
round-trip — a recursive DTO therefore still serializes "partially" there. JSON and MsgPack keep it too
(cycle-safe extraction + seeded helpers, including on polymorphic bases) — see
[Streaming](#streaming-incremental-chunked). Two further *positions* a cycle can sit in are **not**
covered by the ✅: a recursive collection **element** (`List<Node>` **on `Node`**) and a recursive
**dict value** (`Dictionary<string, Node>` on `Node`) are closed in JSON and MsgPack only — TOML and
YAML still drop those members with `PICOSERDE003`, reported as a debt (`⏸`, reopen: the format can
carry one, the emitter has no route for a table inside an array yet).

Two shapes sit outside this matrix. A **collection dictionary value** (`Dictionary<string, List<T>>`;
nested dictionaries are fine) is dropped in **every** format with `PICOSERDE004`, while a **top-level
scalar** target is refused outright — `Serialize<Guid>()`, `Deserialize<int?>()` and friends throw
`InvalidOperationException` ("no serializer registered").

The wire shape behind each ✅ is documented per format: [PicoToml](PicoToml/README.md#limits)
(`[[Table]]`, `G = [[1, 2], [3]]`), [PicoYaml](PicoYaml/README.md#limits) (block sequences, `- []` vs
`- "[]"`), [PicoIni](PicoIni/README.md#limits), [PicoJetson](PicoJetson/README.md#limits),
[PicoMsgPack](PicoMsgPack/README.md#limits).

### Streaming (incremental, chunked)

`DeserializeFromStreamAsync<T>(stream, options, ct)` parses a stream incrementally: for JSON/TOML/YAML/INI the consumed prefix is released as tokens
complete and only the current token/member window is retained, so the document is never buffered wholesale before parsing. The generated delegate
returns a `ReadStatus` — `Success` (the value is complete), `NeedMoreData` (the chunk ends inside a token/section, so the runner refills and resumes
from the reader's mark) or `EndOfInput` (no value in the document — the runner throws `FormatException`) — and an **empty stream** matches the
synchronous path: TOML/YAML/INI read an empty object, JSON throws.

```csharp
await using var stream = File.OpenRead("config.toml");
var config = await TomlSerializer.DeserializeFromStreamAsync<ServerConfig>(stream);
```

**MsgPack is the exception — its stream path buffers**: each read is appended to an accumulator and the whole buffer is re-parsed from the beginning
until the value is complete (correct, resumes cleanly, but O(n²) in the number of chunks — see [PicoMsgPack → Streaming](PicoMsgPack/README.md#streaming)).
The text formats take options here (`JsonOptions`/`TomlOptions`/`YamlOptions`/`IniOptions`); MsgPack has a `MsgPackOptions` type but its streaming
overload is `(stream, ct)` only.

**Recursive DTOs** — JSON and MsgPack support self- and mutually-referencing types (cycle-safe extraction + seeded helpers, recursive members on
polymorphic bases included) and fail loudly on data-level cycles instead of overflowing the stack; TOML/YAML/INI skip the member with `PICOSERDE003`,
never silently. **Polymorphic hierarchies** serialize a concrete base instance with a synthesized, collision-safe discriminator, so bases round-trip as
the base while derived instances keep theirs *and* their nested object/dict/collection members in JSON, MsgPack, TOML and YAML (INI ignores nested
members — its format is flat).

**Diagnostics** (warnings): `PICOSERDE001` a property type the generator could not resolve was ignored; `PICOSERDE002` two distinct types produced
the same generated file name (internal helper names get a stable hashed `UniqueName` suffix); `PICOSERDE003` a recursive member was skipped by a
section-based format; `PICOSERDE004` a member shape the format cannot represent (nested list, nullable value-type element, null object element,
collection dictionary value) was skipped; `PICOSERDE005` an anonymous-type collection member was skipped by a writer that cannot emit one (JSON is
the only writer that can). The matrix behind `003`/`004` is [Format limits](#format-limits-what-each-wire-format-cannot-express); the chunked
reader itself (dual-mode `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>` / `PipeReader`, chunk boundaries included) is
[PicoJetson → Features](PicoJetson/README.md#features).

**Custom/advanced registration** (scripts or hand-written serializers): `JsonSerializer.RegisterStreaming<T>(StreamingFunc<JsonReader, T> func)` with
`where T : notnull`, and `HasStreamingDelegate<T>()` reports whether a delegate is registered; the generator emits that registration in a
`ModuleInitializer` for every discovered type.

> Repository architecture, the feature bullet list that went with it, the three-layer test split and the per-module cross-validation: [`docs/testing-and-ci.md`](docs/testing-and-ci.md).

### PicoJetson JsonOptions

```csharp
byte[] compact = JsonSerializer.SerializeToUtf8Bytes(model);                              // compact (default)
byte[] pretty  = JsonSerializer.SerializeToUtf8Bytes(model, new JsonOptions { Indented = true });
byte[] camel   = JsonSerializer.SerializeToUtf8Bytes(model, new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
```

Nine options in all — `Indented`, `MaxDepth`, `PropertyNamingPolicy`, `DefaultIgnoreCondition`, `NumberHandling`, `AllowTrailingCommas`, `ReadCommentHandling`, `UnmappedMemberHandling`,
`PropertyNameCaseInsensitive` — with defaults and the `WhenWritingNull` / trailing-comma examples in [PicoJetson → Options](PicoJetson/README.md#options).

`DefaultIgnoreCondition` exists on every options class, but what "writing a null" means is fixed by the wire format ([Null Semantics](#null-semantics)):
JSON and MsgPack read it from their options, INI/TOML/YAML accept it and always omit. It applies to every emit path — nested objects, collection
elements, nullable collections, polymorphic dispatch — and is locked by `IgnoreConditionMatrixTests`. `Indented = true` is honored by all five
writers — YAML indents sequence items under their key, TOML table contents, INI section contents; the compact default is unchanged.

`Register` is top-level only; `JsonSerializer.RegisterCustom(new MySerializer(), new MyDeserializer())` also overrides `T` wherever it appears
as a **nested** value (property, list element, dict value) on JSON and MessagePack — the deserialization override stays top-level. Per-property
control is the cross-format `[PicoIgnore]` (see [Shared Attribute Hierarchy](#shared-attribute-hierarchy)).

---

## Shared Attribute Hierarchy

Per-format attributes (`[JsonIgnore]`, `[IniKey]`, ...) inherit shared PicoSerDe.Core bases (`PicoIgnoreAttribute`, `PicoSerializableAttribute`,
`PicoCamelCaseAttribute`, `PicoConstructorAttribute`, `PicoDateTimeFormatAttribute`, `PicoConverterAttribute`) — one implementation per concept,
format-specific public names preserved. `IniKeyAttribute.Key` is the canonical property (`Name` is an obsolete alias).

Per-property control is the cross-format `[PicoIgnore]` (PicoSerDe.Core): bare `[PicoIgnore]` strips a member everywhere (write and read),
`[PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)]` omits it only when null regardless of the global options, and `Condition = Never`
exempts it from the global `DefaultIgnoreCondition`. Conditions affect serialization only — deserialization still maps the member — and
format-specific markers (`[JsonIgnore]`, `[YamlIgnore]`, …) remain single-format unconditional ignores.

---

## Packages

| Package | NuGet |
|---------|:-----:|
| `PicoSerDe.Core` | [![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core) |
| `PicoJetson` | [![NuGet](https://img.shields.io/nuget/v/PicoJetson)](https://www.nuget.org/packages/PicoJetson) |
| `PicoMsgPack` | [![NuGet](https://img.shields.io/nuget/v/PicoMsgPack)](https://www.nuget.org/packages/PicoMsgPack) |
| `PicoIni` | [![NuGet](https://img.shields.io/nuget/v/PicoIni)](https://www.nuget.org/packages/PicoIni) |
| `PicoToml` | [![NuGet](https://img.shields.io/nuget/v/PicoToml)](https://www.nuget.org/packages/PicoToml) |
| `PicoYaml` | [![NuGet](https://img.shields.io/nuget/v/PicoYaml)](https://www.nuget.org/packages/PicoYaml) |

> Each runtime package embeds its generator (`analyzers/dotnet/cs`); the standalone `*.Gen` packages are legacy/optional — installing both would load a duplicate analyzer.

> CI matrix, AOT tiers and the release pipeline: [`docs/testing-and-ci.md`](docs/testing-and-ci.md).

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