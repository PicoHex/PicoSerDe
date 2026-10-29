# PicoSerDe

AOT-first, reflection-free serialization framework. Five formats, one
unified API. Source-generated `ref struct` readers/writers with zero heap
allocation on the hot path — deployable under NativeAOT and trimming where
many serialization libraries cannot run.

[![CI](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml/badge.svg)](https://github.com/PicoHex/PicoSerDe/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Modules

| Format | Package | Status | AOT | Readme |
|--------|---------|--------|:---:|-------|
| JSON | PicoJetson | ✅ Production | ✅ | [→](PicoJetson/README.md) |
| MessagePack | PicoMsgPack | ✅ Production | ✅ | [→](PicoMsgPack/README.md) |
| INI | PicoIni | ✅ Production | ✅ | [→](PicoIni/README.md) |
| TOML | PicoToml | ✅ Production | ✅ | [→](PicoToml/README.md) |
| YAML | PicoYaml | ✅ Production | ✅ | [→](PicoYaml/README.md) |

> PicoYaml is the only YAML library in this comparison that maps **typed POCOs** under
> NativeAOT. YamlDotNet's DOM parser does run there; VYaml was not verified either way.

---

## Test Coverage

Every module has its own suite, cross-validated against a competitor implementation:

| Module | Competitor | Cross-Validation |
|--------|:-----------|:----------------:|
| PicoJetson | System.Text.Json | ✅ bidirectional, all 19 property types |
| PicoToml | Tomlyn | ✅ bidirectional, 20 property types, NestedList via `[[key]]` |
| PicoYaml | YamlDotNet | ✅ bidirectional, 19 property types, DateOnly/TimeOnly converters |
| PicoIni | Microsoft.Extensions.Configuration.Ini | ✅ bidirectional, 16 property types |
| PicoMsgPack | MessagePack-CSharp | ✅ map/array dual-format, 14 property types |
| PicoSerDe.Core | — | — |
| Integration (cross-format) | — | Ignore-condition matrix, anon types, round-trips |

> Test counts are deliberately not pinned in this document: they change with every
> PR and a stale number is worse than none. Run `dotnet test PicoSerDe.slnx -c Debug`
> for the current totals. PicoJetson's suite is split across three projects
> (unit / integration / functional).
>
> Strictness/robustness regression tests live in dedicated per-format suites
> (`Strict*Tests`, `*StrictnessTests`, `*RobustnessTests`): wrong-typed input,
> trailing data, missing `required` members, malformed documents, chunked
> streaming and comment handling all fail loudly instead of silently producing
> defaults.

## Performance

**Direction: every ratio below is `×` = ours ÷ theirs** — the direction the harness itself prints, so
**above 1.00 means PicoSerDe is slower** and **below 1.00 means PicoSerDe is faster**. No reciprocal is
ever taken: the direction above is the only one printed.

Measured **2026-10-29** on one machine (AMD Ryzen 7 6800H, Windows 11, .NET SDK 10.0.401, runtime
10.0.12), in **one window: 17:49 to 18:24 local**. All five rows — JSON, MessagePack, TOML, YAML and
INI, in each execution mode where an opponent exists — were re-measured inside it, so no row on this
page is older than the row beside it and none was carried over from an earlier session. They were
re-measured because three performance commits had landed behind the previous figures: JSON now writes
a short ASCII string in one pass and a property name as one literal (`f46e672`, merge `0234838`), and
MessagePack now reads a flat int list in one call (`5a5d895`, merge `7324990`) and writes one in one
call (`fc4ee5c`, merge `a2b4224`) — see [What the last round of changes
did](#what-the-last-round-of-changes-did). TOML, YAML and INI were re-measured rather than argued
about: nothing on their hot paths moved, and that is what their rows show — their **ratios** are where
the previous window left them, while their **absolute** times came out **9 %–21 % lower on our arms**
(median ≈ −16 %) and **5 %–25 % lower on the opponents'** (median ≈ −15 %) in TOML and YAML, i.e. the
same offset on both sides of every ratio; INI, which has no ratio, read 0 %–13 % on ours and 6 %–18 % on
the DOM walk. The direction of that offset is the machine state, not the code — this window was clean
where the one it replaces was contended — and it is the reason **a µs compared across windows here is biased in
the flattering direction**: an "after" µs below carries roughly a 15 % window discount that the change
itself did not earn. Every ratio on this page is computed from two arms measured inside this window, so
no ratio carries that offset at all, and where a µs is quoted across windows below it is read that
way. A **round** is one whole process; each round prints, per arm, the median µs/op over **5 independent
runs × ≥100k iterations each**, and every figure below is the **median of three rounds**. A row's ratio
is `ours ÷ theirs`, computed from those two medians. AOT and JIT are separate processes and are never
merged into one figure, one column or one ratio. The desktop was shared, not an idle lab box: over this
window the load criteria refused **33 of the 882** arm measurements taken, and 12 of its 39 completed
processes exited `1` under `PICOBENCH_STRICT=1`. Every arm a round refused was either already covered by
three kept rounds or re-measured in a later round, so **no figure in the results table below rests on a
refused arm** — ten rows needed one later round to make up an arm that a kept round had refused, and
those ten are named in the ledger paragraph below. No row comes from an earlier session.

The **17:49–18:24 window** is **39 processes started and 39 completed**, strictly serial, one arm set
per process; nothing was killed mid-run, so nothing had to be excluded after the fact. Ten arm sets were
measured: the eight that appear in the table, and the two JSON JIT companions that exist only to say
what the JIT column compares (the same benchmark project run with `DOTNET_TieredCompilation=0` and with
`DOTNET_TieredPGO=0` — see [What the JIT column is comparing](#what-the-jit-column-is-comparing)). Every
round was preceded by its own load check (the project's own precheck, median of ten 1 s samples), and
**every one of those reads cleared the gate at the first attempt** — medians **1.5 %–20.0 %**, median of
medians 5 % — against eight BUSY readings that had to be waited out in the window this one replaces.
**38 rounds were kept** and **1 was void** (round 6, `msgpack-jit`, 31.2 % of its arms refused); a void
round's log is renamed `.refused.txt` and feeds nothing at all. The protocol's three-consecutive-void
stop signal never came close to firing. What refusals remain are per-arm **`UNSTABLE`** ones — the
harness's own drift, spread and dispersion criteria rejecting five runs that sat next to a load spike,
not a machine that was busy while the arm ran: **33 arms of 882 (3.7 %)**. Every row quotes the median
of the **three earliest kept rounds that published both of its arms**; a later kept round is a
cross-check, never swapped in, except where it fills a row an earlier kept round had left with only two
published arms. Rounds per arm set: JSON AOT `r8`/`r9`/`r10`, JSON JIT `r11`/`r12`/`r13`, MessagePack
AOT `r1`/`r2`/`r3`, MessagePack JIT `r4`/`r5`/`r7`, TOML AOT `r20`/`r21`/`r22`, TOML JIT
`r29`/`r30`/`r31`, YAML JIT `r26`/`r27`/`r28`, INI JIT `r23`/`r24`/`r25`; the JSON JIT companions use
`r14`/`r15`/`r16` (`TC=0`) and `r17`/`r18`/`r19` (`PGO=0`). Ten rows are one kept round short of that and
take the next kept round that published both of their arms: JSON AOT `Simple POCO — Serialize` (`r33` —
`r8` refused that arm), JSON JIT `Simple POCO — Deserialize` and `Complex POCO — Serialize`
(`r34`/`r38` — both refused in `r11` and `r12`), JSON JIT `Collection POCO — Deserialize` (`r34`),
MessagePack JIT `Complex — Deserialize` and `Nested — Serialize` (`r32`), TOML JIT `Medium —
Deserialize (bytes)`, `Nested — Serialize (bytes)` and `Nested — Deserialize (bytes)` (`r37` — `r30`
refused all three), YAML JIT `Simple — SerializeToString` (`r36`/`r39` — `r27` and `r28` each refused
the YamlDotNet arm on that row); one row in a JSON JIT companion does the same, taking `r35` for the
`PGO=0` `LargeFlat 50-fields — Deserialize` row. Nothing on this page is quoted from the 10:20–11:39
session, from the 23:17–00:03 session, from the 08:41–09:00 session or from the 2026-10-27 session:
those windows are superseded rather than kept alongside, and the rows in [What the last round of changes
did](#what-the-last-round-of-changes-did) now quote this window's numbers for the same scenarios.

| Format | Opponent | AOT vs AOT | JIT vs JIT | In one line |
|---|---|---|---|---|
| JSON | System.Text.Json | ahead in **12 of 12** — **0.55×–0.98×**, median **0.79×** | slower in **3 of 12** — **0.51×–1.33×**, median **0.96×** | The AOT column has no loss in it at all: **twelve of twelve** scenarios are ahead, **eight** of them beyond the ±10 % band, and the median is **0.79×**. Under JIT the median is 0.96× with three losses beyond the band — `LargeFlat 50-fields — Deserialize` 1.33×, `LargeFlat 50-fields — Serialize` 1.27×, then `Complex POCO — Deserialize` 1.14× — and the biggest leads are `Nested POCO — Serialize` 0.51×/0.69×, `Collection POCO — Deserialize` 0.55×/0.65× and `Simple POCO — Serialize` 0.61×/0.74×. What that JIT column is comparing is spelled out [below](#what-the-jit-column-is-comparing). |
| MessagePack | MessagePack-CSharp | ahead in **4 of 4** — **0.60×–0.88×**, median **0.72×** (every cell beyond the band) | ahead in **7 of 8** — **0.45×–1.12×**, median **0.78×** | Ahead on every AOT row again, read side included. Under JIT the median moved to 0.78× and the single loss beyond the band is `Complex — Deserialize` **1.12×**; the two collection rows that were this page's worst MessagePack losses are now its two biggest leads — `Collection — Deserialize` 1.43× → **0.55×**, `Collection — Serialize` 1.24× → **0.60×**. |
| TOML | Tomlyn 2.10.1 | faster in **10 of 10** — **0.10×–0.41×**, median **0.21×** (between **2.4× and 10×** faster) | faster in **10 of 10** — **0.09×–0.35×**, median **0.19×** (between **2.9× and 11×** faster) | We are faster in both modes, against an opponent doing the same job in both. |
| YAML | YamlDotNet 18.1.0 | — no AOT opponent exists | faster in **6 of 6** — **0.02×–0.06×**, median **0.04×** (**17×–50×** faster) | We are faster on CoreCLR, and only there. |
| INI | ini-parser 2.5.3 | — no equivalent operation | — no equivalent operation | No ratio exists to divide by; absolute cost only, on a `NON-EQUIVALENT` table. |

**In words.** JSON's AOT column is now the one column on this page with no loss anywhere in it at
all: **12 of 12** scenarios ahead, median **0.79×**, range **0.55×–0.98×**. Eight of the twelve are
beyond the ±10 % band — `Collection POCO — Deserialize` **0.55×** (3.193 vs 5.790 µs), `Nested POCO —
Serialize` **0.69×** (0.312 vs 0.450 µs), `LargeString 1KB — Deserialize` **0.71×** (0.421 vs 0.589 µs),
`Simple POCO — Serialize` **0.74×** (0.123 vs 0.166 µs), `Collection POCO — Serialize` **0.77×**
(1.297 vs 1.694 µs), `LargeString 1KB — Serialize` **0.79×** (0.382 vs 0.486 µs), `Nested POCO —
Deserialize` **0.79×** (1.003 vs 1.267 µs) and `Simple POCO — Deserialize` **0.88×** — and the other four
(`Complex POCO — Serialize` 0.90×, `LargeFlat 50-fields — Serialize` 0.92×, `Complex POCO — Deserialize`
0.94×, `LargeFlat 50-fields — Deserialize` 0.98×) are ahead but inside the band, so they are not
direction claims. Two windows ago this column carried three losses beyond the band; last window it had
none but one level cell at 1.01×; this window it has neither.

Under JIT we are ahead in **9 of 12** with a median of **0.96×**. The three that are not are `LargeFlat
50-fields — Deserialize` **1.33×** (2.654 vs 1.992 µs), `LargeFlat 50-fields — Serialize` **1.27×**
(1.173 vs 0.923 µs) and `Complex POCO — Deserialize` **1.14×** (0.706 vs 0.617 µs) — all three beyond
the band, and all three the same shape: JSON object/member reads and writes, i.e. the part that scales
with property count. `Simple POCO — Deserialize` is level at **1.00×** (0.154 vs 0.154 µs). The four JIT
leads beyond the band are `Nested POCO — Serialize` **0.51×** (0.187 vs 0.366 µs), `Simple POCO —
Serialize` **0.61×** (0.066 vs 0.109 µs), `Collection POCO — Deserialize` **0.65×** (2.180 vs 3.337 µs)
and `Collection POCO — Serialize` **0.68×** (0.874 vs 1.294 µs); `Complex POCO — Serialize` 0.92×,
`LargeString 1KB — Serialize` 0.96×, `LargeString 1KB — Deserialize` 0.97× and `Nested POCO —
Deserialize` 0.99× are ahead but inside the band.

What moved, and by how much: the write-side change is visible exactly where a property name is written,
and most on the rows that write the most of them. `Nested POCO — Serialize` went **0.272 → 0.187 µs
(−31.3 %)** under JIT — **0.79× → 0.51×** — and **0.448 → 0.312 µs (−30.4 %)**, **0.96× → 0.69×**, under
AOT; `Simple POCO — Serialize` went 0.080 → 0.066 µs under JIT (−17.5 %, 0.68× → 0.61×), and AOT
`LargeFlat 50-fields — Serialize` — fifty property names in one document — went **1.609 → 1.395 µs
(−13.3 %)**, **1.00× → 0.92×**. Five of the twenty-four JSON cells moved by more than the ±10 % band,
three in our favour (`Nested POCO — Serialize` −28.1 % AOT and −35.4 % JIT, `Simple POCO — Serialize`
−10.3 % JIT) and two against, which are printed here rather than left for a reader to find: AOT `Simple
POCO — Serialize` 0.64× → **0.74×** (our arm +6.0 %, the System.Text.Json arm −7.8 % — still a lead, now
a smaller one) and JIT `LargeString 1KB — Serialize` 0.76× → **0.96×** (ours +6.2 %, theirs −15.5 %).
JIT `LargeFlat 50-fields — Serialize` also reads 1.19× → **1.27×** here, which is the same row that
measured 1.186× → 1.117× in its own paired A/B when the change landed: that is this machine's
round-to-round room on those three rows, and it is why the ratio — not the µs — is what this page
publishes. The µs deltas are **overstated**, not understated, this time: this window ran ~16 % faster
than the last one on both arms of the paths no commit touched, so part of every µs improvement below is
the window, while the ratios, taken inside one window, carry no such discount.

MessagePack keeps its AOT sweep, and the two JIT cells that used to be its worst are gone. Under AOT the
four rows are **4 of 4** ahead — `Metrics — Serialize` **0.60×**, `Person — Serialize` **0.69×**, `Person — Deserialize` **0.75×**
and `Metrics — Deserialize` **0.88×** — every one of them beyond the band, row median **0.72×**. Under JIT
the eight rows split **7–1** with a median of **0.78×**, and the one loss is `Complex — Deserialize`
**1.12×** (0.335 vs 0.299 µs), beyond the band, with both arms slower than last window (ours +10.9 %,
theirs +4.5 %). The two rows that were this page's worst MessagePack losses are now its two biggest
leads, and they are what the two bulk commits bought: `Collection — Deserialize` **1.016 → 0.382 µs
(−62.4 %)**, **1.43× → 0.55×**, and `Collection — Serialize` **0.254 → 0.136 µs (−46.5 %)**, **1.24× →
0.60×**. Both are structural — one call reads a flat int list, one call writes it, with the per-element
loop kept as the fallback — and both are gated on the type the generator declared, so the other six JIT
rows moved only with the machine (−5.5 %…+15.7 % on our arm, −2.8 %…+10.2 % on MessagePack-CSharp's) and
none of the four AOT rows moved beyond the band (0 material cells of 4). The AOT benchmark has no
collection scenario at all — MessagePack-CSharp's collection formatters do not survive NativeAOT — so the
bulk paths' AOT effect is quoted from the changes' own paired A/Bs instead of from this table: read side
1270 → **468 ns (0.34×, −63 %)**, write side 779.2 → **182.1 ns (0.234×, 8 of 8 rounds lower)**.

> Where a row's own three rounds disagree, the spread stays in the record rather than being averaged
> away. Two rows are worth naming. YAML JIT `Simple — SerializeToString` could not use its second and
> third kept rounds at all: the YamlDotNet arm was refused *unstable* in both (`r27`, `r28`), so the row
> rests on `r26`/`r36`/`r39` and reads 0.135 / 0.138 / 0.159 µs against 6.108 / 6.377 / 6.293 µs — a
> 1.18× swing on our arm across rounds that each passed their own five-run criteria, which is why that
> row's 0.02× is quoted as the published definition rather than as a claim about its second decimal.
> JSON JIT `LargeString 1KB — Serialize` is the widest on our own side: 0.225 / 0.172 / 0.188 µs (1.31×)
> against 0.251 / 0.196 / 0.191 µs — that spread is where its 0.96× comes from, and at 0.96× the row is
> inside the ±10 % band, read as neither a gain nor a loss.

PicoSerDe is faster than Tomlyn 2.10.1 under JIT **and** under NativeAOT — the one format where the
opponent does the same job (one library call, same POCO in and out) in both modes, and here both modes
were re-measured in the same window, three rounds each, on the same machine state: **0.09×–0.35×** under
JIT (median 0.19×) and **0.10×–0.41×** under AOT (median 0.21×), against 0.09×–0.42× and 0.10×–0.40× in
the window this one replaces. Seventeen of the twenty ratios moved by less than the ±10 % band, and the
three that stepped further are not a direction change: under JIT `Simple — Serialize` 0.18× → 0.20× and
`Simple — Deserialize←string` 0.08× → 0.09× moved because **Tomlyn's** arm read 25 %/12 % faster while
ours read 19 %/9 % faster, and `Medium — Serialize` 0.29× → 0.25× moved the other way because our arm
read 17 % faster while Tomlyn's read 5 % faster — on a hot path no commit here has touched. That is the
point of re-measuring such a path: it measures the size of the cross-window offset instead of assuming
it (this window: −16 % median on ours, −15 % on the opponents'). YamlDotNet's typed mapper is beaten on
CoreCLR only: under NativeAOT that layer does not work at all (typed `Deserialize<T>` throws
`MissingMethodException`, typed `Serialize` silently emits `{}`), so there is no AOT opponent to quote
and none is invented; the YAML row reads **0.02×–0.06×**, median **0.04×**, and its two cells that print
differently (0.04× → 0.03×, 0.03× → 0.02×) are two-decimal rounding at 0.0339× and 0.0249×. INI gets no
ratio because ini-parser exposes no typed API in any execution mode — a caller-written DOM walk is not
the same job as one library call — so only its absolute cost is quoted, on a `NON-EQUIVALENT` table:
0.084 µs vs 7.597 µs on the smallest write row and 2.250 µs vs 22.018 µs on the largest read row, our
arms 0 %–13 % faster and the DOM walk 6 %–18 % faster than in the previous window, i.e. the same offset
as everywhere else in it.

### What the JIT column is comparing

The JSON JIT row is measured the way a user runs it — the runtime's default tiered compilation with
dynamic PGO on — and that default is not neutral between two libraries: tiering is a re-compilation
pipeline, and each side converts it into throughput differently. So the same row was measured twice more
in this window, same benchmark project, same arms, same machine state, one runtime knob changed each
time:

| JSON JIT, 12 scenarios | median ratio | slower | range | `LargeFlat 50-fields — Deserialize` |
|---|---:|---:|---|---:|
| **default — tiered JIT + dynamic PGO** (the published row) | **0.96×** | 3 of 12 | 0.51×–1.33× | **1.33×** |
| `DOTNET_TieredPGO=0` — tiering on, dynamic PGO off | **0.94×** | 3 of 12 | 0.59×–1.23× | **1.23×** |
| `DOTNET_TieredCompilation=0` — single-tier JIT, no PGO | **0.58×** | **0 of 12** | 0.32×–0.74× | **0.73×** |

Three rounds each, under the same protocol and the same load gates: `r14`/`r15`/`r16` for `TC=0`,
`r17`/`r18`/`r19` for `PGO=0`, one row in the `PGO=0` companion taking one later kept round (`r35`) for an
arm a kept round had refused.

**In one line: the distance in the JIT column is what the runtime's tiering does for the two libraries,
not what their code does.** With tiered compilation off on both sides the row has **no losing scenario
at all** — 0.58× median, 0.32×–0.74× — and with tiering on but dynamic PGO off it still reads 0.94×.
Measured per arm rather than inferred: turning **dynamic PGO** off costs System.Text.Json a median
**1.14×** (0.91×–1.36×) and costs us **1.13×** (0.89×–1.40×), near enough to symmetric that it explains
essentially none of the gap; turning **tiered compilation** off costs System.Text.Json a median
**1.97×** (1.25×–2.29×) and costs us **1.14×** (0.90×–1.33×) — that difference is the gap, and it is
worth the whole 0.58× → 0.96× on the row median. The largest JIT-scenario loss,
`LargeFlat 50-fields — Deserialize`, reads the same way at 1.33× by default, 1.23× with PGO off and
0.73× with tiering off — which is why that scenario is quoted as a ratio under the runtime's default,
next to the two companion numbers, instead of being presented as a code defect. The other two losses
follow the same shape: `LargeFlat 50-fields — Serialize` 1.27× by default and 0.56× with tiering off,
`Complex POCO — Deserialize` 1.14× by default and 0.74× with tiering off. Neither row is an excuse: a
reader who wants to know what they will actually get reads the published JIT numbers, and a reader who
wants to know how much of them is the tiering pipeline reads this table.

### What a published number requires

A run that misses **any** of these is not published, however good its numbers look:

- **An idle machine, checked rather than assumed** — before every arm a fixed allocation-free probe
  is timed twice, and the arm is refused on drift >**20%**, run-to-run spread >**3.0×** or relative
  dispersion >**25%**; the same **3.0×** limit is then re-applied to the arm's own five measured runs.
- **System-wide CPU is the decisive criterion** — above **35%** of the machine used by processes other
  than the benchmark (own CPU subtracted) the arm is not published, and a host that cannot report load
  at all is *unknown*, which refuses instead of passing. `PICOBENCH_MAX_OTHER_CPU` and
  `PICOBENCH_ALLOW_UNKNOWN_CPU=1` are the only overrides, and both brand the output as untrustworthy.
- **≥100k iterations per operation and ≥5 independent runs** — every run kept, none discarded; the
  iteration count each arm really ran is printed in the table footer, so the floor is auditable
  rather than claimed.
- **Median + dispersion** (min–max and standard deviation) — never a bare average on its own.
- **AOT and JIT in separate columns** — never merged into one figure, and never into one ratio.
- **Equivalent competitor arms** — same input, same typed mapping, same output type. A hand-written
  DOM walk is not the same job as a library call, so such tables carry no ratio at all.
- **Cross-session noise is ±10%, not "a few σ"** — the standard deviation of five runs inside one
  process bounds the measurement, not the machine state, so a ratio within ~10% of 1.00 is not a
  direction claim in either direction. Cross-**window** offsets are larger than that and are not
  assumed away: this window's untouched paths read **9 %–21 % faster** on our arms and **5 %–25 %
  faster** on the opponents' than the previous window's — i.e. the same offset on both sides, which is
  why every published figure is a ratio computed inside one window, and why a µs quoted across windows
  is labelled as such (and why a µs improvement across two windows is overstated by roughly that
  much when, as here, the later window ran faster).
- **The JIT column says which JIT** — a JIT ratio is measured under the runtime's default tiered
  compilation with dynamic PGO on, because that is what a user gets, and that default is not neutral
  between two libraries (see [What the JIT column is
  comparing](#what-the-jit-column-is-comparing)). Where a row's distance could be mistaken for a code
  property, the same row is measured with `DOTNET_TieredCompilation=0` and with `DOTNET_TieredPGO=0` in
  the same window, and all three numbers are published rather than the most flattering one.
- **Three rounds, and a median of medians** — one process is one round, and every figure above is the
  median of the **three earliest rounds that published every arm of its row**. A round that refused any
  arm of a row contributes nothing to that row and is re-run; it is never averaged back in, a later
  round fills a row only when it supplies a missing arm, and a row is
  never filled from an older session. Ten rows here took one such later round, and they are named in the
  ledger paragraph above rather than left to be guessed.

Reproducing it: `dotnet build PicoSerDe.slnx -c Release -m:1`, then one process per format —
`PICOBENCH_STRICT=1 dotnet run --project <format>/benchmarks/<format>.Benchmarks -c Release --no-build`
for `PicoJetson`, `PicoMsgPack`, `PicoIni`, `PicoToml`, `PicoYaml` — three times each, strictly serial.
The AOT arms are the same projects published as `dotnet publish <project> -c Release -r win-x64
-p:PublishAot=true -o <dir>` and the produced `.exe` run the same way; MessagePack's AOT arms live in
`PicoMsgPack.Benchmarks.Aot`, because MessagePack-CSharp's formatters for enums and collections do not
survive NativeAOT (see the capability table below). Each table ends with its own `PICOBENCH_SUMMARY …
published= refused= mode= exit=` line: that line, not prose, is what says whether a run published
anything. The two JSON JIT companions are the same `PicoJetson` project run three times each, unchanged,
with one environment variable in front: `DOTNET_TieredCompilation=0 PICOBENCH_STRICT=1 dotnet run
--project PicoJetson/benchmarks/PicoJetson.Benchmarks -c Release --no-build` and the same line with
`DOTNET_TieredPGO=0` instead — separate processes, never a run merged with the published JIT row.

### NativeAOT support, by competitor (verified, not a performance claim)

These are capability facts, not performance claims. The two columns are deliberately separate, because
"the library runs ahead of time" and "the library maps typed POCOs ahead of time" are different
questions and only the second one is what a serializer user cares about.

| Library | Runs under NativeAOT | Typed POCO round-trip under AOT |
|---------|:--------------------:|:-------------------------------:|
| System.Text.Json | ✅ with a source-generated `JsonSerializerContext` | ✅ |
| MessagePack-CSharp | ⚠️ only statically resolvable types — e.g. `string`, `int`, `bool`, `double`, `DateTime`; **no enum, no generic collection** | ⚠️ enums and generic collections throw `MissingMethodException` — it builds formatters with `MakeGenericType` + `Activator.CreateInstance` |
| ini-parser | ✅ DOM parsing works | ❌ reflection-based, no AOT mapper |
| Tommy | ✅ DOM parsing works | ❌ no typed mapper (only the DOM API) |
| YamlDotNet | ✅ DOM works | ❌ typed `Deserialize<T>` throws `MissingMethodException`; typed `Serialize` **silently emits `{}`** |
| **PicoSerDe** | ✅ | ✅ all five formats |

**PicoSerDe is the only AOT solution above that round-trips typed POCOs across all five formats** (JSON,
TOML, YAML, INI, MessagePack). That is a capability statement, not a throughput statement: it says what
runs at all, not what runs fastest.

> The [`Comparison`](#comparison) table further down scores `AOT` as the second column above
> — typed serialization works ahead of time — which is why ini-parser, Tommy, YamlDotNet and
> MessagePack-CSharp score ❌/⚠️ there even though their parsers themselves do run under AOT.
>
> That table's VYaml `⚠️` is a documentation claim (`[YamlObject]` + source generation),
> **not** something this repo verified — no VYaml publish/run was attempted here.

Full per-scenario tables, per-run logs and the history behind the **2026-10-27** session:
[docs/perf/measurement-2026-10-27.md](docs/perf/measurement-2026-10-27.md). That archive is that
session's record and predates every figure above — it is kept as history, not as a second opinion on
today's numbers. The 2026-10-29 run behind this page is reproducible with the commands above; its raw
logs are session-local and are not committed.

### What the last round of changes did

**The table holds two kinds of evidence, and the "Numbers from" column says which.** The first seven
rows are each change's own before/after A/B on this machine, taken on 2026-10-27 while the change was
being made: "before" is a second worktree at the parent commit, measured in the same sitting, not a
number remembered from an older README. "After" there is that sitting's figure, not the one in the
table above — the table above is a three-round median, and the two differ by cross-session drift as
well as by the change itself (e.g. `LargeString 1KB — Deserialize` measured 0.261 µs in its own A/B
and 0.329 µs in the 08:41 window). The rows below them are **cross-session** instead: "before" is the
figure this page published for that scenario when the change landed, and "after" is that row's figure
from the window in which it was last refreshed — the **17:49–18:24** window for the two newest rows
(the JSON short-string/property-literal write, and the MessagePack bulk list paths, three commits that
all landed after the previous window was taken), the **10:20–11:39** window for the five rows above
them (the long-string write, the `DateTime`/enum read, the temporal/number write, the cold-throw hoist
and the MessagePack read-side span fast path), i.e. the numbers the table above printed until this
window, and the **23:17–00:03** window for the three rows above those (the property-name staging, the
`PeekByte`/fused-read change and the counted scope). That comparison carries the window-to-window
offset on top of the change, and this time it runs the other way: every untouched path read **9 %–21 %
faster on our arms and 5 %–25 % faster on the opponents'** here (TOML and YAML; median ≈ −16 % and
−15 %; INI's absolute costs fell 0 %–13 % on ours and 6 %–18 % on the DOM walk), the JSON opponent's own
arms barely moved (median −0.1 %, range −15.5 % to +8.1 %) and the MessagePack-CSharp arms read
**+3.9 %** (median, −2.8 % to +14.2 %). Because that offset hits our
"after" µs as well, a µs improvement in the two newest rows **overstates** the change it belongs to this
time, while the ratio — two arms inside one window — does not carry it at all; the changes' own paired
A/Bs are quoted under the table.
Several of these changes touch overlapping code, so a single row is the change's footprint on the
scenario it aimed at, not an isolated attribution.

| Change (commit that landed it) | Scenario | Before | After | Numbers from |
|---|---|---|---|---|
| **JSON read: a property name resolved through an exact-match index** instead of a case-folding `Eq` chain per property (`5169116`) | JIT `LargeFlat 50-fields — Deserialize` | 6.120 / 6.132 µs — **2.82× / 2.97×** | 3.095 / 3.184 µs — **1.47× / 1.42×** (−49%) | `.superpowers/sdd/perf-p01/report.md` §5.1 |
| **JSON write: property names written from compiled UTF-8 constants**, `ConvertName` kept only when a naming policy is set (`583b551`) | JIT `LargeFlat 50-fields — Serialize` | median **2.244 µs** of 2.340 / 2.147 — 2.06× | median **1.874 µs** of 1.849 / 1.874 / 1.893 (−16.5%) | `.superpowers/sdd/perf-p02/report.md` §5.1 |
| **JSON strings: SIMD scan, long strings kept zero-copy** (`4c5d107`) | JIT `LargeString 1KB — Deserialize` | 1.472 / 1.660 / 1.743 µs — **3.70×–5.71×** | 0.260 / 0.261 / 0.267 µs — **0.91×–1.07×** (−84%) | `.superpowers/sdd/perf-p03/report.md` §0 |
| **MessagePack: `timestamp64` read the spec's bit layout backwards** (`9e13e78`) | reading a `timestamp64` extension | `2023-11-14T22:15:23.1234567Z` decoded as **`1973-11-29T21:33:10.7000001Z`** | decoded correctly; wire and speed unchanged — a correctness fix, not a speed claim | `.superpowers/sdd/mp-ts64/report.md` §0 |
| **MessagePack: the writer owns a span** instead of calling `GetSpan`/`Advance` per token (`56d86e5`) | JIT `Collection — Serialize` | 0.474 µs — **2.06×** | 0.300 µs — **1.28×** (−37%) | `.superpowers/sdd/perf-mp-span/report.md` §5.2 |
| **MessagePack: strings encoded straight into that span**, no `byte[]` per key (`0a45e16`) | 15-string model, allocation per serialize | **600 B/op** (15 × a 40-byte array) | **0 B/op**; CPU **0.687×** (median of three paired runs) | `.superpowers/sdd/perf-mp-str/report.md` §4.1, §5.1 |
| **MessagePack: `DateTime` written as the spec's timestamp extension** (`d4da7cc`) | JIT `Metrics — Deserialize` | 0.518 µs — **4.16×**; payload **53 B** | 0.156 µs — **1.30×**; payload **30 B** | `.superpowers/sdd/perf-mp-ts/report.md` §5 |
| **MessagePack: the read side gets a span fast path** — span-mode payloads tokenized without the sequence state machine, pinned by a derivation check re-run on every test run (`d45edca`, `d9334ed`; merged `340d351`) | AOT `Metrics — Deserialize`; AOT `Person — Deserialize` | 0.206 µs — **1.51×** (08:41 window); **1.14×** (no µs published) | 0.123 µs — **0.87×** (−40 % µs); 0.103 µs — **0.77×** (1.14× → 0.77×) — that `Metrics` row rested on **eight** kept rounds in the 10:20–11:39 window because five of them refused one of its arms as *unstable*; in this window it reads 0.126 µs / **0.88×** on `r1`/`r2`/`r3` | `.superpowers/sdd/remeasure-4/logs/round{2,5,62}-msgpack-aot.txt` |
| **JSON write: the writer owns a span** instead of calling `GetSpan`/`Advance` per token (`2310b36`) | JIT `LargeFlat 50-fields — Serialize` | 1.628 µs — **1.79×** | 1.319 µs — **1.30×** (−19.0% µs) | `.superpowers/sdd/remeasure-2/logs/round{1,2,3}-json-jit.txt` |
| **JSON strings: "does this need escaping" answered in one vectorized pass**, and the escape count taken before the quote reaches the write cursor (`58f3c9f`) | JIT `LargeString 1KB — Serialize` | 1.020 µs — **4.72×** | 0.389 µs — **1.66×** (−61.9% µs) | `.superpowers/sdd/remeasure-2/logs/round{1,2,3}-json-jit.txt` |
| **JSON read: `TryReadInt32` fused into one pass**, and the generated element loops call the fused read (`886f1b9`, `bbceb67`) | JIT `Collection POCO — Deserialize` | 4.694 µs — **1.51×** | 2.255 µs — **0.67×** (−52.0% µs) | `.superpowers/sdd/remeasure-2/logs/round{1,2,3}-json-jit.txt` |
| **MessagePack: the integer value derived while the tag is in hand**, so `TryGetInt32`/`TryGetInt64` stop re-deriving it (`1eba0fb`) | JIT `Collection — Deserialize` | 1.432 µs — **1.96×** | 1.078 µs — **1.56×** (−24.7% µs) | `.superpowers/sdd/remeasure-2/logs/round{1,2,4}-msgpack-jit.txt` |
| **MessagePack: `TryReadInt32`/`Int64`/`Float64`/`Bool` fused** — advance, classify, decode and materialise in one dispatch (`a3faf4d`, `f5a59e6`) | AOT `Person — Deserialize` | 0.134 µs — **0.99×** | 0.112 µs — **0.80×** (−16.4% µs) | `.superpowers/sdd/remeasure-2/logs/round{1,2,3}-msgpack-aot.txt` |
| **MessagePack: a collection with a known length iterates that count** instead of probing for an end token per element (`491e731`) | JIT `Nested — Deserialize` | 0.363 µs — **0.89×** | 0.324 µs — **0.87×** (−10.7% µs) | `.superpowers/sdd/remeasure-2/logs/round{1,2,3}-msgpack-jit.txt` |
| **JSON write: a property name is staged and emitted as one write** — the opening quote, the name and the colon leave as one span write instead of four (`8fc1db4`, merged `8d59010`) | JIT `Nested POCO — Serialize`; AOT `Nested POCO — Serialize` | 0.373 µs — **1.12×**; 0.679 µs — **1.46×** | 0.253 µs — **0.81×** (−32.2 % µs); 0.471 µs — **1.07×** (−30.6 % µs) | `.superpowers/sdd/remeasure-3/logs/round{1,14,15}-jetson-jit.txt`, `round{1,7,8}-jetson-aot.txt` |
| **JSON read: one `PeekByte` per token, with `int64` and `bool` decoded in the same dispatch as the tag** (`9fd79a8`, merged `bc2255e`) | JIT `Complex POCO — Deserialize`; AOT `Complex POCO — Deserialize` | 0.894 µs — **1.54×**; 1.280 µs — **1.38×** | 0.831 µs — **1.70×** (−7.0 % µs; the ratio rises because the System.Text.Json arm read 0.581 → 0.490 µs); 1.148 µs — **1.32×** (−10.3 % µs) | `.superpowers/sdd/remeasure-3/logs/round{1,14,15}-jetson-jit.txt`, `round{1,7,8}-jetson-aot.txt` |
| **MessagePack: a collection's elements are accounted in a counted scope** instead of per token on the level stacks (`588db56`, merged `f94fb8d`) | JIT `Collection — Deserialize` | 1.078 µs — **1.56×** | 0.853 µs — **1.37×** (−20.9 % µs) | `.superpowers/sdd/remeasure-3/logs/round{1,3,11}-msgpack-jit.txt` |
| **JSON write: a long string is encoded straight into the writer's own span** instead of being copied through two intermediate arrays (`7b1a3b7`, merged `177c550`) | JIT `LargeString 1KB — Serialize`; AOT `LargeString 1KB — Serialize` | 0.346 µs — **1.59×**; 0.536 µs — **1.29×** | 0.177 µs — **0.76×** (−48.8 % µs); 0.333 µs — **0.74×** (−37.9 % µs) | `.superpowers/sdd/remeasure-4/logs/round{23,24,25}-jetson-jit.txt`, `round{9,10,11}-jetson-aot.txt` |
| **JSON read: `DateTime` and enum values answered from the span**, not from a materialised string (`4781c1f`, merged `9861c78`) | JIT `Complex POCO — Deserialize`; AOT `Complex POCO — Deserialize` | 0.831 µs — **1.70×**; 1.148 µs — **1.32×** | 0.695 µs — **1.19×** (−16.4 % µs); 0.934 µs — **1.01×** (−18.6 % µs) | `.superpowers/sdd/remeasure-4/logs/round{23,24,25}-jetson-jit.txt`, `round{9,10,11}-jetson-aot.txt` |
| **JSON write: temporals and numbers written through the span**, with no string in between (`b9546d6`, merged `28d7c26`) | AOT `Collection POCO — Serialize`; JIT `Collection POCO — Serialize`; JIT `Complex POCO — Serialize` | 2.056 µs — **1.31×**; 0.994 µs — **0.82×**; 0.367 µs — **1.20×** | 1.397 µs — **0.80×** (−32.1 % µs); 0.882 µs — **0.67×** (−11.3 % µs); 0.330 µs — **0.91×** (−10.1 % µs) | `.superpowers/sdd/remeasure-4/logs/round{23,24,25}-jetson-jit.txt`, `round{9,10,11}-jetson-aot.txt` |
| **JSON read: the cold throws hoisted out of the token loop, and the case-sensitivity option read once** instead of per token — the 3-byte packed property key it was paired with measured 5 % slower on its own row and was reverted (`67435ab`) (`62177eb`, merged `9bf719b`) | JIT `LargeFlat 50-fields — Deserialize`; AOT `LargeFlat 50-fields — Deserialize` | 2.602 µs — **1.36×**; 3.011 µs — **0.97×** | 2.693 µs — **1.27×** (+3.5 % µs); 3.207 µs — **0.95×** (+6.5 % µs) — the µs carry the 10:20–11:39 window's offset, so the row's claim is the ratio; in this window the same two rows read 2.654 µs / **1.33×** and 3.356 µs / **0.98×** | `.superpowers/sdd/remeasure-4/logs/round{23,24,25}-jetson-jit.txt`, `round{9,10,11}-jetson-aot.txt` |
| **JSON write: a short ASCII string encoded in one pass, and a property name written as one literal** instead of a staged opening-quote / name / colon sequence (`f46e672`, merged `0234838`) | JIT `Nested POCO — Serialize`; AOT `Nested POCO — Serialize`; AOT `LargeFlat 50-fields — Serialize` | 0.272 µs — **0.79×**; 0.448 µs — **0.96×**; 1.609 µs — **1.00×** (10:20–11:39 window) | 0.187 µs — **0.51×** (−31.3 % µs); 0.312 µs — **0.69×** (−30.4 % µs); 1.395 µs — **0.92×** (−13.3 % µs) | `.superpowers/sdd/remeasure-5/logs/round{11,12,13}-jetson-jit.txt`, `round{8,9,10}-jetson-aot.txt`; the change's own paired A/B: `.superpowers/sdd/json-jit-gap/report.md` §0 (JIT `LargeFlat 50-fields — Serialize` 1.186× → 1.117×, AOT 1.049× → 0.908×) |
| **MessagePack: a flat int list read in one call** (`5a5d895`, merged `7324990`) **and written in one call** (`fc4ee5c`, merged `a2b4224`), the per-element loop kept as the fallback and the fast path gated on the type the generator declared | JIT `Collection — Deserialize`; JIT `Collection — Serialize`; AOT (the changes' own A/B — this table's AOT benchmark has no collection scenario) | 1.016 µs — **1.43×**; 0.254 µs — **1.24×** (10:20–11:39 window); AOT read **1270 ns**, AOT write **779.2 ns** | 0.382 µs — **0.55×** (−62.4 % µs); 0.136 µs — **0.60×** (−46.5 % µs); AOT read **468 ns (−63 %)**, AOT write **182.1 ns (0.234×)** | `.superpowers/sdd/remeasure-5/logs/round{4,5,7}-msgpack-jit.txt`; the AOT figures are the changes' own paired A/Bs: `.superpowers/sdd/mp-col-jit/report.md` §0 and `.superpowers/sdd/mp-bulk-port/report.md` §0 |

The three MessagePack A/B pairs at the top of the table (`56d86e5`, `0a45e16`, `d4da7cc`) were taken
with `PICOBENCH_MAX_OTHER_CPU=100`: only the system-wide CPU gate was relaxed, while the drift, spread
and dispersion criteria stayed in force — each of those reports says so in its own measurement section.
The JSON pairs ran under the default 35% gate, and so did every cross-session row here: the 35 % ceiling
is what refused the 33 arms this window had to throw away. Each pair is one sitting, so it is evidence
of direction and rough magnitude; the table above is what this page stands behind. The cross-session
rows (4e and the fifteen below it) are not A/B sittings: their before is an earlier published figure
and their after is the figure of the window named above, so drift sits in both numbers — and for the 4e
row in particular, part of the move from 0.137 µs (21:45–21:53) to 0.123 µs (the 10:20–11:39 window) is
drift on a path no commit here touches, which is why that row is quoted as the published metric rather
than as that change's own effect. The three rows that still carry the 23:17–00:03 window's figures as
their "after" do so because that is the window in which they were last refreshed, and the scenarios they
name read 0.187 µs / 0.51× (`Nested POCO — Serialize`, JIT), 0.706 µs / 1.14× (`Complex POCO —
Deserialize`, JIT) and 0.382 µs / 0.55× (`Collection — Deserialize`, MessagePack JIT) in this window;
the five rows above them read this window's figures in the last paragraph below. The offset now runs the
other way from last time — the whole window ran ~15 % fast, so a µs improvement in the newest rows is
**overstated** by about that much, and their ratio is the claim.
Where a change has its own paired A/B, it reads: JSON write span cache — JIT `LargeFlat 50-fields —
Serialize` **3.045 → 1.671 µs (−45.1%)**, AOT **3.046 → 2.052 µs (−32.6%)**
(`.superpowers/sdd/perf-json-span/report.md` §0); JSON escape vectorisation — JIT `LargeString 1KB —
Serialize` **1.587 → 0.532 µs (−66.5%)**, AOT **1.321 → 0.685 µs (−48.2%)**
(`.superpowers/sdd/perf-json-strscan/report.md` §0); fused JSON integer reads — `Collection DES`
**20,849 → 13,524 ns/op (−35%)**, `TenIntsList DES` **−39%**
(`.superpowers/sdd/t15-json-emit/report.md` §0); MessagePack integer cache — JIT
`Collection — Deserialize` **1.321 → 1.146 µs**, 1.90× → 1.59×
(`.superpowers/sdd/perf-mp-collection/report.md` §0); fused MessagePack members — per-token recovery
**10.0%** for `int32` (`t6-int32`) and **≈16% / ≈13%** for `float64` / `bool` (`t78-fused`), both far
larger than the same changes' end-to-end footprint, exactly as those reports say. Those paired numbers
are the change's effect; the table's cross-session numbers are the published metric moving, which is a
weaker claim and is written that way. The `.superpowers/` report paths are session-local and
gitignored, exactly like the raw benchmark logs; the commit hashes in the first column are the durable,
checkable artefact.

This window's tables carry the check. The rows those changes touched now read: JSON JIT
`Nested POCO — Serialize` **0.187 µs / 0.51×**, `Simple POCO — Serialize` **0.066 µs / 0.61×**,
`Collection POCO — Deserialize` **2.180 µs / 0.65×**, `Collection POCO — Serialize`
**0.874 µs / 0.68×**, `Complex POCO — Deserialize` **0.706 µs / 1.14×**, `LargeFlat 50-fields —
Serialize` **1.173 µs / 1.27×** and `LargeFlat 50-fields — Deserialize` **2.654 µs / 1.33×**; JSON AOT
`Nested POCO — Serialize` **0.312 µs / 0.69×**, `LargeFlat 50-fields — Serialize` **1.395 µs / 0.92×**,
`Complex POCO — Serialize` **0.582 µs / 0.90×**, `Collection POCO — Serialize` **1.297 µs / 0.77×**,
`LargeString 1KB — Serialize` **0.382 µs / 0.79×**, `Complex POCO — Deserialize`
**0.884 µs / 0.94×** and `LargeFlat 50-fields — Deserialize` **3.356 µs / 0.98×**; MessagePack JIT
`Collection — Deserialize` **0.382 µs / 0.55×**, `Collection — Serialize` **0.136 µs / 0.60×**,
`Nested — Serialize` **0.103 µs / 0.45×**, `Complex — Serialize` **0.187 µs / 0.93×** and `Complex —
Deserialize` **0.335 µs / 1.12×**; MessagePack AOT `Metrics — Serialize` **0.077 µs / 0.60×**,
`Person — Deserialize` **0.115 µs / 0.75×** and `Metrics — Deserialize` **0.126 µs / 0.88×**. Every one
of those is this window. Three JSON JIT rows are still losses and the page does not dress them as gains:
`LargeFlat 50-fields — Deserialize` 1.33×, `LargeFlat 50-fields — Serialize` 1.27× and `Complex POCO —
Deserialize` 1.14× are the object-shaped rows that neither of this window's commits aimed at, and all
three turn into leads with tiered compilation off (0.73× / 0.56× / 0.74×). On the JSON AOT side there
is no loss left at all — twelve of twelve ahead — and MessagePack's one remaining loss is `Complex —
Deserialize` 1.12× — a tenth past the band, against the 1.43× its collection read used to be.

---

## Design

```csharp
// One API across all formats. The four text formats return string;
// MsgPack is binary and returns byte[].
JsonSerializer.Serialize<T>(value)                 // → string  (PicoJetson)
TomlSerializer.Serialize<T>(value)                 // → string  (PicoToml)
YamlSerializer.Serialize<T>(value)                 // → string  (PicoYaml)
IniSerializer.Serialize<T>(value)                  // → string  (PicoIni)
MsgPackSerializer.SerializeToUtf8Bytes<T>(value)   // → byte[]  (PicoMsgPack)
```

> `SerializeToUtf8Bytes` is the `byte[]` entry point (JSON has one too); the
> plain `Serialize` overloads of the text formats return `string`.

### Attribute-Driven Registration

PicoSerDe source generators discover types through **four independent pipelines**:

1. **Usage-driven** — calling `Serialize<T>()` or `Deserialize<T>()` triggers generation for `T`
2. **Generic attribute** — `[PicoSerializable]` marks a type for all referenced format modules
3. **Format-specific attribute** — `[PicoJsonSerializable]` / `[PicoIniSerializable]` / etc. marks a type for one format
4. **Shorthand attribute** — `[GenerateSerializer(typeof(T))]` for central registration

```csharp
// All referenced formats generate serializers
[PicoSerializable]
public class UserDto { public string Name { get; set; } }

// JSON only (PicoJetson)
[PicoJsonSerializable]
public class JsonOnlyDto { public string Label { get; set; } }

// Indirect — target type from any assembly
[PicoIniSerializable(typeof(ExternalLibrary.SharedDto))]
class Config { }

// Shorthand — equivalent to PicoSerializable(typeof(T))
[GenerateSerializer(typeof(UserDto))]
[GenerateSerializer(typeof(ProductDto))]
class PicoSerDeConfig { }
```

| Attribute | Scope | Defined in |
|-----------|-------|------------|
| `[PicoSerializable]` | All formats — direct or `typeof(T)` | `PicoSerDe.Core` |
| `[GenerateSerializer]` | Shorthand for `PicoSerializable(typeof(T))` | `PicoSerDe.Core` |
| `[PicoJsonSerializable]` | JSON only | `PicoJetson` |
| `[PicoIniSerializable]` | INI only | `PicoIni` |
| `[PicoTomlSerializable]` | TOML only | `PicoToml` |
| `[PicoMsgPackSerializable]` | MsgPack only | `PicoMsgPack` |
| `[PicoYamlSerializable]` | YAML only | `PicoYaml` |

No attributes are required for basic usage — calling `Serialize<T>()` automatically triggers generation.

## Key Features

### Strict Deserialization (fail-loud, STJ-compatible semantics)

Deserialization validates input shape and types instead of silently producing
default values:

- **Wrong-typed values throw** `FormatException` — `{"age":"abc"}` into an `int`
  property throws instead of yielding `0`
- **Invalid string escapes throw** — RFC 8259 only allows `\" \\ \/ \b \f \n \r \t \uXXXX`;
  unknown escapes (e.g. `\q`) are rejected instead of being silently unescaped (`\b` and `\f` are
  now decoded correctly)
- **Leading commas throw** — `,5` and `[,1]` are invalid JSON and are rejected
- **Top-level `null` returns `null`** for reference-type targets (STJ semantics);
  value-type targets throw
- **Trailing data after the document root throws**
- **Missing `required` members throw** — C# `required` properties are enforced at
  runtime across object, nested, streaming, and polymorphic paths
- **`PicoDocument.IsValid` performs full structural validation** — mismatched
  brackets, bare values in objects, missing property values, and multiple root
  values are all rejected (token-level checks are not enough)
- **Arrays**: `null` elements are allowed for reference-type elements and throw
  for value-type elements; wrong-typed elements throw
- **Comments** (`ReadCommentHandling.Skip`): malformed comment syntax (`/x`) and
  unterminated block comments throw instead of being silently swallowed
- **Streaming** (`DeserializeFromStreamAsync<T>`) honors the same semantics —
  `UnmappedMemberHandling.Disallow` and top-level `null` handling are identical
  to the direct path (v2026.4.11+; both paths share one generated dispatch
  chain). Struct DTOs and propertyless types have no streaming delegate
  (`DeserializeFromStreamAsync<Struct>` throws `InvalidOperationException`)
- **MsgPack rejects empty payloads and trailing bytes** — a zero-length buffer
  or bytes after the root value throw `FormatException`
- **INI list values parse strictly per element kind** — malformed numbers,
  booleans, dates, etc. throw `FormatException` (never silently default to `0`/
  `false`), and formatting/parsing is culture-invariant
- **INI enum values parse strictly** — an unparseable enum text now throws
  `FormatException` naming the member and offset; it used to fall back silently
  to the default (`0`) enum member
- **TOML/YAML/INI unquoted empty values are `null`, not `""`** — `Key =` (TOML/
  INI) and `Key:` (YAML) assign `null` to nullable members; INI previously
  produced the empty string. Quoted `""` is still the empty string
- **`float` keeps its own precision** — `JsonWriter.WriteNumber(float)` emits the
  shortest round-trippable single-precision text instead of widening to `double`
  and printing the widened value

### Null Semantics

Null is not portable across the five formats, so "write a null" and "read a
null" are spelled out per format below. Everything here is observed behaviour of
the generated code, not a format-theoretic claim.

**Writing a null member** (default options, `DefaultIgnoreCondition = Never`):

| Format | Null member written as | Under `WhenWritingNull` |
|--------|------------------------|-------------------------|
| JSON | `"key":null` | member omitted |
| MsgPack | `nil` (`0xC0`; the map/array header is adjusted) | member omitted |
| TOML | member omitted — TOML has no null value | member omitted |
| YAML | member omitted — YAML has no null value on write | member omitted |
| INI | member omitted — INI has no null value | member omitted |

Because the three section-based formats drop the member, **a null does not
round-trip through TOML/YAML/INI**: reading the document back leaves the target
at its default, which is the property initializer's value when one exists.
JSON and MsgPack write an explicit null and do round-trip.

**Reading a null** — accepted spellings and what the target receives:

| Format | Null spellings accepted | nullable target (`T?`, `string?`) | non-nullable value type | non-nullable `string` |
|--------|-------------------------|:---:|:---:|:---:|
| JSON | `null` | `null` | `FormatException` | `FormatException` |
| MsgPack | `nil` | `null` | `FormatException` | `FormatException` |
| TOML | `Key =` (no value), or unquoted `null` (any case) | `null` | `FormatException` | `null` |
| YAML | `Key:` (no value), `~`, or unquoted `null` (any case) | `null` | `FormatException` | `null` |
| INI | `Key =` (no value), or unquoted `null` (any case) | `null` | `FormatException` | `null` |

Rules that hold in every format:

- **A quoted scalar is never null.** `Key = ""` is the empty string and
  `Key = "null"` is the four-character string `null`; both are values, not
  omissions.
- **A missing member is not an error.** It leaves the target at its default.
- **An unquoted empty value *is* the null literal** in TOML/YAML/INI — `Key =`
  with nothing after it assigns `null`; it does not mean "member missing".
- **A null aimed at a non-nullable value type fails loudly** with
  `FormatException` instead of silently leaving `0` / `false` / `Guid.Empty`.
- **The non-nullable `string` column is the generators' NRT-lenient contract:**
  TOML/YAML/INI assign `null!` to an unannotated `string`, while JSON/MsgPack
  keep requiring a value token and reject the null.
- **Scope:** these rules cover scalar members (string, numeric, temporal, enum,
  Guid, and their nullable forms). Collection, object and dictionary branches
  keep their own empty-value semantics and are not routed through the
  null-literal path.

### Polymorphic Deserialization (Type Discriminator)

Base types declare derived types at compile time. Zero reflection, AOT-safe. Since v2026.3.0.

```csharp
[PicoSerializable]
[PicoDerivedType(typeof(MessageEntry), "message")]
[PicoDerivedType(typeof(CompactionEntry), "compaction")]
abstract class SessionEntry { }

class MessageEntry : SessionEntry { public string Content { get; set; } = string.Empty; }
class CompactionEntry : SessionEntry { public int From { get; set; } }

var json = """{"$type":"message","Content":"hello"}"""u8;
var result = JsonSerializer.Deserialize<SessionEntry>(json);
// result is MessageEntry at runtime
```

| Feature | Support |
|---------|---------|
| Serialization + Deserialization | ✅ v2026.3.0 |
| Streaming (PipeReader) | ✅ v2026.3.2 |
| Base class properties | ✅ v2026.3.3 |
| `[JsonConstructor]` on derived types | ✅ |
| Record derived types | ✅ v2026.3.23 |
| Complex/collection ctor params | ✅ v2026.3.24 |
| TOML / YAML poly support | ✅ v2026.3.24 |
| INI / MsgPack poly support | ✅ v2026.4.0 |

### DOM Layer (PicoDocument / PicoElement)

Schema-less JSON inspection without `System.Text.Json`. Zero-copy.

```csharp
var doc = PicoDocument.Parse("""{"name":"Alice","age":30}"""u8.ToArray());

var name = doc.RootElement["name"].GetString();         // "Alice"
var ok   = doc.RootElement.TryGetProperty("age", out _); // true
bool valid = PicoDocument.IsValid("{}"u8);               // true

// Numeric access
long big = doc.RootElement["count"].GetInt64();
double d = doc.RootElement["score"].GetDouble();
if (doc.RootElement["age"].TryGetInt32(out int age))
    Console.WriteLine(age);
```

### C# Records

`record` and `record struct` types are fully supported. Primary constructor auto-detected — no `[JsonConstructor]` needed. `init`-only properties work correctly.

### Top-Level Arrays

`Serialize<T[]>(...)` / `Deserialize<T[]>(...)` and streaming `DeserializeFromStreamAsync<T[]>(stream)` work directly.

### Arrays and Dictionaries

Records (`record`/`record struct` primary constructors) work in every format —
JSON, MsgPack, TOML, YAML and INI — without a format-specific constructor
attribute.

Field-level object arrays (`TObject[]`, `List<TObject[]>`, `TObject[][]`) round-trip
in JSON, MsgPack, TOML and YAML (INI drops object collections because its format
is flat — see
[Format limits](#format-limits-what-each-wire-format-cannot-express) for which
of those drops surface as a build warning). An object sequence *inside* a nested
object round-trips in YAML too (block sequences; see
[PicoYaml → Limits](PicoYaml/README.md#limits)). TOML keeps its
documented loud "nested tables deeper than one level" error. TOML arrays of tables have no chunk-resume strategy, so their streaming
delegate is not registered — `DeserializeFromStreamAsync` then buffers the stream
and uses the synchronous path (correct, just not incremental). Dictionaries accept
scalar, object and nested-dictionary values; a **collection** dict value
(`Dictionary<string, List<T>>`) is dropped with `PICOSERDE004` instead of
generating broken code.

### Nullable Elements

`List<int?>`, `List<string?>`, `List<TObject?>`, `Dictionary<string, TObject?>`
and nested lists of objects round-trip in JSON and MsgPack, including null
elements. TOML and YAML have no null *type*: null *scalar elements* are skipped
on write, and a **nullable element** (`List<int?>`, a nullable object/dict
element, or `int?` at any depth of a nested chain) is dropped with the
`PICOSERDE004` warning. Nested lists themselves are a separate axis — TOML and
YAML both express them (see
[Format limits](#format-limits-what-each-wire-format-cannot-express)). INI cannot
represent any nullable element and drops those members the same way.

### Format limits: what each wire format cannot express

Not every C# shape has a wire representation in every format. The matrix below is
the boundary of the five generated serializers. Every ❌ is enforced at
**generation time**, never at runtime. A member the format cannot express is left
out of the generated serializer and the build reports `PICOSERDE004` —
`Member 'X.Y' cannot be represented by this format — it will be ignored` — while a
self-referencing member reports `PICOSERDE003` —
`Member 'X.Y' is recursive/self-referencing and cannot be represented by this format — it will be ignored`.
The rest of the document is written and read as usual, so the drop shows up in the
build log instead of surfacing later as a mysteriously empty property.

| Shape (C#) | JSON | MsgPack | TOML | YAML | INI |
|---|---|:---:|---|---|---|
| `List<Dto>` — list of objects | ✅ | ✅ | ✅ `[[Table]]` | ✅ block sequence of mappings | ❌ see ¹ |
| `List<List<int>>` — nested primitive list | ✅ | ✅ | ✅ `G = [[1, 2], [3]]` | ✅ nested block sequence | ❌ `PICOSERDE004` |
| `List<List<Dto>>` — list of lists of objects | ✅ | ✅ | ✅ `G = [[{ Name = "a" }]]` | ✅ nested block sequence | ❌ `PICOSERDE004` |
| `List<List<int?>>` — nullable leaf in a nested list | ✅ | ✅ | ❌ `PICOSERDE004` | ✅ `- null` | ❌ `PICOSERDE004` |
| Recursive member (`Node? Child`) | ✅ | ✅ | ✅ nested tables (`[Child]`) ³ | ✅ nested mappings ³ | ❌ `PICOSERDE003` ³ |

✅ means the generated code both writes and reads the shape, and each ✅ shape is
exercised by a round-trip test in the suite. Both diagnostics are
`Warning`-severity and are created with `Location.None`, so
`#pragma warning disable` cannot silence them — suppress them by id in `NoWarn`
only if you accept the data loss.

**How to confirm, in one command** (the default build prints them):

```bash
dotnet build 2>&1 | grep PICOSERDE
# PowerShell: dotnet build 2>&1 | Select-String PICOSERDE
```

¹ **INI object lists.** INI has no array-of-tables syntax, so a `List<SomeDto>`
**member** cannot be represented and is dropped — reported as `PICOSERDE004` like
every other unrepresentable shape (it used to be a bare `continue`, the one silent
loss in the generator; that path is instrumented now). A **top-level**
`List<SomeDto>` does round-trip in INI: each item becomes a numbered section
(`[0]`, `[1]`, …). For an object list *inside* a DTO use TOML, YAML or JSON.

² **YAML nullable leaves.** YAML can express a null (`null`, `Null`, `NULL` or `~`),
so a nullable leaf is written as the null literal — `List<int?>`, `List<List<int?>>`,
`List<string?>`, `List<Dto?>` and `Dictionary<string, int?>` all round-trip, with
"null" (a value), "empty" (an empty collection) and "missing" (no member at all) kept
apart. TOML has no null literal at all, which is why the same shapes are dropped
there; the generator diagnostic, not this table, is the authority for your build.

³ **Recursion.** The row above is the self-referencing *member* (`Node? Child`):
it round-trips in every format except INI, which drops it with `PICOSERDE003`
while the type's other members still round-trip — a recursive DTO therefore
still serializes "partially" there. JSON and MsgPack keep it too (cycle-safe
extraction + seeded helpers, including on polymorphic bases) — see
[Streaming](#streaming-incremental-chunked). Two further *positions* a cycle can
sit in are **not** covered by the ✅: a recursive collection **element**
(`List<Node>` **on `Node`**) and a recursive **dict value**
(`Dictionary<string, Node>` on `Node`) are closed in JSON and MsgPack only —
TOML and YAML still drop those members with `PICOSERDE003`, reported as a debt
(`⏸`, reopen: the format can carry one, the emitter has no route for a table
inside an array yet).

Two shapes sit outside this matrix. A **collection dictionary value**
(`Dictionary<string, List<T>>`; nested dictionaries are fine) is dropped in
**every** format with `PICOSERDE004`. A **top-level scalar** target is not dropped
at all but refused outright — `Serialize<Guid>()`, `Deserialize<int?>()` and friends fail
loudly with `InvalidOperationException` ("no serializer registered").

The wire shape behind each ✅ is documented per format:
[PicoToml](PicoToml/README.md#limits) (`[[Table]]`, `G = [[1, 2], [3]]`),
[PicoYaml](PicoYaml/README.md#limits) (block sequences, `- []` vs `- "[]"`),
[PicoIni](PicoIni/README.md#limits), [PicoJetson](PicoJetson/README.md#limits),
[PicoMsgPack](PicoMsgPack/README.md#limits).

### Streaming (incremental, chunked)

`DeserializeFromStreamAsync<T>(stream, options, ct)` reads a stream
incrementally: for JSON/TOML/YAML/INI the consumed prefix is released as tokens
complete and only the current token/member window is retained, so the document
is never buffered wholesale before parsing.

**MsgPack is the exception — its stream path is a buffering implementation.**
Each read is appended to an accumulator and the whole buffer is re-parsed from
the beginning until the value is complete (correct, and it resumes cleanly, but
it is not incremental and costs O(n²) in the number of chunks).

JSON/TOML/YAML/INI accept an options argument
(`JsonOptions`/`TomlOptions`/`YamlOptions`/`IniOptions`). MsgPack also has a
`MsgPackOptions` type, but its `DeserializeFromStreamAsync` overload takes
`(stream, ct)` only — options are not accepted on the streaming path.

```csharp
await using var stream = File.OpenRead("config.toml");
var config = await TomlSerializer.DeserializeFromStreamAsync<ServerConfig>(stream);
```

The generated delegate returns a `ReadStatus`:

| Status | Meaning |
| --- | --- |
| `Success` | The value is complete |
| `NeedMoreData` | The current chunk ends inside a token/section; the runner refills and resumes from the reader's mark |
| `EndOfInput` | The document contains no value — the runner throws `FormatException` |

**Empty-stream semantics** match the synchronous path: TOML/YAML/INI treat an
empty stream as an empty object; JSON throws `FormatException` ("the document
contains no value").

**Recursive DTOs**: JSON and MsgPack support self- and mutually-referencing
types (cycle-safe extraction + seeded helpers), including recursive members on
polymorphic bases — nested values keep their derived (discriminator) type in
both the sync and chunked streaming paths. TOML/YAML/INI cannot express
unbounded nesting, so the recursive member is skipped and the source generator
reports `PICOSERDE003` (never silent). Data-level object graph cycles fail
loudly instead of overflowing the stack.

**Polymorphic hierarchies**: a concrete polymorphic base instance serializes
with a synthesized discriminator (the base type name, collision-safe) so base
instances round-trip as the base type; derived instances keep their declared
discriminator. They also keep nested object/dict members *and* collection
members (`List<T>`, `Dictionary<string,T>`) of derived types in JSON, MsgPack,
TOML and YAML (previously dropped or non-compiling in the discriminator
branches); INI ignores nested members because its format is flat.

**Diagnostics** (warnings):
`PICOSERDE001` — a property type the generator cannot resolve was ignored.
`PICOSERDE002` — two distinct types produced the same generated file name
(main hint); internal helper names get a stable hash suffix (`UniqueName`).
`PICOSERDE003` — a recursive member was skipped by a section-based format.
`PICOSERDE004` — a member shape the format cannot represent (a nested list the
format has no syntax for, a nullable value-type element, a null object element,
a collection dictionary value) was skipped.
`PICOSERDE005` — an anonymous-type collection member was skipped by a format
whose writer cannot emit one (JSON is the only writer that can).
See [Format limits](#format-limits-what-each-wire-format-cannot-express) for the
support matrix behind `PICOSERDE003`/`PICOSERDE004`.

**Custom/advanced registration** (scripts or hand-written serializers):
`JsonSerializer.RegisterStreaming<T>(StreamingFunc<JsonReader, T> func)` with
`where T : notnull`; `HasStreamingDelegate<T>()` reports whether a delegate is
registered. The source generator emits this registration in a
`ModuleInitializer` for every discovered type.

### Three-Layer Test Structure

PicoJetson tests are split into Unit / Integration / Functional projects with clear boundaries.

> **No non-generic `Serialize(Type, object?)` overloads.** PicoSerDe is designed for AOT-first
> usage where all types are known at compile time. `SerRegistry<TFormat, T>` static fields (PicoSerDe.Core) are shared
> across assemblies and provide faster lookup than a `ConcurrentDictionary<Type, ...>`.
> Framework wrappers should call the generic API internally — the type's serializer is
> guaranteed to be registered via `ModuleInitializer` as long as the type was discovered
> by any pipeline (usage-driven, attribute, or shorthand).

```
┌──────────────────────────────────────────────┐
│                 User Code                     │
└──────────────────┬───────────────────────────┘
                   │  Static SerRegistry<TFormat, T>
┌──────────────────▼───────────────────────────┐
│           PicoSerDe.Core                      │
│  ISerializer<T>  │  IDeserializer<T>         │
│  SerRegistry     │  DesRegistry              │
│  TokenType       │  SimdHelpers (Vector128)  │
│  TextHelpers     │  SerializerExtensions     │
└────┬────────┬─────────┬─────────┬─────────┬──┘
     │        │         │         │         │
 PicoJetson  PicoIni  PicoMsgPack PicoToml PicoYaml
   ││       ││         ││        ││       ││
  .Gen     .Gen       .Gen      .Gen     .Gen      ← embedded in each runtime nupkg
```

- **One-package install**: each format → runtime library (net10.0) with its source generator (netstandard2.0) **embedded** in the nupkg (`analyzers/dotnet/cs` + `build/PicoX.targets`) — a single `PackageReference` is enough; standalone `.Gen` packages are legacy/optional
- **`ref struct`** readers/writers — stack-allocated, zero heap allocation on hot path
- **Static `SerRegistry<TFormat, T>`** — per-format registries in PicoSerDe.Core; JIT/AOT inlineable, no dictionary lookups
- **`file struct`** generated implementations — devirtualization without sealed class overhead
- **Ref struct serialization** — `ref struct` types are supported as serializable types across all 5 formats. Source-generator-generated static methods + delegate dispatch bypass the `ISerializer<T>` interface constraint.
- **`JsonOptions`** — runtime configuration (indentation, naming policy, ignore conditions, etc.) passed **explicitly per call** (no ambient ThreadStatic state; options thread through reader/writer instances and SG-generated code)
- **Polymorphic deserialization** — type discriminator dispatch via `[PicoDerivedType]`; serialization + deserialization + streaming (v2026.3.0); record types (v2026.3.23); TOML/YAML poly (v2026.3.24); INI/MsgPack poly (v2026.4.0)
- **Anonymous type serialization** — `Serialize(new { A = 1, B = "x" })` with nested types, collections, `PropertyNamingPolicy.CamelCase`, `DefaultIgnoreCondition.WhenWritingNull`, and `MaxDepth` enforcement. Works across all 5 formats via C# 12 interceptors + unsafe field access (serialization only, C# 12+, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`) (v2026.4.1)
- **`PicoDocument` / `PicoElement`** — zero-copy JSON DOM for schema-less inspection (v2026.3.4)
- **C# records** — primary constructor auto-detection, `init`-only support (v2026.3.3); poly+record (v2026.3.23); complex/collection ctor params (v2026.3.24)
- **Top-level arrays** — `Serialize<T[]>()` / `Deserialize<T[]>()` with streaming (v2026.3.2)
- **Top-level scalars / `Nullable<T>` are not supported** — `Serialize<Guid>(...)`, `Deserialize<int?>(...)` etc. fail loudly with `InvalidOperationException` ("no serializer registered") instead of emitting an empty object or non-compiling generated code
- **Deep object nesting** — JSON/YAML/MsgPack round-trip objects nested 3+ levels; INI/TOML currently support a single nested level and throw `NotSupportedException` beyond that (loudly, instead of silently losing data).
- **INI nested object lists** — a `List<SomeDto>` property cannot be represented by INI's flat sections and is dropped by `IniSerializer` (reported as `PICOSERDE004` since this release, with the member name and format; see [Format limits](#format-limits-what-each-wire-format-cannot-express)); scalar lists (`List<int>`, `List<string>`, ...) round-trip.

### PicoJetson JsonOptions

```csharp
// Compact (default) — optimal for data transfer
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model);

// Human-readable
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { Indented = true });

// CamelCase naming
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

// Skip null properties
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

// Allow NaN/Infinity
byte[] data = JsonSerializer.SerializeToUtf8Bytes(model,
    new JsonOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
```

Available options:

| Option | Default | Description |
|--------|:-------:|-------------|
| `Indented` | `false` | Human-readable indented output |
| `MaxDepth` | `63` | Maximum nesting depth |
| `PropertyNamingPolicy` | `null` | Naming policy: `CamelCase`, `SnakeCaseLower`, `KebabCaseLower`, `PascalCase` |
| `DefaultIgnoreCondition` | `Never` | Skip null/default properties: `WhenWritingNull`, `WhenWritingDefault` |
| `NumberHandling` | `Strict` | Allow named floats: `AllowNamedFloatingPointLiterals` |
| `PropertyNameCaseInsensitive` | `true` | Property matching is case-insensitive by default; set `false` for exact case-sensitive matching |
| `AllowTrailingCommas` | `false` | Accept trailing commas in objects/arrays |
| `ReadCommentHandling` | `Disallow` | Skip `//` and `/* */` comments |
| `UnmappedMemberHandling` | `Skip` | Throw on unknown properties: `Disallow` |

#### Null handling across formats

Every format's options class (`JsonOptions`, `YamlOptions`, `TomlOptions`, `IniOptions`, `MsgPackOptions`) exposes `DefaultIgnoreCondition`, but what "writing a null" means depends on the wire format:

| Format | Default (`Never`) | `WhenWritingNull` |
|--------|-------------------|-------------------|
| JSON | `"key":null` written | omitted |
| MsgPack | `nil` written (map count adjusts automatically) | omitted |
| TOML / INI | omitted — these formats have no null value | omitted |
| YAML | omitted — YAML has no null value on write; writing `key:` would read back as a default value and break round-trip fidelity | omitted |

> All three section-based formats still *read* an explicit null literal
> (`Key =`, `Key:`, `~`, unquoted `null`) — see
> [Null Semantics](#null-semantics) for the full per-format matrix.

The matrix applies to every emit path — top-level members, nested objects, collection elements, nullable collections, and polymorphic dispatch — and is locked by cross-format regression tests (`IgnoreConditionMatrixTests`).

> **Honored options:** JSON and MsgPack read `DefaultIgnoreCondition` from their options objects. INI/TOML/YAML always omit nulls because those wire formats have no null *value* on the write side; their `DefaultIgnoreCondition` property is accepted by the public API but the generated code treats nulls as always omitted. `Indented = true` is honored: YAML additionally indents sequence items under their key, TOML indents table contents, and INI indents section contents (the compact default is unchanged).

Per-property control is available via the cross-format `[PicoIgnore]` attribute (PicoSerDe.Core):

```csharp
[PicoIgnore]                                                  // stripped everywhere (write + read)
public string Internal { get; set; } = "";

[PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)] // omitted only when null, regardless of global options
public string? Note { get; set; }

[PicoIgnore(Condition = PicoIgnoreCondition.Never)]           // exempt from the global DefaultIgnoreCondition
public string? Pinned { get; set; }
```

Conditions affect serialization only — deserialization still maps conditional properties. Format-specific markers (`[JsonIgnore]`, `[YamlIgnore]`, …) remain single-format unconditional ignores.

#### Custom serializers for nested types

`Register` applies at the top level only. To also override `T` wherever it appears as a **nested** value (object property, list element, dictionary value), use `RegisterCustom` — available on JSON and MessagePack:

```csharp
JsonSerializer.RegisterCustom(new MySerializer(), new MyDeserializer());
// Outer { Foo Inner } now serializes Inner with MySerializer too.
// Deserialization override applies at the top level only.
```

---

## Shared Attribute Hierarchy

Per-format attributes (`[JsonIgnore]`, `[IniKey]`, ...) inherit shared PicoSerDe.Core bases
(`PicoIgnoreAttribute`, `PicoSerializableAttribute`, `PicoCamelCaseAttribute`,
`PicoConstructorAttribute`, `PicoDateTimeFormatAttribute`, `PicoConverterAttribute`) —
one implementation per concept, format-specific public names preserved. `IniKeyAttribute.Key`
is the canonical property (`Name` is an obsolete alias).

## Packages

| Package | NuGet |
|---------|:-----:|
| `PicoSerDe.Core` | [![NuGet](https://img.shields.io/nuget/v/PicoSerDe.Core)](https://www.nuget.org/packages/PicoSerDe.Core) |
| `PicoJetson` | [![NuGet](https://img.shields.io/nuget/v/PicoJetson)](https://www.nuget.org/packages/PicoJetson) |
| `PicoMsgPack` | [![NuGet](https://img.shields.io/nuget/v/PicoMsgPack)](https://www.nuget.org/packages/PicoMsgPack) |
| `PicoIni` | [![NuGet](https://img.shields.io/nuget/v/PicoIni)](https://www.nuget.org/packages/PicoIni) |
| `PicoToml` | [![NuGet](https://img.shields.io/nuget/v/PicoToml)](https://www.nuget.org/packages/PicoToml) |
| `PicoYaml` | [![NuGet](https://img.shields.io/nuget/v/PicoYaml)](https://www.nuget.org/packages/PicoYaml) |

> Each runtime package embeds its generator (`analyzers/dotnet/cs`); the standalone `*.Gen` packages are legacy/optional — installing both would load a duplicate analyzer.

---

## CI/CD

| Target | Runner |
|--------|--------|
| win-x64 | windows-latest |
| win-arm64 | windows-latest |
| linux-x64 | ubuntu-latest |
| linux-arm64 | ubuntu-24.04-arm |
| osx-arm64 | macos-latest |

Every push: build + the full test suite + 5 benchmarks smoke + 5 AOT sample publishes.
Release: `v*` tag → packs 11 packages in dependency order → NuGet.org.
Local feed: run `./scripts/release.ps1 -Version <ver>` **before** pushing the
tag — it runs the test suite, packs all 11 packages into `artifacts/nupkg`
(declared as the `local` NuGet source in `NuGet.config`), then tags and pushes.
Sibling PicoHex repos add the same folder path after nuget.org in their
`NuGet.config` to consume the new version instantly, bypassing nuget.org's
indexing window and 30-minute HTTP cache.

AOT tiers - declare `<AotOptimizationLevel>` per project (`minimal` default /
`aggressive` for samples/benchmarks). Implementations: `minimal` in
`Directory.Build.props` (PublishAot + TrimMode=full), `aggressive` in
`Directory.Build.targets` (adds `StackTraceSupport=false` +
`UseSystemResourceKeys=true`; classic `Ilc*` MSBuild properties are removed in
.NET 10 SDK >= 10.0.400 and silently ignored) - the aggressive block lives in
targets so a csproj-level declaration is visible when it evaluates.
Libraries declare `IsAotCompatible`+`IsTrimmable`; only source generators
(`PicoXxx.Gen`, netstandard2.0) never AOT.

---

## Comparison

| | PicoSerDe | S.T.Json | YamlDotNet | VYaml | MsgPack-CS | Tommy |
|--|:---:|:---:|:---:|:---:|:---:|:---:|
| Formats | **5** | 1 | 1 | 1 | 1 | 1 |
| AOT | ✅ | ✅ | ❌ | ⚠️ | ❌ | ❌ |
| Zero-reflection | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Zero annotations | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| ref struct readers | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| SIMD | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| JSON DOM | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Polymorphic | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |

---

## License

MIT
