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
- extended collections nested as **list elements** or **dictionary values** (e.g. `List<HashSet<int>>`)
- all of the types above in the **INI / TOML / YAML / MessagePack** formats (JSON only for now)

## Limits

JSON expresses every shape in the framework's format-limits matrix — object lists (`List<Dto>`),
nested primitive lists (`List<List<int>>`), nested object lists (`List<List<Dto>>`), nested
nullable elements (`List<List<int?>>`, `null` elements included) and recursive members all
round-trip. `PICOSERDE003` never fires for this format, and the JSON writer is the only one that
can emit a collection of anonymous types (`PICOSERDE005`).

The JSON-specific gap is the **extended type family** listed above (extended collections nested as
list elements or dictionary values, extended scalars used as dictionary keys,
`KeyValuePair<K,V>` / `Tuple<...>` members). Those members are dropped **without** a
`PICOSERDE` diagnostic — the ids cover the section-based-format shapes in the matrix, not this
family. A **top-level scalar** target (`Serialize<Guid>()`, `Deserialize<int?>()`) fails loudly
with `InvalidOperationException` rather than emitting an empty object.

See the [root README](../README.md#format-limits-what-each-wire-format-cannot-express) for the
same matrix across all five formats.

## Options

```csharp
using PicoJetson;

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
| `DefaultIgnoreCondition` | `Never` | `WhenWritingNull` / `WhenWritingDefault` |
| `NumberHandling` | `Strict` | `AllowNamedFloatingPointLiterals` (NaN/Inf) |
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
| `[JsonDateTimeFormat("format")]` | Custom DateTime format |

## Performance

**PicoJetson is slower than System.Text.Json on these micro-benchmarks.** The full tables, the
machine they were taken on and the log behind every number live in the
[2026-10-27 measurement record](../docs/perf/measurement-2026-10-27.md#measured-results-2026-10-27); this page keeps only what is specific
to JSON. Every ratio is **ours ÷ theirs**, the direction the harness prints: above 1.00 we are
slower.

- **AOT vs AOT, 12 scenarios — slower in 11 of 12.** Range 0.92×–3.58×, median 1.95×:
  `Simple POCO — Serialize` 0.282 vs 0.168 µs (1.68×), `Complex POCO — Serialize` 1.248 vs
  0.596 µs (2.10×), `Collection POCO — Deserialize` 6.985 vs 5.531 µs (1.26×), `LargeString 1KB —
  Serialize` 1.543 vs 0.431 µs (3.58×, the widest gap). The single row on our side of 1.00 is
  `Nested POCO — Deserialize` at 0.92× (1.211 vs 1.313 µs).
- **JIT vs JIT, 12 scenarios — slower in 12 of 12.** Range 1.07×–5.60×, median 1.91×: the closest
  rows are `Nested POCO — Deserialize` 1.07× and `Simple POCO — Serialize` 1.11×, the widest
  `LargeString 1KB — Deserialize` 1.522 vs 0.272 µs (5.60×).

Both tables come from 5 independent runs × ≥100,000 iterations per arm on 2026-10-27, on an AMD
Ryzen 7 6800H (Windows 11, .NET 10.0.12) that was **not fully idle** (5%–22% other-CPU on the AOT
table, 7%–29% on the JIT one, against the protocol's 35% limit; no arm was refused). One qualifier
applies to all of it: **a single session's ±stddev is not a cross-session error bar** — five runs in
one process cannot see tiered-JIT landing points or thermal history, so the honest cross-session
band is about ±10%, and a ratio inside that band is not a direction claim (the 2026-10-27 measurement
record spells this out under
[One session's stddev is not a cross-session error bar](../docs/perf/measurement-2026-10-27.md#one-sessions-stddev-is-not-a-cross-session-error-bar)).
`Nested POCO — Deserialize` at 0.92× is inside it and is not claimed as a win.

`dotnet run -c Release --project PicoJetson/benchmarks/PicoJetson.Benchmarks` reproduces the JIT
table; the AOT table is that project published with `-p:PublishAot=true` and run as a native
binary. AOT and JIT are separate processes and are never merged into one figure.

The withdrawn table is still worth knowing about, because it is why every ratio in this repository
is printed as `<ours> ÷ <theirs>`: `Benchmark.Compare(name, baselineName, baselineAction,
candidateName, candidateAction)` had PicoSerDe's own action in the *baseline* slot at every call
site, so each ratio and each column header came out backwards. A runtime role guard now throws on
that inversion.

One capability fact does not depend on any of that: **System.Text.Json is fully usable under
NativeAOT** (with a source-generated `JsonSerializerContext`), so JSON is the one format where
PicoJetson is a design choice rather than an AOT necessity. See the
[competitor/AOT matrix](../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim)
for the rest.

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
