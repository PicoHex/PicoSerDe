# PicoToml

AOT-first, reflection-free TOML serializer.

[![NuGet](https://img.shields.io/nuget/v/PicoToml)](https://www.nuget.org/packages/PicoToml)

## Install

```bash
dotnet add package PicoToml
# The source generator is embedded in PicoToml (analyzers/dotnet/cs) — no separate .Gen reference needed
```

## Quick Start

```csharp
using PicoToml;

public class AppConfig
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool Enabled { get; set; }
}

var config = new AppConfig { Name = "MyApp", Count = 42, Enabled = true };
var toml = TomlSerializer.Serialize(config);
// Name = "MyApp"
// Count = 42
// Enabled = true

var restored = TomlSerializer.Deserialize<AppConfig>(Encoding.UTF8.GetBytes(toml));
```

## Features

- **TOML format** — tables, arrays, inline tables, multi-line strings
- **`ref struct`** reader/writer — stack-allocated on the hot path
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Ref struct serialization** — serialize `ref struct` types directly
- **Multi-line strings** — basic (`"""`) and literal (`'''`) support
- **Inline tables** — `{key = value, ...}` syntax
- **Array tables** — `[[array]]` syntax
- **SIMD-accelerated** whitespace skipping
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[PicoSerializable]                     // all formats
[PicoTomlSerializable]                 // TOML only
[TomlKey("custom_name")]              // override key name
[TomlIgnore]                           // exclude property
[TomlCamelCase]                        // camelCase keys
[TomlConverter(typeof(MyConverter))]   // custom converter
[TomlDateTimeFormat("yyyy-MM-dd")]    // custom DateTime format
```

## Options

`TomlSerializer.Serialize` / `Deserialize` accept `TomlOptions`:

- `Indented` — indents table contents by two spaces (default: compact)
- `DefaultIgnoreCondition` — accepted for API uniformity; TOML has no null value on the write side, so null members are always omitted

## Null Handling

- **Write:** a null member is omitted — TOML has no null value. Omission means a
  null does **not** round-trip: the property keeps its initializer (or stays
  `null` when it has none) on read.
- **Read:** `Key =` with nothing after it, and the unquoted word `null` (any
  case), are null literals and assign `null` to nullable members.
- A quoted `""` is the empty string and a quoted `"null"` is the four-character
  string — quoted scalars are never null.
- A null literal aimed at a **non-nullable value type** throws
  `FormatException`; an unannotated `string` member receives `null` (the
  generators' NRT-lenient contract).

## Limits

- **Object lists** — `List<Dto>` is written as TOML array tables (`[[Rows]]`) and round-trips.
- **Nested primitive arrays** — `List<List<int>>`, `List<int[]>`, `int[][]` are written as an
  inline array of inline arrays (`Grid = [[1, 2], [3]]`) and round-trip. The representable leaf
  kinds are the scalars the TOML list writer has a case for: `string`, the integer widths,
  `float`/`double`, `bool`.
- **`List<List<Dto>>` is not representable** — TOML has no "array table of array tables", so the
  member is dropped and the build reports `PICOSERDE004`
  (`Member 'X.Y' cannot be represented by this format — it will be ignored`). A nested chain with
  a nullable leaf (`List<List<int?>>`) is dropped the same way.
- **Recursive members** (`Node Child` on `Node`) are dropped with `PICOSERDE003`; the rest of the
  document is still written, so a recursive DTO serializes "partially". JSON/MsgPack keep the
  member.
- Objects nested deeper than one level throw `NotSupportedException` instead of silently losing data.

Confirm what was dropped with `dotnet build 2>&1 | grep PICOSERDE` (the diagnostics carry
`Location.None`, so `#pragma warning disable` cannot silence them). The full five-format matrix
lives in the [root README](../README.md#format-limits-what-each-wire-format-cannot-express).

## Performance

AOT self-contained, .NET 10, 100K iterations:

| Benchmark | PicoToml | Tommy¹ | Ratio |
|-----------|:-------:|:-----:|:-----:|
| Simple Serialize | 0.9μs | 0.3μs | 0.31x |
| Simple Deserialize | 1.2μs | 0.5μs | 0.48x |
| Nested Serialize | 2.7μs | 0.6μs | 0.21x |
| Nested Deserialize | 2.7μs | 0.6μs | 0.23x |

> ¹ Tommy uses runtime reflection — incompatible with NativeAOT/trimming.
> PicoToml prioritizes AOT deployability over JIT peak throughput.

## Low-Level API

```csharp
var reader = new TomlReader(data);
while (reader.Read())
{
    if (reader.TokenType == TokenType.PropertyName)
        var key = Encoding.UTF8.GetString(reader.KeySpan);
    var value = Encoding.UTF8.GetString(reader.ValueSpan);
}
```

## Packages

| Package | Description |
|---------|-------------|
| `PicoToml` | Runtime library |
| `PicoToml.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
