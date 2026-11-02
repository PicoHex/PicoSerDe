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
matching, property/value grammar, the `,` between members and elements, single root, no trailing
content).

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

### Closing a container is the commit point

`JsonWriter` is a `ref struct` with **no `Dispose`, no `Complete` and no public `Flush`**. A token is
written into a private span cache, and those bytes reach the caller's `IBufferWriter` at only a few
places: the outermost `WriteEndObject` / `WriteEndArray` (when the depth returns to 0), a `Buffer`
read (that is how `IJsonConverter` and `SerializeCustom` hand over), a span refill every 256 B, and
one commit per token while no container is open. So the contract is: **every container you open must
be closed** — on the happy path *and* on every early return from inside it. Leaving one open is not
something the writer can report: a `ref struct` has no scope hook, and there is no `using` to write.

| What the caller does | Bytes that reach the buffer |
|---|---|
| `WriteStartObject(); WritePropertyName("x"u8); WriteNumber(1);` and never `WriteEndObject()` | `{` — a 1-byte document (the only token written at depth 0) |
| the same with ~40 members, so the 256-B span refilled several times | the document truncated at the **last block that was flushed** (measured: 502 B ending in `,"prop":`) |

That is the whole failure mode: **the tail is never committed**. A small document degrades to `{`;
a large one stops at the last boundary that happened to be committed. Nothing throws, and
`PicoDocument.IsValid` reports `false` on the fragment.

Close from a `finally` when an early exit can skip it:

```csharp
static void WriteUser(ref JsonWriter jw, User user)
{
    jw.WriteStartObject();
    try
    {
        jw.WritePropertyName("name"u8);
        jw.WriteString(user.Name);
        if (user.Id is { } id)              // an early exit INSIDE the container
        {
            jw.WritePropertyName("id"u8);
            jw.WriteNumber(id);
        }
    }
    finally
    {
        // This line IS the commit: skipping it leaves the tail (the closing brace
        // included) in the writer's span cache, where no caller can ever see it.
        jw.WriteEndObject();
    }
}
```

Two things that follow from it:

- **An exception mid-document cannot be repaired by closing.** Closing would emit a container whose
  value never arrived (or throw on the pending property name), so the rule is to write into a
  scratch `IBufferWriter<byte>` and publish it only once the outermost close has run — never hand
  the writer the stream or response body you are about to send. Our generated serializers write
  straight into the caller's buffer, so a `Serialize(IBufferWriter<byte>, T)` that throws leaves
  that buffer holding a broken prefix.
- **The probe is one comparison.** `BytesWritten` counts the bytes *written into* the writer,
  committed or not, so `writer.BytesWritten != buffer.WrittenSpan.Length` means the writer is
  holding bytes back — a document that was never closed (or a writer that was copied by value).

### Misuse throws instead of emitting illegal JSON

Four call shapes used to be accepted silently and then write a document **no reader accepts** — the
same four `System.Text.Json.Utf8JsonWriter` rejects with `InvalidOperationException`. They now throw at
the call site, and they throw *before* a single byte of the offending token is staged, so the caller is
never handed a half-built document to debug:

| Misuse | Bytes it used to write | Now |
|---|---|---|
| a **value inside an object with no property name pending** (`WriteStartObject(); WriteString("x")`) | `{"x"}` — a member with no name | `InvalidOperationException`: *Cannot write a JSON value within an object without a property name: call WritePropertyName first.* |
| **two `WritePropertyName` in a row** (`"a":` immediately followed by `"b":1`) | `{"a":,"b":1}` — the second name even collected a separator, so the bytes were illegal rather than merely odd | *Cannot write a JSON property name following another property name: the first one's value is missing.* |
| an **`End*` with no matching open** (one `WriteEndObject` / `WriteEndArray` too many, i.e. depth would go to −1) | the extra close was swallowed — `{"a":1}` plus one `}` **read as valid JSON** — and with depth at −1 every later `}` failed to commit | *'}' is invalid without a matching open.* (or *']' …*) |
| **`WriteEndArray` closing an object** (`{]`) | `{]` — the writer never knew which container was open | *']' is invalid without a matching open array: the container open at this depth is an object.* |

Each row removes a failure a reader cannot tell from a correct document. The third is the worst of
them: `{"a":1}` followed by one stray `}` *was* the valid document, so nothing downstream could notice
that the caller's bookkeeping had already gone wrong.

Two of the four need to know **which container is open**, and that is one bit per nesting level in the
same `ulong` word the comma bookkeeping already reads (`_kinds`: bit `_depth` set = an object is open,
clear = an array). Bit 0 is never set, so a top-level scalar needs no branch, and one word is enough
because `MaxDepth` is capped at 63 — the deepest container sits on bit 63, which is exactly the
boundary where a mask one bit too narrow would report a phantom kind error.

Evidence is `JsonWriterMisuseGuardTests` — one test per misuse, in the table's order, plus the reverse
protection (a guard that fires on correct code is the same bug from the other side):

- W1 — `W1_Value_written_inside_an_object_without_a_property_name_throws`;
  `W1_Number_written_inside_an_object_without_a_property_name_throws` (the number path stages its own
  separator, so it asks the same question); `W1_Rejection_happens_before_anything_is_written` (the
  buffer still holds the `{` that precedes the mistake).
- W2 — `W2_Two_property_names_in_a_row_throw`.
- W3 — `W3_EndObject_without_a_matching_open_throws`;
  `W3_Extra_EndObject_after_a_complete_document_throws`;
  `W3_EndArray_without_a_matching_open_throws`.
- W4 — `W4_EndArray_closing_an_object_throws`.
- Correct code is untouched — `Legal_uses_of_every_container_kind_still_write_the_same_bytes` (one
  document mixing all three container positions, byte for byte),
  `Top_level_values_still_write_without_any_container`,
  `The_deepest_supported_nesting_still_writes_objects_and_arrays` (`MaxDepth = 63`).

The guard sites are `JsonWriter.cs:642-645` (two names), `:730-731` and `:754-761` (unmatched or
wrong-kind close) and `:813-822` (a value with no pending name), with the kind bit set at `:722` and
cleared at `:749`. The change landed in `bfe8251` (merge `65e31c0`).

This does not soften the commit-point rule above: a container you leave open still commits only the
prefix that happened to be flushed, and no guard can see that. What changed is the other half — a call
that is illegal *by itself* now fails at the call site instead of at the reader.

### `PicoDocument.IsValid` requires the separator

`IsValid` is the ruler this repository (and its downstream) uses for "is this output correct?", so a
false positive there is worse than a wrong writer: it **certifies the wrong bytes**. It used to report
`{"a":1"b":2}` and `[1 2]` as valid — an object accepted a property name whenever the previous token was
a value, and an array kept expecting "value or end", so the `,` between two members was never required.
RFC 8259 requires it, and it is required now: after a completed value (a scalar, or a container
closing) the next token must be `,` or the closing bracket. That also settles the container cases —
`[{"a":1}2]`, `{"a":{}{"b":2}}` and `[[1][2]]` are `false`, because a closed container is a consumed
value like any other.

Comma *misuse* is still rejected (leading, doubled and trailing commas are the reader's business; the
separator state machine reads the same byte gap and keeps the reader's answer), and a
`\uFEFF`-prefixed document gets the same verdict as the same bytes without the BOM.
Evidence is `PicoDocumentSeparatorTests` — `IsValid_MissingCommaBetweenObjectProperties_ReturnsFalse`,
`IsValid_MissingCommaBetweenArrayElements_ReturnsFalse`,
`IsValid_MissingCommaBetweenContainerElements_ReturnsFalse`,
`IsValid_ScalarAfterContainerWithoutComma_ReturnsFalse`,
`IsValid_MissingCommaInNestedContainers_ReturnsFalse`, `IsValid_SeparatedDocuments_ReturnTrue` (the
no-tightening half), `IsValid_BomPrefixedDocuments_StillDetectMissingComma` and
`Parse_MissingComma_Throws`. The new expectation is one extra state in the existing machine
(`ExpectSeparatorOrEnd`, `PicoDocument.cs:497`), and the comma is resolved from the raw byte gap the
validator is handed (`PicoDocument.cs:578-589`), because the reader swallows `,` without emitting a
token.

**What it costs.** `2.8 %`–`6.0 %` more per call (`ns/call`; Release, same process, position-balanced
ABBA rounds, 100k iterations, 1 KB and 100 KB payloads, compact and pretty) against a verbatim copy of
the pre-fix state machine, with a same-code control run in the same session to size that machine's
noise — real, and under the 10 % line. **The allocation delta is 0 B/call**: the pre-existing
560 B/call (the validator's two `byte[maxDepth]` arrays) is unchanged, so a hot path that validates
every message pays time, not GC pressure. Verdict and cost were measured together in `3c83308` (merge
`bdfd527`).

## Packages

| Package | Description |
|---------|-------------|
| `PicoJetson` | Runtime library |
| `PicoJetson.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
