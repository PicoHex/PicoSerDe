# PicoSerDe

High-performance, AOT-first serialization framework for the PicoHex ecosystem. Zero-reflection source generation with `ref struct` readers/writers for zero heap allocation.

## Format Support

| Format | Library | Status | Features |
|--------|---------|--------|----------|
| JSON | PicoJson | ✅ Production | RFC 8259, SIMD-accelerated, Unicode escapes |
| INI | PicoIni | ✅ Production | Sections, comments, quoting, case-insensitive |
| TOML | PicoToml | ✅ Beta | Tables, arrays, inline tables, multi-line strings |
| YAML | PicoYaml | ✅ Beta | Indentation-based, flow style, anchors |
| MessagePack | PicoMsgPack | ✅ Beta | Binary, fixint/fixstr/fixmap, extension types |

## Quick Start

### Installation

```bash
dotnet add package PicoJson
dotnet add package PicoJson.Gen
```

### Usage

```csharp
using PicoJson;

public record Person(string Name, int Age);

// Serialize — source generator creates the implementation automatically
var json = JsonSerializer.Serialize(new Person("Alice", 30));
// → {"Name":"Alice","Age":30}

// Deserialize
var person = JsonSerializer.Deserialize<Person>(
    Encoding.UTF8.GetBytes(json));
// → Person { Name = "Alice", Age = 30 }
```

No attributes required. Add `[JsonPropertyName("custom_name")]` for custom keys. Add `[JsonIgnore]` to exclude properties.

### INI Example

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

### Low-Level Reader/Writer API

All formats provide `ref struct` readers and writers for zero-allocation streaming:

```csharp
var reader = new JsonReader(Encoding.UTF8.GetBytes(json));
while (reader.Read())
{
    switch (reader.TokenType)
    {
        case TokenType.PropertyName:
            var name = reader.GetStringRaw();
            break;
        case TokenType.String:
            var value = reader.GetStringRaw();
            break;
        case TokenType.Int32:
            reader.TryGetInt32(out var n);
            break;
    }
}
```

## Performance

- **Zero heap allocation** on the hot path — all readers/writers are `ref struct`
- **SIMD-accelerated** whitespace and backslash scanning (Vector128)
- **Zero-copy string access** — `GetStringRaw()` returns `ReadOnlySpan<byte>` pointing into source buffer for unescaped strings
- **AOT-compatible** — zero reflection, 100% source generator driven, `IsAotCompatible=true`

## Project Structure

```
PicoSerDe/
├── PicoSerDe.Abs/          # Shared abstractions (ISerializer<T>, IDeserializer<T>, TokenType)
├── PicoJson/               # JSON format (src, gen, tests, benchmarks, samples)
├── PicoIni/                # INI format (src, gen, tests, benchmarks, samples)
├── PicoToml/               # TOML format (src, gen, tests, benchmarks, samples)
├── PicoYaml/               # YAML format (src, gen, tests, benchmarks, samples)
├── shared/                 # Shared utilities
├── docs/superpowers/       # Design specs and implementation plans
└── .github/workflows/      # CI/CD (5-platform matrix + AOT validation)
```

## Design

See [docs/superpowers/specs/](docs/superpowers/specs/) for detailed design documents:
- [Token Layer Design](docs/superpowers/specs/2026-05-24-token-layer-design.md)
- [PicoJson Design](docs/superpowers/specs/2026-05-24-picojson-design.md)
- [PicoIni Design](docs/superpowers/specs/2026-05-26-picoini-design.md)

## License

MIT
