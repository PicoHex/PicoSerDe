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
- **`List<List<Dto>>` is not representable** — TOML has no "array table of array tables", so the
  member is dropped and the build reports `PICOSERDE004`
  (`Member 'X.Y' cannot be represented by this format — it will be ignored`). A nested chain with
  a nullable leaf (`List<List<int?>>`) is dropped the same way.
- **Recursive members** (`Node Child` on `Node`) are dropped with `PICOSERDE003`; the rest of the
  document is still written, so a recursive DTO serializes "partially". JSON/MsgPack keep the
  member.
- Objects nested deeper than one level throw `NotSupportedException` instead of silently losing data.

Confirm what was dropped with `dotnet build 2>&1 | grep PICOSERDE` (the diagnostics carry
`Location.None`, so `#pragma warning disable` cannot silence them). The full five-format matrix
lives in the [root README](../README.md#format-limits-what-each-wire-format-cannot-express).

## Performance

**TOML has a competitor that does the same job, and on it PicoSerDe is faster under JIT *and* under
NativeAOT: 0.08×–0.41× of Tomlyn's cost under JIT (faster in 10 of 10 scenarios, median 0.20×, i.e.
2.4×–12.5×) and 0.10×–0.40× under AOT (10 of 10, median 0.21×, i.e. 2.5×–10×).**
The [2026-10-27 measurement record](../docs/perf/measurement-2026-10-27.md#measured-results-2026-10-27) carries both
per-scenario tables — median µs/op with min–max ±stddev, ratio printed as **ours ÷ theirs** — beside
each run's own summary line. Two highlights under JIT: `Simple — Deserialize` 0.141 vs 1.688 µs
(0.08×) and `Nested — Deserialize` 0.592 vs 5.094 µs (0.12×); under AOT the same two rows read
0.240 vs 2.291 µs (0.10×) and 0.796 vs 6.378 µs (0.12×).

Tomlyn 2.10.1 is the equivalent opponent the earlier sessions did not have: a typed API with its own
source generator, one library call per row, the same POCO in and out, round-tripping under
NativeAOT. Three things are stated rather than tucked away — they hold for the AOT table too, whose
`[equiv]` lines are identical:

- **The same document is produced, with one exception.** The harness prints `[equiv] simple`,
  `medium` and `large text identical = True` plus a round-trip check; `[equiv] nested text identical
  = False` because the key order inside `[Address]` differs (ours `City` then `Street`, Tomlyn
  `Street` then `City`). The two `Nested` rows carry that known, unquantifiable shape difference —
  disclosed, and far too small to account for a 0.36×.
- **Our deserialize rows pay an extra decode:** `Deserialize<T>(byte[])` decodes UTF-8 inside the
  timed region, while Tomlyn's `Deserialize<T>(string)` is handed a string built outside it. That
  can only understate us.
- **Tomlyn's serialize arm is not handicapped:** it uses the Stream overload into one reused
  `MemoryStream`, and the `[equiv]` lines are the evidence that its bytes are identical to ours.

The **Tommy** reference table is kept and is still labelled `NON-EQUIVALENT`, with **no ratio**:
Tommy 3.1.2 is DOM-only (`TOML.Parse` / `TomlTable`) with no typed POCO mapping, so the `TomlTable`
walk and the typed conversion are caller-written. Its numbers may not be divided by ours. The ratio
that used to be here compared our serialization against a hand-written `TomlTable` build plus typed
mapping on the Tommy side, and both actions also went through `Benchmark.Compare` in the wrong
slots; it has been withdrawn rather than corrected in place.

Scope, honestly: **both modes now carry data, from two different sessions on the same machine and the
same date.** The JIT table is the second measurement session's. The **AOT** table is a later, separate
window, and **three AOT attempts were needed: the first two were refused, and the third is the one
published.** The first, inside the JIT session (`.superpowers/sdd/bench-measure-2/logs/PicoToml-Aot.txt`),
lost **20 of its 40 arms** — 18 on system-wide CPU (39.4%–80.6% against the 35% limit) and 2 on
drift, after external load rose mid-window. The second, run in the re-measure's own directory
(`.superpowers/sdd/bench-aot-toml/logs/PicoToml-Aot.txt`), lost **21 of its 40 arms** — 18 on
system-wide CPU (**42.8%–95.2%**; the first reads `other processes used 42.8% of the machine's CPU
during the check (limit 35%) — the machine is not idle`) and 3 on drift (28.7%, 31.2%, 46.9%) —
publishing 19 of 20 equivalent arms and none of its 20 Tommy arms. The third published **all 20 arms**
of its equivalent table (`Mode: Aot, IL on disk: no, IsDynamicCodeSupported: no`; load self-check
drift 1.3%, spread 1.38×, dispersion 5.9% at other-CPU 10%; per-arm other-CPU 3%–26% across those
20 arms), and its source is
`AOT · 2026-10-27 · .superpowers/sdd/bench-aot-toml/logs/PicoToml-Aot-run2.txt`. Its Tommy table
published 19 of 20 — the missing arm was refused at 48.4% other-CPU — so no Tommy figure is quoted
from this window.

The two modes are **never mixed**, and in this case that rule has teeth: AOT costs more than JIT for
**both** libraries — our `Large — Serialize` moves 1.351 → 2.617 µs, Tomlyn's 4.185 → 6.579 µs — so a
cell from one table may not be compared with a cell from the other. What does cross the boundary is
the **ratio**, and it holds: 0.32× under JIT, 0.40× under AOT for that row, 0.10×–0.40× overall, no
row anywhere near 1.00. The margin does narrow slightly on the serialize rows under AOT (median
0.20× → 0.21×); that is reported rather than smoothed over.

Ratios are read to the session's precision: a single session's ±stddev comes from five runs in one
process, and the honest band between sessions is about ±10% (see
[the 2026-10-27 measurement record's note](../docs/perf/measurement-2026-10-27.md#one-sessions-stddev-is-not-a-cross-session-error-bar)) — the
widest row at 0.41× is still an order of magnitude clear of that band.

Capability fact that does not depend on measurement: **Tommy's DOM API (`TOML.Parse`, `TomlTable`)
works under NativeAOT** — it has no typed POCO mapper to run there. PicoToml does both, and so does
Tomlyn. Run `dotnet run -c Release --project PicoToml/benchmarks/PicoToml.Benchmarks` for the JIT
table, and

```bash
dotnet publish PicoToml/benchmarks/PicoToml.Benchmarks/PicoToml.Benchmarks.csproj \
  -c Release -r win-x64 -o /tmp/toml-aot -p:PublishAot=true
/tmp/toml-aot/PicoToml.Benchmarks.exe
```

for the AOT one. Both print their own `PICOBENCH_SUMMARY` line, and both refuse to publish an arm
measured on a busy machine.

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
