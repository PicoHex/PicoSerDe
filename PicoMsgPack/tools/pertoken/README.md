# `per-token` micro-benchmark (task T4)

What one token costs to read. Three arms, two payloads, ~10⁶ token operations per arm per round:

| arm | call shape | what it is |
|---|---|---|
| 1 | `Read()` | classify + materialise one token (the state machine alone) |
| 2 | `Read()` + `TryGetInt32()` | the two-stage shape **generated code emits today** |
| 3 | `TryReadInt32()` | the fused shape (T6): classify **and** decode in one dispatch |

The difference between arms 2 and 3 is the quantity the plan's stopping rule is decided on
(`docs/superpowers/plans/2026-10-28-fused-token-reader.md` §5 T6 criterion 6 → stop when per-token recovery is
below **15 %**, i.e. half of spec §9 U1's 30 % floor). Spec §9 U2 asks for exactly arm 2 — "one reader, 10⁶ calls
of `Read()` + `TryGetInt32`" — because spec §1.2's `13.4 ns/token` was **arithmetic**, not a measurement.

**This project is not part of the build.** It is not in `PicoSerDe.slnx`, nothing in `src/` or `tests/` references
it, and `dotnet build PicoSerDe.slnx` never sees it (same convention as [`../readspan/`](../readspan/README.md) and
`tests/FormatCapabilities.Probe`; plan §5 T4 criterion 3 requires `grep pertoken PicoSerDe.slnx` to stay empty). It is
*tracked* for the opposite reason: the version of this harness that T6 used lived under `.superpowers/`, which
`.gitignore` deletes from every clone — so when the stopping rule was applied, the tool that was supposed to decide
it could not be found by the next person.

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

Options: `--rounds N` (odd, default 21), `--blocks N` (default 16) and `--dump` (print the payload's token stream,
one line per `Read()`). A run takes about a second and a half.

**Exit code 3 means "the timing is not quotable"** — the box was too busy (see *publishable* below). It is a status,
not an error: the numbers are still printed, with `publishable=no`.

**The mode is part of the result, not decoration.** With tiering and DynamicPGO on (the default) the two-stage arm
keeps `TryGetInt32` inlinable and the fused member does **not** win; with `DOTNET_TieredCompilation=0` or under
NativeAOT it does. Two sessions quoting "per-token recovery" without saying which mode they measured in quote
opposite numbers for the same code — that is exactly what happened between T6 and this harness.

One trap worth knowing: `<PublishAot>true</PublishAot>` — which `Directory.Build.props` supplies to every project —
also injects `System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported=false` into `runtimeconfig.json`,
so a *framework-dependent* build reports itself as AOT. This project therefore sets `PublishAot=false` and opts into
AOT at publish time only; a `dotnet run` that prints "NativeAOT" is lying to you.

## Output

```
arm                           median      min      max   spread   per-round ns/token
Read()                         5.067    4.244    6.108    36.8%   5.02 5.31 ...
Read() + TryGetInt32()         5.711    4.512    7.611    54.3%   5.55 6.11 ...
TryReadInt32()                 5.748    4.710    7.409    47.0%   5.54 6.07 ...
Read() [CONTROL]               5.083    4.178    6.096    37.7%   5.09 5.28 ...
```

- **median**, not mean: one preempted round must not move the answer (README `:137`'s rule).
- **spread** = peak-to-peak as a percentage of the median. On a desktop with a browser open this reaches several
  hundred percent — which is exactly why the headline figure is *not* computed from these per-arm columns.
- **recovery** is the **median of the per-round ratios**, not the ratio of the per-round medians. Every round
  measures all three arms in the same interleaved blocks, so a round's ratio is the load-insensitive quantity; two
  medians taken from different halves of a drifting machine are not. Per-round min/max are printed next to it.
- **CONTROL** is arm 1 measured a second time in its own rotating slot. Its per-round spread is the harness's noise
  floor; a recovery figure inside it is not a figure.
- **publishable** requires cpu share ≥ 0.95, |CONTROL − 1| ≤ 0.03 and a comparable arm set (below). A rejected run
  still prints everything, so the rejection is documented rather than silent.
- **`ARMS NOT COMPARABLE`** fires when the fused arm comes out below 0.75 × `Read()` alone. The fused integer path
  can legitimately be slightly cheaper than `ReadSpan()` (shorter prologue), but no implementation classifies the
  same token stream a quarter cheaper than the reader it contains — that reading means the two arms ran in different
  JIT states and the table is unusable.
- **`cpu share`** per shape = process CPU ÷ wall over the measured rounds (1.00 = never preempted).
- **recovery** is printed raw and with reader construction removed. Every arm builds one reader per pass
  (~0.15 ns/token), so construction cancels in the arm-to-arm difference but dilutes the percentage.
- The three arms issue **the same number of token operations**: passes are fixed-length, not `while (Read())` and not
  `while (TryReadInt32())` — the latter would stop at the first non-int token (`TryReadInt32` returns `false` for a
  map header, by contract). So no loop-shape gain leaks into the fused column. That gain (one
  `TokenType != ArrayEnd` check per element, which T6 measured at 3.7 % and which belongs to the *emitter* task
  T11/E-3, not to the member) is deliberately **not** in these numbers.

### Why the arms are interleaved

The first version of this file timed one arm to completion, then the next. On a quiet machine it reported **−7.7 %**
recovery; twenty minutes later, same binary, it reported **+33 %** — the box had got busy between the two arms. No
amount of medians fixes a design where arm A and arm B are measured at different minutes of a machine that drifts by
2×. Each round now cuts its work into 16 blocks and rotates the arm order block by block, so a load spike lands on
every arm instead of on one.

That change is why the numbers below differ from the first published version of this tool (which said −7.7 % where
this one says −1.4 %): the earlier figure was the *order* effect, not the code.

## The payloads, and the ledger they are asserted against

Both shapes are real wire bytes, and both are verified token-for-token before anything is timed — a drifted payload
stops the run instead of silently becoming a new denominator. A protocol gate additionally runs all three arms once
and requires them to consume the same token count and decode the same integers.

**`collection`** — the mandated shape (spec §1.2):
`map{ Scores:int[100], Metadata:{source:"benchmark"} }`, 124 bytes, **110 tokens**, 102 of them int tokens. Two
corrections against §1.2's hand count, both found by this harness:

- §1.2 counts 107 **value** tokens and omits the three container-close tokens (`ArrayEnd`, `ObjectEnd`,
  `ObjectEnd`) — each is one more `Read()`, so the honest divisor is 110.
- §1.2 lists the map keys as strings. With `[MsgPackKey(n)]` the generator writes them as **integer indices**, so the
  payload carries two 1-byte `fixint` keys and only two string tokens (110 tokens / 124 bytes, not 107 + string keys
  / 138 bytes).

Neither shifts the measured quantity by more than ~3 %; they are recorded so the ledger matches the bytes.

**`fixints1000`** — T6's per-token shape: a bare `fixarray16(1000)` of `fixint`s, 1002 tokens, 1000 int tokens, no
non-int token at all. It is measured because the difference between the two shapes is the price the fused member pays
on the 8 tokens it cannot serve: `TryReadInt32`'s `default:` arm forwards to `TokenReaderFused.TryReadInt32`, i.e. it
dispatches on the tag byte *and then* runs the full `Read()` switch.

## Reference run (2026-10-29, this repository, `win-x64`)

Three independent runs per configuration, 21 rounds each, `Collection` shape (the payload that ships); ns/token is
the median of the per-round values, recovery is the median of the per-round ratios. Every run here reported
`publishable=yes` (cpu share 0.94–1.10, CONTROL 0.995–1.010). Full logs:
`.superpowers/sdd/t4-pertoken/evidence/` (gitignored), summarised in `.superpowers/sdd/t4-pertoken/report.md`.

| configuration | run | `Read()` | `Read()`+`TryGetInt32()` | `TryReadInt32()` | recovery |
|---|---:|---:|---:|---:|---:|
| JIT, tiering on (default) | 1 | 5.026 | 5.545 | 5.539 | **−1.40 %** |
| JIT, tiering on (default) | 2 | 5.067 | 5.711 | 5.748 | **−1.44 %** |
| JIT, tiering on (default) | 3 | 4.976 | 5.652 | 5.741 | **−1.73 %** |
| JIT, `DOTNET_TieredCompilation=0` | 1 | 6.186 | 6.405 | 5.828 | **+9.17 %** |
| JIT, `DOTNET_TieredCompilation=0` | 2 | 5.783 | 6.227 | 5.685 | **+9.50 %** |
| JIT, `DOTNET_TieredCompilation=0` | 3 | 5.563 | 5.819 | 5.293 | **+9.05 %** |
| NativeAOT | 1 | 5.642 | 6.129 | 5.633 | **+8.41 %** |
| NativeAOT | 2 | 5.585 | 6.168 | 5.643 | **+8.84 %** |
| NativeAOT | 3 | 6.809 | 7.743 | 6.936 | **+9.49 %** |

Same harness, `fixints1000` (all-int, T6's shape): tiered **+4.7 / +5.0 / +5.3 %**, `TC=0` **+15.7 / +15.4 /
+16.3 %**, AOT **+15.7 / +15.7 / +15.6 %**.

How to read it:

1. **On the payload that ships, recovery never reaches 15 %** — 8.4–9.5 % in AOT, 9.1–9.5 % with tiering off
   ⇒ the stopping rule (RK-6) is **triggered** in every configuration.
2. **Under the shipped tiered JIT the two shapes are a dead heat** (fused ≈ 1.5 % *slower*). Tiered PGO inlines the
   tiny cache-reading `TryGetInt32` into the caller's loop, while `TryReadInt32` stays an out-of-line ~200-line
   method that pays a prologue per token; the saving it buys is one call. This is why T6's 10 % and this table can
   both be true: T6 measured under `DOTNET_TieredCompilation=0`, where the same comparison gives 9 %.
3. **The all-int shape hides the fallback.** 100 % int tokens ⇒ ≈15.6 % (TC=0/AOT); 102/110 ⇒ ≈9 %. The 8 non-int
   tokens of the `Collection` shape are what the fused member cannot help with, and it charges for them twice.
4. **§1.2's 13.4 ns/token is ~2.3× the measured 5.6–6.2 ns/token.** The two numbers are not the same quantity: §1.2
   divides a full *deserialization* (token read + `List<int>` growth + dictionary inserts) by a token count, while
   arm 2 is the read side alone. The gap is not evidence that the arithmetic was wrong; it is evidence that roughly
   half of `Collection — Deserialize` is not token reading.

**What this harness does not claim**: absolute ns across machines or sessions (a busy box moves them by up to 40 %),
anything about the emitted loop shape (that is T11/E-3), or a throughput assertion of any kind — no test runs this
project, and none should.
