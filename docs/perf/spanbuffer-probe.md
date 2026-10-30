# PicoSerDe — the span-manager probe, and why it did not become a change

Record behind [`measurement.md`](measurement.md). This page answers one question that the
performance archive cannot: **should the two writers' self-managed span bookkeeping move into
`PicoSerDe.Core` as one shared `SpanBuffer`?** It was measured, answered **no**, and the answer is
written down here — with the numbers, the two things that were technically *possible*, and the
conditions under which the question may be reopened — so that the next person does not have to run
the probe again to find out.

Nothing here changed a shipped line. The probe lived on branch `probe/spanbuffer` (forked from
`36a3d3d`) with **no production file touched**; the two writers still own their own `_span` /
`_spanPos` / `Ensure` / `Flush` today, and the one durable outcome of the probe is the cross-format
[commit-timing test](../../tests/PicoSerDe.Integration.Tests/WriterCommitTimingTests.cs) that pins
*when* each writer publishes its bytes (see [What the probe left behind](#what-the-probe-left-behind)).

Contents: [The question](#the-question) · [Q1 — does the language allow it?](#q1--does-the-language-allow-it)
· [Q2 — is the hot path still free of regressions?](#q2--is-the-hot-path-still-free-of-regressions)
· [The verdict](#the-verdict-dont_lift) · [Why it is not worth it](#why-it-is-not-worth-it)
· [If it is ever redone](#if-it-is-ever-redone) · [What the probe left behind](#what-the-probe-left-behind)
· [Evidence and reproduction](#evidence-and-reproduction)

---

## The question

`PicoJetson.JsonWriter` and `PicoMsgPack.MsgPackWriter` each cache a span from the
`IBufferWriter<byte>` they write into and touch that buffer only per *chunk*, not per token:

```csharp
private Span<byte> _span;   // bytes handed out by _buffer, not yet committed
private int _spanPos;       // write cursor inside _span

private void Ensure(int n) { if (_span.Length - _spanPos < n) { Flush(); _span = _buffer.GetSpan(...); } }
private void Flush()       { if (_spanPos > 0) { _buffer.Advance(_spanPos); _spanPos = 0; } _span = default; }
```

That is two fields, two methods and a comment block, **twice**. Lifting it into `PicoSerDe.Core` as a
`ref struct SpanBuffer` would delete the duplicate and give a third format one mechanism to reuse —
`−43` lines of pure code (`−21` JSON, `−22` MessagePack; `−75` counting the doc comments and
`[MethodImpl]` attributes) for `+≈120` lines of `SpanBuffer` including its own docs. The probe asked
whether the abstraction is free at run time. Two sub-questions, in this order, because the second is
the expensive one.

## Q1 — does the language allow it?

Six minimal console projects (`repro/v1_embed … v6_spanprop`), each compiled **twice** with
`LangVersion=default` and `LangVersion=latest`: twelve builds, and the answer does not depend on the
language version.

| Variant | What it asks | `default` | `latest` |
|---|---|---|---|
| `v1_embed` | a `ref struct` holds `private SpanBuffer _b;` and delegates `WriteByte`/`WriteRaw` to it | succeeded, 0 warning | same |
| `v2_refparam` | the embedded field passed by `ref` to a static helper, and `ref var b = ref _b;` locally | succeeded, 0 warning | same |
| `v3_refreturn` | `public ref SpanBuffer BufferRef() => ref _b;` | **FAILED (CS8170)** | **FAILED (CS8170)** |
| `v4_byvalue` | `public SpanBuffer Inner => _b;` — hand the manager out by value | succeeded (and **silently lost bytes**) | same |
| `v5_stackonly` | a writer holding the manager used in a method with `stackalloc` + `scoped ReadOnlySpan<byte>` | succeeded, 0 warning | same |
| `v6_spanprop` | writing straight through it: `_b.Span[i] = v`, `value.TryFormat(_b.Span[pos..], …)`, `src.CopyTo(…)`, `Encoding.UTF8.GetBytes(chars, …)` | succeeded, 0 warning | same |

**Q1's answer is "yes, with one hard limit".** Embedding a `ref struct` in a `ref struct`, passing
it by `ref`/`scoped`, using it behind a `ref` local, and writing through its `Span` all compile and
run. The limit is CS8170 — *struct members cannot return `this` or other instance members by
reference* — so the ideal accessor (`ref SpanBuffer SpanBuffer => ref _buf;`) **does not exist**;
crossing a method boundary needs a `ref` parameter or a `ref` local.

`v4` is the reason any such plan needs a rule instead of an accessor. Handing the manager out **by
value** compiles, runs, and **loses bytes in silence** — the copy carries its own cursor and span,
so writes through the copy never reach the original. That is the same shape as the `BuildMap`
incident that dropped four bytes: a `ref struct` copy is not a reference. Any `SpanBuffer` would have
to forbid by-value access at the API level (no `Inner`-style property at all), not by documentation.
**Q1 was never the obstacle.**

## Q2 — is the hot path still free of regressions?

The same probe rewrote the production hot path four ways and timed them against each other in one
process, alternating A/B, rotating the starting arm every round:

| Arm | Shape |
|---|---|
| **A** | today: the writer's own `_span`/`_spanPos` + its own `Ensure`/`Flush` (a verbatim copy of the production code) |
| **B** | lift, verbatim: embed `SpanBuffer`, delegate at token level (`_buf.Span`, `_buf.Pending`, `_buf.Ensure(…)`) — derived mechanically from A by `gen_arms.py`, which fails if a single span token survives |
| **C** | lift + one span read per reservation hoisted into a local (`hoist.py`) — the shape a maintainer would actually write |
| **D** | lift + `ref var b = ref _buf;` at the top of each hot method (`ref_local.py`) — the only way to "bind" a struct field |

Method, because a 2 % claim is worthless without it: **16 arms** — the four shapes × two
byte-identical copies, one such set for each of the two formats — over **5 workloads**, round-robin
in one process, every sample the minimum of 5 timed repetitions, pairwise ratios taken inside the same
round, 60 warm-up samples per (arm, workload) so tier-1 code is what is being timed, and **3
independent processes per environment**. The two copies of a shape (`A1`/`A2`, `B1`/`B2`, …) differ
only by the runtime's own per-type codegen, so the distance between them is the **CONTROL band** —
and no shape difference counts until it crosses that band. Byte equivalence is asserted before any
timing: `all arms byte-identical`, and a separate `eqcheck` project proves arm A equal to the real
`JsonWriter`/`MsgPackWriter` byte for byte, so the thing being timed is the thing that ships.

Workloads: `json-longstr` (8 × 300-char strings — `TryWriteLongString` + `WriteRaw`), `json-propname`
(30 property-name tokens — `WritePropertyName`), `json-numbers` (100 numbers — `WriteFormattable<int>`,
the hottest per-token path), `mp-int32array` (1024-element `int[]` — the `WriteInt32Array` fast run),
`mp-strings` (16 × 40-byte strings).

### NativeAOT — the environment with the tightest CONTROL band

Ratios of shape to today (median of three processes; four copy-crossings each):

| workload | B (verbatim lift) | C (hoisted) | D (ref-local) | same-code CONTROL | reading |
|---|---|---|---|---|---|
| `json-longstr` | +0.5…+1.7 % | +0.2…+2.3 % | −0.3…+1.0 % | ±0.7 % | level |
| `json-propname` | **+2.9…+3.7 %** | **+2.0…+5.2 %** | **+1.1…+4.7 %** | ±2.0 % | **slower** |
| `json-numbers` | +0.3…+1.5 % | −6.1…+3.7 % (copies disagree) | −0.8…+1.5 % | ≤1 % | level |
| `mp-int32array` | +2.3…+34 % (copies disagree) | −0.4…+31.6 % (copies disagree) | −2.3…+0.2 % | 28…30 % | **not decidable** |
| `mp-strings` | **+4.3…+6.9 %** | **+3.4…+6.9 %** | **+1.6…+3.0 %** | ±2.6 % | **slower** |

The worst row is `mp-strings`: B1/A1 **+6.6 %**, B1/A2 **+6.9 %**, B2/A1 **+4.7 %**, B2/A2 **+4.3 %**
— four copy-crossings, all the same sign, against a CONTROL band of ±2.6 %. `json-propname` is
+2.9/+3.5/+3.2/+3.7 % against ±2.0 %. Two independent copies of the *same* lifted source land in the
band, so this is the shape, not the copy. (`mp-int32array` reads +28…30 % between two byte-identical
copies on this environment, which is ILC's per-type inlining flipping — that row is reported as
undecidable rather than as evidence in either direction.)

### `DOTNET_TieredCompilation=0` — the JIT environment the repo's protocol uses

| workload | B | C | D |
|---|---|---|---|
| `json-longstr` | −1.4 % | +0.3 % | +2.4 % |
| `json-propname` | +0.9…+3.6 % | +1.7…+3.5 % | +0.2…+1.0 % |
| `json-numbers` | **+1.2…+1.8 %** | **+3.5…+6.0 %** | **+8.4…+9.7 %** |
| `mp-int32array` | −21 % (sign flips vs AOT) | −20 % (sign flips) | −41 % (sign flips) |
| `mp-strings` | **+6.6…+9.5 %** | **+1.1…+7.2 %** | +0.5…+2.7 % |

Same two rows again — `json-numbers` and `mp-strings` — and `mp-int32array` flips sign relative to
AOT, which is why it counts as evidence in neither direction.

### Default environment (tiered compilation + dynamic PGO)

```
json-propname  A2/A1   +45.87 %   +43.98 %   +39.90 %   median +43.98 %   <-- two copies, one source
mp-strings     D/A    +140.36 %  +140.06 %  +137.13 %   median +140.06 %
mp-int32array  D2/D1   +2.15 %   -26.28 %   +11.67 %   median  +2.15 %
```

Two byte-identical copies of one source differ by **44 %** here, and a shape ratio swings to
**+140 %**. The CONTROL band on this environment is therefore **±40…140 %**, one to two orders of
magnitude wider than the ±2 % being judged: the default environment **cannot in principle** answer
"did this row get 2 % slower", and a ratio from it that looks like a −25 % win (`mp-strings` B/A) is
the same measurement noise. That is also why the repo's published JIT figures fix the JIT side at
`DOTNET_TieredCompilation=0` (see [What a published number requires](measurement.md#what-a-published-number-requires)).

## The verdict: `DONT_LIFT`

Judged on the two environments whose CONTROL band is narrower than the effect:

- **NativeAOT**: `mp-strings` +4.3…6.9 % (B), `json-propname` +1.1…5.2 % across the three shapes.
- **JIT(TC=0)**: `json-numbers` +1.2…9.7 %, `mp-strings` +1.1…9.5 % — the same two families.
- **No shape escapes.** Not plain delegation, not hoisted span reads, not `ref` locals. `ref` locals
  are best (`mp-int32array` −2.3…+0.2 %, level) and still +1.1…4.7 % on `json-propname`.
- **The default environment is not evidence in either direction** — its CONTROL band is 20× the effect.

The mechanism matches the numbers: today `_span`/`_spanPos` are the writer's **direct** fields; lifted
they become `_writer._buf._span` / `_writer._buf._pos`, one extra base-load on the per-token path, and
a write to `_buf._pos` the JIT/ILC cannot always prove distinct from a loop-local `s = _buf.Span`, so
the span gets reloaded. Token-dense loops (`json-numbers`, `mp-int32array`) go first; `json-longstr`
does not, because a 900-byte reservation dominated by UTF-8 encoding hides the bookkeeping — which is
exactly why "big strings look fine" is not a rebuttal. The rows that regress are the ones that pay
the indirection **once per token**: property names and short strings.

## Why it is not worth it

| | |
|---|---|
| **Gained** | `−43` lines of duplicated mechanism (`−75` with docs), one `SpanBuffer` for a future third self-managing format to reuse |
| **Paid** | **+4.3…6.9 % on `mp-strings`, +1.1…5.2 % on `json-propname` under NativeAOT** — the column PicoSerDe publishes as its strongest — and `+≈120` new lines in `Core`, including an API that must forbid by-value access to avoid `v4`'s silent byte loss |

Trading a 4–7 % regression on the hottest paths of the strongest column for the deletion of 43 lines
of straightforward, well-commented code is not a trade. **The abstraction is correct and the
duplication is cheaper than the abstraction** — that is the whole finding, and it is a finding about
the *runtime*, not about the design: the same bytes leave the same branches either way.

One consequence is a rule, not a change: **the commit policy stays in each writer.** `SpanBuffer` as
drafted would only have provided mechanism (`Ensure` / `Advance` / `Flush` / `Refill` / `Write`); JSON
still decides "commit when the structural depth is back at 0" and MessagePack still decides "commit on
the `Buffer` hand-off or when the outermost container closes". Parameterising that would have turned
two deliberate policies into one configuration knob. The two-answer policy is now a tested contract —
see below.

## If it is ever redone

Reopening this is legitimate (a third format needing the mechanism, or a runtime that stops charging
for the extra indirection), but it needs two things this probe could not supply:

1. **Fix the default-environment noise first.** The blocker is not the ±4 % AOT regression, it is that
   the *default* environment cannot resolve ±2 % at all: two copies of one source read 44 % apart.
   Before the question is asked again, the harness has to hold a CONTROL band under ~2 % there —
   longer rounds, a quieter window, and **≥4 independent copies per shape** (with 2 copies, ILC's
   per-type inlining can flip 28…30 % inside one shape, as it did on `mp-int32array`). More rounds is
   cheap; a wrong ±2 % conclusion is not.
2. **Judge on AOT against its own CONTROL band**, not on a default-JIT win. The decision rule that
   works is "no row worse than the same-code CONTROL band on NativeAOT", because that is the
   environment where the two-copy band is ±0.2…2.6 %.

The harness is reusable as it stands: 16 arms, the mechanical derivation scripts (`gen_arms.py`,
`hoist.py`, `ref_local.py` — each with a self-check that the derived source contains no surviving span
token), the `eqcheck` fidelity project, `run_matrix.sh` for 3 environments × 3 processes, and
`summarize.py`. Keep the byte-identity assertion and the `eqcheck` proof: they are what make a timing
comparison mean "same bytes, different bookkeeping".

## What the probe left behind

The probe's one durable outcome is the alternative it recommended instead of the lift: **write the
commit timing down as a test.** `tests/PicoSerDe.Integration.Tests/WriterCommitTimingTests.cs` asserts
both writers' published contracts in one place —

- the agreements: an empty document commits nothing; **no token inside an open container is
  committed**; closing the outermost container commits the tail; reading `Buffer` commits what is
  pending and is idempotent; a document crossing several spans commits every byte exactly once; both
  serializers leave nothing pending at the public entry point;
- the deliberate divergence: a **document-level** value is published by `JsonWriter` the moment the
  call returns (`CommitBare()` at depth 0) and is **staged** by `MsgPackWriter` until the caller hands
  off (`_ = mw.Buffer;`, the call generated serializers emit).

That test makes the divergence reviewable: a change that quietly makes one writer behave like the
other — in either direction — fails there, and has to argue its case. It does not need an abstraction
to be true.

## Evidence and reproduction

The probe's record is session-local, like this page's raw logs: worktree `.worktrees/probe-spanbuffer`
(branch `probe/spanbuffer`, forked from `36a3d3d`), report at
`.superpowers/sdd/probe-spanbuffer/report.md`, with

| Path (under that worktree) | Contents |
|---|---|
| `repro/v1_embed … v6_spanprop/` | Q1's six minimal projects; `repro/logs/q1-compile.txt` holds all twelve build logs |
| `bench/JsonInline.cs`, `bench/MpInline.cs` | arm A — a verbatim copy of the production hot path |
| `bench/SpanBuffer.cs` | the archived `SpanBuffer` API draft (mechanism only; policy stays in the writers) |
| `bench/gen_arms.py`, `hoist.py`, `ref_local.py`, `gen_program.py` | mechanical derivation of arms B/C/D and the arm pipeline, each with a self-check |
| `bench/logs/{jit,tc0,aot}-p{1,2,3}.txt` | the nine processes' raw `ROUND` / `LOAD` lines |
| `bench/logs/summary-{jit,tc0,aot}.txt` | the three environments × three processes summary this page quotes |
| `eqcheck/` | arm A == real `JsonWriter`/`MsgPackWriter`, byte for byte |

```bash
cd .worktrees/probe-spanbuffer/.superpowers/sdd/probe-spanbuffer

# Q1: six variants × two LangVersions
cd repro
for v in v1_embed v2_refparam v3_refreturn v4_byvalue v5_stackonly v6_spanprop; do
  for lv in default latest; do dotnet build $v/$v.csproj -m:1 -v:q -p:LangVersion=$lv -c Release; done
  dotnet run --project $v/$v.csproj -c Release
done

# Q2: byte identity, then the matrix
cd ../eqcheck && dotnet run -c Release --project EqCheck.csproj
cd ../bench
python gen_arms.py && python hoist.py && python ref_local.py && python gen_program.py
dotnet build SpanBufferBench.csproj -m:1 -c Release
dotnet run -c Release --no-build --project SpanBufferBench.csproj -- assert
dotnet publish SpanBufferBench.csproj -c Release -r win-x64 -p:PublishAot=true -o aot
./aot/SpanBufferBench.exe assert
bash run_matrix.sh 15 60 3
for t in jit tc0 aot; do python summarize.py --tag $t logs/$t-p{1,2,3}.txt; done
```

**Not covered.** Writer side only (which is where the mechanism lives); `NeedsEscapingVectorized` in
the arms uses BCL `IndexOfAny` rather than the production `JsonReader.IndexOfQuoteEscapeOrControl`
(shared by both sides of every ratio, so it does not bias the comparison); two independent copies per
shape rather than three, which is why `mp-int32array` could only be called undecidable.
