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
- `DefaultIgnoreCondition` — accepted for API uniformity; YAML has no null value on the write side, so null members are always omitted

## Null Handling

- **Write:** a null member is omitted — YAML has no null value on the write
  path (`key:` would read back as a default). Omission means a null does **not**
  round-trip: the property keeps its initializer (or stays `null` when it has
  none) on read.
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

Not representable, dropped at generation time with a warning (never emitted as code that would
not compile):

- a **nullable value leaf** in a nested chain — `List<List<int?>>`, `List<List<MyEnum?>>`,
  `List<List<Dto?>>`, `Dictionary<string, List<int?>>` report `PICOSERDE004`;
- a **recursive member** (`Node Child` on `Node`) reports `PICOSERDE003`.

A nullable *reference* leaf stays supported: `List<List<string?>>` round-trips and null string
items are skipped on write (YAML writes no null). The generator diagnostic is the authority for
your build — `dotnet build 2>&1 | grep PICOSERDE`. The full five-format matrix lives in the
[root README](../README.md#format-limits-what-each-wire-format-cannot-express).

## Performance

**This file reports no ratio and claims no speedup — it has no competitor to measure.**
YamlDotNet parses a DOM under NativeAOT, but its reflection-based typed POCO mapping fails
there (see the [competitor/AOT matrix](../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim)),
so no like-for-like AOT pair exists.

`PicoYaml.Benchmarks` is therefore a **self-benchmark**: one `Benchmark.Run` per operation,
printed as absolute cost with no comparison and no speedup figure. It used to print a
"Serialize vs Deserialize" ratio built from two of our own operations — with the "Deserialize"
row in fact calling `SerializeToUtf8Bytes` — which measured nothing meaningful and is gone.

Run `dotnet run -c Release --project PicoYaml/benchmarks/PicoYaml.Benchmarks` for current
local timings.

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
