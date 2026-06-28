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
- **Ref struct serialization** — `ref struct` types can be serialized directly via source-generated static methods
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>` (PipeReader)
- Unicode escape sequences including surrogate pairs

## Options

```csharp
using PicoJetson;

// Compact (default) — optimal for data transfer
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model);

// Human-readable
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { Indented = true });

// CamelCase naming
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

// Skip null properties
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

// Accept trailing commas and comments (lenient parsing)
var result = JsonSerializer.Deserialize<MyModel>(json,
    new JsonOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
```

### Available Options

| Option | Default | Description |
|--------|:-------:|-------------|
| `Indented` | `false` | Human-readable indented output |
| `MaxDepth` | `63` | Maximum nesting depth |
| `PropertyNamingPolicy` | `null` | `JsonNamingPolicy.CamelCase`, `.SnakeCaseLower`, `.KebabCaseLower`, `.PascalCase` |
| `DefaultIgnoreCondition` | `Never` | `WhenWritingNull` / `WhenWritingDefault` |
| `NumberHandling` | `Strict` | `AllowNamedFloatingPointLiterals` (NaN/Inf) |
| `AllowTrailingCommas` | `false` | Accept trailing commas |
| `ReadCommentHandling` | `Disallow` | `Skip` — ignore `//` and `/* */` |
| `UnmappedMemberHandling` | `Skip` | `Disallow` — throw on unknown properties |

## JSON Lines (JSONL)

JSON Lines (`.jsonl` / NDJSON) — each line is a complete JSON value, per [jsonlines.org](https://jsonlines.org/).

```csharp
using PicoJetson;

// Sync batch: serialize/deserialize collections
var people = new[] { new Person("Alice", 30), new Person("Bob", 25) };
byte[] jsonl = JsonSerializer.SerializeLines(people);
// → {"Name":"Alice","Age":30}\n{"Name":"Bob","Age":25}\n

var restored = JsonSerializer.DeserializeLines<Person>(jsonl);
// → [ Person { Alice, 30 }, Person { Bob, 25 } ]

// Streaming: process large files line-by-line
using var stream = File.OpenRead("data.jsonl");
await foreach (var person in JsonSerializer.DeserializeAsyncEnumerable<Person>(stream))
{
    Console.WriteLine(person.Name);
}

// Streaming JSON array mode (root-level [...])
using var arrStream = new MemoryStream("[{\"Name\":\"A\"},{\"Name\":\"B\"}]"u8.ToArray());
await foreach (var p in JsonSerializer.DeserializeAsyncEnumerable<Person>(
    arrStream, topLevelValues: false))
{
    Console.WriteLine(p.Name);
}
```

### JSONL API Reference

| Method | Description |
|--------|-------------|
| `SerializeLines<T>(IEnumerable<T>)` | Serialize collection to `byte[]` with `\n` between values |
| `DeserializeLines<T>(ReadOnlySpan<byte>)` | Deserialize JSONL byte span to `T?[]` |
| `DeserializeAsyncEnumerable<T>(Stream, topLevelValues, options, ct)` | `IAsyncEnumerable<T?>` — `topLevelValues: true` = JSONL, `false` = root-level JSON array `[...]` |

## Attributes

| Attribute | Description |
|-----------|-------------|
| `[PicoSerializable]` | Generate serializer for all referenced formats |
| `[PicoJsonSerializable]` | Generate JSON serializer only |
| `[JsonPropertyName("name")]` | Override JSON key name |
| `[JsonIgnore]` | Exclude from serialization |
| `[JsonCamelCase]` | camelCase naming per class |
| `[JsonConverter(typeof(T))]` | Custom converter |
| `[JsonConstructor]` | Constructor for immutable deserialization |
| `[JsonDateTimeFormat("format")]` | Custom DateTime format |

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
