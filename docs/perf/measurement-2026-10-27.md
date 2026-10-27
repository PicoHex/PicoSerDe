# PicoSerDe — full measurement record (session of 2026-10-27)

Archive behind the summary in [`README.md` → Performance](../../README.md#performance). Everything
the README does not need in its front page lives here, **moved rather than summarised**: every
per-scenario table, every `PICOBENCH_SUMMARY` line with the log file it was printed by, the harness
defects that came before the session and how each one was resolved, the ±10% cross-session noise
argument, and the known boundaries. No figure was edited on the way in — the tables, medians,
min–max ranges and standard deviations are the ones the session printed. The only edits are
structural: heading levels are demoted one rank, two section headings were added, and two
cross-references that used to point inside the README now point at it.

The one part of the README's Performance section that is **not** copied here is the
[competitor/NativeAOT capability matrix](../../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim),
because that is a current capability fact rather than a measurement.

Contents: [Why this session replaced the earlier figures](#why-this-session-replaced-the-earlier-figures) ·
[What was wrong, and how each defect was resolved](#what-was-wrong-with-the-earlier-numbers-and-how-each-defect-was-resolved) ·
[Measurement protocol](#measurement-protocol) ·
[Measured results](#measured-results-2026-10-27) ·
[Known boundaries](#known-boundaries)

---

## Why this session replaced the earlier figures

**Everything in this section comes from the second measurement session run under the fixed
harness — a session that replaced every figure that was here before.** The verdict is unchanged
where it was bad and changed where it was unfair:

- **JSON and MessagePack — PicoSerDe is slower.** AOT-vs-AOT JSON: slower in **11 of 12**
  scenarios, **0.92×–3.58×** (median **1.95×** of System.Text.Json's cost). AOT-vs-AOT
  MessagePack: slower in **3 of 4**, **0.93×–3.34×** (median **1.40×**). JIT JSON: slower in
  **12 of 12**, **1.07×–5.60×** (median **1.91×**). JIT MessagePack: slower in **5 of 8**,
  **0.76×–2.26×** (median **1.44×**).
- **TOML — PicoSerDe is faster under JIT *and* under AOT (versus Tomlyn 2.10.1), and the opponent
  does the same job in both.** Tomlyn's typed, source-generated API replaces the DOM-walk stand-in
  as TOML's competitor: under **JIT** we land at **0.08×–0.41× of Tomlyn's cost (median 0.20×),
  faster in 10 of 10 scenarios** — between **2.4× and 12.5× faster** — and under **NativeAOT** at
  **0.10×–0.40× (median 0.21×), also faster in 10 of 10** — between **2.5× and 10× faster**. The
  old Tommy DOM walk is kept as a labelled reference table with no ratio, because it is still not
  the same job.
- **YAML — faster on CoreCLR, and only there.** Against YamlDotNet's typed API: **0.02×–0.06×
  (17×–50× faster), 6 of 6 scenarios**. **There is no AOT opponent**, and that absence is a fact
  about YamlDotNet, not a choice of ours — see
  [Why there is no AOT YAML opponent](#why-there-is-no-aot-yaml-opponent).
- **INI — no ratio at all, because no equivalent operation exists to divide by.** Absolute cost
  only, on a `NON-EQUIVALENT` table.

Everything quoted now was re-measured from scratch on 2026-10-27; see
[Measured results](#measured-results-2026-10-27). The old speedup table stays withdrawn rather than
corrected in place: it was measured with the defects below, so no figure in it can be trusted — not
even the ones that happened to point the right way.

## What was wrong with the earlier numbers, and how each defect was resolved

None of it was a change to the serializers themselves. **All four are fixed, one of them only
partly**, which is why INI still gets no ratio:

1. **Every `Benchmark.Compare` call passed its two actions in the wrong slots** (fixed). PicoBench's
   signature is `Compare(name, baselineName, baselineAction, candidateName, candidateAction)` and
   `Speedup = Baseline.Avg / Candidate.Avg`. Every call site passed PicoSerDe's own action as the
   *baseline* and the competitor's as the *candidate*, which swapped the column labels in every
   table and turned each ratio upside down: a row that read as a speedup was in truth the same
   magnitude of slowdown, with the two column headers printed backwards. The harness now prints one
   direction only — `ours ÷ theirs`, this document's own — so no printed figure ever needs a
   reciprocal taken before it is quoted.
2. **Published numbers were measured without a protocol** — **fixed**: the protocol is the next
   subsection. It was a measurement-design defect, and *not* an AOT defect. Nothing pinned machine
   load, iteration count, run count or run-to-run dispersion, and AOT and JIT were not kept in
   separate columns, so the *same* binary could be reported with values orders of magnitude apart
   depending on what else the machine was doing. That spread used to be read here as an "AOT"
   absolute-cost blow-up; **that reading is retracted**, and the retraction is not replaced by its
   opposite: AOT and JIT are two separate processes and are reported in separate tables precisely
   because they are not interchangeable.
3. **Some competitor arms did non-equivalent work** — **fixed for TOML and YAML, still open for
   INI** (and, by design, for the Tommy reference table). A "competitor" arm that does a
   hand-written DOM walk plus typed mapping while ours is a single library call is not the same job,
   and no ratio may be computed from it. TOML now carries a genuine equivalent opponent (`Tomlyn`,
   same POCO in and out, one library call per row) *and* keeps the non-equivalent Tommy walk as a
   clearly labelled reference with no ratio. YAML's comparable opponent is new in this session too.
   INI remains non-equivalent, because ini-parser has no typed API to compare against at all.
   YamlDotNet's earlier defect was in our own benchmark rather than in the design: the "Deserialize"
   row used to be a second `Serialize` call, and that comparison is gone.
4. **A `PrintComparison` helper labelled the candidate (the competitor) as `PicoJetson=`**
   (fixed).

A runtime role guard (`shared/BenchRoleGuard.cs`, linked into every benchmark project) now throws
`InvalidOperationException` — naming both actual benchmark names — if a comparison's candidate slot
is not a `Pico*` action or its baseline slot is. Re-breaking the argument order crashes the
benchmark instead of silently printing a backwards table.

## Measurement protocol

Item 2 above, written down as a rule. A run that misses **any** of these is not published, however
good its numbers look:

- **Idle machine** — nothing else compiling, publishing or debugging, no antivirus first scan.
- **≥100k iterations per operation and ≥5 independent runs** — every run kept, none discarded; the
  product is asserted in code and the iteration count each arm really ran is printed in the table
  footer, so the floor is auditable rather than claimed.
- **Median + dispersion** (min/max, standard deviation) — never a bare average on its own.
- **AOT and JIT in separate columns** — never merged into one figure, and never into one ratio.
- **Equivalent competitor arms** — same input, same typed mapping, same output type; a hand-written
  DOM walk is not the same job as a library call.

The rules say what the benchmarks must do; the harness also says *how* it decides, because "we
think the machine was idle" is not checkable:

- **The idle self-check is a fixed workload, not a feeling.** Before every arm the harness times
  the same allocation-free loop twice, at least 200 ms per probe, and refuses the arm on any of
  three *relative* criteria — the ones that fire in practice, and the ones the banner prints:
  **drift** (the second probe's median moved more than **20%** away from the first's), **spread**
  (either probe's run-to-run max/min exceeds **3.0×**), or **dispersion** (either probe's relative
  standard deviation, stddev/median, exceeds **25%**). The same **3.0×** run-to-run limit applies
  once more to the arm's own five measured runs, once the idle check has passed: an action that had
  not settled (tiered JIT, GC) or a machine that got busy while it ran cannot be turned into a
  figure by averaging harder.
- **System-wide CPU is the decisive criterion.** Windows (`GetSystemTimes`) and Linux
  (`/proc/stat`) are asked how much of the machine processes *other than the benchmark* used (own
  CPU is subtracted). Above **35%** — override: `PICOBENCH_MAX_OTHER_CPU` — the arm is not
  published. It is decisive and not just one more probe because the three relative criteria above
  are blind to a machine that is *uniformly* loaded: every probe is slowed by the same factor, drift
  stays at zero, and the number is inflated anyway. When the host cannot answer at all (macOS, or a
  container with no readable `/proc/stat`) the answer is *unknown*, and unknown **refuses** the arm
  instead of passing it: a criterion that cannot be evaluated is not a criterion that was met.
  `PICOBENCH_ALLOW_UNKNOWN_CPU=1` is the only way to publish there, and it brands the banner and
  the table footer with a "load unknown — numbers not trustworthy" warning and sets `cpu=unknown`
  in the summary line.
- **A garbage collection runs before every measured run.** Otherwise the first run measures a small
  heap and the later ones pay for the garbage the earlier ones produced.
- **Warm-up runs to convergence, and a timeout is visible.** Each burst is the median of three
  timings, and an arm counts as warm only after three consecutive bursts agree within 5% (and after
  a minimum budget, deliberately generous under tiered JIT). If the budget runs out first the arm
  is marked in the table (`w⚠…`); `PICOBENCH_STRICT=1` refuses it outright.
- **Every table ends with a machine-readable summary line, and the line names the scope of every
  counter.** Scope matters because one process can print more than one table — TOML now prints
  **two**, one against Tomlyn and one against Tommy — and conflating the scopes is how a run that
  "published nothing" comes to look like a run that "published something". The line:
  `PICOBENCH_SUMMARY scenarios=10 published=20 refused=0 mode=Jit equivalent=false cpu=known
  exit=0 exit_scope=process-so-far process_published=40 process_refused=0`.
  - `scenarios` — rows in **this** table.
  - `published` / `refused` — **this table's** arms that were / were not published. Arms, not rows:
    two per row in a comparison table, and a row the harness refuses for load counts both of its
    arms as refused.
  - `mode` — the process-wide execution mode the banner prints (`Jit` or `Aot`).
  - `equivalent` — `false` on a `NON-EQUIVALENT` table, where no ratio exists or is printed.
  - `cpu` — `known` or `unknown`: whether system-wide load could be read at all.
  - `exit` — the exit code the run will return. `exit_scope=process-so-far` says that value is a
    snapshot taken at the moment this line is printed, so on a two-table run **only the last line is
    the process's final word**; an earlier line reports what the process had done so far.
  - `process_published` / `process_refused` — the **whole process** at that same moment. This is why
    TOML's second line reads `published=20 … process_published=40`: 20 arms in this table, 40 in the
    process so far. Both numbers are correct and neither may be read as the other.

  A run that publishes *nothing at all* is a broken run rather than a finding, so `published=0`
  with `refused>0` exits `1` — except on a host that cannot measure load at all (the criterion is
  unavailable rather than violated, so the environment failed, not the run) and unless
  `PICOBENCH_ALLOW_ZERO_PUBLISH=1`. A single refused arm still exits `0`. `PICOBENCH_STRICT=1`
  overrides every one of these: any refusal exits `1`.

None of this makes a number quotable. The protocol excludes the interference it can measure; it
cannot see thermal throttling, cgroup quotas, cache contention, or a busy neighbour that stays
below the CPU limit.

## Measured results (2026-10-27)

This is the **second** session measured on that date, and it replaces the first one's figures
wholesale. Every relative criterion and the 35% other-CPU ceiling passed on each arm that carries a
number below; the machine was nevertheless **not** perfectly idle, which the environment table
states rather than hides. Nothing here is a leftover from the broken harness.

**Exactly one table here comes from a different window**, and it is labelled where it stands: TOML's
**AOT** table, measured in a separate later session on the same machine and date because this
session's own TOML AOT attempt was refused. Its JIT counterpart, all four JSON/MessagePack tables
and the YAML and INI tables are this session's. The two are never mixed, and AOT cells are never
compared with JIT cells.

**The numbers do not flatter the project in the places that matter most:** on these micro-benchmarks
PicoSerDe is slower than System.Text.Json on the AOT JSON table (11 of 12 scenarios) and slower than
MessagePack-CSharp on the AOT MessagePack table (3 of 4). It is faster than Tomlyn on the TOML table,
under JIT **and** under AOT (10 of 10 in each), and faster than YamlDotNet on the CoreCLR YAML table
(6 of 6) — two formats where the opponent's typed path costs a builder plus reflection (YamlDotNet)
or its own source generator (Tomlyn 2.10.1). Source generation is therefore not the explanation for
the TOML gap; the gap is measured, not attributed.

- **AOT vs AOT, JSON** (PicoJetson vs System.Text.Json, 12 scenarios): slower in **11 of 12**, by
  **0.92×–3.58×**, median **1.95×**. The single row on our side of 1.00 is
  `Nested POCO — Deserialize` at 0.92×.
- **AOT vs AOT, MessagePack** (PicoMsgPack vs MessagePack-CSharp, 4 scenarios): slower in **3 of
  4**, by **0.93×–3.34×**, median **1.40×** — `Metrics — Deserialize` is 3.34× slower, and
  `Person — Serialize` is 0.93×, inside the cross-session band discussed below.
- **JIT vs JIT, JSON** (12 scenarios): slower in **12 of 12**, **1.07×–5.60×**, median **1.91×**.
- **JIT vs JIT, MessagePack** (8 scenarios): slower in **5 of 8**, **0.76×–2.26×**, median
  **1.44×** — the small shapes are marginally ahead (0.76×–0.98×, of which only the 0.89× is outside
  the noise: one row is the least stable figure on this page and two sit inside the band), while the
  `Complex` and `Collection` shapes are **1.84×–2.26×** behind.
- **AOT vs AOT, TOML** (PicoToml vs Tomlyn, 10 scenarios): **faster in 10 of 10**, by
  **0.10×–0.40×**, median **0.21×** — the same pair as the JIT TOML table below, measured ahead of
  time in a separate session on the same date; see
  [AOT vs AOT — PicoToml vs Tomlyn](#aot-vs-aot--picotoml-vs-tomlyn-10-scenarios).
- **JIT vs JIT, TOML** (10 scenarios): **faster in 10 of 10**, **0.08×–0.41×**, median **0.20×**.
- **JIT vs JIT, YAML** (6 scenarios): **faster in 6 of 6**, **0.02×–0.06×**. CoreCLR only.
- **INI and the Tommy reference table are not compared at all** — their competitor arms are
  non-equivalent, so they get no ratio, only their own cost.

In every ratio column "×" is **ours ÷ theirs**, the direction the harness itself prints, so
**above 1.00 means PicoSerDe is slower** and **below 1.00 means PicoSerDe is faster**. There is no
reciprocal anywhere in this document to be taken. The harness's own `Verdict` column carries a `✓`
when a row is not a loss for us and a `✗` when it is; `·` marks a `NON-EQUIVALENT` table, which has
no verdict to give.

### Environment, and what was imperfect about it

| Item | Detail |
|---|---|
| CPU | AMD Ryzen 7 6800H with Radeon Graphics — 8 physical / 16 logical cores (mobile APU) |
| OS | Windows 11 (`Microsoft Windows NT 10.0.26100.0`) |
| SDK / runtime | `dotnet --version` = `10.0.401`; `Environment.Version` = `10.0.12` in all 8 banners |
| Measured on | **2026-10-27**, 13:46:2x–13:52:10 local; 7 published processes, strictly serial, ≈5.9 min wall clock including three AOT publishes |
| Publication | **142 of 142 arms published across those 7 processes (8 tables), 0 refused**; all 7 exited `0` with `exit=0` in the summary line |
| Machine load | **The machine was not fully idle.** Per-arm other-CPU ran **5%–29%** across the 142 published arms (quietest arm 5%, closest to the line 29%), against the protocol's 35% limit — no arm was refused on that criterion, and none was measured on a silent machine either. Self-checks: drift ≤9.0%, spread ≤1.64×, dispersion ≤10.0% — inside the protocol's 20% / 3.0× / 25%. |
| Execution modes | AOT runs report `Mode: Aot \| IL on disk: no \| IsDynamicCodeSupported: no`. JIT runs report `Mode: Jit \| IL on disk: yes`, but **four of the five JIT benchmarks still carry the AOT feature switches** (`IsDynamicCodeSupported: no`) because their csproj sets `PublishAot=true` while the harness executes IL; only PicoMsgPack's unified benchmark is plain JIT (`IsDynamicCodeSupported: yes`). AOT and JIT figures are never merged, and never divided by each other. |
| The run that is **not** counted above | One extra run was attempted after the seven — TOML under AOT — and it is **not** part of the 142 arms: **20 of its 40** were refused, **18 on system-wide CPU** (39.4%–80.6% against the 35% limit; the first reads `other processes used 60.4% of the machine's CPU during the check (limit 35%) — the machine is not idle`) and **2 on drift** (34.2% and 39.2%, against the 20% limit), after external load on the machine rose mid-window. Nothing from it is published as a table here; TOML's AOT parity needed a re-measure on an idle machine — done in the row below. The protocol refused those arms rather than reporting inflated ones, which is the entire point of it. |
| The TOML AOT **re-measure** — a separate session later the same date (`14:54–15:05` local) | **Three AOT attempts are involved here, and only the third one published, so the two refusals are named separately.** The first is the row above — `.superpowers/sdd/bench-measure-2/logs/PicoToml-Aot.txt`, **20 of its 40 arms** refused: 18 on system-wide CPU (39.4%–80.6% against the 35% limit, the first reading `other processes used 60.4% of the machine's CPU during the check (limit 35%) — the machine is not idle`) and 2 on drift (34.2%, 39.2%). The second was run in *this* re-measure directory — `.superpowers/sdd/bench-aot-toml/logs/PicoToml-Aot.txt` — and fared no better: **21 of its 40 arms** refused, **18 on system-wide CPU at 42.8%–95.2%** (the first reads `other processes used 42.8% of the machine's CPU during the check (limit 35%) — the machine is not idle`) and **3 on drift** (28.7%, 31.2%, 46.9%); that process published 19 of 20 equivalent arms and **0 of 20** Tommy arms. The third attempt is **this row's source** — `.superpowers/sdd/bench-aot-toml/logs/PicoToml-Aot-run2.txt` — and it published: TOML under AOT **20 of 20 arms in its equivalent table, 0 refused** (banner `Mode: Aot, IL on disk: no, IsDynamicCodeSupported: no`), load self-check drift 1.3% / spread 1.38× / dispersion 5.9% at other-CPU 10%, per-arm other-CPU **3%–26% across that table's 20 arms**. It is **not** part of the 142 arms above, and its figures are never mixed with them or with any JIT figure — it is its own session with its own log. Its second (Tommy, `NON-EQUIVALENT`) table published 19 of 20, the missing arm refused at `other processes used 48.4% of the machine's CPU while this action was measured (limit 35%) — every run is slowed unknown factor, so the figure is not trustworthy`, which is why no timing from that second table is quoted anywhere. |

Each table cites the run it came from, as
`<mode> · 2026-10-27 · .superpowers/sdd/bench-measure-2/logs/<file>.txt`, next to that run's own
`PICOBENCH_SUMMARY` line, so a digit can be traced to the process that printed it. Those log files
are the harness's verbatim stdout for that session and are **not checked in** — `.gitignore`
excludes `.superpowers/`, so a reader cannot open them; what a reader *can* check is the quoted
`PICOBENCH_SUMMARY` line, the per-arm other-CPU range and the footer printed beside each table,
which is why they are copied verbatim rather than summarised. Re-running is the only way to
reproduce the raw logs, and that is a different session on a differently loaded machine: the same
command on the same project reproduces **the same rows in the same footer format, but not the same
numbers** — medians, min–max and other-CPU percentages move run to run, so every figure below is
this session's, not a constant.

These are micro-benchmarks on one machine: two-to-fifty-field POCOs and a 1 KB string, no I/O, no
contention, no cold start. They say nothing about allocation behaviour, streaming, large documents
or startup cost. **One anomaly to keep in view:** in this session the AOT tables come out slower
than the JIT tables for *both* libraries — System.Text.Json's `Simple POCO — Serialize` is 0.138 µs
under JIT and 0.168 µs under AOT, its `Complex POCO — Serialize` 0.338 → 0.596 µs — even though the
AOT runs had the cleaner machine (self-check other-CPU 9% and 14%, against 17% and 28%). The cause
is not established here, so the AOT columns are reported as measured and are not used to argue that
PicoSerDe's AOT output is worse or better *relative* to System.Text.Json's — both sides moved
together.

### One session's stddev is not a cross-session error bar

This is the most important methodological finding of the second session, and it governs how every
ratio below should be read.

The ±stddev printed in each cell comes from **five runs inside one process** — the same JIT/tiering
state, the same cache and frequency state, the same neighbours. It captures sampling noise and
little else. Between two sessions there are variables those five runs cannot see: tiered-JIT/PGO
landing points, code and data alignment, thermal and frequency history, background processes. **The
honest cross-session noise band is therefore about ±10%, not "a few σ".** Every "inside the noise"
judgement in this document is made at ±10%, never at the multiple-of-stddev level.

The cleanest demonstration is an AOT arm with a *tiny* within-session spread:

| Arm | Within one session (5 runs) | Between the two sessions | Conclusion |
|---|---|---|---|
| MsgPack AOT `Person — Serialize` | **±0.001 on 0.096 µs ≈ ±1%** | the median moved **17%** | "17 σ" is the wrong ruler; ±1% is not the arm's uncertainty |
| MsgPack AOT `Metrics — Deserialize` | ±0.013 on 0.478 µs ≈ ±2.7% | the median moved **7.9%** | inside the ±10% band — no conclusion |

Both statements are true at once because they answer different questions: the first bounds the
*measurement*, the second bounds the *machine state*. Publishing only the first — which a bare
`±0.001` invites a reader to do — would overstate the precision by an order of magnitude.

Three consequences are applied below:

- **A ratio within ~10% of 1.00 is not a direction claim.** Four rows changed sides of 1.00 between
  the two sessions, all of them between 0.88× and 1.15×: they are reported as measured, marked
  indistinguishable, and used to support nothing.
- **A row can be far jitterier than its neighbours.** MessagePack's JIT `Simple — Serialize` is the
  least stable figure on this page in *both* sessions (this time 0.058 with a max of 0.101 and a
  stddev of 0.019 — 33% of the median — against a counterpart at 0.077 ±0.000). Its 0.76× is
  reported because the protocol published it, and it is not cited as a win: the two ranges overlap
  and what the ratio is made of is our own dispersion.
- **The band cuts both ways.** A 7-point lead (0.93×) and an 8-point lead (0.92×) are as
  unquotable as an 11-point deficit, and are labelled that way even though they favour us.

### AOT vs AOT — PicoJetson vs System.Text.Json (12 scenarios)

µs/op, median over 5 independent runs, parenthesised min–max ±stddev. `equivalent=true` on every
row, so every row carries a ratio. **Ratio = ours ÷ theirs: above 1.00 we are slower.**

| Scenario | PicoJetson (ours) | System.Text.Json | Pico ÷ STJ |
|---|---:|---:|---:|
| Simple POCO — Serialize | 0.282 (0.261–0.283 ±0.010) | 0.168 (0.158–0.174 ±0.006) | 1.68× |
| Simple POCO — Deserialize | 0.320 (0.306–0.338 ±0.013) | 0.277 (0.265–0.303 ±0.015) | 1.16× |
| Complex POCO — Serialize | 1.248 (1.131–1.257 ±0.053) | 0.596 (0.567–0.656 ±0.035) | 2.10× |
| Complex POCO — Deserialize | 1.794 (1.506–1.975 ±0.171) | 0.934 (0.897–1.035 ±0.056) | 1.92× |
| Nested POCO — Serialize | 0.958 (0.918–0.974 ±0.023) | 0.489 (0.471–0.518 ±0.019) | 1.96× |
| Nested POCO — Deserialize | 1.211 (1.190–1.255 ±0.025) | 1.313 (1.282–1.322 ±0.018) | 0.92× |
| Collection POCO — Serialize | 3.840 (3.717–3.883 ±0.066) | 1.858 (1.763–1.883 ±0.055) | 2.07× |
| Collection POCO — Deserialize | 6.985 (6.971–7.060 ±0.036) | 5.531 (5.503–5.737 ±0.104) | 1.26× |
| LargeFlat 50-fields — Serialize | 3.800 (3.759–3.969 ±0.090) | 1.568 (1.493–1.590 ±0.041) | 2.42× |
| LargeFlat 50-fields — Deserialize | 6.547 (6.441–6.598 ±0.068) | 3.385 (3.364–3.412 ±0.018) | 1.93× |
| LargeString 1KB — Serialize | 1.543 (1.511–1.586 ±0.028) | 0.431 (0.409–0.524 ±0.045) | 3.58× |
| LargeString 1KB — Deserialize | 1.582 (1.528–1.811 ±0.117) | 0.533 (0.513–0.600 ±0.034) | 2.97× |

> Source: **AOT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoJetson-Aot.txt`** —
> `PICOBENCH_SUMMARY scenarios=12 published=24 refused=0 mode=Aot equivalent=true cpu=known
> exit=0 exit_scope=process-so-far process_published=24 process_refused=0`;
> per-arm other-CPU 5%–22%; 100,000–260,715 iterations per arm; warm-up 24/24 converged
> (0.1–0.4 s, 8–37 bursts).
>
> `Nested POCO — Deserialize` (0.92×) is the only row on our side of 1.00, and its two ranges do not
> overlap — but at 8% it sits inside the ±10% cross-session band, so it is reported and not claimed.

### AOT vs AOT — PicoMsgPack vs MessagePack-CSharp (4 scenarios)

AOT changes the model set: only statically resolvable types work under MessagePack-CSharp's AOT
path, so this table's scenarios (`Person`, `Metrics`) are **not** the JIT table's below, and the two
must not be aligned by scenario name. **Ratio = ours ÷ theirs: above 1.00 we are slower.**

| Scenario | PicoMsgPack (ours) | MessagePack-CSharp | Pico ÷ MPC |
|---|---:|---:|---:|
| Person — Serialize | 0.096 (0.096–0.098 ±0.001) | 0.104 (0.101–0.107 ±0.002) | 0.93× |
| Person — Deserialize | 0.162 (0.162–0.164 ±0.001) | 0.131 (0.128–0.143 ±0.007) | 1.24× |
| Metrics — Serialize | 0.187 (0.185–0.193 ±0.003) | 0.120 (0.119–0.123 ±0.001) | 1.55× |
| Metrics — Deserialize | 0.478 (0.467–0.500 ±0.013) | 0.143 (0.142–0.144 ±0.001) | **3.34×** |

Wire size, same run (verbatim from the log): `Size: Person Pico=16B MPC=14B | Metrics Pico=53B
MPC=26B`.

> Source: **AOT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoMsgPack-Aot.txt`** —
> `PICOBENCH_SUMMARY scenarios=4 published=8 refused=0 mode=Aot equivalent=true cpu=known exit=0
> exit_scope=process-so-far process_published=8 process_refused=0`;
> per-arm other-CPU 5%–25%; 100,000–300,000 iterations per arm; warm-up 8/8 converged
> (0.1–0.2 s, 19–96 bursts).
>
> `Person — Serialize` (0.93×) is this table's one row on our side of 1.00, and it is also the arm
> whose within-session spread is ±1% while its median moved 17% between sessions — the example in
> [the uncertainty note](#one-sessions-stddev-is-not-a-cross-session-error-bar). Seven points from
> 1.00 is inside the band; it is not claimed as a win.

### JIT vs JIT — PicoJetson vs System.Text.Json (12 scenarios)

Same models as the AOT JSON table above, in a separate process with its own numbers.
`equivalent=true` on every row. **Ratio = ours ÷ theirs: above 1.00 we are slower.**

| Scenario | PicoJetson (ours) | System.Text.Json | Pico ÷ STJ |
|---|---:|---:|---:|
| Simple POCO — Serialize | 0.153 (0.152–0.267 ±0.051) | 0.138 (0.129–0.140 ±0.004) | 1.11× |
| Simple POCO — Deserialize | 0.246 (0.242–0.252 ±0.004) | 0.221 (0.219–0.225 ±0.003) | 1.11× |
| Complex POCO — Serialize | 0.702 (0.682–0.715 ±0.013) | 0.338 (0.331–0.344 ±0.005) | 2.08× |
| Complex POCO — Deserialize | 1.178 (1.167–1.244 ±0.031) | 0.594 (0.565–0.602 ±0.016) | 1.98× |
| Nested POCO — Serialize | 0.574 (0.547–0.583 ±0.015) | 0.314 (0.298–0.349 ±0.019) | 1.83× |
| Nested POCO — Deserialize | 0.878 (0.834–0.962 ±0.052) | 0.819 (0.794–0.854 ±0.028) | 1.07× |
| Collection POCO — Serialize | 2.242 (2.177–2.464 ±0.111) | 1.414 (1.385–1.455 ±0.028) | 1.59× |
| Collection POCO — Deserialize | 5.351 (5.164–5.620 ±0.190) | 3.351 (3.338–3.545 ±0.096) | 1.60× |
| LargeFlat 50-fields — Serialize | 2.074 (1.950–2.098 ±0.066) | 0.959 (0.935–1.011 ±0.033) | 2.16× |
| LargeFlat 50-fields — Deserialize | 6.298 (6.228–6.828 ±0.249) | 2.023 (1.950–2.103 ±0.055) | 3.11× |
| LargeString 1KB — Serialize | 1.081 (1.063–1.132 ±0.026) | 0.270 (0.259–0.285 ±0.010) | 4.01× |
| LargeString 1KB — Deserialize | 1.522 (1.483–1.610 ±0.057) | 0.272 (0.244–0.368 ±0.055) | 5.60× |

> Source: **JIT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoJetson.txt`** —
> `PICOBENCH_SUMMARY scenarios=12 published=24 refused=0 mode=Jit equivalent=true cpu=known
> exit=0 exit_scope=process-so-far process_published=24 process_refused=0`;
> per-arm other-CPU 7%–29%; 100,000–112,770 iterations per arm; warm-up 24/24 converged
> (1.0 s, 46–930 bursts). This is one of the four `Mode: Jit` runs that still carry the AOT feature
> switches (see the environment table); it is not a plain-JIT environment.
>
> `Simple POCO — Serialize` is the weakest row: 0.153 with a min–max of 0.152–0.267 and a stddev of
> 0.051 (33% of the median), against a counterpart at 0.138 ±0.004. But the two ranges do **not**
> overlap and 1.11× is 11 points from 1.00, so it is a real — if small — deficit, not noise.

### JIT vs JIT — PicoMsgPack vs MessagePack-CSharp (8 scenarios)

`equivalent=true` on every row. The scenarios differ from the AOT table above — the unified models
carry `DayOfWeek`, `Dictionary<string,string>` and `List<int>`, which MessagePack-CSharp cannot map
ahead of time. **Ratio = ours ÷ theirs: above 1.00 we are slower.**

| Scenario | PicoMsgPack (ours) | MessagePack-CSharp | Pico ÷ MPC |
|---|---:|---:|---:|
| Simple — Serialize | 0.058 (0.057–0.101 ±0.019) | 0.077 (0.076–0.077 ±0.000) | 0.76× |
| Simple — Deserialize | 0.104 (0.101–0.107 ±0.002) | 0.101 (0.096–0.105 ±0.003) | 1.03× |
| Complex — Serialize | 0.333 (0.321–0.374 ±0.021) | 0.181 (0.177–0.224 ±0.020) | 1.84× |
| Complex — Deserialize | 0.617 (0.587–0.630 ±0.019) | 0.301 (0.290–0.343 ±0.024) | 2.05× |
| Nested — Serialize | 0.217 (0.215–0.219 ±0.002) | 0.244 (0.243–0.246 ±0.001) | 0.89× |
| Nested — Deserialize | 0.390 (0.384–0.395 ±0.004) | 0.399 (0.391–0.404 ±0.006) | 0.98× |
| Collection — Serialize | 0.474 (0.458–0.484 ±0.011) | 0.210 (0.205–0.225 ±0.009) | 2.26× |
| Collection — Deserialize | 1.434 (1.380–1.456 ±0.029) | 0.662 (0.617–0.686 ±0.028) | 2.17× |

> Source: **JIT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoMsgPack.txt`** —
> `PICOBENCH_SUMMARY scenarios=8 published=16 refused=0 mode=Jit equivalent=true cpu=known
> exit=0 exit_scope=process-so-far process_published=16 process_refused=0`;
> per-arm other-CPU 8%–21%; 100,000–121,000 iterations per arm; warm-up 16/16 converged
> (1.0 s, 208–2437 bursts). This is the only plain-JIT run of the seven
> (`IsDynamicCodeSupported: yes`).
>
> Its `Simple — Serialize` row is the least trustworthy figure published here: 0.058 with a max of
> 0.101 and a stddev of 0.019 (33% of the median), against a counterpart at 0.077 ±0.000. The 0.76×
> should be read as "indistinguishable", not as "ahead": the two ranges overlap, and what the ratio
> is made of is our own dispersion. `Nested — Serialize` (0.89×) is the only row of the two
> MessagePack tables that is ahead *without* overlapping ranges, and at 11 points it is just outside
> the band; `Nested — Deserialize` (0.98×, ranges overlapping) is inside it.

### AOT vs AOT — PicoToml vs Tomlyn (10 scenarios)

**The same equivalent pair as the JIT table below, with both sides published ahead of time.**
Tomlyn 2.10.1 round-trips typed POCOs under NativeAOT, so the comparison survives the mode change
and a ratio still exists: one library call per row, same POCO in and out. **Ratio = ours ÷ theirs:
above 1.00 we are slower, below 1.00 we are faster — every row here is below 1.00, and the run's
verdict column reads ✓ ten times.**

| Scenario | PicoToml (ours) | Tomlyn | Pico ÷ Tomlyn |
|---|---:|---:|---:|
| Simple — Serialize (bytes) | 0.182 (0.181–0.185 ±0.002) | 0.757 (0.728–0.792 ±0.025) | 0.24× |
| Simple — Deserialize (bytes) | 0.240 (0.237–0.245 ±0.003) | 2.291 (2.195–2.312 ±0.051) | 0.10× |
| Medium — Serialize (bytes) | 0.510 (0.483–0.529 ±0.017) | 1.495 (1.475–1.578 ±0.041) | 0.34× |
| Medium — Deserialize (bytes) | 0.586 (0.551–0.610 ±0.024) | 5.185 (4.981–5.495 ±0.220) | 0.11× |
| Nested — Serialize (bytes) | 0.712 (0.690–0.754 ±0.025) | 1.825 (1.782–1.854 ±0.028) | 0.39× |
| Nested — Deserialize (bytes) | 0.796 (0.771–0.827 ±0.026) | 6.378 (6.228–6.647 ±0.156) | 0.12× |
| Large — Serialize (bytes) | 2.617 (2.599–2.717 ±0.052) | 6.579 (6.452–6.725 ±0.117) | 0.40× |
| Large — Deserialize (bytes) | 3.478 (3.402–3.607 ±0.083) | 19.773 (19.720–19.941 ±0.112) | 0.18× |
| Simple — SerializeToString | 0.187 (0.181–0.194 ±0.006) | 0.520 (0.504–0.554 ±0.021) | 0.36× |
| Simple — Deserialize←string | 0.225 (0.217–0.231 ±0.006) | 2.227 (2.159–2.245 ±0.034) | 0.10× |

> Source: **AOT · 2026-10-27 · `.superpowers/sdd/bench-aot-toml/logs/PicoToml-Aot-run2.txt`** (table 1
> of 2 in that process) — `PICOBENCH_SUMMARY scenarios=10 published=20 refused=0 mode=Aot
> equivalent=true cpu=known exit=0 exit_scope=process-so-far process_published=20
> process_refused=0`; banner `Mode: Aot | IL on disk: no | IsDynamicCodeSupported: no`; per-arm
> other-CPU 3%–26% across table 1's arms; load self-check drift 1.3%, spread 1.38×, dispersion 5.9% at other-CPU 10%;
> 100,000–230,520 iterations per arm; warm-up 20/20 converged (0.1–0.2 s, 4–27 bursts). This is a
> **separate session** from every other table on this page: same machine, same date, later window —
> run precisely because the earlier window's TOML AOT attempt was refused. Its second table is the
> `NON-EQUIVALENT` Tommy reference (19 of 20 arms published, no ratio).

Four things this table says, all of them about ratio or scope rather than about absolutes:

- **AOT does not reverse the TOML result.** 10 of 10 rows stay below 1.00, at 0.10×–0.40×, median
  **0.21×** against the JIT table's 0.20× — between **2.5× and 10× faster**.
- **AOT costs more than JIT for *both* libraries, which is why the two TOML tables are never
  subtracted from each other.** `Large — Serialize (bytes)` is 1.351 µs for us under JIT and
  2.617 µs here, while Tomlyn's moves 4.185 → 6.579 µs; the same AOT-above-JIT direction is recorded
  for the JSON tables above, and it is the anomaly this section already flags rather than a new one.
  What survives the mode change is the **ratio**: 0.32× under JIT, 0.40× under AOT.
- **The margin narrows slightly under AOT, and it is reported narrowing.** Two rows move up
  materially (`Medium — Serialize` 0.26× → 0.34×, `Large — Serialize` 0.32× → 0.40×), `Nested —
  Serialize` gains 3 points, `Simple — SerializeToString` is the only row that moves down (0.41× →
  0.36×, −5 points), and the other five move by two points or less. Median 0.20× → 0.21×. Nothing
  crosses 1.00, and at this resolution a move of two points is the cross-session band rather than a
  trend.
- **The JIT table's caveats apply here unchanged:** `[equiv] nested text identical = False` (key
  order inside `[Address]`), our deserialize rows pay a UTF-8 decode inside the timed region that
  Tomlyn's `Deserialize<T>(string)` does not, and Tomlyn's serialize arm is not handicapped.

**A same-day JIT re-measure is what the AOT ratios are checked against.** `dotnet run -c Release`
was this session's first process, and its Tomlyn table reproduced the published JIT figures —
**0.07×–0.42×, median 0.20×** against **0.08×–0.41×, median 0.20×**; nine of the ten ratios land
inside the ±10% cross-session band, and the tenth (`Simple — Deserialize←string`, 0.08× → 0.07×,
−12.5%) is the row whose Tomlyn arm carried the widest spread in that run (±0.621 on 2.071 µs,
other-CPU 32%) — the noisiest cell of the ten, not a new finding. No row crossed 1.00 in either
session. Source: `JIT · 2026-10-27 · .superpowers/sdd/bench-aot-toml/logs/PicoToml-Jit.txt`,
`mode=Jit published=20 refused=0`. Background load rose during that process and cost its *Tommy*
table 13 of its 20 arms; its Tomlyn table published all 20, and no Tommy figure is quoted from it.

### JIT vs JIT — PicoToml vs Tomlyn (10 scenarios)

**TOML is one of the two formats where PicoSerDe comes out ahead, and here the comparison is a fair
one.** Tomlyn 2.10.1 has a typed API with its own source generator: one library call per row, the
same POCO in and out, and it round-trips under NativeAOT. That makes it the same job as ours, so a
ratio exists.
**Ratio = ours ÷ theirs: above 1.00 we are slower, below 1.00 we are faster — every row here is
below 1.00.**

| Scenario | PicoToml (ours) | Tomlyn | Pico ÷ Tomlyn |
|---|---:|---:|---:|
| Simple — Serialize (bytes) | 0.121 (0.114–0.158 ±0.020) | 0.562 (0.510–0.633 ±0.045) | 0.22× |
| Simple — Deserialize (bytes) | 0.141 (0.136–0.152 ±0.007) | 1.688 (1.623–1.785 ±0.062) | 0.08× |
| Medium — Serialize (bytes) | 0.252 (0.247–0.277 ±0.012) | 0.979 (0.911–1.008 ±0.040) | 0.26× |
| Medium — Deserialize (bytes) | 0.368 (0.351–0.391 ±0.015) | 3.643 (3.577–3.739 ±0.061) | 0.10× |
| Nested — Serialize (bytes) | 0.371 (0.350–0.385 ±0.014) | 1.040 (0.992–1.102 ±0.041) | 0.36× |
| Nested — Deserialize (bytes) | 0.592 (0.572–0.610 ±0.014) | 5.094 (5.024–5.109 ±0.034) | 0.12× |
| Large — Serialize (bytes) | 1.351 (1.239–1.385 ±0.070) | 4.185 (4.006–4.621 ±0.249) | 0.32× |
| Large — Deserialize (bytes) | 2.639 (2.519–2.920 ±0.155) | 14.954 (14.822–15.917 ±0.520) | 0.18× |
| Simple — SerializeToString | 0.130 (0.120–0.141 ±0.009) | 0.317 (0.304–0.320 ±0.008) | 0.41× |
| Simple — Deserialize←string | 0.134 (0.130–0.155 ±0.010) | 1.716 (1.645–1.791 ±0.057) | 0.08× |

> Source: **JIT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoToml.txt`** (table 1 of 2
> in that process) — `PICOBENCH_SUMMARY scenarios=10 published=20 refused=0 mode=Jit
> equivalent=true cpu=known exit=0 exit_scope=process-so-far process_published=20
> process_refused=0`; per-arm other-CPU 7%–20%; 100,000–120,675 iterations per arm; warm-up 20/20
> converged (1.0–1.1 s, 22–1223 bursts). The same process then prints the Tommy table below, which
> is why that log contains a second summary line ending `process_published=40`.

Three properties of this table are stated rather than left to be discovered:

- **The two arms produce the same document, with one exception.** The harness's own evidence lines
  read `[equiv] simple text identical = True`, `[equiv] medium … True`, `[equiv] large … True`, and
  `[equiv] tomlyn round-trip SimplePoco = Hello World/42 (equal to the POCO = True)`. The exception
  is `[equiv] nested text identical = False`: the same data, but a different **key order** inside
  `[Address]` — ours emits `City` then `Street`, Tomlyn emits `Street` then `City`. The two `Nested`
  rows therefore carry a known shape difference that cannot be quantified; it is disclosed, and it
  is not what explains the 0.36× and 0.12× those rows measure.
- **The deserialize rows understate us, if anything.** Our `Deserialize<T>(byte[])` is handed UTF-8
  bytes and pays the UTF-8 decode *inside* the timed region; Tomlyn's `Deserialize<T>(string)` is
  handed a string built outside it. Both do one library call on the same document, but our
  deserialize columns carry one extra decode that theirs does not.
- **Tomlyn's serialize arm is not handicapped.** Its `Serialize (bytes)` arm uses the Stream overload
  into one reused `MemoryStream`, and the `[equiv]` lines above are the evidence that those bytes are
  identical to ours. The ~10 lines of `TomlynBenchContext` are a compile-time configuration cost,
  not a per-operation one, so they are not timed.

### NON-EQUIVALENT — PicoToml vs Tommy, reference only

**NON-EQUIVALENT — the competitor arm is a hand-written DOM walk, so no ratio is computed or
printed.** Tommy 3.1.2 is DOM-only (`TOML.Parse(TextReader) → TomlTable`) with no typed POCO mapping
at all; the `TomlTable` walk and the typed conversion in the benchmark's helpers are caller-written.
Kept for continuity with the earlier sessions, side by side with the equivalent Tomlyn table above,
and deliberately without a ratio. `equivalent=false` in this run's summary line. **The two columns
below are not the same job and may not be divided by each other.**

| Scenario | PicoToml (ours) | Tommy (reference) | Ratio |
|---|---:|---:|---:|
| Simple — Serialize (bytes) | 0.103 (0.099–0.121 ±0.008) | 0.598 (0.543–0.615 ±0.029) | — |
| Simple — Deserialize (bytes) | 0.161 (0.146–0.176 ±0.012) | 0.835 (0.744–0.849 ±0.044) | — |
| Medium — Serialize (bytes) | 0.277 (0.271–0.287 ±0.007) | 1.320 (1.229–1.339 ±0.046) | — |
| Medium — Deserialize (bytes) | 0.378 (0.359–0.397 ±0.016) | 2.803 (2.696–2.881 ±0.085) | — |
| Nested — Serialize (bytes) | 0.376 (0.372–0.488 ±0.050) | 2.316 (2.250–2.413 ±0.073) | — |
| Nested — Deserialize (bytes) | 0.571 (0.539–0.589 ±0.019) | 5.463 (5.404–5.976 ±0.234) | — |
| Large — Serialize (bytes) | 1.374 (1.329–1.385 ±0.028) | 5.481 (5.257–5.518 ±0.119) | — |
| Large — Deserialize (bytes) | 2.833 (2.575–2.952 ±0.165) | 14.131 (13.990–14.519 ±0.225) | — |
| Simple — SerializeToString | 0.120 (0.119–0.139 ±0.009) | 0.534 (0.530–0.600 ±0.030) | — |
| Simple — Deserialize←string | 0.149 (0.146–0.153 ±0.002) | 0.828 (0.782–0.857 ±0.032) | — |

> Source: **JIT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoToml.txt`** (table 2 of the
> same process as the Tomlyn table) — `PICOBENCH_SUMMARY scenarios=10 published=20 refused=0 mode=Jit
> equivalent=false cpu=known exit=0 exit_scope=process-so-far process_published=40
> process_refused=0`; per-arm other-CPU 6%–12%; 100,000–118,560 iterations per arm; warm-up 20/20
> converged (1.0–1.3 s, 26–502 bursts).

### JIT vs JIT — PicoYaml vs YamlDotNet (6 scenarios)

**YAML's comparable opponent is new in this session.** YamlDotNet 18.1.0's typed POCO API is one call
per row, and both of its serializers are built once, outside the measured delegates — rebuilding them
per call would time the builder instead of the operation, a mistake that measures ~80–120 µs/op where
these rows measure 6–20 µs/op. **Ratio = ours ÷ theirs: above 1.00 we are slower — every row here is
far below 1.00.**

| Scenario | PicoYaml (ours) | YamlDotNet | Pico ÷ YamlDotNet |
|---|---:|---:|---:|
| Simple — SerializeToString | 0.163 (0.162–0.237 ±0.033) | 7.107 (7.064–7.761 ±0.296) | 0.02× |
| Simple — Deserialize←string | 0.282 (0.262–0.322 ±0.026) | 6.043 (5.903–6.375 ±0.218) | 0.05× |
| Nested — SerializeToString | 0.567 (0.552–0.605 ±0.020) | 18.013 (17.901–18.268 ±0.155) | 0.03× |
| Nested — Deserialize←string | 1.000 (0.951–1.078 ±0.048) | 17.404 (17.264–17.804 ±0.242) | 0.06× |
| Collection — SerializeToString | 1.692 (1.579–1.704 ±0.054) | 69.600 (68.840–69.919 ±0.468) | 0.02× |
| Collection — Deserialize←string | 5.301 (4.974–5.355 ±0.155) | 84.649 (83.615–85.649 ±0.797) | 0.06× |

> Source: **JIT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoYaml.txt`** —
> `PICOBENCH_SUMMARY scenarios=6 published=12 refused=0 mode=Jit equivalent=true cpu=known exit=0
> exit_scope=process-so-far process_published=12 process_refused=0`; per-arm other-CPU 8%–16%;
> 100,000–104,615 iterations per arm; warm-up 12/12 converged (1.0–2.0 s, 4–866 bursts).

Read this table as a factor of ten, not as two significant digits:

- **The numerator is the jittery side.** Our `Simple — SerializeToString` is 0.163 ±0.033 — a stddev
  of 20% of the median, with a max of 0.237 (1.45× the median) — while every YamlDotNet arm has a
  stddev of 0.7%–4.2% of its own median. The `0.02×` therefore means "roughly fifty times faster",
  not "50.0".
- **YamlDotNet's two `Collection` arms converged on very little evidence.** The harness's convergence
  rule (three bursts within 5%) passed for them on 5 and 4 bursts, against 174 and 60 for ours. The
  protocol allows that; it is worth knowing before quoting `0.02×`.
- **The deserialize rows again favour the opponent on input shape:** our `Deserialize<T>(byte[])`
  decodes UTF-8 inside the timed region, YamlDotNet's `Deserialize<T>(string)` receives a string
  built outside it.
- The runner's own equivalence evidence: `[equiv] YamlDotNet round-trips our text = Hello World/42
  (one call each side; input text pre-encoded outside the delegates)`.

### Why there is no AOT YAML opponent

There is no YAML AOT table, and that is a property of YamlDotNet under NativeAOT rather than a gap in
our measurement. The same two calls the table above measures **do not work at all** when published
ahead of time:

- **Typed deserialization throws `MissingMethodException`** — `Deserialize<T>` cannot build the
  reflective machinery it relies on.
- **Typed serialization silently produces `{}`** — no exception, no warning, an empty document. That
  is worse than failing loudly, because a caller who does not diff its output would never notice.
- **DOM parsing still works** under NativeAOT. Only the typed POCO layer does not.

So a like-for-like AOT pair does not exist to publish: one side has no working operation. Comparing
our AOT numbers against a competitor arm that emits `{}` would measure nothing, and comparing them
against its DOM parse would be exactly the non-equivalent comparison this document refuses to make
for INI. YamlDotNet is therefore listed in the
[competitor/AOT matrix](../../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim) as a
library that runs ahead of time but does not map typed POCOs there, and PicoYaml gets **one** table —
on CoreCLR, against a real opponent.

### NON-EQUIVALENT — PicoIni, absolute cost only

**NON-EQUIVALENT — ini-parser has no equivalent operation to compare against, so no ratio is computed
or printed.** The run's own banner carries the evidence:

> `ini-parser 2.5.3 has no typed POCO API in any execution mode: 0 public generic methods and 0
> parameters of type System.Type — the only entry point is Parse(string) → IniData, a DOM. Any POCO
> mapping is therefore caller-written by definition, so the column above is this file's IniData walk
> plus conversion and there is no equivalent operation to divide by. Its DOM parsing is AOT-safe; the
> question "does reflection-based mapping survive AOT" never arises for it, because it has no
> reflective mapping to begin with.`

Ini-parser exposes **zero public generic methods and zero parameters of type `System.Type`**; its only
entry point is `Parse(string) → IniData`. A POCO mapping on its side is *always* the caller's own
code — never one library call against ours, in any execution mode — so no ratio can be formed. That
is why this table has no ratio column and `equivalent=false` in its summary line. PicoIni's own cost
is what is published; the competitor column exists in the log and is deliberately not reproduced,
so that no reader is invited to divide one by the other.

| Scenario | PicoIni median µs/op (min–max ±stddev) |
|---|---:|
| Simple — Serialize (bytes) | 0.106 (0.105–0.161 ±0.025) |
| Simple — Deserialize (bytes) | 0.13–0.17 across both sessions — see note |
| Medium — Serialize (bytes) | 0.238 (0.230–0.292 ±0.025) |
| Medium — Deserialize (bytes) | 0.336 (0.324–0.353 ±0.012) |
| Complex — Serialize (bytes) | 0.392 (0.378–0.398 ±0.008) |
| Large — Serialize (bytes) | 1.123 (1.108–1.190 ±0.033) |
| Large — Deserialize (bytes) | 2.476 (2.389–2.593 ±0.080) |
| Simple — SerializeToString | 0.100 (0.098–0.114 ±0.006) |
| Simple — Deserialize←string | 0.139 (0.131–0.139 ±0.004) |

> Source: **JIT · 2026-10-27 · `.superpowers/sdd/bench-measure-2/logs/PicoIni.txt`** —
> `PICOBENCH_SUMMARY scenarios=9 published=18 refused=0 mode=Jit equivalent=false cpu=known exit=0
> exit_scope=process-so-far process_published=18 process_refused=0`; per-arm other-CPU 7%–22%;
> 100,000–117,275 iterations per arm; warm-up 18/18 converged (1.0–1.6 s, 14–1299 bursts).
>
> **`Simple — Deserialize (bytes)` deliberately carries no single figure.** Between the two sessions
> its median moved −49.2% — the only row in the whole set that moved more than 20% — and the earlier
> value carried an outlier run well below its own median band. What is stable, and all that is
> claimed, is that **both sessions landed in the 0.13–0.17 µs band** for this row. Quoting either
> session's median would be quoting one of two numbers that disagree by half.
>
> `Simple — Serialize (bytes)` is the second-weakest row: max 0.161 against a median of 0.106. It
> stayed inside the protocol's 3.0× spread limit, so it is published rather than discarded, with that
> caveat attached.

### What these numbers do not say

They are not a claim about production throughput, latency under load, memory or cold start, and they
do not compare INI or the Tommy reference against anything. What they do say, however the table is
sliced: **on this machine and these micro-benchmarks, System.Text.Json and MessagePack-CSharp are
faster than PicoSerDe, while PicoSerDe is faster than Tomlyn under both JIT and AOT, and faster
than YamlDotNet's typed mapper under JIT.** In AOT, System.Text.Json and MessagePack-CSharp are
faster than PicoSerDe while Tomlyn is still slower, in the three formats where an AOT opponent
exists at all (JSON, MessagePack, TOML). YAML has no AOT opponent — YamlDotNet's typed layer does
not work ahead of time — so it is the only one of the five whose ratio column is JIT-only, and its
table says so.

So that "indistinguishable" is not left to the eye, it has one concrete meaning here: **the two arms'
min–max ranges overlap, or the ratio sits within the ±10% cross-session band.** Of the 36 ratio cells
on the four JSON and MessagePack tables, **31 are losses** and **5 are on our side of 1.00** — and of
those five only one clears both tests:

- **Ahead, and distinguishable:** MessagePack JIT `Nested — Serialize` **0.89×** (0.215–0.219 vs
  0.243–0.246 — no overlap, 11 points from 1.00).
- **Ahead, but inside the band:** AOT JSON `Nested POCO — Deserialize` **0.92×**, AOT MessagePack
  `Person — Serialize` **0.93×**, MessagePack JIT `Nested — Deserialize` **0.98×** (ranges overlap).
  Reported, not claimed.
- **Ahead, but the jittery row:** MessagePack JIT `Simple — Serialize` **0.76×** — ranges overlap,
  and this is the least stable arm on the page in both sessions.
- **Behind, but inside the band:** MessagePack JIT `Simple — Deserialize` **1.03×**.

Everything else on those four tables is a loss outside the band, from 1.07× to 5.60×. The TOML and
YAML wins are of a different order — 0.08×–0.41× and 0.02×–0.06×, i.e. 2.4×–12.5× and 17×–50× — and
the band does not touch them: a few percent of jitter on either side moves none of those rows.

PicoSerDe's claim is that it runs everywhere ahead of time and maps typed POCOs across five formats
(see the [competitor/NativeAOT matrix](../../README.md#nativeaot-support-by-competitor-verified-not-a-performance-claim)),
not that it is the fastest thing in it. Where it is slower it says so; where it is faster it names
the runtime that was measured and why the opponent's typed path cannot be measured on the other one.

---

## Known boundaries

Every caveat that travels with the figures above, in one list so that none of them is missed. Each
one is stated where it applies; this table is the index, not a second measurement.

| Boundary | Where it is stated |
|---|---|
| The machine was **not fully idle** — inside the protocol's limit, but not silent | [Environment, and what was imperfect about it](#environment-and-what-was-imperfect-about-it) |
| Our deserialize rows pay a UTF-8 decode the opponent does not; Tomlyn's serialize arm uses a reused `MemoryStream` | [AOT vs AOT — PicoToml vs Tomlyn](#aot-vs-aot--picotoml-vs-tomlyn-10-scenarios), [JIT vs JIT — PicoToml vs Tomlyn](#jit-vs-jit--picotoml-vs-tomlyn-10-scenarios) |
| Benchmark configuration cost is compile-time, not per-operation — so it is not timed on either side | the same two TOML sections, and [JIT vs JIT — PicoYaml vs YamlDotNet](#jit-vs-jit--picoyaml-vs-yamldotnet-6-scenarios) |
| `Nested` key order inside `[Address]` differs from Tomlyn's, and the difference cannot be quantified | [JIT vs JIT — PicoToml vs Tomlyn](#jit-vs-jit--picotoml-vs-tomlyn-10-scenarios) |
| INI's `Simple — Deserialize (bytes)` has no single quotable figure — both sessions land inside one band | [NON-EQUIVALENT — PicoIni, absolute cost only](#non-equivalent--picoini-absolute-cost-only) |
| AOT came out slower than JIT for **both** libraries; the cause is not established, so AOT cells are never divided by JIT cells | [Environment, and what was imperfect about it](#environment-and-what-was-imperfect-about-it) |
| The raw logs are not checked in (`.superpowers/` is gitignored) — only the quoted `PICOBENCH_SUMMARY` lines are independently checkable | [Measured results](#measured-results-2026-10-27) |
| Micro-benchmarks only: no I/O, no contention, no cold start, no allocation or streaming claim | [What these numbers do not say](#what-these-numbers-do-not-say) |
