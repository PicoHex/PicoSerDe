# PicoYaml

The only YAML library in this comparison that maps **typed POCOs** under NativeAOT —
reflection-free source generation with `ref struct` readers/writers. Supports indentation-based
block mappings/sequences, anchors & aliases, and multi-document streams.

**Coverage:** Core YAML 1.2 subset. Complex block scalar combinations,
advanced merge key patterns, full tag resolution, and flow sequences (`[a, b]`)
are not yet supported (the writer only emits the empty-sequence marker `[]`, which the
reader accepts). Flow mappings (`{key: value}`) are partially supported.

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
- **Flow mappings** — inline `{key: value}` blocks (partial; `[...]` flow sequences are not supported yet)
- **SIMD-accelerated** whitespace skipping
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

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

Nullable leaves are **supported** in both directions. The writer emits the YAML null literal —
`- null` as a sequence item (inside the dash block of a nested sequence too) — so `List<int?>`,
`List<string?>`, `List<Dto?>`, `List<List<int?>>`, `List<List<MyEnum?>>`, `List<List<Dto?>>` and
`Dictionary<string, int?>` all keep their nulls, and a null element, an empty collection (an empty
sequence is `V: []`, or `- []` for an inner one) and a missing member stay three different
documents. A null *member* — the collection itself being null — is still omitted rather than
written (see [Null Handling](#null-handling)).

Not representable, dropped at generation time with a warning (never emitted as code that would
not compile):

- a **recursive member** (`Node Child` on `Node`) reports `PICOSERDE003`.

The generator diagnostic is the authority for your build — `dotnet build 2>&1 | grep PICOSERDE`.
The full five-format matrix lives in the
[root README](../README.md#format-limits-what-each-wire-format-cannot-express).

## Performance

**PicoYaml has a comparable opponent on CoreCLR, and against it PicoSerDe is faster: 0.02×–0.06× of
YamlDotNet's cost, 6 of 6 scenarios (17×–50×).** The root README's
[Measured results](../docs/perf/measurement-2026-10-27.md#measured-results-2026-10-27) section carries the full table — median
µs/op with min–max ±stddev, ratio printed as **ours ÷ theirs**, above 1.00 meaning we are slower.
`Simple — SerializeToString` 0.163 vs 7.107 µs, `Collection — Deserialize←string` 5.301 vs
84.649 µs.

Read it as a factor of ten, not two significant digits. Our `Simple — SerializeToString` is
0.163 ±0.033 — a stddev of 20% of the median — while every YamlDotNet arm sits at 0.7%–4.2% of its
own, so the jitter is on our side of the division. YamlDotNet's two `Collection` arms also converged
on 5 and 4 warm-up bursts (against 174 and 60 for ours), which the protocol allows and which is
worth knowing before quoting `0.02×`. And our deserialize rows pay an extra cost theirs does not:
`Deserialize<T>(byte[])` decodes UTF-8 inside the timed region while `Deserialize<T>(string)` on the
other side receives a string built outside it.

**There is no AOT opponent, and that is a property of YamlDotNet rather than a gap in this
measurement.** Under NativeAOT the same two typed calls do not work at all: `Deserialize<T>` throws
`MissingMethodException`, and `Serialize` **silently produces `{}`** — no exception, no warning, an
empty document. Its DOM parsing does still work ahead of time; only the typed POCO layer does not
(see the [competitor/AOT matrix](../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim)).
Comparing our AOT numbers against an arm that emits `{}` would measure nothing, and comparing them
against its DOM parse would be exactly the non-equivalent comparison this repository refuses to make
for INI — so YAML gets **one** table, on CoreCLR, against a real opponent.

The self-benchmark is gone, and so is the "Serialize vs Deserialize" ratio it used to print: that
ratio was built from two of our own operations, and its "Deserialize" row was in fact calling
`SerializeToUtf8Bytes`, so it measured nothing meaningful.

Run `dotnet run -c Release --project PicoYaml/benchmarks/PicoYaml.Benchmarks` for current local
timings.

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
