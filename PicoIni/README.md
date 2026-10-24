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

## Limitations

- Nested object lists (`List<SomeDto>` as a property) cannot be represented by flat INI sections and are ignored.
- Objects nested deeper than one level throw `NotSupportedException` instead of silently losing data.
- List values parse strictly and culture-invariantly: malformed elements throw `FormatException`; `List<int>`, `List<bool>`, `List<DateOnly>`, `List<TimeSpan>`, etc. round-trip.
- Enum values parse strictly too: an unparseable enum text throws `FormatException` naming the member and offset. It previously fell back silently to the default (`0`) enum member.

## Performance

AOT self-contained, .NET 10, 100K iterations:

| Benchmark | PicoIni | ini-parser¹ | Ratio |
|-----------|:------:|:----------:|:-----:|
| Simple Serialize | 1.4μs | 0.2μs | 0.16x |
| Simple Deserialize | 11.3μs | 0.6μs | 0.05x |
| Complex Serialize | 2.3μs | 0.5μs | 0.23x |

> ¹ ini-parser uses runtime reflection — incompatible with NativeAOT/trimming.
> PicoIni prioritizes AOT deployability over JIT peak throughput.

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
