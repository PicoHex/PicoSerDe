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

**The numbers are back, they come from the fixed harness and the protocol below, and they say
PicoSerDe is slower than its competitors on micro-benchmarks.** On the AOT-vs-AOT JSON table it
is slower in 12 of 12 scenarios (1.07×–3.33×, median 1.94×); on the AOT-vs-AOT MessagePack
table, 4 of 4 (1.15×–3.53×). The old speedup table stays withdrawn rather than corrected in
place: it was measured with the defects below, so no figure in it can be trusted — not even the
ones that happened to point the right way. Everything quoted now was re-measured from scratch on
2026-10-27; see [Measured results](#measured-results-2026-10-27).

What was wrong — none of it a change to the serializers themselves. **Three of the four are
fixed** (the inverted call sites and the `PrintComparison` labels in `bench/fix-inversion`, and
the missing *protocol*, now written down in the two subsections below); **one is still open** —
the non-equivalent INI/TOML competitor arms, which is why those two formats get no ratio:

1. **Every `Benchmark.Compare` call passed its two actions in the wrong slots** (fixed here).
   PicoBench's signature is
   `Compare(name, baselineName, baselineAction, candidateName, candidateAction)`
   and `Speedup = Baseline.Avg / Candidate.Avg`. All 26 call sites passed PicoSerDe's own
   action as the *baseline* and the competitor's as the *candidate*, which swapped the column
   labels in every table and turned each ratio upside down: a row that read as a speedup was
   in truth the same magnitude of slowdown, with the two column headers printed backwards.
2. **Published numbers were measured without a protocol** — **fixed**: the protocol is the next
   subsection. It was a measurement-design defect, and *not* an AOT defect. Nothing pinned
   machine load, iteration count, run count or run-to-run dispersion, and AOT and JIT were not
   kept in separate columns, so the *same* binary could be reported with values orders of
   magnitude apart depending on what else the machine was doing. That spread used to be read
   here as an "AOT" absolute-cost blow-up; **that reading is retracted**, and the retraction is
   not replaced by its opposite: AOT and JIT are two separate processes and are reported in two
   separate tables precisely because they are not interchangeable. In the 2026-10-27 session the
   AOT table came out higher than the JIT table for *both* libraries — a fact about that session
   that is still unexplained ([Measured results](#measured-results-2026-10-27)), not a verdict
   on ahead-of-time compilation.
3. **The INI/TOML competitor arms still do non-equivalent work** — **still open**, a
   measurement-design defect: the "competitor" arm does a hand-written DOM walk plus typed
   mapping while ours is a single library call, so the two arms are not the same job. The
   third case in that family, YAML, was not a design question but a plain bug, and it is
   fixed: the "Deserialize" row was a second `Serialize` call, so the comparison was
   removed — PicoYaml now reports absolute cost only, with no competitor and no ratio.
4. **A `PrintComparison` helper labelled the candidate (the competitor) as `PicoJetson=`**
   (fixed here).

A runtime role guard (`shared/BenchRoleGuard.cs`, linked into every benchmark project) now
throws `InvalidOperationException` — naming both actual benchmark names — if a comparison's
candidate slot is not a `Pico*` action or its baseline slot is. Re-breaking the argument order
crashes the benchmark instead of silently printing a backwards table.

### Measurement protocol

Item 2 above, written down as a rule. A run that misses **any** of these is not published,
however good its numbers look:

- **Idle machine** — nothing else compiling, publishing or debugging, no antivirus first scan.
- **≥100k iterations per operation and ≥5 independent runs** — every run kept, none discarded;
  the product is asserted in code and the iteration count each arm really ran is printed in the
  table footer, so the floor is auditable rather than claimed.
- **Median + dispersion** (min/max, standard deviation) — never a bare average on its own.
- **AOT and JIT in separate columns** — never merged into one figure, and never into one ratio.
- **Equivalent competitor arms** — same input, same typed mapping, same output type; a
  hand-written DOM walk is not the same job as a library call.

The rules say what the benchmarks must do; the harness also says *how* it decides, because
"we think the machine was idle" is not checkable:

- **The idle self-check is a fixed workload, not a feeling.** Before every arm the harness times
  the same allocation-free loop twice, at least 200 ms per probe, and refuses the arm on any of
  three *relative* criteria — the ones that fire in practice, and the ones the banner prints:
  **drift** (the second probe's median moved more than **20%** away from the first's),
  **spread** (either probe's run-to-run max/min exceeds **3.0×**), or
  **dispersion** (either probe's relative standard deviation, stddev/median, exceeds **25%**).
  The same **3.0×** run-to-run limit applies once more to the arm's own five measured runs, once
  the idle check has passed: an action that had not settled (tiered JIT, GC) or a machine that
  got busy while it ran cannot be turned into a figure by averaging harder.
- **System-wide CPU is the decisive criterion.** Windows (`GetSystemTimes`) and Linux
  (`/proc/stat`) are asked how much of the machine processes *other than the benchmark* used
  (own CPU is subtracted). Above **35%** — override: `PICOBENCH_MAX_OTHER_CPU` — the arm is not
  published. It is decisive and not just one more probe because the three relative criteria
  above are blind to a machine that is *uniformly* loaded: every probe is slowed by the same
  factor, drift stays at zero, and the number is inflated anyway. When the host cannot answer
  at all (macOS, or a container with no readable `/proc/stat`) the answer is *unknown*, and
  unknown **refuses** the arm instead of passing it: a criterion that cannot be evaluated is
  not a criterion that was met. `PICOBENCH_ALLOW_UNKNOWN_CPU=1` is the only way to publish
  there, and it brands the banner and the table footer with a "load unknown — numbers not
  trustworthy" warning and sets `cpu=unknown` in the summary line.
- **A garbage collection runs before every measured run.** Otherwise the first run measures a
  small heap and the later ones pay for the garbage the earlier ones produced.
- **Warm-up runs to convergence, and a timeout is visible.** Each burst is the median of three
  timings, and an arm counts as warm only after three consecutive bursts agree within 5% (and
  after a minimum budget, deliberately generous under tiered JIT). If the budget runs out first
  the arm is marked in the table (`w⚠…`); `PICOBENCH_STRICT=1` refuses it outright.
- **Every table ends with a machine-readable summary line** (the counters are per process, which
  is per table in every benchmark here), for example
  `PICOBENCH_SUMMARY scenarios=6 published=0 refused=6 mode=Jit equivalent=false cpu=known exit=1`.
  Fields: `scenarios` = rows in this table; `published` / `refused` = arms that were / were not
  published (two per row in a comparison table); `mode` = the process-wide execution mode the
  banner prints; `equivalent` = `false` on a `NON-EQUIVALENT` table, where no ratio exists;
  `cpu` = `known` or `unknown`, whether system-wide load could be read at all; `exit` = the exit
  code the run will return. A run that publishes *nothing at all* is a broken run rather than a
  finding, so `published=0` with `refused>0` exits `1` — except on a host that cannot measure
  load at all (the criterion is unavailable rather than violated, so the environment failed,
  not the run) and unless `PICOBENCH_ALLOW_ZERO_PUBLISH=1`. A single refused arm still exits
  `0`. `PICOBENCH_STRICT=1` overrides every one of these: any refusal exits `1`.

None of this makes a number quotable. The protocol excludes the interference it can measure; it
cannot see thermal throttling, cgroup quotas, cache contention, or a busy neighbour that stays
below the CPU limit.

### Measured results (2026-10-27)

The first figures published since the withdrawal, measured end to end under the protocol
above — nothing here is a leftover from the broken harness. **They do not flatter the project:
on these micro-benchmarks PicoSerDe is slower than System.Text.Json (all 12 AOT JSON scenarios)
and slower than MessagePack-CSharp (all 4 AOT MessagePack scenarios).** The one mixed table is
MessagePack's JIT one, where PicoSerDe is 2–6% faster on four of its eight rows — inside the two
arms' shared dispersion, and not something to build a claim on.

- **AOT vs AOT, JSON** (PicoJetson vs System.Text.Json, 12 scenarios): **slower in 12 of 12**,
  by **1.07×–3.33× per scenario, median 1.94×**.
- **AOT vs AOT, MessagePack** (PicoMsgPack vs MessagePack-CSharp, 4 scenarios): **slower in 4 of
  4**, by **1.15×–3.53×, median 1.36×** — `Metrics — Deserialize` is 3.53× slower.
- **JIT vs JIT, JSON** (12 scenarios): slower in 11 of 12, **0.88×–5.33×, median 1.76×**; the one
  exception is `Nested POCO — Deserialize` at 0.88×.
- **JIT vs JIT, MessagePack** (8 scenarios): slower in 4 of 8 — the small shapes are marginally
  ahead (0.94×–0.98×, where the two arms are within each other's dispersion), the `Complex` and
  `Collection` shapes are **1.71×–2.16× behind**.
- **INI and TOML are not compared at all** — their competitor arms are non-equivalent, so they get
  no ratio, only their own cost; **YAML is measured against nothing but itself.**

In the ratio columns "×" is *PicoSerDe ÷ competitor*, so **above 1.00 means PicoSerDe is
slower**. The harness prints the reciprocal in its own `Verdict` column (the AOT JSON table's
`0.59x` sits where this table shows `1.68x`), which is the same measurement read the other way
round — confusing the two readings is what inverted the old table.

#### Environment, and what was imperfect about it

| Item | Detail |
|---|---|
| CPU | AMD Ryzen 7 6800H with Radeon Graphics — 8 physical / 16 logical cores (mobile APU) |
| OS | Windows 11 (`Microsoft Windows NT 10.0.26100.0`) |
| SDK / runtime | `dotnet --version` = `10.0.401`; `Environment.Version` = `10.0.12` in every banner |
| Measured on | **2026-10-27**, 11:06:3x–11:11:15 local; 7 processes, strictly serial, ≈4.8 min of measuring |
| Publication | **115 of 116 arms published, 1 refused**; all 7 processes exited `0` |
| The one refusal | PicoYaml `Simple — Deserialize`: `other processes used 37.0% of the machine's CPU while this action was measured (limit 35%)`. It carries **no number** here — that row is left empty rather than re-run until it produced a publishable figure. |
| Machine load | **The machine was not fully idle.** System-wide other-CPU sat between **6% and 32%** for the whole session (mostly this session's own host/tooling processes); the limit is 35% and the closest published arm reached 32%. Drift ≤12.7%, spread ≤2.12×, dispersion ≤15.0% — inside the protocol's 20% / 3.0× / 25% — so no arm was refused on those criteria. |
| Execution modes | AOT runs report `Mode: Aot \| IL on disk: no \| IsDynamicCodeSupported: no`. JIT runs report `Mode: Jit \| IL on disk: yes`, but **four of the five JIT benchmarks still carry the AOT feature switches** (`IsDynamicCodeSupported: no`, `JsonSerializer.IsReflectionEnabledByDefault: false`) because their csproj sets `PublishAot=true` while the harness executes IL; only PicoMsgPack's unified benchmark is plain JIT (`IsDynamicCodeSupported: yes`). AOT and JIT figures are never merged, and never divided by each other. |

Each table cites the run it came from, as `<mode> · 2026-10-27 · bench-measure/logs/<file>.txt`,
next to that run's own `PICOBENCH_SUMMARY` line, so a digit can be traced to the process that
printed it. Those log files are the harness's verbatim stdout for that session and are not
checked in (`.superpowers/` is gitignored); the same command on the same project regenerates the
same table, footers included.

These are micro-benchmarks on one machine: two-to-fifty-field POCOs and a 1 KB string, no I/O, no
contention, no cold start. They say nothing about allocation behaviour, streaming, large documents
or startup cost. **One anomaly to keep in view:** in this session the AOT table is slower than the
JIT table for *both* libraries (System.Text.Json: 0.125 → 0.163 µs on `Simple POCO — Serialize`,
0.390 → 0.654 on `Complex POCO — Serialize`) even though the AOT run had the cleaner machine
(other-CPU 7% vs 30%). The cause is not established here, so the AOT columns are reported as
measured and are not used to argue that PicoSerDe's AOT output is worse or better *relative* to
System.Text.Json's — both sides moved together.

#### AOT vs AOT — PicoJetson vs System.Text.Json (12 scenarios)

µs/op, median over 5 independent runs, parenthesised min–max ±stddev. `equivalent=true` on every
row, so every row carries a ratio.

| Scenario | PicoJetson (ours) | System.Text.Json | Pico ÷ STJ |
|---|---:|---:|---:|
| Simple POCO — Serialize | 0.274 (0.272–0.288 ±0.007) | 0.163 (0.158–0.173 ±0.005) | 1.68× |
| Simple POCO — Deserialize | 0.354 (0.330–0.356 ±0.011) | 0.280 (0.267–0.298 ±0.012) | 1.26× |
| Complex POCO — Serialize | 1.258 (1.210–1.262 ±0.022) | 0.654 (0.621–0.657 ±0.015) | 1.92× |
| Complex POCO — Deserialize | 1.643 (1.590–1.836 ±0.095) | 0.965 (0.878–0.975 ±0.042) | 1.70× |
| Nested POCO — Serialize | 1.016 (0.965–1.062 ±0.039) | 0.439 (0.417–0.468 ±0.019) | 2.31× |
| Nested POCO — Deserialize | 1.275 (1.210–1.278 ±0.030) | 1.193 (1.186–1.260 ±0.034) | 1.07× |
| Collection POCO — Serialize | 3.895 (3.603–3.899 ±0.127) | 1.749 (1.695–1.796 ±0.036) | 2.23× |
| Collection POCO — Deserialize | 6.955 (6.625–6.997 ±0.157) | 5.502 (5.481–5.764 ±0.121) | 1.26× |
| LargeFlat 50-fields — Serialize | 3.832 (3.696–3.991 ±0.112) | 1.586 (1.526–1.636 ±0.044) | 2.42× |
| LargeFlat 50-fields — Deserialize | 6.919 (6.748–7.014 ±0.130) | 3.550 (3.455–3.603 ±0.069) | 1.95× |
| LargeString 1KB — Serialize | 1.493 (1.351–1.623 ±0.106) | 0.449 (0.433–0.475 ±0.018) | 3.33× |
| LargeString 1KB — Deserialize | 1.630 (1.564–1.637 ±0.036) | 0.496 (0.469–0.508 ±0.015) | 3.29× |

> Source: **AOT · 2026-10-27 · `bench-measure/logs/PicoJetson-Aot.txt`** —
> `PICOBENCH_SUMMARY scenarios=12 published=24 refused=0 mode=Aot equivalent=true cpu=known exit=0`;
> per-arm other-CPU 3%–21%; 100,000–278,460 iterations per arm; warm-up 24/24 converged (0.2–0.5 s).

#### AOT vs AOT — PicoMsgPack vs MessagePack-CSharp (4 scenarios)

AOT changes the model set: only statically resolvable types work under MessagePack-CSharp's AOT
path, so this table's scenarios (`Person`, `Metrics`) are **not** the JIT table's below, and the
two must not be aligned by scenario name.

| Scenario | PicoMsgPack (ours) | MessagePack-CSharp | Pico ÷ MPC |
|---|---:|---:|---:|
| Person — Serialize | 0.116 (0.114–0.118 ±0.002) | 0.101 (0.098–0.103 ±0.002) | 1.15× |
| Person — Deserialize | 0.164 (0.159–0.196 ±0.017) | 0.138 (0.136–0.142 ±0.002) | 1.19× |
| Metrics — Serialize | 0.190 (0.189–0.192 ±0.001) | 0.124 (0.123–0.127 ±0.001) | 1.53× |
| Metrics — Deserialize | 0.519 (0.480–0.542 ±0.027) | 0.147 (0.135–0.161 ±0.011) | **3.53×** |

Wire size, same run (verbatim from the log): `Size: Person Pico=16B MPC=14B | Metrics Pico=53B MPC=26B`.

> Source: **AOT · 2026-10-27 · `bench-measure/logs/PicoMsgPack-Aot.txt`** —
> `PICOBENCH_SUMMARY scenarios=4 published=8 refused=0 mode=Aot equivalent=true cpu=known exit=0`;
> per-arm other-CPU 4%–22%; 100,000–300,000 iterations per arm; warm-up 8/8 converged (0.1–0.2 s).

#### JIT vs JIT — PicoJetson vs System.Text.Json (12 scenarios)

Same models as the AOT JSON table above, in a separate process with its own numbers.
`equivalent=true` on every row.

| Scenario | PicoJetson (ours) | System.Text.Json | Pico ÷ STJ |
|---|---:|---:|---:|
| Simple POCO — Serialize | 0.162 (0.159–0.203 ±0.019) | 0.125 (0.123–0.127 ±0.001) | 1.30× |
| Simple POCO — Deserialize | 0.243 (0.230–0.260 ±0.011) | 0.183 (0.175–0.191 ±0.006) | 1.33× |
| Complex POCO — Serialize | 0.684 (0.674–0.808 ±0.056) | 0.390 (0.386–0.396 ±0.004) | 1.75× |
| Complex POCO — Deserialize | 1.157 (1.123–1.174 ±0.020) | 0.614 (0.602–0.634 ±0.014) | 1.88× |
| Nested POCO — Serialize | 0.580 (0.551–0.598 ±0.019) | 0.330 (0.317–0.339 ±0.009) | 1.76× |
| Nested POCO — Deserialize | 0.877 (0.838–0.926 ±0.032) | 1.002 (0.934–1.073 ±0.057) | 0.88× |
| Collection POCO — Serialize | 2.349 (2.260–2.391 ±0.059) | 1.414 (1.307–1.470 ±0.062) | 1.66× |
| Collection POCO — Deserialize | 5.713 (5.591–6.067 ±0.179) | 3.551 (3.511–3.794 ±0.115) | 1.61× |
| LargeFlat 50-fields — Serialize | 2.173 (2.130–2.363 ±0.113) | 1.000 (0.980–1.035 ±0.020) | 2.17× |
| LargeFlat 50-fields — Deserialize | 6.574 (6.527–6.677 ±0.071) | 2.326 (2.234–2.627 ±0.165) | 2.83× |
| LargeString 1KB — Serialize | 1.018 (0.965–1.061 ±0.038) | 0.261 (0.260–0.274 ±0.007) | 3.90× |
| LargeString 1KB — Deserialize | 1.598 (1.507–1.653 ±0.058) | 0.300 (0.277–0.405 ±0.052) | 5.33× |

> Source: **JIT · 2026-10-27 · `bench-measure/logs/PicoJetson.txt`** —
> `PICOBENCH_SUMMARY scenarios=12 published=24 refused=0 mode=Jit equivalent=true cpu=known exit=0`;
> per-arm other-CPU 6%–24%; 100,000–114,720 iterations per arm; warm-up 24/24 converged (1.0–1.1 s).
> This is one of the four `Mode: Jit` runs that still carry the AOT feature switches (see the
> environment table); it is not a plain-JIT environment.

#### JIT vs JIT — PicoMsgPack vs MessagePack-CSharp (8 scenarios)

`equivalent=true` on every row. The scenarios differ from the AOT table above — the unified models
carry `DayOfWeek`, `Dictionary<string,string>` and `List<int>`, which MessagePack-CSharp cannot map
ahead of time.

| Scenario | PicoMsgPack (ours) | MessagePack-CSharp | Pico ÷ MPC |
|---|---:|---:|---:|
| Simple — Serialize | 0.065 (0.062–0.133 ±0.031) | 0.069 (0.068–0.072 ±0.002) | 0.94× |
| Simple — Deserialize | 0.097 (0.096–0.119 ±0.010) | 0.099 (0.095–0.128 ±0.013) | 0.98× |
| Complex — Serialize | 0.357 (0.331–0.372 ±0.017) | 0.209 (0.206–0.210 ±0.002) | 1.71× |
| Complex — Deserialize | 0.602 (0.561–0.625 ±0.031) | 0.320 (0.315–0.321 ±0.003) | 1.88× |
| Nested — Serialize | 0.205 (0.202–0.229 ±0.011) | 0.218 (0.214–0.230 ±0.006) | 0.94× |
| Nested — Deserialize | 0.352 (0.341–0.376 ±0.015) | 0.375 (0.366–0.381 ±0.006) | 0.94× |
| Collection — Serialize | 0.466 (0.450–0.504 ±0.024) | 0.222 (0.218–0.260 ±0.018) | 2.10× |
| Collection — Deserialize | 1.382 (1.361–1.435 ±0.030) | 0.639 (0.598–0.680 ±0.031) | 2.16× |

> Source: **JIT · 2026-10-27 · `bench-measure/logs/PicoMsgPack.txt`** —
> `PICOBENCH_SUMMARY scenarios=8 published=16 refused=0 mode=Jit equivalent=true cpu=known exit=0`;
> per-arm other-CPU 6%–23%; 100,000–126,200 iterations per arm; warm-up 16/16 converged (1.0 s).
> This is the only plain-JIT run of the seven (`IsDynamicCodeSupported: yes`).
>
> Its `Simple — Serialize` row is the least trustworthy figure published here: 0.065 with a max of
> 0.133 and a stddev of 0.031 (48% of the median), against a counterpart at 0.069 ±0.002. The
> 0.94× on those two rows should be read as "indistinguishable", not as "ahead" — the difference is
> smaller than the ours-side dispersion.

#### NON-EQUIVALENT — PicoIni, absolute cost only

**NON-EQUIVALENT — the competitor arm is a hand-written DOM walk, so no ratio is computed.** The
ini-parser arm parses a DOM and maps it with hand-written code while PicoIni's arm is a single
library call; the two are not the same job. PicoIni's own cost is what is published here, and the
competitor column is deliberately **not** reproduced: it exists in the log, but no reader should be
invited to divide one by the other. `equivalent=false` in this run's summary line.

| Scenario | PicoIni median µs/op (min–max ±stddev) |
|---|---:|
| Simple — Serialize (bytes) | 0.102 (0.101–0.160 ±0.026) |
| Simple — Deserialize (bytes) | 0.256 (0.165–0.277 ±0.044) |
| Medium — Serialize (bytes) | 0.242 (0.237–0.249 ±0.005) |
| Medium — Deserialize (bytes) | 0.376 (0.353–0.406 ±0.021) |
| Complex — Serialize (bytes) | 0.384 (0.373–0.387 ±0.005) |
| Large — Serialize (bytes) | 1.233 (1.176–1.253 ±0.030) |
| Large — Deserialize (bytes) | 2.426 (2.349–2.511 ±0.059) |
| Simple — SerializeToString | 0.113 (0.112–0.127 ±0.007) |
| Simple — Deserialize←string | 0.138 (0.134–0.154 ±0.008) |

> Source: **JIT · 2026-10-27 · `bench-measure/logs/PicoIni.txt`** —
> `PICOBENCH_SUMMARY scenarios=9 published=18 refused=0 mode=Jit equivalent=false cpu=known exit=0`;
> per-arm other-CPU 7%–23%; 100,000–117,630 iterations per arm; warm-up 18/18 converged (1.0–1.4 s).
>
> Two rows are the weakest in this table and are shown as measured: `Simple — Deserialize`'s min is
> 0.165 against a median of 0.256 (the other four runs sit at 0.25–0.277), and `Simple — Serialize`'s
> max is 0.160 against a median of 0.102. Both stayed inside the protocol's 3.0× spread limit, so
> they are published rather than discarded — and quoted with that caveat attached.

#### NON-EQUIVALENT — PicoToml, absolute cost only

**NON-EQUIVALENT — the competitor arm is a hand-written DOM walk, so no ratio is computed.** Tommy
is a DOM-only TOML parser (`TOML.Parse` / `TomlTable`) with no typed POCO mapping, so the two arms
do not perform the same work. PicoToml's own cost only; the competitor column is in the log and
deliberately not reproduced here. `equivalent=false` in the summary line.

| Scenario | PicoToml median µs/op (min–max ±stddev) |
|---|---:|
| Simple — Serialize (bytes) | 0.120 (0.115–0.144 ±0.012) |
| Simple — Deserialize (bytes) | 0.147 (0.144–0.152 ±0.003) |
| Medium — Serialize (bytes) | 0.300 (0.299–0.327 ±0.013) |
| Medium — Deserialize (bytes) | 0.367 (0.343–0.391 ±0.020) |
| Nested — Serialize (bytes) | 0.428 (0.414–0.439 ±0.010) |
| Nested — Deserialize (bytes) | 0.694 (0.659–0.723 ±0.026) |
| Large — Serialize (bytes) | 1.437 (1.367–1.563 ±0.075) |
| Large — Deserialize (bytes) | 2.871 (2.733–3.169 ±0.172) |
| Simple — SerializeToString | 0.134 (0.128–0.144 ±0.007) |
| Simple — Deserialize←string | 0.158 (0.153–0.163 ±0.004) |

> Source: **JIT · 2026-10-27 · `bench-measure/logs/PicoToml.txt`** —
> `PICOBENCH_SUMMARY scenarios=10 published=20 refused=0 mode=Jit equivalent=false cpu=known exit=0`;
> per-arm other-CPU 8%–25%; 100,000–121,275 iterations per arm; warm-up 20/20 converged (1.0–1.4 s).

#### PicoYaml — self-measured, no opponent, no ratio

There is no YAML comparison here at all: the format is benchmarked against itself, which is the only
honest thing to publish while the library that would be its competitor maps typed POCOs by
reflection and does not run under AOT. One row of six was refused and stays empty.

| Scenario | PicoYaml median µs/op (min–max ±stddev) | Status |
|---|---:|---|
| Simple — Serialize | 0.219 (0.183–0.248 ±0.028) | published |
| Simple — Deserialize | — | **refused**: other-CPU 37.0% > 35% limit |
| Nested — Serialize | 0.676 (0.634–0.693 ±0.022) | published (other-CPU 32%) |
| Nested — Deserialize | 1.071 (1.049–1.163 ±0.052) | published (other-CPU 29%) |
| Collection — Serialize | 1.788 (1.688–1.805 ±0.047) | published |
| Collection — Deserialize | 5.477 (5.093–5.526 ±0.185) | published |

> Source: **JIT · 2026-10-27 · `bench-measure/logs/PicoYaml.txt`** —
> `PICOBENCH_SUMMARY scenarios=6 published=5 refused=1 mode=Jit equivalent=true cpu=known exit=0`;
> per-arm other-CPU 12%–32% on the published arms; 100,000–106,095 iterations per arm; warm-up 5/5
> converged (1.0 s). The two `Nested` rows were measured at 32% and 29% other-CPU, i.e. 3–6 points
> below the refusal threshold, and are worth less than the `Collection` rows measured at 15% and
> 12%.

#### What these numbers do not say

They are not a claim about production throughput, latency under load, memory or cold start, and they
do not compare INI or TOML against anything. What they do say, however the table is sliced: **on
this machine and these micro-benchmarks, System.Text.Json and MessagePack-CSharp are faster than
PicoSerDe** — in AOT and in JIT, with JSON's `Nested POCO — Deserialize` (JIT) and MessagePack's
four small-shape rows (`Simple` and `Nested`, serialize and deserialize) as the only rows where the
two are indistinguishable. PicoSerDe's claim is that
it runs everywhere ahead of time and maps typed POCOs across five formats (see the next table), not
that it is the fastest thing in it.

### NativeAOT support, by competitor (verified, not a performance claim)

These are capability facts, not performance claims. The two columns are deliberately
separate, because "the library runs ahead of time" and "the library maps typed POCOs ahead of
time" are different questions and only the second one is what a serializer user cares about.

| Library | Runs under NativeAOT | Typed POCO round-trip under AOT |
|---------|:--------------------:|:-------------------------------:|
| System.Text.Json | ✅ with a source-generated `JsonSerializerContext` | ✅ |
| MessagePack-CSharp | ⚠️ only statically resolvable types — e.g. `string`, `int`, `bool`, `double`, `DateTime`; **no enum, no generic collection** | ⚠️ enums and generic collections throw `MissingMethodException` — it builds formatters with `MakeGenericType` + `Activator.CreateInstance` |
| ini-parser | ✅ DOM parsing works | ❌ reflection-based, no AOT mapper |
| Tommy | ✅ DOM parsing works | ❌ no typed mapper (only the DOM API) |
| YamlDotNet | ✅ DOM works | ❌ reflection-based typed POCO mapping fails |
| **PicoSerDe** | ✅ | ✅ all five formats |

**PicoSerDe is the only AOT solution above that round-trips typed POCOs across all five
formats** (JSON, TOML, YAML, INI, MessagePack). That is a capability statement, not a
throughput statement: it says what runs at all, not what runs fastest.

> The [`Comparison`](#comparison) table further down scores `AOT` as the second column above
> — typed serialization works ahead of time — which is why ini-parser, Tommy, YamlDotNet and
> MessagePack-CSharp score ❌/⚠️ there even though their parsers themselves do run under AOT.
>
> That table's VYaml `⚠️` is a documentation claim (`[YamlObject]` + source generation),
> **not** something this repo verified — no VYaml publish/run was attempted here.

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
