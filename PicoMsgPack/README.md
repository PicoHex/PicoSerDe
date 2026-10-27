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

**PicoMsgPack is slower than MessagePack-CSharp on these micro-benchmarks, and the figures are
now published.** The full tables, the machine and the log behind every number live in the root
README's [Measured results](../README.md#measured-results-2026-10-27); the MessagePack-specific
summary:

- **AOT vs AOT, 4 scenarios — slower in 4 of 4.** Range 1.15×–3.53×, median 1.36×:
  `Person — Serialize` 0.116 vs 0.101 µs (1.15×), `Metrics — Deserialize` 0.519 vs 0.147 µs
  (**3.53×**, the widest gap in the whole measurement). Wire size, same run:
  `Person` 16 B vs 14 B, `Metrics` 53 B vs 26 B.
- **JIT vs JIT, 8 scenarios — slower in 4 of 8.** `Simple` and `Nested` (serialize and
  deserialize) land at 0.94×–0.98×, i.e. inside each other's dispersion and not a win;
  `Complex` and `Collection` are 1.71×–2.16× behind.

Both tables come from 5 independent runs × ≥100,000 iterations per arm on 2026-10-27, on an AMD
Ryzen 7 6800H (Windows 11, .NET 10.0.12) that was **not fully idle** (6%–23% other-CPU on these
tables). The AOT table uses a different model set from the JIT one on purpose — only statically
resolvable types run under MessagePack-CSharp's AOT path — so the two must not be aligned by
scenario name, and AOT and JIT are never merged into one figure.

The withdrawn table is still worth knowing about, because it is why every ratio in this repository
is printed as `<ours> ÷ <theirs>`: the harness had PicoSerDe's action in PicoBench's *baseline*
slot at all 26 call sites, so each ratio and each column header came out backwards. A runtime role
guard now throws on that inversion.

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
