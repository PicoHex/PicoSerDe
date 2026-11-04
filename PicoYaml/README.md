# PicoYaml

The only YAML library in this comparison that maps **typed POCOs** under NativeAOT —
reflection-free source generation with `ref struct` readers/writers. Supports indentation-based
block mappings/sequences, anchors & aliases, and multi-document streams.

**Coverage:** Core YAML 1.2 subset. Complex block scalar combinations, advanced merge key
patterns and full tag resolution are not yet supported. Flow sequences (`[a, b]`) are
**read** — one line, trailing comma and line breaks inside the brackets included, with
nested flow `[[1, 2], [3]]` failing loudly (`FormatException`) — but never **written**: the
writer emits only the empty-sequence marker `[]`. Flow mappings (`{key: value}`) are
partially supported. The reader's own stream is documented under
[Token stream](#token-stream-itokenreader).

[![NuGet](https://img.shields.io/nuget/v/PicoYaml)](https://www.nuget.org/packages/PicoYaml)

## Install

```bash
dotnet add package PicoYaml
# The source generator is embedded in PicoYaml (analyzers/dotnet/cs) — no separate .Gen reference needed
```

## Quick Start

```csharp
using PicoYaml;

public class Config
{
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public List<string> Tags { get; set; } = new();
}

var yaml = YamlSerializer.Serialize(new Config { Name = "MyApp", Port = 8080, Tags = ["prod", "api"] });
// Name: MyApp
// Port: 8080
// Tags:
// - prod
// - api

var restored = YamlSerializer.Deserialize<Config>(Encoding.UTF8.GetBytes(yaml));
```

## Features

- **YAML format** — indentation-based block mappings/sequences, anchors & aliases
- **`ref struct`** reader/writer — stack-allocated on the hot path
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Ref struct serialization** — serialize `ref struct` types directly
- **Only YAML library with typed POCO mapping under NativeAOT** — YamlDotNet parses a DOM
  there but its reflection-based typed mapping fails; VYaml was not verified either way
- **Anchors & aliases** — `&name` / `*name` with self-referencing support
- **Multi-document** — `---` separator support
- **Complex keys** — `? key\n: value` syntax
- **Flow collections** — inline `{key: value}` mappings (partial) and `[1, 2, 3]` sequences on the
  **read** side; the writer emits block sequences and the `[]` marker only (see
  [Token stream](#token-stream-itokenreader))
- **SIMD-accelerated** whitespace skipping
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Token stream (`ITokenReader`)

The generated serializers are not the only consumer: `YamlReader` implements `ITokenReader`
(`PicoSerDe.Core`), so anything reading the stream itself needs to know what it emits. Two things
matter — **which container token** a shape produces, and **what `Depth` says** while it does.

### A sequence is an array, a mapping is an object

| YAML shape | Container tokens |
|---|---|
| mapping — block, or flow `{k: v}` | `ObjectStart` … `ObjectEnd` |
| sequence — block (`- x`), flow `[1, 2]` / `[]`, or a document whose root is a sequence | `ArrayStart` … `ArrayEnd` |

Both shapes used to open `ObjectStart`/`ObjectEnd`, so nothing on the wire told "`A:` is a list" from
"`A:` is a map" and a consumer had to guess from whatever followed. The reported symptom: two adjacent
sequence values flattened to the same path — `B`'s elements came out as `0:0` and the key vanished.
Sequences now open and close the array pair at every position: a value (`A:\n  - 1`), dashes at the
key's own indentation (`A:\n- 1`, which used to carry no container token at all), the document root
(`- 1`), inside another sequence (`Grid:\n-\n  - 1`) and in the flow form. Mappings are untouched.
Sequence **items** are unchanged: one `String` token per item line, with `IsNestedSequenceItem` set for
a bare dash (a `-` alone on its line, whose value is the indented block below). The array pair is a
wrapper around that shape, not a promise about element encoding.

Tests: `YamlSequenceTokenKindTests` — one exact stream per form
(`MappingValueSequence_IsArrayDelimited_AndSiblingMappingIsAnObject`,
`TwoAdjacentSequenceValues_KeepBothKeys_EachWithItsOwnArray` is the reported symptom as a stream,
`FlatSequenceAtKeyIndentation_IsArrayDelimited`, `TopLevelBlockSequence_IsArrayDelimited`,
`NestedBlockSequence_OpensAnArrayPerLevel`, `FlowSequence_IsArrayDelimited`,
`FlowMapping_StaysAnObject`, `MultiDocument_EachSequenceDocumentIsClosed`) plus the end-to-end version
(`GenericConsumer_KeepsBothKeys_WhenTheDistinctionIsOnTheWire` → `A[0]=1 A[1]=2 B[0]=3`).

### Depth is balanced: opens == closes, and the walk ends at 0

Every container that opens has its own closing token — an array pair for a sequence, an object pair
for a mapping — and a walk over the whole document ends at `Depth == 0`, in both reader forms
(`ReadOnlySpan<byte>` and `ReadOnlySequence<byte>`; the sequence constructor copies the sequence into
one rented buffer and leaves `_isSequence = false` — `YamlReader.cs:348-367` — so "both forms" is
this same span-mode reader reached two ways, and the unreached `ReadSeq` half says as much at
`:1920-1923`). A frame is a container, not an indentation bookmark (push == open, pop == close). It
used to be otherwise: the first indent of an indented document pushed two frames for one
`ObjectStart`, so *every* nested document carried one `ObjectEnd` too many and finished at
`Depth == -1` — something downstream had already worked around by hand (PicoCfg's flattener drops a
trailing `ObjectEnd`). Fixed in `1b5f92f`.

Tests: `TokenReaderConformanceTests.Yaml_NestedDocument_ClosesEveryContainerItOpens` (five nested
shapes × both reader forms, counting both container kinds and rejecting a negative depth),
`YamlSequenceTokenKindTests.EveryContainerCloses_AndEndsAtDepthZero` (eight shapes × both reader
forms), and the flat case in `TokenReaderConformanceTests.Reader_ReadsTokens_AndDepthReturnsToZero` —
whose YAML argument (`a: 1`) is why the invariant went unnoticed for so long: that document has no
container token at all.

### Known shapes a consumer must handle

Two wire forms put more than one YAML structure into a **single `String` token**, and a generic consumer
has to know it before it flattens anything:

| YAML | Tokens |
|---|---|
| `- name: x` — the **first inline member** of a sequence item | `ArrayStart`, `String("name: x")`, … — the item's first member is one token holding the raw `name: x` text; the item's continuation lines are normal tokens, so `- name: x\n  v: 1\n` reads as `ArrayStart`, `String("name: x")`, `ObjectStart`, `PropertyName(v)`, `ObjectEnd`, `ArrayEnd` |
| `- - 1` — an **inline nested sequence** | `String("- 1")` — the inner dash and its scalar are one item token, not an `ArrayStart`. The writer never emits this form (it puts the dash alone on its line), the reader accepts it |

Neither shape is what **YAML** asks for, and the difference is the format's own syntax, not a reading
preference. `- name: x` is a sequence whose entry **is** a block mapping: the YAML reference reading of
`- name: x\n  v: 1\n` is `[{'name': 'x', 'v': 1}]` (PyYAML, `yaml.safe_load`), so a spec-shaped token
stream opens the mapping on the same line the dash does — `ObjectStart`, `PropertyName(name)`,
`String(x)`, then the item's continuation members (`PropertyName(v)`, `String(1)`), and only **last** the
`ObjectEnd` that closes the mapping: `ArrayStart`, `ObjectStart`, `PropertyName(name)`, `String(x)`,
`PropertyName(v)`, `String(1)`, `ObjectEnd`, `ArrayEnd` in full. We emit one fused
`String("name: x")` instead because this reader takes "the rest of the line after the dash" as a single
scalar (`PicoYaml/src/PicoYaml/YamlReader.cs:1069-1079`). The second row is that same shortcut, not a
different rule: `- - 1` is a sequence inside a sequence (`[[1]]`), whose inner dash should open its own
`ArrayStart`.

So this is a **debt, and it is recorded as ⏸** — the `⏸` reading in `AGENTS.md`: the format can hold the
shape, *we* do not emit it yet. It is not a format limit (`⛔`), and the `ITokenReader` contract does
not make it a format fact either: the contract fixes the container kinds and the balance invariants,
not a format's token *shapes*, so it does not **forbid** the fused form — but "the contract does not
forbid it" is not "the format asks for it". The generated deserializer reads the fused forms
correctly, which is why the shortcut stayed invisible this long; that is a property of our
deserializer, not a licence. **Reopen: emit mapping tokens for an item's first inline member** —
`ObjectStart`, `PropertyName(name)`, `String(x)`, then the continuation members (`PropertyName(v)`,
`String(1)`), and the `ObjectEnd` **last** — **plus the matching `ArrayStart` for an
inline nested sequence**. That is an **x-axis (wire contract) change**, not a cosmetic one: consumers,
generated readers and the tests below are all written against the fused shape today. What it costs a
generic consumer *until* then is the assumption "every member arrives as `PropertyName` followed by a
value" — for an item's first inline member, it does not.

Tests: `YamlSequenceTokenKindTests.SequenceOfMappingsUnderKey_WrapperIsAnArray_ItemBodyIsAnObject` and
`YamlSequenceTokenKindTests.TopLevelSequenceOfMappings_IsArrayDelimited_AndItemsStayObjects` pin
`String(name: a)`; the item text is "the rest of the line", captured at
`PicoYaml/src/PicoYaml/YamlReader.cs:1069-1079` — the same mechanism that yields `"- 1"` for an inner
dash.

### Flow collections do not move `Depth`

`A: [1, 2, 3]` and `A: {x: 1}` emit their container pair and their elements, but the flow readers never
touch the depth counter: every token of a root-level flow collection reports `Depth == 0` — the
enclosing level — instead of one level deeper. **The pairing still holds** (one pair, as many closes as
opens, the document still ends where it started), so a balance check is unaffected; what is missing is
the *level*, and only for the flow forms. A consumer that answers "am I inside a collection?" from
`Depth` alone has to use the container tokens there instead.

Tokens: `YamlSequenceTokenKindTests.FlowSequence_IsArrayDelimited` and
`YamlSequenceTokenKindTests.FlowMapping_StaysAnObject`. The accounting gap itself is in the code:
`ReadFlowSeqSpan` (`PicoYaml/src/PicoYaml/YamlReader.cs:1443-1500`) and `ReadFlowSpan`
(`PicoYaml/src/PicoYaml/YamlReader.cs:1535`) emit their start and end tokens without a single `_depth`
write, unlike every block path (`YamlReader.cs:905-1010`).

One more thing the stream cannot tell you: **no element count**. `HasCountedCollections` is `false` and
`TryGetCollectionCount` answers `false` for every token (`YamlReader.cs:723-739`), because YAML's wire
form carries no count — you read the items.

## Customization

```csharp
[PicoSerializable]                     // all formats
[PicoYamlSerializable]                 // YAML only
[YamlKey("custom_name")]              // override key name
[YamlIgnore]                           // exclude property
[YamlCamelCase]                        // camelCase keys
[YamlConverter(typeof(MyConverter))]   // custom converter
[YamlDateTimeFormat("yyyy-MM-dd")]    // custom DateTime format
```

## Options

`YamlSerializer.Serialize` / `Deserialize` accept `YamlOptions`:

- `Indented` — additionally indents sequence items under their key (nested block mappings are always indented; default: compact sequence items)
- `DefaultIgnoreCondition` — accepted for API uniformity; a null **member** is always omitted (see [Null Handling](#null-handling))

## Null Handling

- **Write:** a null **member** is omitted — the key does not appear at all, so a
  null member does **not** round-trip: the property keeps its initializer (or
  stays `null` when it has none) on read. A null *element* and a null
  *dictionary value* are a different story: the writer emits the null literal for
  them (`- null`, `key: null` — see [Limits](#limits)), so those read back null.
- **Read:** `Key:` with nothing after it, `~`, and the unquoted word `null`
  (any case) are null literals and assign `null` to nullable members.
- A quoted `""` is the empty string and a quoted `"null"` is the four-character
  string — quoted scalars are never null.
- A null literal aimed at a **non-nullable value type** throws
  `FormatException`; an unannotated `string` member receives `null` (the
  generators' NRT-lenient contract).

## Limits

Nested sequences are **supported** and written as block sequences (there is no flow `[a, b]`
syntax on the write side):

- `List<Dto>` — one `- ` item per row, each an indented block mapping.
- `List<List<int>>` / `List<int[]>` — an item whose value is the following indented sequence.
  The dash stands alone on its line (the writer emits no trailing space):

  ```yaml
  Grid:
  -
    - 1
    - 2
  -
    - 3
  Count: 5
  ```

- An **empty inner sequence** is `- []` on its own item line; an **empty outer sequence** is
  `Grid: []`.
- A *string* that looks like the marker is quoted, so `- "[]"` is the two-character string `[]`,
  not an empty sequence.
- `List<List<Dto>>` (objects at the innermost level) round-trips the same way.

A **zero-member type** (`public sealed class Mark { }`) is **supported** in both positions, because a
mapping with no members is still a mapping: the writer closes it with the flow pair on the key's own
line (`Mark: {}`), never as a bare `Mark:` — a key with no value is YAML for null, so that spelling
would reach the reader as a missing value. A list of them is one `-` line per element whose value is
the same flow pair (`Marks: [{}]` semantically). PyYAML reads both back as `{'Marks': [{}]}`; our own
reader tokenizes the member as `PropertyName,ObjectStart,ObjectEnd`, i.e. the ordinary mapping stream
with no members, so no consumer of the token stream needs a special case. Mappings that have members
are byte-for-byte what they always were — the writer only supplies the form it owed an empty one.

Nullable leaves are **supported** in both directions. The writer emits the YAML null literal —
`- null` as a sequence item (inside the dash block of a nested sequence too) — so `List<int?>`,
`List<string?>`, `List<Dto?>`, `List<List<int?>>`, `List<List<MyEnum?>>`, `List<List<Dto?>>` and
`Dictionary<string, int?>` all keep their nulls, and a null element, an empty collection (an empty
sequence is `V: []`, or `- []` for an inner one) and a missing member stay three different
documents. A null *member* — the collection itself being null — is still omitted rather than
written (see [Null Handling](#null-handling)).

Not representable, dropped at generation time with a warning (never emitted as code that would
not compile):

- a **recursive collection element** (`List<Node>` on `Node`) or a **recursive dict value**
  (`Dictionary<string, Node>` on `Node`) reports `PICOSERDE003` as a `⏸` debt — YAML can carry the
  cycle, the emitter has no route for a block sequence inside an array yet. A recursive **plain
  member** (`Node Child` on `Node`) is supported and round-trips as nested mappings
  (`RecursionRoundTripTests.Yaml_SelfRecursion_RoundTripsTheWholeSpine`).

The generator diagnostic is the authority for your build — `dotnet build 2>&1 | grep PICOSERDE`.
The full five-format matrix lives in the
[root README](../README.md#format-limits-what-each-wire-format-cannot-express).

## Performance

**From the 2026-10-29 window (see the [archive](../docs/perf/measurement.md#per-scenario-tables-2026-10-29-window)).**
CoreCLR only, because that is the only mode in which YamlDotNet's typed layer exists at all (see below). Each pair
is `PicoYaml vs YamlDotNet 18.1.0` in µs/op: per arm the median of 5 independent runs × ≥100k iterations, quoted
as the median of three rounds (2026-10-29, 17:49–18:24 local — AMD Ryzen 7 6800H, Windows 11, .NET SDK 10.0.401,
runtime 10.0.12) behind the harness's idle-machine gate. **The multiple is that pair divided, larger by the
smaller** — `6.293 ÷ 0.138 = 45.60 → 45.6×` — read off the µs themselves, with the winner put in words. No row is
inside the ±10 % band, so every row below is a direction claim.

**PicoYaml has a comparable opponent on CoreCLR and is faster against it: ahead in 6 of 6 scenarios,
0.02×–0.06× of YamlDotNet's cost (row median 0.04×; the row multiples run 15.7×–45.6×).**

| Scenario | PicoYaml | YamlDotNet | Faster by |
|---|---:|---:|---|
| Simple — SerializeToString | 0.138 | 6.293 | **PicoYaml 45.6×** |
| Collection — SerializeToString | 1.514 | 60.867 | **PicoYaml 40.2×** |
| Nested — SerializeToString | 0.552 | 16.288 | **PicoYaml 29.5×** |
| Simple — Deserialize←string | 0.239 | 5.297 | **PicoYaml 22.2×** |
| Nested — Deserialize←string | 0.913 | 15.539 | **PicoYaml 17.0×** |
| Collection — Deserialize←string | 4.752 | 74.536 | **PicoYaml 15.7×** |

Every row carries the division of its own pair, the top one included. Read that top row as a factor of ten, not as
two significant digits: `Simple — SerializeToString` could not use its second and third kept rounds, because the
YamlDotNet arm was refused *unstable* in both (`r27`, `r28`), so the row rests on `r26`/`r36`/`r39` and reads
0.135 / 0.138 / 0.159 µs on our arm — a 1.18× swing across rounds that each passed their own five-run criteria —
against 6.108 / 6.377 / 6.293 µs on YamlDotNet's. That is why its 0.02× is quoted as the published definition
rather than as a claim about its second decimal; its µs pair and its division are published all the same.

Two more properties are stated rather than left to be discovered. Our deserialize rows pay an extra cost theirs
does not: `Deserialize<T>(byte[])` decodes UTF-8 inside the timed region while `Deserialize<T>(string)` on the
other side receives a string built outside it. And the margin is wide on both sides of the table, write and read.

**There is no AOT opponent, and that is a property of YamlDotNet rather than a gap in this measurement.** Under
NativeAOT the same two typed calls do not work at all: `Deserialize<T>` throws `MissingMethodException`, and
`Serialize` **silently produces `{}`** — no exception, no warning, an empty document. Its DOM parsing does still
work ahead of time; only the typed POCO layer does not (see the
[competitor/AOT matrix](../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim)). Comparing
our AOT numbers against an arm that emits `{}` would measure nothing, and comparing them against its DOM parse
would be exactly the non-equivalent comparison this repository refuses to make for INI — so YAML gets **one**
table, on CoreCLR, against a real opponent.

The self-benchmark is gone, and so is the "Serialize vs Deserialize" ratio it used to print: that ratio was built
from two of our own operations, and its "Deserialize" row was in fact calling `SerializeToUtf8Bytes`, so it
measured nothing meaningful.

Run `dotnet run -c Release --project PicoYaml/benchmarks/PicoYaml.Benchmarks` for current local timings.

## Why PicoYaml

| | PicoYaml | YamlDotNet | VYaml |
|--|:---:|:---:|:---:|
| Typed POCO mapping under AOT | ✅ | ❌ | ⚠️¹ |
| Zero attributes | ✅ | ✅ | ❌ |
| Zero reflection | ✅ | ❌ | ❌ |
| ref struct readers | ✅ | ❌ | ❌ |

> The AOT row means *typed POCO mapping ahead of time*, which is what a serializer user
> needs: YamlDotNet's parser does run under NativeAOT, its reflection-based typed mapping does
> not.
>
> ¹ VYaml has source generator support but requires `[YamlObject]` on every type; this
> repo did not verify VYaml under NativeAOT either way.

## Packages

| Package | Description |
|---------|-------------|
| `PicoYaml` | Runtime library |
| `PicoYaml.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
