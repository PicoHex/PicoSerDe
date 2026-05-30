# PicoYaml

The **only AOT-compatible YAML library** for .NET. Zero-reflection source generation with stack-allocated readers/writers.

[![NuGet](https://img.shields.io/nuget/v/PicoYaml)](https://www.nuget.org/packages/PicoYaml)

## Install

```bash
dotnet add package PicoYaml
dotnet add package PicoYaml.Gen
```

## Quick Start

```csharp
using PicoYaml;

public class Config
{
    public string Name { get; set; } = "";
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

- **YAML format** — indentation-based, flow style, anchors & aliases
- **Zero heap allocation** — `ref struct` reader/writer
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Only AOT YAML library** — YamlDotNet and VYaml cannot run under NativeAOT
- **Anchors & aliases** — `&name` / `*name` with self-referencing support
- **Multi-document** — `---` separator support
- **Complex keys** — `? key\n: value` syntax
- **Flow style** — inline `{key: value}` blocks
- **SIMD-accelerated** whitespace skipping
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[YamlKey("custom_name")]             // override key name
[YamlIgnore]                          // exclude property
[YamlCamelCase]                       // camelCase keys
[YamlConverter(typeof(MyConverter))]  // custom converter
[YamlDateTimeFormat("yyyy-MM-dd")]   // custom DateTime format
```

## Performance

AOT self-contained, .NET 10, 100K iterations:

| Benchmark | Serialize | Deserialize | Ratio |
|-----------|:---------:|:-----------:|:-----:|
| Simple | 0.1μs | 0.3μs | 0.48 |
| Nested | 0.5μs | 0.5μs | 0.88 |
| Collection | 3.5μs | 3.7μs | 0.95 |

## Why PicoYaml

| | PicoYaml | YamlDotNet | VYaml |
|--|:---:|:---:|:---:|
| AOT-compatible | ✅ | ❌ | ⚠️¹ |
| Zero attributes | ✅ | ✅ | ❌ |
| Zero reflection | ✅ | ❌ | ❌ |
| ref struct readers | ✅ | ❌ | ❌ |

> ¹ VYaml has source generator support but requires `[YamlObject]` on every type.

## Packages

| Package | Description |
|---------|-------------|
| `PicoYaml` | Runtime library |
| `PicoYaml.Gen` | Roslyn source generator |

## License

MIT
