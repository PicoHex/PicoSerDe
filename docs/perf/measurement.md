# PicoSerDe — how every number is measured

Archive behind [`README.md` → Performance](../../README.md#performance). Everything the front
page does not need lives here, **moved rather than summarised**: the window ledger, the five rows
with the per-scenario commentary that belongs to each of them, the JIT-column companions, and the
protocol a run has to satisfy before its numbers may be published. No figure was edited on the way
in — the tables, medians and ranges below are the ones the README carried. The only edits are
structural: two `###` headings are promoted to `##`, and four cross-references that used to point
inside the README now point at the right file.

Contents: [The window behind the published figures](#the-window-behind-the-published-figures) ·
[The five rows, as published](#the-five-rows-as-published) ·
[What the JIT column is comparing](#what-the-jit-column-is-comparing) ·
[What a published number requires](#what-a-published-number-requires) ·
[The 2026-10-27 archive](#the-2026-10-27-archive)

---

## The window behind the published figures

Measured **2026-10-29** on one machine (AMD Ryzen 7 6800H, Windows 11, .NET SDK 10.0.401, runtime
10.0.12), in **one window: 17:49 to 18:24 local**. All five rows — JSON, MessagePack, TOML, YAML and
INI, in each execution mode where an opponent exists — were re-measured inside it, so no row on this
page is older than the row beside it and none was carried over from an earlier session. They were
re-measured because three performance commits had landed behind the previous figures: JSON now writes
a short ASCII string in one pass and a property name as one literal (`f46e672`, merge `0234838`), and
MessagePack now reads a flat int list in one call (`5a5d895`, merge `7324990`) and writes one in one
call (`fc4ee5c`, merge `a2b4224`) — see [What the last round of changes
did](changelog.md#what-the-last-round-of-changes-did). TOML, YAML and INI were re-measured rather than argued
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
did](changelog.md#what-the-last-round-of-changes-did) now quote this window's numbers for the same scenarios.

## The five rows, as published

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

## What the JIT column is comparing

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

## What a published number requires

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
survive NativeAOT (see the [capability table on the front page](../../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim)). Each table ends with its own `PICOBENCH_SUMMARY …
published= refused= mode= exit=` line: that line, not prose, is what says whether a run published
anything. The two JSON JIT companions are the same `PicoJetson` project run three times each, unchanged,
with one environment variable in front: `DOTNET_TieredCompilation=0 PICOBENCH_STRICT=1 dotnet run
--project PicoJetson/benchmarks/PicoJetson.Benchmarks -c Release --no-build` and the same line with
`DOTNET_TieredPGO=0` instead — separate processes, never a run merged with the published JIT row.

## The 2026-10-27 archive

Full per-scenario tables, per-run logs and the history behind the **2026-10-27** session:
[measurement-2026-10-27.md](measurement-2026-10-27.md). That archive is that
session's record and predates every figure above — it is kept as history, not as a second opinion on
today's numbers. The 2026-10-29 run behind this page is reproducible with the commands above; its raw
logs are session-local and are not committed.
