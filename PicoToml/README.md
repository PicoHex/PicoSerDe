# PicoToml

AOT-first, reflection-free TOML serializer.

[![NuGet](https://img.shields.io/nuget/v/PicoToml)](https://www.nuget.org/packages/PicoToml)

## Install

```bash
dotnet add package PicoToml
# The source generator is embedded in PicoToml (analyzers/dotnet/cs) — no separate .Gen reference needed
```

## Quick Start

```csharp
using PicoToml;

public class AppConfig
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool Enabled { get; set; }
}

var config = new AppConfig { Name = "MyApp", Count = 42, Enabled = true };
var toml = TomlSerializer.Serialize(config);
// Name = "MyApp"
// Count = 42
// Enabled = true

var restored = TomlSerializer.Deserialize<AppConfig>(Encoding.UTF8.GetBytes(toml));
```

## Features

- **TOML format** — tables, arrays, inline tables, multi-line strings
- **`ref struct`** reader/writer — stack-allocated on the hot path
- **AOT-compatible** — `IsAotCompatible=true`, zero reflection
- **Ref struct serialization** — serialize `ref struct` types directly
- **Multi-line strings** — basic (`"""`) and literal (`'''`) support
- **Inline tables** — `{key = value, ...}` syntax
- **Array tables** — `[[array]]` syntax
- **SIMD-accelerated** whitespace skipping
- **Dual-mode** reader: `ReadOnlySpan<byte>` + `ReadOnlySequence<byte>`

## Customization

```csharp
[PicoSerializable]                     // all formats
[PicoTomlSerializable]                 // TOML only
[TomlKey("custom_name")]              // override key name
[TomlIgnore]                           // exclude property
[TomlCamelCase]                        // camelCase keys
[TomlConverter(typeof(MyConverter))]   // custom converter
[TomlDateTimeFormat("yyyy-MM-dd")]    // custom DateTime format
```

## Options

`TomlSerializer.Serialize` / `Deserialize` accept `TomlOptions`:

- `Indented` — indents table contents by two spaces (default: compact)
- `DefaultIgnoreCondition` — accepted for API uniformity; TOML has no null value on the write side, so null members are always omitted

## Null Handling

- **Write:** a null member is omitted — TOML has no null value. Omission means a
  null does **not** round-trip: the property keeps its initializer (or stays
  `null` when it has none) on read.
- **Read:** `Key =` with nothing after it, and the unquoted word `null` (any
  case), are null literals and assign `null` to nullable members.
- A quoted `""` is the empty string and a quoted `"null"` is the four-character
  string — quoted scalars are never null.
- A null literal aimed at a **non-nullable value type** throws
  `FormatException`; an unannotated `string` member receives `null` (the
  generators' NRT-lenient contract).

## Limits

- **Object lists** — `List<Dto>` is written as TOML array tables (`[[Rows]]`) and round-trips.
- **Nested primitive arrays** — `List<List<int>>`, `List<int[]>`, `int[][]` are written as an
  inline array of inline arrays (`Grid = [[1, 2], [3]]`) and round-trip. The representable leaf
  kinds are the scalars the TOML list writer has a case for: `string`, the integer widths,
  `float`/`double`, `bool`.
- **`List<List<Dto>>` round-trips** — an inline array of inline tables (`V = [[{ Name = "a" }]]`)
  is a form TOML can hold, so the member is emitted and nothing is reported
  (`TomlNestedObjectArrayTests`, `RecursiveDiagnosticTests.NestedObjectListMember_IsEmittedAndNotReported`).
  A nested chain with a nullable *value* leaf (`List<List<int?>>`) is still not representable and is
  dropped with `PICOSERDE004`.
- **Recursive members** (`Node Child` on `Node`) round-trip as nested tables (`[Child]`, `[B.A]`).
  The remaining debt is the cycle *position*: a recursive collection **element** (`List<Node>` on
  `Node`) or a recursive **dict value** (`Dictionary<string, Node>` on `Node`) is still dropped with
  `PICOSERDE003`, reported as a `⏸` debt because TOML can carry the shape.
- Objects nested deeper than one level throw `NotSupportedException` instead of silently losing data.

Confirm what was dropped with `dotnet build 2>&1 | grep PICOSERDE` (the diagnostics carry
`Location.None`, so `#pragma warning disable` cannot silence them). The full five-format matrix
lives in the [root README](../README.md#format-limits-what-each-wire-format-cannot-express).

## Performance

**From the 2026-10-29 window (see the [archive](../docs/perf/measurement.md#per-scenario-tables-2026-10-29-window)).**
TOML is the one format with a competitor doing the same job in **both** execution modes — Tomlyn 2.10.1 has a
typed API with its own source generator, one library call per row, the same POCO in and out, and it round-trips
under NativeAOT — so both of its tables carry a real opponent, and both were re-measured inside this one window.
Each pair is `PicoToml vs Tomlyn` in µs/op: per arm the median of 5 independent runs × ≥100k iterations, quoted
as the median of three rounds (2026-10-29, 17:49–18:24 local — AMD Ryzen 7 6800H, Windows 11, .NET SDK 10.0.401,
runtime 10.0.12) behind the harness's idle-machine gate. **The multiple is that pair divided, larger by the
smaller** — `1.931 ÷ 0.200 = 9.655 → 9.7×` — read off the µs themselves, with the winner put in words. No row in
either mode is inside the ±10 % band, so every row below is a direction claim.

**PicoToml is faster than Tomlyn under JIT *and* under NativeAOT:** **10 of 10** scenarios in each mode — 0.09×–
0.35× of Tomlyn's cost under JIT (row median 0.19×; the row multiples run 2.8×–11.4×) and 0.10×–0.41× under AOT
(row median 0.21×; 2.4×–9.7×).

**NativeAOT — ahead in 10 of 10** (harness 0.10×–0.41×, median 0.21×):

| Scenario | PicoToml | Tomlyn | Faster by |
|---|---:|---:|---|
| Simple — Deserialize←string | 0.200 | 1.931 | **PicoToml 9.7×** |
| Simple — Deserialize (bytes) | 0.207 | 1.898 | **PicoToml 9.2×** |
| Medium — Deserialize (bytes) | 0.520 | 4.305 | **PicoToml 8.3×** |
| Nested — Deserialize (bytes) | 0.700 | 5.595 | **PicoToml 8.0×** |
| Large — Deserialize (bytes) | 3.040 | 17.906 | **PicoToml 5.9×** |
| Simple — Serialize (bytes) | 0.158 | 0.642 | **PicoToml 4.1×** |
| Simple — SerializeToString | 0.171 | 0.522 | **PicoToml 3.1×** |
| Medium — Serialize (bytes) | 0.466 | 1.383 | **PicoToml 3.0×** |
| Large — Serialize (bytes) | 2.280 | 5.666 | **PicoToml 2.5×** |
| Nested — Serialize (bytes) | 0.634 | 1.551 | **PicoToml 2.4×** |

**JIT, runtime default — ahead in 10 of 10** (harness 0.09×–0.35×, median 0.19×):

| Scenario | PicoToml | Tomlyn | Faster by |
|---|---:|---:|---|
| Simple — Deserialize←string | 0.146 | 1.661 | **PicoToml 11.4×** |
| Simple — Deserialize (bytes) | 0.142 | 1.540 | **PicoToml 10.8×** |
| Medium — Deserialize (bytes) | 0.345 | 3.344 | **PicoToml 9.7×** |
| Nested — Deserialize (bytes) | 0.506 | 4.632 | **PicoToml 9.2×** |
| Large — Deserialize (bytes) | 2.674 | 14.449 | **PicoToml 5.4×** |
| Simple — Serialize (bytes) | 0.095 | 0.479 | **PicoToml 5.0×** |
| Medium — Serialize (bytes) | 0.244 | 0.969 | **PicoToml 4.0×** |
| Large — Serialize (bytes) | 1.309 | 3.913 | **PicoToml 3.0×** |
| Nested — Serialize (bytes) | 0.341 | 0.978 | **PicoToml 2.9×** |
| Simple — SerializeToString | 0.115 | 0.324 | **PicoToml 2.8×** |

The read side carries the wider margin in both modes: the cost of this format is dominated by the opponent's
parser, not by our writer, and the same shape repeats in the AOT table.

Tomlyn 2.10.1 is that equivalent opponent: a typed API with its own source generator, one library call per row,
the same POCO in and out, round-tripping under NativeAOT. Three things are stated rather than tucked away — they
are properties of the two writers and of the harness's own equivalence check, so they hold for both tables:

- **The same document is produced, with one exception.** The equivalence check reports text-identical for
  `simple`, `medium` and `large`, plus a typed round-trip check; `nested` differs because the key order inside
  `[Address]` is ours `City` then `Street`, Tomlyn's `Street` then `City`. The two `Nested` rows carry that known,
  unquantifiable shape difference — disclosed, and far too small to account for a 0.41× (AOT) or a 0.35× (JIT).
- **Our deserialize rows pay an extra decode:** `Deserialize<T>(byte[])` decodes UTF-8 inside the timed region,
  while Tomlyn's `Deserialize<T>(string)` is handed a string built outside it. That can only understate us.
- **Tomlyn's serialize arm is not handicapped:** it uses the Stream overload into one reused `MemoryStream`, and
  the equivalence lines are the evidence that its bytes are identical to ours.

The **Tommy** reference table is kept and is still labelled `NON-EQUIVALENT`, with **no ratio**: Tommy 3.1.2 is
DOM-only (`TOML.Parse` / `TomlTable`) with no typed POCO mapping, so the `TomlTable` walk and the typed conversion
are caller-written. Its numbers may not be divided by ours. The ratio that used to exist compared our
serialization against a hand-written `TomlTable` build plus typed mapping on the Tommy side, and both actions also
went through `Benchmark.Compare` in the wrong slots; it has been withdrawn rather than corrected in place.

Scope, honestly: **both tables above are one window, not two.** This is the one format where the opponent is
equivalent in both modes and both modes were re-measured side by side — TOML AOT `r20`/`r21`/`r22` and TOML JIT
`r29`/`r30`/`r31`, with three JIT rows filled by a later kept round (`r37`) that supplied an arm a kept round had
refused. Seventeen of the twenty ratios moved by less than the ±10 % band against the window this one replaces;
the three that stepped further are **not** a direction change — under JIT `Simple — Serialize` 0.18× → 0.20× and
`Simple — Deserialize←string` 0.08× → 0.09× moved because **Tomlyn's** arm read 25 %/12 % faster while ours read
19 %/9 % faster, and `Medium — Serialize` 0.29× → 0.25× moved the other way because our arm read 17 % faster
while Tomlyn's read 5 % faster — on hot paths no commit here has touched. That is what re-measuring such a path
buys: it measures the cross-window offset instead of assuming it (this window: −16 % median on ours, −15 % on the
opponents').

Which is also why the µs above are the weaker part of any cross-window claim: on the paths no commit touched this
window ran **9 %–21 % faster on our arms** and **5 %–25 % faster on the opponents'** — the same offset on both
sides of every ratio, so no ratio carries it, while a µs compared across windows is biased in the flattering
direction. The archive states that once rather than per row.

The two modes are **never mixed**, and in this case that rule has teeth: AOT costs more than JIT for **both**
libraries — our `Large — Serialize` moves 1.309 → 2.280 µs, Tomlyn's 3.913 → 5.666 µs — so a cell from one table
may not be compared with a cell from the other. What does cross the boundary is the **ratio**, and it holds:
0.33× under JIT, 0.40× under AOT for that row, 0.09×–0.41× overall, no row anywhere near 1.00. The AOT row
median sits one notch wider than the JIT one's (0.21× against 0.19×); that is reported rather than smoothed
over.

Capability fact that does not depend on measurement: **Tommy's DOM API (`TOML.Parse`, `TomlTable`) works under
NativeAOT** — it has no typed POCO mapper to run there. PicoToml does both, and so does Tomlyn. Run
`dotnet run -c Release --project PicoToml/benchmarks/PicoToml.Benchmarks` for the JIT table, and

```bash
dotnet publish PicoToml/benchmarks/PicoToml.Benchmarks/PicoToml.Benchmarks.csproj \
  -c Release -r win-x64 -o /tmp/toml-aot -p:PublishAot=true
/tmp/toml-aot/PicoToml.Benchmarks.exe
```

for the AOT one. Both print their own `PICOBENCH_SUMMARY` line, and both refuse to publish an arm measured on a
busy machine.

## Low-Level API

```csharp
var reader = new TomlReader(data);
while (reader.Read())
{
    if (reader.TokenType == TokenType.PropertyName)
        var key = Encoding.UTF8.GetString(reader.KeySpan);
    var value = Encoding.UTF8.GetString(reader.ValueSpan);
}
```

## Packages

| Package | Description |
|---------|-------------|
| `PicoToml` | Runtime library |
| `PicoToml.Gen` | Roslyn source generator — embedded in the runtime package (standalone package is legacy/optional) |

## License

MIT
