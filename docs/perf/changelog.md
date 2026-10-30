# PicoSerDe — what each round of performance changes did

Archive behind [`README.md` → Performance](../../README.md#performance). Every change that moved a
number on that page: what changed, which row it moved, and the number it moved from and to. **Moved
rather than summarised** — the original table, its two kinds of evidence and every caveat about
them are reproduced below exactly as the README carried them, its heading promoted from `###` to `##`.
The index above the record is the same 23 rows in reverse chronological order, generated from that
table, with its numbers copied verbatim. The published three-round median table these rows are
measured against lives in [`measurement.md`](measurement.md#the-five-rows-as-published).

## Rounds, newest first

One line per round: what changed (with the commit that landed it), the row it moved, and the
before/after numbers that round's own evidence quotes.

| # | Commit | What changed | Row it moved | Before | After |
|--:|---|---|---|---|---|
| 1 | `5a5d895` `7324990` `fc4ee5c` `a2b4224` | MessagePack: a flat int list read in one call | JIT `Collection — Deserialize`; JIT `Collection — Serialize`; AOT (the changes' own A/B — this table's AOT benchmark has no collection scenario) | 1.016 µs — **1.43×**; 0.254 µs — **1.24×** (10:20–11:39 window); AOT read **1270 ns**, AOT write **779.2 ns** | 0.382 µs — **0.55×** (−62.4 % µs); 0.136 µs — **0.60×** (−46.5 % µs); AOT read **468 ns (−63 %)**, AOT write **182.1 ns (0.234×)** |
| 2 | `f46e672` `0234838` | JSON write: a short ASCII string encoded in one pass, and a property name written as one literal | JIT `Nested POCO — Serialize`; AOT `Nested POCO — Serialize`; AOT `LargeFlat 50-fields — Serialize` | 0.272 µs — **0.79×**; 0.448 µs — **0.96×**; 1.609 µs — **1.00×** (10:20–11:39 window) | 0.187 µs — **0.51×** (−31.3 % µs); 0.312 µs — **0.69×** (−30.4 % µs); 1.395 µs — **0.92×** (−13.3 % µs) |
| 3 | `67435ab` `62177eb` `9bf719b` | JSON read: the cold throws hoisted out of the token loop, and the case-sensitivity option read once | JIT `LargeFlat 50-fields — Deserialize`; AOT `LargeFlat 50-fields — Deserialize` | 2.602 µs — **1.36×**; 3.011 µs — **0.97×** | 2.693 µs — **1.27×** (+3.5 % µs); 3.207 µs — **0.95×** (+6.5 % µs) — the µs carry the 10:20–11:39 window's offset, so the row's claim is the ratio; in this window the same two rows read 2.654 µs / **1.33×** and 3.356 µs / **0.98×** |
| 4 | `b9546d6` `28d7c26` | JSON write: temporals and numbers written through the span | AOT `Collection POCO — Serialize`; JIT `Collection POCO — Serialize`; JIT `Complex POCO — Serialize` | 2.056 µs — **1.31×**; 0.994 µs — **0.82×**; 0.367 µs — **1.20×** | 1.397 µs — **0.80×** (−32.1 % µs); 0.882 µs — **0.67×** (−11.3 % µs); 0.330 µs — **0.91×** (−10.1 % µs) |
| 5 | `4781c1f` `9861c78` | JSON read: DateTime and enum values answered from the span | JIT `Complex POCO — Deserialize`; AOT `Complex POCO — Deserialize` | 0.831 µs — **1.70×**; 1.148 µs — **1.32×** | 0.695 µs — **1.19×** (−16.4 % µs); 0.934 µs — **1.01×** (−18.6 % µs) |
| 6 | `7b1a3b7` `177c550` | JSON write: a long string is encoded straight into the writer's own span | JIT `LargeString 1KB — Serialize`; AOT `LargeString 1KB — Serialize` | 0.346 µs — **1.59×**; 0.536 µs — **1.29×** | 0.177 µs — **0.76×** (−48.8 % µs); 0.333 µs — **0.74×** (−37.9 % µs) |
| 7 | `588db56` `f94fb8d` | MessagePack: a collection's elements are accounted in a counted scope | JIT `Collection — Deserialize` | 1.078 µs — **1.56×** | 0.853 µs — **1.37×** (−20.9 % µs) |
| 8 | `9fd79a8` `bc2255e` | JSON read: one PeekByte per token, with int64 and bool decoded in the same dispatch as the tag | JIT `Complex POCO — Deserialize`; AOT `Complex POCO — Deserialize` | 0.894 µs — **1.54×**; 1.280 µs — **1.38×** | 0.831 µs — **1.70×** (−7.0 % µs; the ratio rises because the System.Text.Json arm read 0.581 → 0.490 µs); 1.148 µs — **1.32×** (−10.3 % µs) |
| 9 | `8fc1db4` `8d59010` | JSON write: a property name is staged and emitted as one write | JIT `Nested POCO — Serialize`; AOT `Nested POCO — Serialize` | 0.373 µs — **1.12×**; 0.679 µs — **1.46×** | 0.253 µs — **0.81×** (−32.2 % µs); 0.471 µs — **1.07×** (−30.6 % µs) |
| 10 | `491e731` | MessagePack: a collection with a known length iterates that count | JIT `Nested — Deserialize` | 0.363 µs — **0.89×** | 0.324 µs — **0.87×** (−10.7% µs) |
| 11 | `a3faf4d` `f5a59e6` | MessagePack: TryReadInt32/Int64/Float64/Bool fused | AOT `Person — Deserialize` | 0.134 µs — **0.99×** | 0.112 µs — **0.80×** (−16.4% µs) |
| 12 | `1eba0fb` | MessagePack: the integer value derived while the tag is in hand | JIT `Collection — Deserialize` | 1.432 µs — **1.96×** | 1.078 µs — **1.56×** (−24.7% µs) |
| 13 | `886f1b9` `bbceb67` | JSON read: TryReadInt32 fused into one pass | JIT `Collection POCO — Deserialize` | 4.694 µs — **1.51×** | 2.255 µs — **0.67×** (−52.0% µs) |
| 14 | `58f3c9f` | JSON strings: "does this need escaping" answered in one vectorized pass | JIT `LargeString 1KB — Serialize` | 1.020 µs — **4.72×** | 0.389 µs — **1.66×** (−61.9% µs) |
| 15 | `2310b36` | JSON write: the writer owns a span | JIT `LargeFlat 50-fields — Serialize` | 1.628 µs — **1.79×** | 1.319 µs — **1.30×** (−19.0% µs) |
| 16 | `d45edca` `d9334ed` `340d351` | MessagePack: the read side gets a span fast path | AOT `Metrics — Deserialize`; AOT `Person — Deserialize` | 0.206 µs — **1.51×** (08:41 window); **1.14×** (no µs published) | 0.123 µs — **0.87×** (−40 % µs); 0.103 µs — **0.77×** (1.14× → 0.77×) — that `Metrics` row rested on **eight** kept rounds in the 10:20–11:39 window because five of them refused one of its arms as *unstable*; in this window it reads 0.126 µs / **0.88×** on `r1`/`r2`/`r3` |
| 17 | `d4da7cc` | MessagePack: DateTime written as the spec's timestamp extension | JIT `Metrics — Deserialize` | 0.518 µs — **4.16×**; payload **53 B** | 0.156 µs — **1.30×**; payload **30 B** |
| 18 | `0a45e16` | MessagePack: strings encoded straight into that span | 15-string model, allocation per serialize | **600 B/op** (15 × a 40-byte array) | **0 B/op**; CPU **0.687×** (median of three paired runs) |
| 19 | `56d86e5` | MessagePack: the writer owns a span | JIT `Collection — Serialize` | 0.474 µs — **2.06×** | 0.300 µs — **1.28×** (−37%) |
| 20 | `9e13e78` | MessagePack: timestamp64 read the spec's bit layout backwards | reading a `timestamp64` extension | `2023-11-14T22:15:23.1234567Z` decoded as **`1973-11-29T21:33:10.7000001Z`** | decoded correctly; wire and speed unchanged — a correctness fix, not a speed claim |
| 21 | `4c5d107` | JSON strings: SIMD scan, long strings kept zero-copy | JIT `LargeString 1KB — Deserialize` | 1.472 / 1.660 / 1.743 µs — **3.70×–5.71×** | 0.260 / 0.261 / 0.267 µs — **0.91×–1.07×** (−84%) |
| 22 | `583b551` | JSON write: property names written from compiled UTF-8 constants | JIT `LargeFlat 50-fields — Serialize` | median **2.244 µs** of 2.340 / 2.147 — 2.06× | median **1.874 µs** of 1.849 / 1.874 / 1.893 (−16.5%) |
| 23 | `5169116` | JSON read: a property name resolved through an exact-match index | JIT `LargeFlat 50-fields — Deserialize` | 6.120 / 6.132 µs — **2.82× / 2.97×** | 3.095 / 3.184 µs — **1.47× / 1.42×** (−49%) |

## What the last round of changes did

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
