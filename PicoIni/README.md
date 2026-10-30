# PicoIni

AOT-first, reflection-free INI serializer.

[![NuGet](https://img.shields.io/nuget/v/PicoIni)](https://www.nuget.org/packages/PicoIni)

## Install

```bash
dotnet add package PicoIni
# The source generator is embedded in PicoIni (analyzers/dotnet/cs) — no separate .Gen reference needed
```

## Quick Start

```csharp
using PicoIni;

public class AppConfig
{
    public string Title { get; set; } = string.Empty;
    public ServerConfig Server { get; set; } = new();
}

public class ServerConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
}

var config = new AppConfig { Title = "MyApp", Server = new() { Host = "localhost", Port = 8080 } };
var ini = IniSerializer.Serialize(config);
// Title = MyApp
//
// [Server]
// Host = localhost
// Port = 8080
```

## Features

- **INI format** — sections, key-value pairs, comments (`;` / `#`)
- **`ref struct`** reader/writer — stack-allocated on the hot path
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Ref struct serialization** — serialize `ref struct` types directly
- **Quoted values** — with escape sequence support (`\"`, `\n`, `\t`, `\r`, `\\`)
- **Case-insensitive** property name matching
- **Zero-allocation formatting** — stackalloc-based UTF-8 encoding for DateTime, Guid, TimeSpan
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[PicoSerializable]                     // all formats
[PicoIniSerializable]                  // INI only
[IniKey("custom_name")]               // override key name
[IniSection("MySection")]              // override section name
[IniIgnore]                            // exclude property
[IniComment("doc comment")]            // emit comment
[IniCamelCase]                         // camelCase keys
[IniConverter(typeof(MyConverter))]    // custom converter
[IniDateTimeFormat("yyyy-MM-dd")]     // custom DateTime format
```

## Options

`IniSerializer.Serialize` / `Deserialize` accept `IniOptions`:

- `Indented` — indents section contents by two spaces (default: compact)
- `DefaultIgnoreCondition` — accepted for API uniformity; INI has no null value on the write side, so null members are always omitted

## Null Handling

- **Write:** a null member is omitted — INI has no null value. Omission means a
  null does **not** round-trip: the property keeps its initializer (or stays
  `null` when it has none) on read.
- **Read:** `Key =` with nothing after it, and the unquoted word `null` (any
  case), are null literals and assign `null` to nullable members. An unquoted
  empty value used to produce `""`; it is now `null`.
- A quoted `""` is the empty string and a quoted `"null"` is the four-character
  string — quoted scalars are never null.
- A null literal aimed at a **non-nullable value type** throws
  `FormatException`; an unannotated `string` member receives `null` (the
  generators' NRT-lenient contract).

## Limits

- **No object-list *members*.** INI has no array-of-tables syntax, so a `List<SomeDto>` property
  cannot be represented and is dropped. This is the one unsupported shape that is currently
  **not** reported: no `PICOSERDE004` warning is emitted for it, so check the member with a
  round-trip test if a list mysteriously comes back empty. If you need an object list inside a
  DTO, use **TOML**, **YAML** or **JSON** instead.
- **A top-level `List<SomeDto>` does round-trip** — each item is written as a numbered section
  (`[0]`, `[1]`, ...).
- **Nested lists, nullable elements and recursion are reported.** `List<List<int>>`, `List<int?>`
  and a recursive member (`Node Child` on `Node`) are dropped with `PICOSERDE004` /
  `PICOSERDE003` — visible in the build log, not silent.
- Objects nested deeper than one level throw `NotSupportedException` instead of silently losing data.
- List values parse strictly and culture-invariantly: malformed elements throw `FormatException`; `List<int>`, `List<bool>`, `List<DateOnly>`, `List<TimeSpan>`, etc. round-trip.
- Enum values parse strictly too: an unparseable enum text throws `FormatException` naming the member and offset. It previously fell back silently to the default (`0`) enum member.

## Performance

**No ratio is published for this format; the absolute cost is.** The reason is not modesty but that
**no equivalent operation exists to divide by**: ini-parser 2.5.3 exposes **0 public generic methods
and 0 parameters of type `System.Type`**, and its only entry point is `Parse(string) → IniData` — a
DOM. Any POCO mapping on its side is therefore the caller's own code, by definition, so it is never
"one library call" against ours, in any execution mode. The root README's
[Measured results](../docs/perf/measurement-2026-10-27.md#measured-results-2026-10-27) section carries PicoIni's own
per-scenario cost — nine scenarios, median µs/op with min–max ±stddev — under a table labelled
`NON-EQUIVALENT`, with the competitor column deliberately left out so that no reader is invited to
divide one arm by the other. `equivalent=false` in that run's summary line.

Two rows are weaker than the rest and are flagged rather than published as equals: the small
`Simple — Deserialize` case is quoted only as "both sessions landed in the 0.13–0.17 µs band" — its
median moved −49.2% between the two sessions, the only row in the whole set to move more than 20%,
and the earlier value carried an outlier run — and `Simple — Serialize` has a max of 0.161 against a
median of 0.106.

The ratio that used to be here was therefore not a measurement of the two libraries, and both actions
also went through `Benchmark.Compare` in the wrong slots; it has been withdrawn rather than corrected
in place.

Capability fact that does not depend on measurement: **ini-parser's DOM parsing works under
NativeAOT** — it has no reflection-free *typed* mapping, so it can parse a document ahead of time
but cannot produce a POCO. PicoIni does both. Run
`dotnet run -c Release --project PicoIni/benchmarks/PicoIni.Benchmarks` for current local
timings.

## Low-Level API

```csharp
var reader = new IniReader(data);
while (reader.Read())
{
    if (reader.TokenType == TokenType.PropertyName)
        var key = Encoding.UTF8.GetString(reader.GetStringRaw());
    // value follows on next Read() call
}
```

## Packages

| Package | Description |
|---------|-------------|
| `PicoIni` | Runtime library |
| `PicoIni.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
