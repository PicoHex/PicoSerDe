# PicoJetson

AOT-first JSON serializer with SIMD-accelerated parsing and source-generated zero-reflection code.

[![NuGet](https://img.shields.io/nuget/v/PicoJetson)](https://www.nuget.org/packages/PicoJetson)

## Install

```bash
dotnet add package PicoJetson
dotnet add package PicoJetson.Gen
```

## Quick Start

```csharp
using PicoJetson;

public record Person(string Name, int Age);

var json = JsonSerializer.Serialize(new Person("Alice", 30));
// → {"Name":"Alice","Age":30}

var person = JsonSerializer.Deserialize<Person>(Encoding.UTF8.GetBytes(json));
// → Person { Name = "Alice", Age = 30 }
```

No attributes required.

## Features

- **RFC 8259** compliant JSON
- **SIMD-accelerated** whitespace & backslash scanning (Vector128)
- **Zero-copy** string access — `GetStringRaw()` returns `ReadOnlySpan<byte>` into source buffer
- **Zero heap allocation** on the hot path — `ref struct` reader/writer
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>` (PipeReader)
- Unicode escape sequences including surrogate pairs

## Customization

```csharp
[JsonPropertyName("custom_name")]  // override key name
[JsonIgnore]                        // exclude property
[JsonCamelCase]                     // camelCase naming per class
[JsonConverter(typeof(MyConverter))] // custom converter
[JsonConstructor]                   // constructor for deserialization
[DateTimeFormat("yyyy-MM-dd")]     // custom DateTime format
```

## Performance

AOT self-contained, .NET 10, 100K iterations:

| Benchmark | PicoJetson | System.Text.Json | Speedup |
|-----------|:----------:|:----------------:|:-------:|
| Complex Serialize | 0.7μs | 1.2μs | **1.77x** |
| Nested Serialize | 0.5μs | 0.9μs | **1.98x** |
| Collection Serialize | 1.7μs | 3.7μs | **2.20x** |

## Low-Level API

```csharp
var reader = new JsonReader(Encoding.UTF8.GetBytes(json));
while (reader.Read())
{
    switch (reader.TokenType)
    {
        case TokenType.PropertyName: var name = reader.GetStringRaw(); break;
        case TokenType.String:       var value = reader.GetStringRaw(); break;
        case TokenType.Int32:        reader.TryGetInt32(out int n); break;
    }
}
```

## Packages

| Package | Description |
|---------|-------------|
| `PicoJetson` | Runtime library |
| `PicoJetson.Gen` | Roslyn source generator |

## License

MIT
