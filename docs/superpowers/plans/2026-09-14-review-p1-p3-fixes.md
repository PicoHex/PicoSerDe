# PicoSerDe P1–P3 Review-Fix Plan

> For agentic workers: execute task-by-task with TDD (test first, watch it fail, minimal fix, watch pass, commit). Checkbox steps are inline in each task.

**Goal:** Fix the P1–P3 findings from the 2026-09-14 code review with test-first changes.

**Architecture:** Minimal, surgical fixes at the root-cause sites identified in the review; no speculative features. Generator fixes go in `shared/Gen` where possible so all 5 formats inherit.

**Tech stack:** C# / net10.0, TUnit 1.65.38 (MTP), Roslyn 5.9 incremental generators, CSharpier 1.3.0 (local tool).

**Spec:** the 2026-09-14 review report (this conversation) + `docs/bug-audit-report-2026-08-16.md` for prior-audit items.

**Constraints:**
- TDD for every production change. No production code before a failing test.
- Run tests with `dotnet run --project <test.csproj> -c Release -p:PublishAot=false` (tunit-runner skill: `dotnet test` is unreliable in this repo).
- Commit after each task; run `dotnet csharpier format <changed .cs>` before staging (git-commit-csharp skill).
- Work on branch `fix/review-p1-p3`. Never touch `main`.

**Scope decision (P1-1):** The review accepted "unpack Nullable<T> like int, **or skip**". Full top-level scalar support is a feature, not a fix. We skip top-level `Nullable<T>` and scalar-kind structs (Guid/DateOnly/…) so the generator never emits non-compiling code, and the runtime fails loudly ("No serializer registered") instead of silently emitting `{}`. Full scalar support can be a follow-up plan.

---

### Task 1 — P2-3: ScalarCodec over-long inputs throw FormatException

**Files:** `PicoSerDe.Core/src/ScalarCodec.cs`; Test: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/ScalarCodecTests.cs` (create if absent)

- [ ] RED: for each of `ParseChar`, `ParseVersion`, `ParseInt128`, `ParseUInt128`, `ParseNInt` (and `ParseHalf`), feed a 100-byte input and assert `FormatException`.
- [ ] Verify RED: tests fail with `ArgumentException` (probe confirmed: "output char buffer is too small").
- [ ] GREEN: add a length guard before `Decode` (pattern already used by `ParseDateTimeOffset`): if `utf8.Length > MaxChars` (or `> 4` for char) throw `FormatException` with the offending value.
- [ ] Verify GREEN + full Core test project.
- [ ] Commit: `fix(picoserde-core): throw FormatException for over-long extended scalar text`

### Task 2 — P1-3/P1-4: culture-invariant INI/TOML/YAML writers and YAML reader

**Files:** `PicoIni/src/PicoIni/IniWriter.cs`, `PicoToml/src/PicoToml/TomlWriter.cs`, `PicoYaml/src/PicoYaml/YamlWriter.cs`, `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`
**Tests:** `PicoIni/tests/PicoIni.Tests/CultureTests.cs`, same for Toml/Yaml (new files)

- [ ] RED (3 tests): set `CultureInfo.CurrentCulture = de-DE`, serialize `double 1.5`, assert output contains `1.5` (not `1,5`); restore culture in `finally`.
- [ ] RED (1 test): de-DE, `YamlSerializer.Deserialize<DoubleDto>("Value: 1.5")` must be `1.5`.
- [ ] Verify RED: probes confirmed `1,5` / `15`.
- [ ] GREEN: pass `CultureInfo.InvariantCulture` to every `TryFormat` for float/double/decimal in the 3 writers; emit `System.Globalization.CultureInfo.InvariantCulture` in the YAML generator's `float.Parse`/`double.Parse` sites (lines ~719/724/1182/1189/2724/2729).
- [ ] Verify GREEN + full 3 test projects.
- [ ] Commit: `fix(picoserde): make INI/TOML/YAML numeric I/O culture-invariant`

### Task 3 — P1-1: never emit broken code for top-level Nullable<T> / scalar structs

**Files:** `shared/Gen/GenInfrastructure.cs` (guard), `shared/Gen/TypeKindResolver.cs` if needed
**Tests:** `PicoJetson/tests/PicoJetson.Integration.Tests/GeneratorTopLevelScalarTests.cs` (new, uses `CSharpGeneratorDriver`)

- [ ] RED: driver test compiles a snippet calling `JsonSerializer.Deserialize<int?>("1")`; assert the produced compilation has no `CS0721/CS0722` and that running the generator emits no source for `Nullable`.
- [ ] Verify RED: current generator emits `Nullable`-typed broken code.
- [ ] GREEN: in `GenInfrastructure.TransformTypeSymbol`, return `null` when the type is `Nullable<T>` or `TypeKindResolver.Resolve` yields a scalar kind (not `object`/`list`/`array`/`dict`/`bytes`). This covers all 5 generators' usage/attribute pipelines.
- [ ] Add runtime regression test: `JsonSerializer.Serialize(Guid.NewGuid())` throws `InvalidOperationException` (not `{}`).
- [ ] Verify GREEN + full generators' test projects compile (many projects).
- [ ] Commit: `fix(picoserde): skip top-level scalar/nullable types in source generators`

### Task 4 — P1-2: anonymous types with integer family no longer crash the generator

**Files:** `shared/Gen/AnonTypeHandler.cs`
**Tests:** `tests/PicoSerDe.Integration.Tests/AnonTypeSerializationTests.cs` (extend)

- [ ] RED: serialize/deserialize-ready test: `JsonSerializer.Serialize(new { U = 1u, B = (byte)7, C = 'x', S = (short)-3, US = (ushort)3, Ul = 1ul })` asserts exact JSON.
- [ ] Verify RED: currently fails at runtime with "No serializer registered" (generator died with CS8785 KeyNotFoundException).
- [ ] GREEN: add `int16/uint16/uint32/uint64/sbyte/byte/char` to `TypeLayout` with correct sizes/alignments (2/2, 2/2, 4/4, 8/8, 1/1, 1/1, 2/2); in `BuildAnonTypeInfo`, return `null` when a field kind is missing from `TypeLayout` instead of indexing (no crash; unsupported kind is simply not intercepted).
- [ ] Verify GREEN + full Integration test project.
- [ ] Commit: `fix(picoserde-gen): support full integer family in anonymous types, never crash`

### Task 5 — P2-5a: JSON rejects invalid escape sequences

**Files:** `PicoJetson/src/PicoJetson/JsonReader.cs` (seq + span unescape paths), `PicoJetson/src/PicoJetson/PicoDocument.cs` (DOM unescape)
**Tests:** `PicoJetson/tests/PicoJetson.Unit.Tests/JsonReaderTests.cs`, `PicoDocumentTests.cs`

- [ ] RED: `"\q"` in a string → `FormatException` (reader and DOM `IsValid == false`); valid `\t`, `\u0041`, `\\`, `\"`, `\/`, `\b`, `\f`, `\n`, `\r` still parse.
- [ ] Verify RED: probe confirmed `\q` → `q` silently.
- [ ] GREEN: replace `default: buf[di++] = b;` with `throw new FormatException(...)` in all unescape paths.
- [ ] Verify GREEN + full Jetson test projects (PicoDocument/JSON tests may rely on lenient escapes — fix any such test only if it asserts incorrect behavior).
- [ ] Commit: `fix(picojetson): reject invalid escape sequences`

### Task 6 — P2-5b: JSON rejects leading commas

**Files:** `PicoJetson/src/PicoJetson/JsonReader.cs`
**Tests:** `PicoJetson/tests/PicoJetson.Integration.Tests/StrictDeserializationTests.cs`

- [ ] RED: `,5` and `[,1]` throw `FormatException`; `[1,2]` and trailing-comma option behavior unchanged.
- [ ] Verify RED: probe confirmed `[,1]` → `[1]`.
- [ ] GREEN: in the comma branch of `Read()`, throw when the previous `_tokenType` is `None`, `ObjectStart`, or `ArrayStart` (leading comma). Keep `AllowTrailingCommas` handling untouched.
- [ ] Verify GREEN + full Jetson projects.
- [ ] Commit: `fix(picojetson): reject leading commas`

### Task 7 — P2-5c: TryReadNextInt32 accepts int.MinValue

**Files:** `PicoJetson/src/PicoJetson/JsonReader.cs`
**Tests:** `PicoJetson/tests/PicoJetson.Unit.Tests/ReaderRobustnessTests.cs`

- [ ] RED: `TryReadNextInt32` on `-2147483648` returns true with `int.MinValue`; `2147483648` still false; `2147483647` true.
- [ ] Verify RED: probe confirmed `false` for `int.MinValue`.
- [ ] GREEN: accumulate in `long`; bail only when `result > int.MaxValue + (neg ? 1 : 0)`.
- [ ] Verify GREEN + unit project.
- [ ] Commit: `fix(picojetson): accept int.MinValue in TryReadNextInt32 fast path`

### Task 8 — P2-5d: TOML fast-path integer overflow

**Files:** `PicoToml/src/PicoToml/TomlReader.cs`
**Tests:** `PicoToml/tests/PicoToml.Tests/TomlReaderFastPathTests.cs`

- [ ] RED: `TryReadInt32ArrayFast([99999999999])` and `TryReadInt64ArrayFast([99999999999999999999])` return 0 (bail), not wrapped values.
- [ ] Verify RED: probe confirmed wrapped values.
- [ ] GREEN: overflow-checked accumulation (bail/return 0) in both fast paths; same guard in `TryReadNextInt32Span` if reachable.
- [ ] Verify GREEN + TOML test project.
- [ ] Commit: `fix(picotoml): bail on integer overflow in fast array readers`

### Task 9 — P2-5e: JsonReader no longer double-returns pooled buffers

**Files:** `PicoJetson/src/PicoJetson/JsonReader.cs`
**Tests:** `PicoJetson/tests/PicoJetson.Unit.Tests/ReaderRobustnessTests.cs`

- [ ] RED: with a >256-char string in sequence mode, `reader.Dispose()` must leave `ReturnedBufferCount == PeakTrackedBufferCount` (expose an internal counter).
- [ ] Verify RED: current code returns the grown buffer at growth time AND again at Dispose (returns=3 vs peak=2).
- [ ] GREEN: do not `ArrayPool.Return` the old buffer at growth sites; keep it tracked so `Dispose` returns it exactly once.
- [ ] Verify GREEN + unit project.
- [ ] Commit: `fix(picojetson): stop double-returning pooled buffers on string growth`

### Task 10 — P2-1: generated code stops emitting nullability warnings

**Files:** JSON/MsgPack/TOML/YAML generators (dict null-value handling + any-dict helper type); test csproj guards
**Tests:** `tests/PicoSerDe.Integration.Tests` + `PicoJetson/tests/PicoJetson.Integration.Tests` (WarningsAsErrors guard), null-dict runtime test

- [ ] RED (build-level): add `CS8619;CS8620;CS8625` to `WarningsAsErrors` in both integration test projects → build fails on generated code (verified: CS8625 ×5 sites, CS8619 ×6 sites).
- [ ] RED (runtime): JSON `{"m":{"k":null}}` into `Dictionary<string,string>` throws `FormatException`; into `Dictionary<string,string?>` yields null.
- [ ] GREEN: emit `null` only for nullable reference value types (`ElementIsNullableReference`); otherwise throw. Emit `Dictionary<string, object?>` in the any-dict helper / inner helpers where the target is `object?`.
- [ ] Verify GREEN: both integration projects build with the guard and pass.
- [ ] Commit: `fix(picoserde): generate nullability-clean dictionary deserializers`

### Task 11 — P2-2: options reachable from INI/TOML/YAML/MsgPack public APIs

**Files:** `PicoIni/src/PicoIni/IniSerializer.cs`, `PicoToml/src/PicoToml/TomlSerializer.cs`, `PicoYaml/src/PicoYaml/YamlSerializer.cs`, `PicoMsgPack/src/PicoMsgPack/MsgPackSerializer.cs`
**Tests:** each project's `OptionsTests.cs`

- [ ] RED: `SerializeToUtf8Bytes(value, new TomlOptions { Indented = true })` overload does not exist (compile) — write the test and watch it fail to compile (this is the RED for an API addition).
- [ ] GREEN: add `XxxOptions? options = null` parameters and forward to `SerializerFacade`; add options to MsgPack `Deserialize`.
- [ ] Verify: `Indented = true` produces indented output per format; defaults unchanged; full 4 test projects.
- [ ] Commit: `feat(picoserde): expose options on INI/TOML/YAML/MsgPack serializer APIs`

### Task 12 — P2-4: MsgPack rejects empty and trailing-data payloads

**Files:** `PicoMsgPack/src/PicoMsgPack/` (serializer/reader/generated top-level deserializer emitter)
**Tests:** `PicoMsgPack/tests/PicoMsgPack.Tests/` (new strict test)

- [ ] RED: `Deserialize<Dto>([] )` throws `FormatException`; valid payload + 1 trailing byte throws.
- [ ] Verify RED: probe confirmed default object / silent success.
- [ ] GREEN: emit/serialize a trailing-byte check + empty-input check in the MsgPack top-level deserialize path.
- [ ] Verify GREEN + MsgPack tests.
- [ ] Commit: `fix(picomessagepack): reject empty and trailing-byte payloads`

### Task 13 — P2-6: AssemblyPrefix concurrency isolation

**Files:** `shared/Gen/GenInfrastructure.cs`
**Tests:** `PicoJetson/tests/PicoJetson.Integration.Tests/GenInfrastructureGuardTests.cs`

- [ ] RED: set `AssemblyPrefix` on one `Task.Run` flow; assert another concurrent flow sees `null` (AsyncLocal semantics), and the setter/getter still work in the same flow.
- [ ] GREEN: back the `AssemblyPrefix` property with `AsyncLocal<string?>` (netstandard2.0 compatible, no call-site changes).
- [ ] Verify GREEN + all generator-consuming test projects (JSON/INI/TOML/YAML/MsgPack).
- [ ] Commit: `fix(picoserde-gen): isolate AssemblyPrefix per execution flow`

### Task 14 — P3-1: PICO*004 diagnostic only when anonymous types are actually used

**Files:** 5 generators' `RegisterSourceOutput(compilationProvider)` blocks + anon pipelines
**Tests:** `PicoJetson/tests/PicoJetson.Integration.Tests/GeneratorDiagnosticTests.cs` (driver-based)

- [ ] RED: driver test on a snippet with NO anonymous serializer usage and `AllowUnsafeBlocks=false` → assert no `PICOJETSON004`; with an anon usage → assert `PICOJETSON004`.
- [ ] Verify RED: warning fires unconditionally (28 across the repo build).
- [ ] GREEN: report the diagnostic only when an anon candidate exists (thread the capability flags through the anon pipeline output).
- [ ] Verify GREEN: full rebuild has no PICO*004 in projects without anon usage.
- [ ] Commit: `fix(picoserde-gen): report unsafe-anonymous diagnostic only when used`

### Task 15 — P3: documentation / hygiene / CI

**Files:** `README.md`, `PicoYaml/tests/PicoYaml.Tests/YamlReaderFastPathTests.cs`, `JsonSerializerGenerator.cs` (CS8604), `.github/workflows/ci.yml`, `.gitignore`

- [ ] README: update test counts to the measured 1249 (per-module table); correct the "Every format's options" claim for the now-reachable APIs (Task 11) and the nullability claim.
- [ ] YAML: fix the stale `docs/beta-issues.md` reference; add overflow guard to `TryReadInt32ArrayFast`/`TryReadInt64ArrayFast` (same as Task 8).
- [ ] Generator CS8604: null-check before `EmitExtendedScalarWrite`/`ExtendedScalarReadExpr`.
- [ ] CI: switch `dotnet test` to `dotnet run --project … -p:PublishAot=false` per tunit-runner skill (or add an explicit `--`-forwarded invocation).
- [ ] `.gitignore`: add `.superpowers/` (and `docs/superpowers/` if the team wants it out of git).
- [ ] Commit: `docs: sync README counts and hygiene notes`

---

## Self-review of this plan

- **Spec coverage:** every P1/P2/P3 finding from the review maps to a task (P1-1→3, P1-2→4, P1-3/4→2, P2-1→10, P2-2→11, P2-3→1, P2-4→12, P2-5a→5, P2-5b→6, P2-5c→7, P2-5d→8, P2-5e→9, P2-6→13, P3-1→14, P3 rest→15).
- **Placeholders:** none — every task names exact files and the failing behavior to assert.
- **Type consistency:** no new public type names besides optional `options` parameters matching existing `XxxOptions` classes.
