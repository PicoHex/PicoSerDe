# PicoSerDe.Core

Shared serialization infrastructure for the PicoSerDe framework. Provides unified contracts, SIMD-accelerated utilities, and zero-allocation helpers used by all PicoSerDe format libraries.

[![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core)

## Contents

| Component | Description |
|-----------|-------------|
| `ISerializer<T>` | Unified serialization contract |
| `IOptionsSerializer<T>` | Optional extension consumed by generated serializers to receive per-call options (e.g. `Indented`) |
| `IDeserializer<T>` | Unified deserialization contract |
| `TokenType` | Cross-format token enum (ObjectStart, PropertyName, Int32, String, etc.) |
| `SimdHelpers` | SIMD-accelerated whitespace skipping (Vector512/256/128 with scalar fallback) |
| `TextHelpers` | `IsDigit`, `Trim`, `TrimEnd`, case-insensitive byte-span `Eq` |
| `SerializerExtensions` | `RentWriter()` (ThreadStatic pooled), `ThrowNoSerializer<T>` |
| `DeserializerExtensions` | `Stream`, `PipeReader`, `string`, `byte[]` convenience overloads |
| `IFusedTokenReader` | Opt-in fused-read capability of `ITokenReader`: a token is classified and its value decoded in one dispatch (`TryReadInt32/Int64/Float64/Bool`, `TryGetCollectionCount`). Implementing `ITokenReader` alone stays unaffected |
| `TokenReaderFused` | Reference semantics of the fused reads - each method is literally `Read() && TryGetX(out value)`, so a format that does not optimize one forwards here instead of re-deriving the pair |

## Usage

```csharp
using PicoSerDe.Core;

// Implement the interface (or use a source generator)
public class MySerializer : ISerializer<MyType>
{
    public void Serialize(IBufferWriter<byte> writer, MyType value) { ... }
}

// Use built-in extensions
var bytes = serializer.SerializeToBytes(value);
var str = serializer.SerializeToString(value);
```

## License

MIT
