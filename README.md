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
- **Faster than the incumbent in every format where an opponent exists.** Medians below, measured
  AOT against AOT and JIT against JIT in one window; JSON is the one row with a losing column, and
  the footnote says so.

**Direction:** `×` is **ours ÷ theirs**, the direction the harness prints, so **`< 1.00` means
PicoSerDe is faster** and **`> 1.00` means it is slower**.

| Format | Opponent | Median `×` | Scale of the win (verbatim from [Performance](#performance)) |
| --- | --- | --- | --- |
| JSON | System.Text.Json | AOT **0.79×** · JIT **0.96×** | AOT ahead in **12 of 12** — **0.55×–0.98×**; JIT slower in **3 of 12** — **0.51×–1.33×** ¹ |
| MessagePack | MessagePack-CSharp | AOT **0.72×** · JIT **0.78×** | AOT ahead in **4 of 4** — **0.60×–0.88×**; JIT ahead in **7 of 8** — **0.45×–1.12×** |
| TOML | Tomlyn 2.10.1 | AOT **0.21×** · JIT **0.19×** | AOT faster in **10 of 10** — **0.10×–0.41×**, between **2.4× and 10×** faster; JIT faster in **10 of 10** — **0.09×–0.35×**, between **2.9× and 11×** faster |
| YAML | YamlDotNet 18.1.0 | JIT **0.04×** | JIT faster in **6 of 6** — **0.02×–0.06×**, **17×–50×** faster; AOT — no opponent exists |

¹ **The losing column, stated plainly:** on the runtime default (tiered JIT + dynamic PGO) JSON is
slower in **3 of 12** scenarios — **0.51×–1.33×**, median **0.96×** — while its AOT column wins all
twelve. Multiples are quoted only where the full table states one; keys are never computed, rounded or
re-windowed here, and ratios inside ~±10 % of 1.00 are not a direction claim either way.

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

**Direction: every ratio below is `×` = ours ÷ theirs** — the direction the harness itself prints, so
**above 1.00 means PicoSerDe is slower** and **below 1.00 means PicoSerDe is faster**. No reciprocal is
ever taken: the direction above is the only one printed.

A ratio inside ~±10 % of 1.00 is not a direction claim in either direction.

| Format | Opponent | AOT vs AOT | JIT vs JIT |
| --- | --- | --- | --- |
| JSON | System.Text.Json | ahead in **12 of 12** — **0.55×–0.98×**, median **0.79×** | slower in **3 of 12** — **0.51×–1.33×**, median **0.96×** |
| MessagePack | MessagePack-CSharp | ahead in **4 of 4** — **0.60×–0.88×**, median **0.72×** (every cell beyond the band) | ahead in **7 of 8** — **0.45×–1.12×**, median **0.78×** |
| TOML | Tomlyn 2.10.1 | faster in **10 of 10** — **0.10×–0.41×**, median **0.21×** (between **2.4× and 10×** faster) | faster in **10 of 10** — **0.09×–0.35×**, median **0.19×** (between **2.9× and 11×** faster) |
| YAML | YamlDotNet 18.1.0 | — no AOT opponent exists | faster in **6 of 6** — **0.02×–0.06×**, median **0.04×** (**17×–50×** faster) |
| INI | ini-parser 2.5.3 | — no equivalent operation | — no equivalent operation |

**How a number gets here.** A **round** is one whole process; each round prints, per arm, the median
µs/op over **5 independent runs × ≥100k iterations each**, and every figure is the **median of three
rounds** taken inside **one** window — 2026-10-29, 17:49–18:24 local, AMD Ryzen 7 6800H, Windows 11,
.NET SDK 10.0.401, runtime 10.0.12 — behind an idle-machine gate that refuses any arm missing its
drift, spread or dispersion criteria. A row's ratio is `ours ÷ theirs` from those two medians, and
**AOT and JIT are never merged into one figure, one column or one ratio.**

**The JIT column is the runtime's default** — tiered compilation with dynamic PGO on, i.e. what a
user actually gets — and that default is not neutral between two libraries, so the same row is also
measured with `DOTNET_TieredPGO=0` and with `DOTNET_TieredCompilation=0`.

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

## Key Features

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
  and top-level `null` behave exactly as on the direct path (v2026.4.11+; both paths use one
  generated dispatch chain). Struct DTOs and propertyless types have no streaming delegate and throw
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

Null is not portable across the five formats: "write a null" and "read a null" are spelled out per
format below, as observed behaviour of the generated code.

**Writing a null member** (default options, `DefaultIgnoreCondition = Never`):

| Format | Null member written as | Under `WhenWritingNull` |
|--------|------------------------|-------------------------|
| JSON | `"key":null` | member omitted |
| MsgPack | `nil` (`0xC0`; the map/array header is adjusted) | member omitted |
| TOML | member omitted — TOML has no null value | member omitted |
| YAML | member omitted — YAML has no null value on write | member omitted |
| INI | member omitted — INI has no null value | member omitted |

Because the three section-based formats drop the member, **a null does not round-trip through
TOML/YAML/INI** — reading the document back leaves the target at its default (the property
initializer's value when one exists). JSON and MsgPack write an explicit null and do round-trip.

**Reading a null** — accepted spellings and what the target receives:

| Format | Null spellings accepted | nullable target (`T?`, `string?`) | non-nullable value type | non-nullable `string` |
|--------|-------------------------|:---:|:---:|:---:|
| JSON | `null` | `null` | `FormatException` | `FormatException` |
| MsgPack | `nil` | `null` | `FormatException` | `FormatException` |
| TOML | `Key =` (no value), or unquoted `null` (any case) | `null` | `FormatException` | `null` |
| YAML | `Key:` (no value), `~`, or unquoted `null` (any case) | `null` | `FormatException` | `null` |
| INI | `Key =` (no value), or unquoted `null` (any case) | `null` | `FormatException` | `null` |

Rules that hold in every format:

- **A quoted scalar is never null.** `Key = ""` is the empty string and `Key = "null"` is the four-character string `null` — both are values, not omissions.
- **A missing member is not an error.** It leaves the target at its default.
- **An unquoted empty value *is* the null literal** in TOML/YAML/INI: `Key =` with nothing after it assigns `null`, not "member missing".
- **A null aimed at a non-nullable value type fails loudly** with `FormatException`, never a silent `0` / `false` / `Guid.Empty`.
- **The non-nullable `string` column is the generators' NRT-lenient contract:** TOML/YAML/INI assign
  `null!` to an unannotated `string`, while JSON/MsgPack require a value token and reject the null.
- **Scope:** scalar members only (string, numeric, temporal, enum, Guid and their nullable forms);
  collection, object and dictionary branches keep their own empty-value semantics.

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

### Nullable Elements

`List<int?>`, `List<string?>`, `List<TObject?>`, `Dictionary<string, TObject?>` and nested lists of
objects round-trip in JSON and MsgPack, null elements included. TOML and YAML have no null *type*:
null *scalar elements* are skipped on write, and a **nullable element** (`List<int?>`, a nullable
object/dict element, or `int?` at any depth of a nested chain) is dropped with the `PICOSERDE004`
warning — nested lists themselves are a separate axis, and both formats express them (see
[Format limits](#format-limits-what-each-wire-format-cannot-express)). INI cannot represent any
nullable element and drops those members the same way.

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

`DeserializeFromStreamAsync<T>(stream, options, ct)` reads a stream incrementally: for
JSON/TOML/YAML/INI the consumed prefix is released as tokens complete and only the current
token/member window is retained, so the document is never buffered wholesale before parsing.

**MsgPack is the exception — its stream path buffers.** Each read is appended to an accumulator and
the whole buffer is re-parsed from the beginning until the value is complete (correct, and it resumes
cleanly, but not incremental — it costs O(n²) in the number of chunks).

JSON/TOML/YAML/INI accept an options argument (`JsonOptions`/`TomlOptions`/`YamlOptions`/`IniOptions`).
MsgPack has a `MsgPackOptions` type too, but its `DeserializeFromStreamAsync` overload takes
`(stream, ct)` only — options are not accepted on the streaming path.

```csharp
await using var stream = File.OpenRead("config.toml");
var config = await TomlSerializer.DeserializeFromStreamAsync<ServerConfig>(stream);
```

The generated delegate returns a `ReadStatus`:

| Status | Meaning |
| --- | --- |
| `Success` | The value is complete |
| `NeedMoreData` | The current chunk ends inside a token/section; the runner refills and resumes from the reader's mark |
| `EndOfInput` | The document contains no value — the runner throws `FormatException` |

**Empty-stream semantics** match the synchronous path: TOML/YAML/INI treat an empty stream as an empty
object; JSON throws `FormatException` ("the document contains no value").

**Recursive DTOs**: JSON and MsgPack support self- and mutually-referencing types (cycle-safe
extraction + seeded helpers), including recursive members on polymorphic bases — nested values keep
their derived (discriminator) type on both the sync and chunked paths. TOML/YAML/INI cannot express
unbounded nesting, so the recursive member is skipped and the generator reports `PICOSERDE003` (never
silent). Data-level object-graph cycles fail loudly instead of overflowing the stack.

**Polymorphic hierarchies**: a concrete polymorphic base instance serializes with a synthesized
discriminator (the base type name, collision-safe), so base instances round-trip as the base type while
derived instances keep their declared discriminator. They also keep nested object/dict members *and*
collection members (`List<T>`, `Dictionary<string,T>`) of derived types in JSON, MsgPack, TOML and YAML;
INI ignores nested members because its format is flat.

**Diagnostics** (warnings): `PICOSERDE001` — a property type the generator cannot resolve was ignored.
`PICOSERDE002` — two distinct types produced the same generated file name (main hint); internal helper
names get a stable hash suffix (`UniqueName`). `PICOSERDE003` — a recursive member was skipped by a
section-based format. `PICOSERDE004` — a member shape the format cannot represent (a nested list the
format has no syntax for, a nullable value-type element, a null object element, a collection dictionary
value) was skipped. `PICOSERDE005` — an anonymous-type collection member was skipped by a format whose
writer cannot emit one (JSON is the only writer that can). See
[Format limits](#format-limits-what-each-wire-format-cannot-express) for the support matrix behind
`PICOSERDE003`/`PICOSERDE004`.

**Custom/advanced registration** (scripts or hand-written serializers):
`JsonSerializer.RegisterStreaming<T>(StreamingFunc<JsonReader, T> func)` with `where T : notnull`;
`HasStreamingDelegate<T>()` reports whether a delegate is registered. The source generator emits this
registration in a `ModuleInitializer` for every discovered type.

> Repository architecture, the feature bullet list that went with it, the three-layer test split and the per-module cross-validation: [`docs/testing-and-ci.md`](docs/testing-and-ci.md).

### PicoJetson JsonOptions

```csharp
byte[] compact = JsonSerializer.SerializeToUtf8Bytes(model);                 // compact (default)
byte[] pretty  = JsonSerializer.SerializeToUtf8Bytes(model,                  // human-readable
    new JsonOptions { Indented = true });
byte[] camel   = JsonSerializer.SerializeToUtf8Bytes(model,                  // CamelCase names
    new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
byte[] nonulls = JsonSerializer.SerializeToUtf8Bytes(model,                  // skip null members
    new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
byte[] nan     = JsonSerializer.SerializeToUtf8Bytes(model,                  // allow NaN/Infinity
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

Every options class (`JsonOptions`, `YamlOptions`, `TomlOptions`, `IniOptions`, `MsgPackOptions`)
exposes `DefaultIgnoreCondition`, but what "writing a null" means is fixed by the wire format: JSON
writes `"key":null`, MsgPack writes `nil` (map count adjusted), and TOML/YAML/INI omit the member —
YAML because writing `key:` would read back as a default and break round-trip fidelity. The matrix
applies to every emit path (nested objects, collection elements, nullable collections, polymorphic
dispatch) and is locked by `IgnoreConditionMatrixTests`; the full write-side matrix, `WhenWritingNull`
included, is in [Null Semantics](#null-semantics).

> **Honored options:** JSON and MsgPack read `DefaultIgnoreCondition` from their options objects. INI/TOML/YAML always omit nulls because those wire formats have no null *value* on the write side; their `DefaultIgnoreCondition` property is accepted by the public API but the generated code treats nulls as always omitted. `Indented = true` is honored: YAML additionally indents sequence items under their key, TOML indents table contents, and INI indents section contents (the compact default is unchanged).

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
(`PicoIgnoreAttribute`, `PicoSerializableAttribute`, `PicoCamelCaseAttribute`, `PicoConstructorAttribute`,
`PicoDateTimeFormatAttribute`, `PicoConverterAttribute`) — one implementation per concept, format-specific
public names preserved. `IniKeyAttribute.Key` is the canonical property (`Name` is an obsolete alias).

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