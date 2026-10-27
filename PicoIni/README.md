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

**No performance numbers are published right now** — see the
[root README](../README.md#performance). The ratio that used to be here was not a measurement
of the two libraries: our serialization was compared against a hand-written DOM parse plus
typed mapping on the ini-parser side, and both actions went through `Benchmark.Compare` in the
wrong slots. It has been withdrawn rather than corrected in place.

Capability fact that does not depend on measurement: **ini-parser's DOM parsing works under
NativeAOT** — it has no reflection-free *typed* mapping, so it can parse a document ahead of
time but cannot produce a POCO. PicoIni does both. Run
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
