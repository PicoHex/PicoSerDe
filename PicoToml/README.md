# PicoToml

Zero-allocation, AOT-first TOML serializer with zero-reflection source generation.

[![NuGet](https://img.shields.io/nuget/v/PicoToml)](https://www.nuget.org/packages/PicoToml)

## Install

```bash
dotnet add package PicoToml
dotnet add package PicoToml.Gen
```

## Quick Start

```csharp
using PicoToml;

public class AppConfig
{
    public string Name { get; set; } = "";
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
- **Zero heap allocation** — `ref struct` reader/writer
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Multi-line strings** — basic (`"""`) and literal (`'''`) support
- **Inline tables** — `{key = value, ...}` syntax
- **Array tables** — `[[array]]` syntax
- **SIMD-accelerated** whitespace skipping
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[TomlKey("custom_name")]             // override key name
[TomlIgnore]                          // exclude property
[TomlCamelCase]                       // camelCase keys
[TomlConverter(typeof(MyConverter))]  // custom converter
[TomlDateTimeFormat("yyyy-MM-dd")]   // custom DateTime format
```

## Performance

AOT self-contained, .NET 10, 100K iterations:

| Benchmark | PicoToml | Tommy | Ratio |
|-----------|:-------:|:-----:|:-----:|
| Serialize String | 0.8μs | 0.2μs | 0.29x |
| Deserialize String | 1.4μs | 0.2μs | 0.15x |
| Nested Serialize | 3.3μs | 0.6μs | 0.20x |

> PicoToml trades JIT peak performance for **full AOT compatibility** — the competitor uses runtime reflection unavailable in AOT.

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
| `PicoToml.Gen` | Roslyn source generator |

## License

MIT
