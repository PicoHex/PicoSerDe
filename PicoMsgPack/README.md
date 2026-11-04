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

The drops that do happen come from rules shared with the other formats: the JSON-only **extended
scalars** (`char`, `DateTimeOffset`, `Uri`, `Version`, `Half`, `BigInteger`, `Int128`, `UInt128`,
`nint`) and `ConcurrentDictionary<K,V>` are dropped without a diagnostic; an anonymous-type
**collection** member is dropped with `PICOSERDE005` — a gap in **this generator's
anonymous-type emitter**, not a property of this format. MessagePack holds such a collection
exactly like the shapes above (`List<Dto>`, `List<List<int>>`), and the element count is known
before a byte is written (`List<T>.Count`), so a counted map/array header has nothing missing to
announce — the anonymous-type emitter simply has no collection form for it yet. That is a
recorded debt (⏸, carrying a `reopen:` pointer in the diagnostic), pinned as
`AnonCollectionFormats = { "json" }` in `shared/Gen/DecisionTables.cs:383-386` and read through
`AnonCollectionSupported` / `AnonCollectionIsFormatFact` in
`shared/Gen/FormatCapabilities.cs:342-352`. A **top-level scalar** target fails loudly
instead (`Serialize<Guid>()` throws `InvalidOperationException`).

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
(`DefaultIgnoreCondition = PicoSerDe.Core.PicoIgnoreCondition.WhenWritingNull` omits null members); `Deserialize`
accepts options for API uniformity.

### `DateTime` on the wire

`DateTime` is written as the MessagePack **timestamp extension** (tag `0xFF`): timestamp 32
when the value has no sub-second part and its seconds fit 4 bytes, timestamp 64 (`(nsec << 34)
| sec`: 30 bits of nanoseconds above 34 bits of seconds) while the seconds stay below 2^34, and
timestamp 96 (nanoseconds + signed 64-bit seconds) otherwise. That is byte-for-byte what
MessagePack-CSharp writes — `TimestampInteropTests` checks both directions, to the tick.

The extension carries an **instant**, not a `DateTimeKind`: `Utc` is written as is, `Local` is
converted with `ToUniversalTime()`, `Unspecified` is taken at face value (as UTC), and every
value reads back as `DateTimeKind.Utc`. The reader still accepts the 28-character ISO (`"O"`)
string earlier builds wrote, so already-stored payloads stay readable. Set
`MsgPackOptions.DateTimeFormat = MsgPackDateTimeFormat.IsoString` to write that text form
again.

## Performance

**From the 2026-10-29 window (see the [archive](../docs/perf/measurement.md#per-scenario-tables-2026-10-29-window)).**
Each pair is `PicoMsgPack vs MessagePack-CSharp` in µs/op: per arm the median of 5 independent runs × ≥100k
iterations, quoted as the median of three rounds, every arm measured inside one window (2026-10-29, 17:49–18:24
local — AMD Ryzen 7 6800H, Windows 11, .NET SDK 10.0.401, runtime 10.0.12). **The multiple is that pair divided,
larger by the smaller** — `0.231 ÷ 0.103 = 2.243 → 2.2×` — read off the µs themselves, with the winner put in
words, so no reader has to invert anything. A row inside **~±10 %** of 1.00 is **level**: it prints the harness's
own `ours ÷ theirs` ratio beside it and is not a direction claim either way. AOT and JIT are separate processes
and are never merged into one figure, one column or one multiple.

**In one line: ahead of MessagePack-CSharp in 4 of 4 scenarios under NativeAOT and in 7 of 8 under JIT — the one
JIT loss is `Complex — Deserialize` at 1.12×, stated here rather than smoothed over.**

**NativeAOT — ahead in 4 of 4**, every cell past the band (row median 0.72×, i.e. ≈1.4× faster). The AOT
benchmarks run their own model set — `Person` and `Metrics`, with no collection scenario, because
MessagePack-CSharp's collection formatters do not survive NativeAOT:

| Scenario | PicoMsgPack | MessagePack-CSharp | Faster by |
|---|---:|---:|---|
| Metrics — Serialize | 0.077 | 0.129 | **PicoMsgPack 1.7×** |
| Person — Serialize | 0.072 | 0.104 | **PicoMsgPack 1.4×** |
| Person — Deserialize | 0.115 | 0.153 | **PicoMsgPack 1.3×** |
| Metrics — Deserialize | 0.126 | 0.144 | **PicoMsgPack 1.1×** |

Read side included: no row of this mode is level and none is lost, and the four cells span 0.60×–0.88× —
every one of them past the band, so this column is a direction claim on all four rows.

**JIT — ahead in 7 of 8** (harness 0.45×–1.12×, row median 0.78×, i.e. ≈1.3× faster; six rows past the band,
one level, one lost):

| Scenario | PicoMsgPack | MessagePack-CSharp | Faster by |
|---|---:|---:|---|
| Nested — Serialize | 0.103 | 0.231 | **PicoMsgPack 2.2×** |
| Collection — Deserialize | 0.382 | 0.694 | **PicoMsgPack 1.8×** |
| Collection — Serialize | 0.136 | 0.228 | **PicoMsgPack 1.7×** |
| Simple — Deserialize | 0.076 | 0.100 | **PicoMsgPack 1.3×** |
| Simple — Serialize | 0.055 | 0.069 | **PicoMsgPack 1.3×** |
| Nested — Deserialize | 0.325 | 0.400 | **PicoMsgPack 1.2×** |
| Complex — Serialize | 0.187 | 0.200 | level (0.93×, ±10%) |
| Complex — Deserialize | 0.335 | 0.299 | **MessagePack-CSharp 1.12×** |

The two collection rows are what the bulk-path commits bought: one call reads a flat int list, one call writes
it, with the per-element loop kept as the fallback. Against the window this one replaces they read **1.43× →
0.55×** on the read side and **1.24× → 0.60×** on the write side, and the archive records the µs behind both
(`1.016 → 0.382 µs`, `0.254 → 0.136 µs` — cross-window deltas, which carry the later window's ~16 % discount;
the ratios do not, because both arms of every ratio were measured inside one window). Those two paths are gated
on the type the generator declared, so the other six JIT rows moved only with the machine. The single loss is
the complex read: `Complex — Deserialize` **1.12×** (0.335 vs 0.299 µs), beyond the band, with both arms slower
than in the previous window (ours +10.9 %, theirs +4.5 %) — reported, not explained away.

The AOT benchmark has no collection scenario at all, so the bulk paths' AOT effect is not in the table above; it
is quoted from the two changes' own paired A/Bs instead: read side 1270 → **468 ns (0.34×, −63 %)**, write side
779.2 → **182.1 ns (0.234×, 8 of 8 rounds lower)**.

The AOT table uses a different model set from the JIT one on purpose — only statically resolvable types run under
MessagePack-CSharp's AOT path — so the two must not be aligned by scenario name, and AOT and JIT are never merged
into one figure. Reproduction: `PICOBENCH_STRICT=1 dotnet run --project
PicoMsgPack/benchmarks/PicoMsgPack.Benchmarks -c Release --no-build` for the JIT table, and the AOT arms from
`PicoMsgPack.Benchmarks.Aot` published with `-p:PublishAot=true` and run as a native binary. Each round prints
its own `PICOBENCH_SUMMARY … published= refused= mode= exit=` line, and an arm measured on a busy machine is
refused rather than published (drift, spread and dispersion per arm, system-wide CPU under 35 %).

The withdrawn table is still worth knowing about, because it is why every ratio in this repository is printed as
`<ours> ÷ <theirs>`: the harness had PicoSerDe's action in PicoBench's *baseline* slot at every call site, so each
ratio and each column header came out backwards. A runtime role guard now throws on that inversion.

What *is* verified, and is a capability fact rather than a ratio: **MessagePack-CSharp only partly works under
NativeAOT.** It builds formatters for enums and generic collections at run time with `MakeGenericType` +
`Activator.CreateInstance`, which NativeAOT cannot do, so any `[MessagePackObject]` containing an enum or a
generic collection (`List<int>`, `Dictionary<string,string>`) throws
`MissingMethodException: No parameterless constructor defined for type ...` on first use. Types restricted to
`string`/`int`/`bool`/`double`/`DateTime` are statically resolvable and do run ahead of time — that is the model
set `PicoMsgPack.Benchmarks.Aot` uses, deliberately. The unified benchmark's models carry `DayOfWeek`,
`Dictionary<string,string>` and `List<int>`, so that benchmark is JIT-only and says so in its csproj.

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
