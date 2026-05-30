# PicoIni

Zero-allocation, AOT-first INI serializer with zero-reflection source generation.

[![NuGet](https://img.shields.io/nuget/v/PicoIni)](https://www.nuget.org/packages/PicoIni)

## Install

```bash
dotnet add package PicoIni
dotnet add package PicoIni.Gen
```

## Quick Start

```csharp
using PicoIni;

public class AppConfig
{
    public string Title { get; set; } = "";
    public ServerConfig Server { get; set; } = new();
}

public class ServerConfig
{
    public string Host { get; set; } = "";
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
- **Zero heap allocation** — `ref struct` reader/writer
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Quoted values** — with escape sequence support (`\"`, `\n`, `\t`, `\r`, `\\`)
- **Case-insensitive** property name matching
- **Zero-allocation formatting** — stackalloc-based UTF-8 encoding for DateTime, Guid, TimeSpan
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[IniKey("custom_name")]             // override key name
[IniSection("MySection")]            // override section name
[IniIgnore]                          // exclude property
[IniComment("doc comment")]          // emit comment
[IniCamelCase]                       // camelCase keys
[IniConverter(typeof(MyConverter))]  // custom converter
[IniDateTimeFormat("yyyy-MM-dd")]   // custom DateTime format
```

## Performance

AOT self-contained, .NET 10, 100K iterations:

| Benchmark | PicoIni | ini-parser | Ratio |
|-----------|:------:|:----------:|:-----:|
| Serialize String | 1.0μs | 0.1μs | 0.13x |
| Deserialize String | 13.4μs | 0.2μs | 0.01x |

> PicoIni trades JIT peak performance for **full AOT compatibility** — the competitor uses runtime reflection unavailable in AOT.

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
| `PicoIni.Gen` | Roslyn source generator |

## License

MIT
