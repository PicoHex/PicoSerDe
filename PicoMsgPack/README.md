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

**PicoMsgPack is slower than MessagePack-CSharp on these micro-benchmarks.** The full tables, the
machine and the log behind every number live in the root README's
[Measured results](../README.md#measured-results-2026-10-27); this page keeps the
MessagePack-specific summary. Every ratio is **ours ÷ theirs**: above 1.00 we are slower.

- **AOT vs AOT, 4 scenarios — slower in 3 of 4.** Range 0.93×–3.34×, median 1.40×:
  `Person — Deserialize` 0.162 vs 0.131 µs (1.24×), `Metrics — Serialize` 0.187 vs 0.120 µs
  (1.55×), `Metrics — Deserialize` 0.478 vs 0.143 µs (**3.34×**, the widest gap in the whole
  measurement). `Person — Serialize` is the one row on our side, 0.096 vs 0.104 µs (0.93×), and it
  is 7 points from 1.00 — inside the ±10% cross-session band, so it is reported and not claimed.
  Wire size, same run: `Person` 16 B vs 14 B, `Metrics` 53 B vs 26 B.
- **JIT vs JIT, 8 scenarios — slower in 5 of 8.** `Complex` and `Collection` are 1.84×–2.26× behind.
  Three rows are ahead — 0.76×, 0.89× and 0.98× — and two of them are inside the noise: `Simple —
  Serialize`'s 0.76× comes from the least stable arm on the page (0.058 with a max of 0.101 and a
  stddev of 0.019 — 33% of the median — against a counterpart at 0.077 ±0.000, so the ranges
  overlap), and `Nested — Deserialize`'s 0.98× overlaps the same way. `Nested — Serialize`'s
  **0.89×** is the one measurable MessagePack win here: the ranges do not overlap (0.215–0.219 vs
  0.243–0.246) and 11 points sits just outside the ±10% band. The other small row,
  `Simple — Deserialize` (1.03×), is a loss inside the band.

Both tables come from 5 independent runs × ≥100,000 iterations per arm on 2026-10-27, on an AMD
Ryzen 7 6800H (Windows 11, .NET 10.0.12) that was **not fully idle** (5%–25% other-CPU on the AOT
table, 8%–21% on the JIT one, against the protocol's 35% limit; no arm was refused). A single
session's ±stddev is not a cross-session error bar: five runs in one process share a JIT and a
thermal state, so the honest band between sessions is about ±10% — the root README explains this
under
[One session's stddev is not a cross-session error bar](../README.md#one-sessions-stddev-is-not-a-cross-session-error-bar),
with this format's AOT `Person — Serialize` as the worked example (±1% within the session, 17%
between sessions).

The AOT table uses a different model set from the JIT one on purpose — only statically resolvable
types run under MessagePack-CSharp's AOT path — so the two must not be aligned by scenario name, and
AOT and JIT are never merged into one figure.

The withdrawn table is still worth knowing about, because it is why every ratio in this repository
is printed as `<ours> ÷ <theirs>`: the harness had PicoSerDe's action in PicoBench's *baseline* slot
at every call site, so each ratio and each column header came out backwards. A runtime role guard
now throws on that inversion.

What *is* verified, and is a capability fact rather than a ratio: **MessagePack-CSharp only partly
works under NativeAOT.** It builds formatters for enums and generic collections at run time with
`MakeGenericType` + `Activator.CreateInstance`, which NativeAOT cannot do, so any
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
