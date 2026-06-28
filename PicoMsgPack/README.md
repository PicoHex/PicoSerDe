# PicoMsgPack

AOT-compatible MessagePack serializer with zero-reflection source generation.

[![NuGet](https://img.shields.io/nuget/v/PicoMsgPack)](https://www.nuget.org/packages/PicoMsgPack)

## Install

```bash
dotnet add package PicoMsgPack
dotnet add package PicoMsgPack.Gen
```

## Quick Start

```csharp
using PicoMsgPack;

public class User
{
    [MsgPackKey(0)] public string Name { get; set; } = "";
    [MsgPackKey(1)] public int Age { get; set; }
}

var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new User { Name = "Alice", Age = 30 });
var user = MsgPackSerializer.Deserialize<User>(bytes);
```

## Features

- **Full MessagePack spec** — fixint, fixstr, fixmap, fixarray, bin, ext types
- **Zero heap allocation** — `ref struct` reader/writer, stack-allocated stacks
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Ref struct serialization** — serialize `ref struct` types directly
- **Extension type support** — `TryGetExtension(out byte tag, out ReadOnlySpan<byte> data)`
- **Binary primitives** — `BinaryPrimitives.ReadInt16BigEndian` for efficient decoding
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[PicoSerializable]                     // all formats
[PicoMsgPackSerializable]              // MsgPack only
[MsgPackKey(0)]                        // integer key
[MsgPackIgnore]                        // exclude property
[MsgPackConverter(typeof(MyConv))]     // custom converter
```

## Performance

JIT, .NET 10, 100K iterations:

| Benchmark | PicoMsgPack | MessagePack-CSharp | Speedup |
|-----------|:----------:|:------------------:|:-------:|
| Complex Serialize | 0.4μs | 0.7μs | **1.81x** |
| Collection Deserialize | 0.8μs | 1.7μs | **2.18x** |
| Simple Deserialize | 0.3μs | 0.5μs | **1.79x** |

## Low-Level API

```csharp
var reader = new MsgPackReader(bytes);
while (reader.Read())
{
    switch (reader.TokenType)
    {
        case TokenType.ObjectStart: /* map */ break;
        case TokenType.String: reader.GetStringRaw(); break;
        case TokenType.Int32: reader.TryGetInt32(out int v); break;
        case TokenType.Extension: reader.TryGetExtension(out var tag, out var data); break;
    }
}
```

## Packages

| Package | Description |
|---------|-------------|
| `PicoMsgPack` | Runtime library |
| `PicoMsgPack.Gen` | Roslyn source generator |

## License

MIT
