# `per-token` micro-benchmark (task T4)

What one token costs to read. Three arms, one payload, one million token operations each:

| arm | call shape | what it is |
|---|---|---|
| 1 | `Read()` | classify + materialise one token (the state machine alone) |
| 2 | `Read()` + `TryGetInt32()` | the two-stage shape **generated code emits today** |
| 3 | `TryReadInt32()` | the fused shape (T6): classify **and** decode in one dispatch |

The difference between arms 2 and 3 is the quantity the plan's stopping rule is decided on
(`docs/superpowers/plans/2026-10-28-fused-token-reader.md` §5 T6 criterion 6 → stop when per-token recovery is
below **15 %**, i.e. half of spec §9 U1's 30 % floor). Spec §9 U2 asks for exactly arm 2 — "one reader, 10⁶
calls of `Read()` + `TryGetInt32`" — because spec §1.2's `13.4 ns/token` was **arithmetic**, not a measurement.

**This project is not part of the build.** It is not in `PicoSerDe.slnx`, nothing in `src/` or `tests/`
references it, and `dotnet build PicoSerDe.slnx` never sees it (same convention as
[`../readspan/`](../readspan/README.md) and `tests/FormatCapabilities.Probe`, and plan §5 T4 criterion 3
requires `grep pertoken PicoSerDe.slnx` to stay empty). It is *tracked* for the opposite reason: the version of
this harness that T6 used lived under `.superpowers/`, which `.gitignore` deletes from every clone — so when the
stopping rule was applied, the tool that was supposed to decide it could not be found by the next person.

## Running it

```console
# JIT, tiering on — the shipped default. This is the number that describes production.
dotnet run -c Release --project PicoMsgPack/tools/pertoken

# JIT, tiering off — the protocol the T6/T7/T8 reports measured under (they used TC=0 to kill variance).
DOTNET_TieredCompilation=0 dotnet run -c Release --project PicoMsgPack/tools/pertoken

# NativeAOT — what the AOT benchmark rows run on.
dotnet publish PicoMsgPack/tools/pertoken/PerToken.csproj -c Release -r win-x64 -p:PublishAot=true -o out/pertoken-aot
out/pertoken-aot/PerToken.exe
```

Options: `--rounds N` (odd, default 21) and `--dump` (print the payload's token stream, one line per `Read()`).
A run takes about two seconds.

**The mode is part of the result, not decoration.** With tiering and DynamicPGO on (the default) the two-stage
arm keeps `TryGetInt32` inlinable and the fused member does **not** win; with `DOTNET_TieredCompilation=0` or
under NativeAOT it does. Two sessions quoting "per-token recovery" without saying which mode they measured in
quote opposite numbers for the same code — that is exactly what happened between T6 and this harness.

One trap worth knowing: `<PublishAot>true</PublishAot>` — which `Directory.Build.props` supplies to every
project — also injects `System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported=false` into
`runtimeconfig.json`, so a *framework-dependent* build reports itself as AOT. This project therefore sets
`PublishAot=false` and opts into AOT at publish time only; a `dotnet run` that prints "NativeAOT" is lying to
you.

## Output

```
arm                           median      min      max   spread   per-round ns/token
Read()                         4.964    4.552    5.153    12.1%   4.59 5.03 5.15 ...
Read() + TryGetInt32()         5.254    4.800    5.492    13.2%   5.03 5.39 5.49 ...
TryReadInt32()                 5.651    5.198    6.651    25.7%   5.48 5.88 6.13 ...
Read() [CONTROL]               4.964    4.511    5.218    14.2%   4.89 5.06 4.74 ...
```

- **median**, not mean: one preempted round must not move the answer (README `:137`'s rule).
- **spread** = peak-to-peak as a percentage of the median — the machine's own noise band. Ratios smaller than
  the CONTROL's disagreement are not signal.
- **CONTROL** is arm 1 measured a second time in every round, alternating which run goes first. It is not a
  formality: its ratio is the noise floor, and the recovery figure is only readable next to it.
- **`cpu share`** per shape = process CPU ÷ wall over the measured rounds (1.00 = never preempted). Under ~0.95
  the absolute ns are inflated by the machine, while the *ratios* survive.
- **recovery** is printed raw and with reader construction removed. Every arm builds one reader per pass
  (~0.15 ns/token), so construction cancels in the arm-to-arm difference but dilutes the percentage; the second
  figure divides by the corrected denominator.
- The two arms issue **the same number of token operations**: passes are fixed-length, not `while (Read())` and
  not `while (TryReadInt32())` — the latter would stop at the first non-int token (`TryReadInt32` returns
  `false` for a map header, by contract). So no loop-shape gain leaks into the fused column. That gain (one
  `TokenType != ArrayEnd` check per element, which T6 measured at 3.7 % and which belongs to the *emitter*
  task T11/E-3, not to the member) is deliberately **not** in these numbers.

## The payloads, and the ledger they are asserted against

Both shapes are real wire bytes, and both are verified token-for-token before anything is timed — a drifted
payload stops the run instead of silently becoming a new denominator.

**`collection`** — the mandated shape (spec §1.2): `map{ Scores:int[100], Metadata:{source:"benchmark"} }`,
124 bytes, **110 tokens**, 102 of them int tokens. Two corrections against §1.2's hand count, both found by
this harness:

- §1.2 counts 107 **value** tokens and omits the three container-close tokens (`ArrayEnd`, `ObjectEnd`,
  `ObjectEnd`) — each is one more `Read()`, so the honest divisor is 110.
- §1.2 lists the map keys as strings. With `[MsgPackKey(n)]` the generator writes them as **integer indices**,
  so the payload carries two 1-byte `fixint` keys and only two string tokens (110 tokens / 124 bytes, not
  107 + string keys / 138 bytes).

Neither shifts the measured quantity by more than ~3 %; they are recorded so the ledger matches the bytes.

**`fixints1000`** — T6's per-token shape: a bare `fixarray16(1000)` of `fixint`s, 1002 tokens, 1000 int tokens,
no non-int token at all. It is measured because the difference between the two shapes is the price the fused
member pays on the 8 tokens it cannot serve: `TryReadInt32`'s `default:` arm forwards to
`TokenReaderFused.TryReadInt32`, i.e. it dispatches on the tag byte *and then* runs the full `Read()` switch.

## Reference run (2026-10-29, this repository, `win-x64`)

Medians of 21 rounds per arm; two runs per configuration; `Collection` shape unless stated. Full logs:
`.superpowers/sdd/t4-pertoken/evidence/` (gitignored), summarised in
`.superpowers/sdd/t4-pertoken/report.md`.

| configuration | run | `Read()` | `Read()`+`TryGetInt32()` | `TryReadInt32()` | recovery | CONTROL |
|---|---:|---:|---:|---:|---:|---:|
| JIT, tiering on (default) | 1 | 4.800 | 5.068 | 5.459 | **−7.7 %** | 0.995 |
| JIT, tiering on (default) | 2 | 4.964 | 5.254 | 5.651 | **−7.6 %** | 1.000 |
| JIT, `DOTNET_TieredCompilation=0` | 1 | 5.434 | 5.917 | 5.314 | **+10.2 %** | 1.015 |
| JIT, `DOTNET_TieredCompilation=0` | 2 | 5.685 | 6.373 | 5.646 | **+11.4 %** | 0.964 |
| NativeAOT | 1 | 5.526 | 6.145 | 5.529 | **+10.0 %** | 0.997 |
| NativeAOT | 2 | 7.189 | 8.179 | 7.174 | **+12.3 %** | 1.000 |

Same harness, `fixints1000` (all-int, T6's shape): −0.3 % / +0.6 % (JIT tiered), +16.5 % / +14.7 %
(JIT TC=0), +15.3 % / +15.5 % (AOT).

How to read it:

1. **On the payload that ships, recovery never reaches 15 %** — 10.0–12.3 % in AOT, 10.2–11.4 % with tiering
   off ⇒ the stopping rule (RK-6) is **triggered** in every configuration.
2. **Under the shipped tiered JIT the fused member is ~8 % *slower*** than `Read()` + `TryGetInt32`. Tiered
   PGO inlines the tiny cache-reading `TryGetInt32` into the caller's loop, while `TryReadInt32` stays an
   out-of-line ~200-line method that also pays a prologue per token; the saving it buys is one call. This is
   why T6's 10 % and this table can both be true: T6 measured under `DOTNET_TieredCompilation=0`.
3. **The all-int shape hides the fallback.** 100 % int tokens ⇒ +15 %; 102/110 ⇒ +10 % (TC=0/AOT). The 8
   non-int tokens of the `Collection` shape are what the fused member cannot help with, and it charges for
   them twice.
4. **§1.2's 13.4 ns/token is 2.5× the measured 5.1–5.4 ns/token.** The two numbers are not the same quantity:
   §1.2 divides a full *deserialization* (token read + `List<int>` growth + dictionary inserts) by a token
   count, while arm 2 is the read side alone. The gap is not evidence that the arithmetic was wrong; it is
   evidence that ~60 % of `Collection — Deserialize` is not token reading.

**What this harness does not claim**: absolute ns across machines or sessions (a busy box moves the absolute
numbers by up to 40 % — see the two AOT runs, where the ratios held at 10.0 vs 12.3 % while every absolute
figure moved), nor anything about the emitted loop shape (that is T11/E-3), nor a throughput assertion of any
kind — no test runs this project, and none should.
