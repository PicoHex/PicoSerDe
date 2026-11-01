# PicoJetson

AOT-first JSON serializer with SIMD-accelerated parsing and source-generated zero-reflection code.

[![NuGet](https://img.shields.io/nuget/v/PicoJetson)](https://www.nuget.org/packages/PicoJetson)

## Install

```bash
dotnet add package PicoJetson
# The source generator is embedded in PicoJetson (analyzers/dotnet/cs) — no separate .Gen reference needed
```

## Quick Start

```csharp
using PicoJetson;

public record Person(string Name, int Age);

var json = JsonSerializer.Serialize(new Person("Alice", 30));
// → {"Name":"Alice","Age":30}

var person = JsonSerializer.Deserialize<Person>(Encoding.UTF8.GetBytes(json));
// → Person { Name = "Alice", Age = 30 }
```

No attributes required.

## Features

- **RFC 8259** compliant JSON
- **SIMD-accelerated** whitespace & backslash scanning (Vector128)
- **Zero-copy** string access — `GetStringRaw()` returns `ReadOnlySpan<byte>` into source buffer
- **Zero heap allocation** on the hot path — `ref struct` reader/writer
- **Ref struct serialization** — `ref struct` types can be serialized directly via source-generated static methods
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>` (PipeReader)
- Unicode escape sequences including surrogate pairs

## Extended Types (JSON)

Beyond primitives, collections, and nested objects, the JSON generator supports:

| Family | Types | Notes |
|--------|-------|-------|
| Scalars | `DateTimeOffset`, `char`, `Uri`, `Version`, `Half`, `BigInteger`, `Int128`, `UInt128`, `nint` | `DateTimeOffset` keeps its UTC offset (ISO 8601 round-trip format); parsing is fail-loud |
| Collections | `HashSet<T>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`, `ImmutableArray<T>`, `Memory<T>`, `ReadOnlyMemory<T>` | serialized as arrays; `Stack<T>` round-trips with its enumeration order preserved |
| Dictionaries | `SortedDictionary<K,V>`, `ConcurrentDictionary<K,V>` | serialized as objects, constructed as the declared type on read |
| Tuples | `ValueTuple<...>` (e.g. `(int, string)`) | serialized via its `Item1..ItemN` fields |

Helper APIs used by generated code: `PicoSerDe.Core.ScalarCodec` (UTF-8 text codecs) and
`PicoJetson.TypeIo` (reader/writer adapters).

### Nullability fidelity

Generated code reproduces reference-nullability annotations exactly as declared —
`List<string?>`, `Dictionary<string, object?>`, `Dictionary<string, string?>` and
nullable-annotated constructor parameters all round-trip, so nullable-enabled
consumers that promote `CS8619`/`CS8620` to errors keep building. The integration
test project enforces this at **compile time** (`<WarningsAsErrors>CS8619;CS8620;CS8625</WarningsAsErrors>`).
JSON `null` follows the documented rule everywhere: value-type targets throw,
reference-type targets (list elements and dictionary values) yield `null`.

**Not supported yet** (members are dropped without a diagnostic — no serialization error):

- **Top-level scalar / `Nullable<T>` targets** — `Serialize<Guid>(...)`, `Deserialize<int?>(...)` etc. fail loudly with `InvalidOperationException` instead of emitting an empty object
- `KeyValuePair<K,V>` and `Tuple<...>` members
- extended scalars used as **dictionary keys** (e.g. `Dictionary<DateTimeOffset, int>`)
- the extended **scalars** above and `ConcurrentDictionary<K,V>` in the **INI / TOML / YAML / MessagePack** formats (JSON only for now). The extended **collections** are not JSON-only: all five generators resolve the kind and write it as a plain sequence, so a `HashSet<int>` **member** round-trips in JSON, MsgPack, TOML and YAML, and so does an **element** position (`List<HashSet<int>>`) — INI drops that member with `PICOSERDE004` (`ExtendedKindCollectionTests`)

## Limits

JSON expresses every shape in the framework's format-limits matrix — object lists (`List<Dto>`),
nested primitive lists (`List<List<int>>`), nested object lists (`List<List<Dto>>`), nested
nullable elements (`List<List<int?>>`, `null` elements included) and recursive members all
round-trip. `PICOSERDE003` never fires for this format, and the JSON writer is the only one that
can emit a collection of anonymous types (`PICOSERDE005`).

The JSON-specific gap is the **extended type family** listed above (extended scalars as members and
used as dictionary keys, `ConcurrentDictionary<K,V>`, `KeyValuePair<K,V>` / `Tuple<...>` members).
Those members are dropped **without** a `PICOSERDE` diagnostic — the ids cover the
section-based-format shapes in the matrix, not this family. A **top-level scalar** target (`Serialize<Guid>()`, `Deserialize<int?>()`) fails loudly
with `InvalidOperationException` rather than emitting an empty object.

See the [root README](../README.md#format-limits-what-each-wire-format-cannot-express) for the
same matrix across all five formats.

## Options

```csharp
using PicoJetson;
using PicoSerDe.Core;   // PicoIgnoreCondition lives here, not in PicoJetson (CS0103 without it)

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
    new JsonOptions { DefaultIgnoreCondition = PicoIgnoreCondition.WhenWritingNull });

// NaN / Infinity as named literals
var withNaN = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });

// Accept trailing commas and comments (lenient parsing)
var result = JsonSerializer.Deserialize<MyModel>(json,
    new JsonOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
```

### Available Options

| Option | Default | Description |
|--------|:-------:|-------------|
| `Indented` | `false` | Human-readable indented output |
| `MaxDepth` | `63` | Maximum nesting depth |
| `PropertyNamingPolicy` | `null` | `JsonNamingPolicy.CamelCase`, `.SnakeCaseLower`, `.KebabCaseLower`, `.PascalCase` |
| `DefaultIgnoreCondition` | `Never` | `PicoIgnoreCondition` (`PicoSerDe.Core`): `Never` / `WhenWritingNull` / `WhenWritingDefault` |
| `NumberHandling` | `Strict` | `AllowNamedFloatingPointLiterals` (NaN/Inf; also admits an out-of-range literal such as `1e400` as ±∞, which Strict rejects) |
| `IncludeFields` | `false` | Serialize public fields in addition to properties |
| `AllowTrailingCommas` | `false` | Accept trailing commas |
| `ReadCommentHandling` | `Disallow` | `Skip` — ignore `//` and `/* */` |
| `UnmappedMemberHandling` | `Skip` | `Disallow` — throw on unknown properties |
| `PropertyNameCaseInsensitive` | `true` | Case-insensitive matching by default; `false` for exact matching |

## Strict Deserialization

Deserialization fails loudly instead of silently producing defaults
(STJ-compatible semantics):

```csharp
JsonSerializer.Deserialize<Model>("{\"age\":\"abc\"}"u8); // throws FormatException
JsonSerializer.Deserialize<Model>("null"u8);                 // returns null (reference types)
JsonSerializer.Deserialize<Model>("{\"a\":1} {\"a\":2}"u8);  // throws: trailing data
JsonSerializer.Deserialize<Model>("{}"u8);                  // throws if Model has required members
JsonSerializer.Deserialize<int[]>("[1,null]"u8);            // throws: null into value type
JsonSerializer.Deserialize<Model>("{\"score\":1e400}"u8);      // throws: number out of range
```

`PicoDocument.IsValid` performs full structural validation (container
matching, property/value grammar, single root, no trailing content).

## JSON Lines (JSONL)

JSON Lines (`.jsonl` / NDJSON) — each line is a complete JSON value, per [jsonlines.org](https://jsonlines.org/).

```csharp
using PicoJetson;

// Sync batch: serialize/deserialize collections
var people = new[] { new Person("Alice", 30), new Person("Bob", 25) };
byte[] jsonl = JsonSerializer.SerializeLines(people);
// → {"Name":"Alice","Age":30}\n{"Name":"Bob","Age":25}\n

var restored = JsonSerializer.DeserializeLines<Person>(jsonl);
// → [ Person { Alice, 30 }, Person { Bob, 25 } ]

// Streaming: process large files line-by-line
using var stream = File.OpenRead("data.jsonl");
await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(stream))
{
    Console.WriteLine(person.Name);
}

// Streaming JSON array mode (root-level [...])
using var arrStream = new MemoryStream("[{\"Name\":\"A\"},{\"Name\":\"B\"}]"u8.ToArray());
await foreach (var p in JsonSerializer.DeserializeAsyncEnumerable<Person>(
    arrStream, topLevelValues: false))
{
    Console.WriteLine(p.Name);
}
```

### JSONL API Reference

| Method | Description |
|--------|-------------|
| `SerializeLines<T>(IEnumerable<T>)` | Serialize collection to `byte[]` with `\n` between values |
| `DeserializeLines<T>(ReadOnlySpan<byte>)` | Deserialize JSONL byte span to `T?[]` |
| `DeserializeAsyncEnumerable<T>(Stream, topLevelValues, options, ct)` | `IAsyncEnumerable<T?>` — `topLevelValues: true` = JSONL, `false` = root-level JSON array `[...]` |

## Attributes

| Attribute | Description |
|-----------|-------------|
| `[PicoSerializable]` | Generate serializer for all referenced formats |
| `[PicoJsonSerializable]` | Generate JSON serializer only |
| `[JsonPropertyName("name")]` | Override JSON key name |
| `[JsonIgnore]` | Exclude from serialization |
| `[JsonCamelCase]` | camelCase naming per class |
| `[JsonConverter(typeof(T))]` | Custom converter |
| `[JsonConstructor]` | Constructor for immutable deserialization |
| `[DateTimeFormat("format")]` | Custom DateTime format |

## Performance

**From the 2026-10-29 window (see the [archive](../docs/perf/measurement.md#per-scenario-tables-2026-10-29-window)).**
Each pair is `PicoJetson vs System.Text.Json` in µs/op: per arm the median of 5 independent runs × ≥100k
iterations, quoted as the median of three rounds, every arm measured inside one window (2026-10-29, 17:49–18:24
local — AMD Ryzen 7 6800H, Windows 11, .NET SDK 10.0.401, runtime 10.0.12) behind the harness's idle-machine gate.
**The multiple is that pair divided, larger by the smaller** — `5.790 ÷ 3.193 = 1.813 → 1.8×` — read off the µs
themselves, with the winner put in words, so no reader has to invert anything. A row inside **~±10 %** of 1.00 is
**level**: it prints the harness's own `ours ÷ theirs` ratio beside it and is not a direction claim either way.
AOT and JIT are separate processes and are never merged into one figure, one column or one multiple.

**In one line: ahead of System.Text.Json in 12 of 12 scenarios under NativeAOT, and in 9 of 12 under JIT — the
three JIT losses are 1.14×–1.33× and are stated here rather than smoothed over.**

**NativeAOT — ahead in 12 of 12** (harness 0.55×–0.98×, row median 0.79×, i.e. ≈1.3× faster; eight rows past the
band, four level):

| Scenario | PicoJetson | System.Text.Json | Faster by |
|---|---:|---:|---|
| Collection POCO — Deserialize | 3.193 | 5.790 | **PicoJetson 1.8×** |
| Nested POCO — Serialize | 0.312 | 0.450 | **PicoJetson 1.4×** |
| LargeString 1KB — Deserialize | 0.421 | 0.589 | **PicoJetson 1.4×** |
| Simple POCO — Serialize | 0.123 | 0.166 | **PicoJetson 1.3×** |
| Collection POCO — Serialize | 1.297 | 1.694 | **PicoJetson 1.3×** |
| LargeString 1KB — Serialize | 0.382 | 0.486 | **PicoJetson 1.3×** |
| Nested POCO — Deserialize | 1.003 | 1.267 | **PicoJetson 1.3×** |
| Simple POCO — Deserialize | 0.269 | 0.307 | **PicoJetson 1.1×** |
| Complex POCO — Serialize | 0.582 | 0.650 | level (0.90×, ±10%) |
| LargeFlat 50-fields — Serialize | 1.395 | 1.513 | level (0.92×, ±10%) |
| Complex POCO — Deserialize | 0.884 | 0.936 | level (0.94×, ±10%) |
| LargeFlat 50-fields — Deserialize | 3.356 | 3.418 | level (0.98×, ±10%) |

The AOT column carries **no loss in it at all**: the object/member paths and the collection paths are both ahead,
the largest single lead is the collection read, and the four rows at the bottom are ahead but inside the band, so
they are not direction claims.

**JIT, runtime default — slower in 3 of 12** (harness 0.51×–1.33×, row median 0.96× — itself inside the band):

| Scenario | PicoJetson | System.Text.Json | Faster by |
|---|---:|---:|---|
| Nested POCO — Serialize | 0.187 | 0.366 | **PicoJetson 2.0×** |
| Simple POCO — Serialize | 0.066 | 0.109 | **PicoJetson 1.7×** |
| Collection POCO — Deserialize | 2.180 | 3.337 | **PicoJetson 1.5×** |
| Collection POCO — Serialize | 0.874 | 1.294 | **PicoJetson 1.5×** |
| Complex POCO — Serialize | 0.296 | 0.322 | level (0.92×, ±10%) |
| LargeString 1KB — Serialize | 0.188 | 0.196 | level (0.96×, ±10%) |
| LargeString 1KB — Deserialize | 0.311 | 0.322 | level (0.97×, ±10%) |
| Nested POCO — Deserialize | 0.780 | 0.787 | level (0.99×, ±10%) |
| Simple POCO — Deserialize | 0.154 | 0.154 | level (1.00×, ±10%) |
| LargeFlat 50-fields — Deserialize | 2.654 | 1.992 | **System.Text.Json 1.33×** |
| LargeFlat 50-fields — Serialize | 1.173 | 0.923 | **System.Text.Json 1.27×** |
| Complex POCO — Deserialize | 0.706 | 0.617 | **System.Text.Json 1.14×** |

The three losses sit at the bottom of that table, and all three are the JSON object/member path — the part that
scales with property count — and all three are a property of the runtime's default tiering, not of the code:
measured in the same window with tiered compilation off, the same three rows read **0.73×**, **0.56×** and
**0.74×**. That comparison is spelled out in [JSON under JIT](../README.md#json-under-jit-and-why-the-default-is-not-neutral)
and in the archive's [JIT-column section](../docs/perf/measurement.md#what-the-jit-column-is-comparing); the row
medians are **0.96×** under the runtime default, **0.94×** with `DOTNET_TieredPGO=0` (still 3 lost) and **0.58×**
with `DOTNET_TieredCompilation=0` (**no** losing scenario, 0.32×–0.74×). A reader who wants to know what they will
actually get reads the published JIT table above; a reader who wants to know how much of it is the tiering pipeline
reads those two.

Reproducing it: `PICOBENCH_STRICT=1 dotnet run --project PicoJetson/benchmarks/PicoJetson.Benchmarks -c Release
--no-build` three times, strictly serial, one process per round; the AOT table is the same project published with
`-p:PublishAot=true` and run as a native binary, its own three processes. Each table ends with its own
`PICOBENCH_SUMMARY … published= refused= mode= exit=` line, and the harness refuses an arm measured on a busy
machine (its own drift, spread and dispersion criteria on each arm, system-wide CPU under 35 %). Where a row's own
three rounds disagree the spread stays in the record: JSON JIT `LargeString 1KB — Serialize` reads 0.225 / 0.172 /
0.188 µs on our arm against 0.251 / 0.196 / 0.191 µs on System.Text.Json's — that spread is where its 0.96× comes
from, and at 0.96× it is level, read as neither a gain nor a loss.

The withdrawn table is still worth knowing about, because it is why every ratio in this repository is printed as
`<ours> ÷ <theirs>`: `Benchmark.Compare(name, baselineName, baselineAction, candidateName, candidateAction)` had
PicoSerDe's own action in the *baseline* slot at every call site, so each ratio and each column header came out
backwards. A runtime role guard now throws on that inversion.

One capability fact does not depend on any of that: **System.Text.Json is fully usable under NativeAOT** (with a
source-generated `JsonSerializerContext`), so JSON is the one format where PicoJetson is a design choice rather
than an AOT necessity. See the
[competitor/AOT matrix](../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim) for the rest.

## Low-Level API

```csharp
var reader = new JsonReader(Encoding.UTF8.GetBytes(json));
while (reader.Read())
{
    switch (reader.TokenType)
    {
        case TokenType.PropertyName: var name = reader.GetStringRaw(); break;
        case TokenType.String:       var value = reader.GetStringRaw(); break;
        case TokenType.Int32:        reader.TryGetInt32(out int n); break;
    }
}
```

The writer side mirrors `Utf8JsonWriter`, including `WriteRawValue` (v2026.4.3+) for injecting pre-validated JSON verbatim — no validation or escaping is performed:

```csharp
using System.Buffers;

var buffer = new ArrayBufferWriter<byte>();
var jw = new JsonWriter(buffer);
jw.WriteStartObject();
jw.WritePropertyName("payload"u8);
jw.WriteRawValue(value.PreSerializedJson);  // pre-validated UTF-8 JSON
jw.WriteEndObject();
```

All `WriteNumber` overloads format with `CultureInfo.InvariantCulture` — output never depends on the ambient culture. `WriteNumber(float)` emits the shortest round-trippable single-precision text, so a `float` keeps its own precision instead of being widened to `double` and printing the widened value.

## Packages

| Package | Description |
|---------|-------------|
| `PicoJetson` | Runtime library |
| `PicoJetson.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
