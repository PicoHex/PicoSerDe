# PicoMsgPack

AOT-compatible MessagePack serializer with zero-reflection source generation.

[![NuGet](https://img.shields.io/nuget/v/PicoMsgPack)](https://www.nuget.org/packages/PicoMsgPack)

## Install

```bash
dotnet add package PicoMsgPack
# The source generator is embedded in PicoMsgPack (analyzers/dotnet/cs) — no separate .Gen reference needed
```

## Quick Start

```csharp
using PicoMsgPack;

public class User
{
    [MsgPackKey(0)] public string Name { get; set; } = string.Empty;
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
- **MessagePack-CSharp interop decoding** — temporal types decode from all
  standard encodings: `DateTime` from timestamp extensions (32/64/96-bit, type -1)
  or strings, `TimeSpan` from strings or integer ticks, `DateOnly` from strings
  or day numbers, `TimeOnly` from strings or integer ticks, enums from strings
  or integers, and unsigned integer encodings (uint8/16/32/64) into integer
  targets with range checks

## Customization

```csharp
[PicoSerializable]                     // all formats
[PicoMsgPackSerializable]              // MsgPack only
[MsgPackKey(0)]                        // integer key
[MsgPackIgnore]                        // exclude property
[MsgPackConverter(typeof(MyConv))]     // custom converter
```

## Strictness

- Empty payloads and bytes after the root value throw `FormatException`
- Deeply nested objects (3+ levels) round-trip
- Member values are type-checked: a `nil` maps to `null` for a nullable member
  and throws `FormatException` for a non-nullable one, and a wrong-typed value
  (e.g. a string where an `int` is declared) throws instead of silently
  becoming a default

## Limits

MessagePack has native arrays and maps, so every shape in the framework's format-limits matrix is
expressible: object lists (`List<Dto>`), nested primitive lists (`List<List<int>>`), nested object
lists (`List<List<Dto>>`), nested nullable elements (`List<List<int?>>`, `nil` elements included)
and recursive members. `PICOSERDE003` never fires for this format.

The drops that do happen come from rules shared with the other formats and are all reported: a
**collection dictionary value** (`Dictionary<string, List<T>>`) and a nested chain with a nullable
*value* leaf are dropped with `PICOSERDE004`; an anonymous-type **collection** member is dropped
with `PICOSERDE005` (the map/array headers need an element count up front, so only the JSON writer
can emit those). A **top-level scalar** target fails loudly instead (`Serialize<Guid>()` throws
`InvalidOperationException`).

See the [root README](../README.md#format-limits-what-each-wire-format-cannot-express) for the
same matrix across all five formats.

## Streaming

`DeserializeFromStreamAsync<T>(stream, ct)` is a **buffering** implementation:
each read is appended to an internal accumulator and the whole buffer is
re-parsed from the beginning until the value is complete. It is correct and
resumes cleanly across chunk boundaries, but it is not incremental (O(n²) in
the number of chunks), and unlike the other formats the overload takes no
options argument.

## Options

`MsgPackSerializer.SerializeToUtf8Bytes` / `Serialize` accept `MsgPackOptions`
(`DefaultIgnoreCondition = WhenWritingNull` omits null members); `Deserialize`
accepts options for API uniformity.

## Performance

**No performance numbers are published right now** — see the
[root README](../README.md#performance). The harness had a defect that inverted every
comparison (PicoSerDe's action passed in PicoBench's *baseline* slot, at all 26 call sites),
so the previous speedup table described the opposite of what it claimed and has been withdrawn.

What *is* verified, and is a capability fact rather than a ratio: **MessagePack-CSharp only
partly works under NativeAOT.** It builds formatters for enums and generic collections at run
time with `MakeGenericType` + `Activator.CreateInstance`, which NativeAOT cannot do, so any
`[MessagePackObject]` containing an enum or a generic collection (`List<int>`,
`Dictionary<string,string>`) throws
`MissingMethodException: No parameterless constructor defined for type ...` on first use.
Types restricted to `string`/`int`/`bool`/`double`/`DateTime` are statically resolvable and do
run ahead of time — that is the model set `PicoMsgPack.Benchmarks.Aot` uses, deliberately. The
unified benchmark's models carry `DayOfWeek`, `Dictionary<string,string>` and `List<int>`, so
that benchmark is JIT-only and says so in its csproj.

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
| `PicoMsgPack.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
