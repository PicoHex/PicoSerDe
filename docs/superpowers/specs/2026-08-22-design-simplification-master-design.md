# PicoSerDe Design Simplification — Master Spec

**Status**: Implemented (all 4 phases merged; commits below)

**Implementation log**:
- Phase 1 (options + facade + streaming): `ce5ce0a` `5316806` `c4341dd` `91292b7` `bdb5ad3` `8e03541`
- Phase 2 (attribute consolidation): `b8bf700`
- Phase 3 (ITokenReader + hardening + TokenType): `c72774d`
- Phase 4 (build config centralization): `0fd8f1b` + follow-up commit
**Date**: 2026-08-22
**Topic**: 6 consolidation workstreams across PicoSerDe.Core + 5 format modules (PicoIni / PicoJetson / PicoMsgPack / PicoToml / PicoYaml)
**Scope**: Master planning spec. Each phase gets its own detailed spec before implementation. This document fixes the direction, boundaries, and dependencies between phases.

---

## 1. Motivation

PicoSerDe grew as 5 parallel format implementations (INI / JSON / MsgPack / TOML / YAML) around one shared core (PicoSerDe.Core, formerly PicoSerDe.Abs). The shared layer is real (TokenType, SerRegistry, GenInfrastructure, TypeKindResolver) but stops at the half-way point: each format still carries near-identical copies of the serializer facade, attribute families, reader contracts, and options plumbing. The result is duplicated code, API inconsistency between formats, and — per the 2026-08-16 bug audit — the same class of bug recurring independently in 3 readers.

The TFM unification question is **already settled**: PicoSerDe.Abs → PicoSerDe.Core has been net10.0 since scaffold (commit `1a934e5`), all 26 non-Gen projects are net10.0, and the 5 Gen source generators stay netstandard2.0 (Roslyn host compatibility — this is the PicoHex standard, matching org repo commit `8a62dd7`). This spec is about the remaining structural duplication.

## 2. Verified Baseline

Facts below were re-verified on 2026-08-22 against a clean full rebuild (`rm -rf bin/obj` + `dotnet build PicoSerDe.slnx -c Release` → **Build succeeded, 35 warnings, 0 errors**).

| # | Fact | Evidence |
|---|------|----------|
| 1 | 31 csproj total: 26 non-Gen all net10.0; 5 Gen netstandard2.0 | csproj scan |
| 2 | Serializer facades: Ini 145, Toml 144, Yaml 144, MsgPack 184, Json 604 lines; shared method surface: Register / RegisterDeserializer / RegisterStreaming / SerializeToUtf8Bytes / Serialize ×2 / Deserialize / DeserializeFromStreamAsync | line count + method diff |
| 3 | `RegisterCustom` exists in JSON and MsgPack only | grep |
| 4 | Options classes: Json 192 lines (full naming-policy family); Ini/Toml/Yaml/MsgPack 16–24 lines each; **all five use `[ThreadStatic] static Current`** | source |
| 5 | Streaming: **4 formats (INI/TOML/YAML/MsgPack) use an identical state-export `DeserializeStreamingCore`** (reader reconstructed from exported state at `NeedMoreData`); **JSON is the sole outlier** — its reader has `ExportState`, but `DeserializeFromStreamAsync` accumulates bytes and re-parses from scratch per chunk (audit P3: O(n²)) | source |
| 6 | 43 attribute files = 37 module-private + 6 Core cross-format (`PicoIgnore`, `PicoSerializable`, `PicoDerivedType`, `PicoPolymorphic`, `GenerateSerializer`, `InterceptsLocation`); key-attribute shapes diverge: `IniKeyAttribute.Name(string)`, `TomlKeyAttribute.Key(string)`, `YamlKeyAttribute.Key(string)`, `MsgPackKeyAttribute.Key(int)`; Ignore attributes are identical except class name | file count + diff |
| 7 | All 5 readers are `ref struct`s with a near-identical method surface (Read / TryGetInt32 / TryGetInt64 / TryGetFloat64 / TryGetBool / TrySkip / NeedsMoreData / Depth / BytesConsumed) | source |
| 8 | TokenType: UInt8/16/32/64, Int32/64, Float32/64 used by real readers/writers; **Int8 / Int16 / Float16 used by nothing except the enum-completeness test `TokenTypeTests`** | repo-wide grep |
| 9 | 20 projects repeat `PublishAot=true` + `TrimMode=full`; `Directory.Build.props` is minimal (no AOT-level pattern) | csproj grep |
| 10 | 5 Gen projects each `Compile Include` the same 5 shared files (`TypeKindResolver`, `GenInfrastructure`, `AnonTypeHandler`, `AnonFormatConfig`, `IsExternalInit`) | csproj |
| 11 | Audit (2026-08-16, 1110 tests green): P1 = `JsonOptions.Current` `[ThreadStatic]` lost across `await` in streaming + cross-request bleed; P0 ×3 = same out-of-bounds bug class in Yaml/Ini/Toml readers (key scan ignores line ends, unconditional `_position++`); P3 = O(n²) streaming re-parse | audit report |
| 12 | `PicoSerDe.Abs.dll/.pdb` stale artifacts in `out/` (pre-rename leftovers) | file list |

## 3. Cross-Cutting Constraints

- **Public API preservation**: consumers' source code and SG-generated code must keep compiling across phases (see §8 for the breaking-change policy). Generated code in consumer assemblies calls `PicoIni.IniSerializer.Register(...)` etc. — facade method names must not move.
- **Zero-allocation hot paths**: registry dispatch (`SerRegistry<,>`, `SerDelegate<T>`) and reader token loops stay allocation-free. No per-call closures on the serialize/deserialize path.
- **AOT-first**: every phase must keep `IsAotCompatible` clean; no reflection added; no Microsoft.* dependencies (PicoHex stack rule).
- **Perf parity**: existing benchmarks (PicoBench projects) are the gate — each phase re-runs them and must not regress the measured hot paths.
- **Existing tests stay green**: 1110 tests across 9 test projects are the regression floor; each phase adds its own tests (see §7).
- **Gen stays netstandard2.0**: no phase may change the Gen TFM.

## 4. Phase 1 — Options Context Redesign (B) + Serializer Facade Consolidation (A)

**Goal**: eliminate the ambient `[ThreadStatic] Current` options mechanism (root cause of audit P1) and collapse the 5 near-identical facades into one shared implementation.

**Design direction — options (B)**:
- Options stop flowing through ambient thread state. They become an **explicit parameter** on the public facade entry points — JSON already has it (`Serialize<T>(T, JsonOptions?)`); the other 4 formats **gain a nullable options parameter** (additive, default `null`). Options are **threaded explicitly through generated serialize/deserialize helper code**.
- The registry hot path (`SerRegistry<,>`, `SerDelegate<T>` = `void(IBufferWriter<byte>, T)`) is **unchanged** — the delegate signature decision (keep vs. add an options slot) is deferred to the Phase 1 spec, with the constraint that no per-call allocation may appear.
- Async streaming paths capture options into locals before the first `await`; generated code reads options only from explicit parameters, never from static state.
- The `[ThreadStatic] Current` fields are removed from all 5 options classes. A compatibility shim (obsolete accessor that throws or returns a stored default) may exist only if the Phase 1 spec can prove no consumer-generated code path reads it.

**Design direction — facade (A)**:
- One shared facade implementation lives in PicoSerDe.Core (generic over a per-format marker type, e.g. `SerializerFacade<TFormat>`), covering: Register / RegisterDeserializer / RegisterStreaming / SerializeToUtf8Bytes / Serialize ×2 / Deserialize / DeserializeFromStreamAsync + the streaming core loop.
- Each format's public static class (`IniSerializer`, `JsonSerializer`, ...) becomes a thin partial wrapper over the shared core, preserving the exact public method surface (constraint §3).
- JSON-only extras (JSONL: `SerializeLines` / `DeserializeLines` / `DeserializeAsyncEnumerable`, `RegisterCustom`, `SerializeCustom`) stay JSON-only but are implemented on top of the shared core where possible.
- Streaming strategy converges on **state-export** — already the pattern in 4 of 5 serializers (identical `DeserializeStreamingCore` copies). Phase 1: (a) collapses the 4 copies into the shared facade core, (b) migrates JSON's `DeserializeFromStreamAsync` to the same pattern (its reader already has `ExportState`; the O(n²) re-parse loop is deleted). (Boundary: no half-migrated readers.)

**Deliverables (master level)**: Phase 1 spec with (a) exact options threading design incl. SerDelegate decision, (b) facade core API, (c) migration of the 5 public classes, (d) regression tests for the P1 scenario (async streaming with options, concurrent requests), (e) streaming chunk-boundary round-trip tests (every byte boundary) for all 5 formats, (f) benchmark gate.

## 5. Phase 2 — Attribute Family Consolidation (D)

**Goal**: one shape per attribute concept across the 5 formats; shared implementation where semantics are identical; no silent behavior changes.

**Design direction**:
- **Normalize shapes**: string-key attributes expose `Key` consistently (`TomlKey.Key`, `YamlKey.Key` are the target shape; `IniKeyAttribute.Name` gets an `[Obsolete]` alias to `Key`); `MsgPackKeyAttribute.Key(int)` stays int (binary format, different semantics — documented exception).
- **Share implementations**: attributes whose semantics are identical across formats (Ignore, CamelCase, Serializable, Constructor, DateTimeFormat, Converter) get a single shared implementation. Two candidate mechanics, decided in the Phase 2 spec:
  1. per-format public attribute classes in Core base + per-format subclass (public API unchanged);
  2. per-format attributes become thin wrappers over one internal Core implementation (file-count reduction, public API unchanged).
- **Cross-format attributes already in Core** (`PicoIgnore`, `PicoSerializable`) are not renamed; module-private duplicates that overlap them (e.g. `IniIgnoreAttribute` vs `PicoIgnoreAttribute`) are documented as aliases with a single implementation.
- **No semantic changes**: identical wire behavior before/after; migration guide per package; `[Obsolete]` with message, not silent removal.

**Deliverables (master level)**: Phase 2 spec with the mechanics decision, the full attribute matrix (37 module + 6 Core → target shape), and per-package migration notes.

## 6. Phase 3 — Token Reader Contract (E) + Text-Scan Hardening + TokenType Cleanup

**Goal**: one reader contract, one formalized streaming contract, one set of line-scan helpers — eliminating the audit's recurring bounds-bug class.

**Design direction — contract (E)**:
- New `ITokenReader` interface in PicoSerDe.Core, constrained `allows ref struct` (C# 13 ref-struct interfaces; all 5 readers are already `ref struct` — verified). Member set follows the shared surface from §2#7.
- All 5 readers implement it. SG-generated deserializer emit paths may genericize over the contract where it pays (decision in Phase 3 spec; the fallback is: contract exists for cross-format tooling/tests, generated code keeps concrete reader types for JIT quality).
- Readers remain `ref struct`; zero-alloc guarantee stays.

**Design direction — streaming contract**: the state-export streaming contract (already implemented in 4 formats; JSON migrated in Phase 1) is formalized as the single contract — chunk-boundary semantics, `NeedMoreData` handling, and state round-trip rules are pinned down here so generated streaming code and the Phase 1 facade core agree.

**Design direction — hardening**:
- `TextHelpers` gains line-bound-aware scan helpers (`ScanUntilLineEnd`-family). Yaml/Ini/Toml readers adopt them, closing audit P0 (key scan ignoring `\n`/`\r` + unconditional `_position++`). The Phase 3 spec enumerates the exact call sites from the audit report.

**Design direction — TokenType cleanup**:
- Remove `Int8` / `Int16` / `Float16` (unused by every format — only `TokenTypeTests` references them; that test is updated). Fix the stale "reserved" comment block. UInt8/16/32/64, Int32/64, Float32/64 stay (real usage).

**Deliverables (master level)**: Phase 3 spec with the ITokenReader member set, the state-export contract shape, the TextHelpers API, and the audit-call-site checklist.

## 7. Phase 4 — Build Configuration Centralization (G) + Housekeeping

**Goal**: remove per-project AOT repetition; clean stale artifacts.

**Design direction**:
- Adopt the org-level `AotOptimizationLevel` pattern in `Directory.Build.props`: `minimal` (default: `PublishAot=true`, `TrimMode=full`) for tests; `aggressive` for samples/benchmarks; **netstandard2.0 guard block after the level blocks** (overrides `PublishAot=false`, `IsAotCompatible=false`, `IsTrimmable=true`, `TrimMode=partial`, `EnableTrimAnalyzer=true`) — the guard serves the 5 Gen projects exactly as in the org standard.
- The 20 projects that currently repeat `PublishAot=true` + `TrimMode=full` declare only their level (or nothing → inherit `minimal`).
- Delete stale `out/PicoSerDe.Abs.*` artifacts. Historical plan docs referencing `PicoSerDe.Abs` are kept (history).
- The interceptor mechanism (`InterceptorsNamespaces` in `Directory.Build.props` + per-module `build/*.targets`) is **out of scope** — it works as designed.

**Deliverables (master level)**: Phase 4 spec (small; may be folded into Phase 1's plan as a prep commit).

## 8. Compatibility & Migration Policy

- **Baseline**: min TFM net10.0 confirmed; Gen netstandard2.0 unchanged.
- **Source compatibility**: consumer source code compiles unchanged against every phase (constraints §3). Public API additions only, except explicitly listed breaks below.
- **Explicit breaks (each gated behind its own spec + major-version bump of the affected packages)**:
  - Phase 1: removal of `[ThreadStatic] Current` fields (public API removal); `SerDelegate<T>` signature gains `SerOptions?`; `DesRegistry` storage type becomes `DeserializeDelegate<T>?`; per-format `StreamingFunc<T>` delegates are replaced by Core `StreamingFunc<TReader, T>` (with a `partial` resume parameter); generated `ISerializer`/`IDeserializer` wrapper classes are removed (internal detail) — all implemented 2026-08-22.
  - Phase 2: `IniKeyAttribute.Name` → `Key` (obsolete first, removed one minor later).
  - Phase 3: TokenType enum value removal (`Int8/Int16/Float16`).
- Generated code contract changes (SG output) are internal to the SG pipeline — consumer *source* is the compatibility surface, not the generated *binary*.
- README updates per phase (public API changes), release notes via existing release flow.

## 9. Testing & Verification Strategy

- **Regression floor**: 1110 existing tests stay green per phase (full suite run, not subset).
- **Phase 1**: new tests — options survive async streaming (P1 scenario: two concurrent streams with different options); no-options default path; facade parity tests (each format's public surface delegates correctly).
- **Phase 2**: attribute equivalence tests (old attr ↔ new shape produce identical wire output); obsolete-alias compile tests.
- **Phase 3**: `ITokenReader` conformance tests × 5 formats; audit-P0 regression tests (the exact inputs from the audit report); TokenType enum update.
- **Phase 4**: build matrix check (test/sample/benchmark inherit correct levels; Gen unaffected).
- **Perf gate**: PicoBench runs for reader dispatch (Phase 3), facade path (Phase 1), streaming (Phase 1/3). No regression on measured hot paths.

## 10. Risks & Open Questions

| Risk | Mitigation |
|------|-----------|
| Options threading through generated code is invasive (all 5 SG emit paths) | Phase 1 spec enumerates every emit call site first; generated-code contract changes are internal to the SG pipeline |
| Ref-struct interface dispatch JIT/AOT cost unknown | Phase 3 benchmarks gate the genericize-vs-concrete decision |
| JSON streaming migration (Phase 1) could regress strict-deserialization behavior (11db424) | Existing strict-deserialization tests + audit report test inputs as the guard |
| Attribute aliasing complexity (37 classes) | Phase 2 spec defines the full matrix before any code change |
| Phase coupling (options ↔ facade ↔ streaming) | Phases sequenced so each has an independent shippable state; Phase 1 boundary note in §4 |

**Open questions (deferred to phase specs)**:
- Phase 1: `SerDelegate<T>` signature — keep `(IBufferWriter<byte>, T)` vs. add options slot; shared options base class vs. per-format classes; JSON streaming migration scope (reuse the 4-format `DeserializeStreamingCore` shape).
- Phase 2: wrapper-vs-subclass mechanics; whether `PicoIgnore`/`PicoSerializable` become the canonical attributes (breaking) or stay parallel.
- Phase 3: exact `ITokenReader` member set; generated-code genericization scope; streaming state contract formalization details.
- Phase 4: `aggressive` level adoption for samples/benchmarks.

## 11. Phase Sequencing & Dependencies

```
Phase 1 (B+A: options + facade)      ← no dependencies; highest value (kills P1)
   │
   ├──→ Phase 3 (E: reader contract + hardening)    ← builds on Phase 1 facade core (streaming contract)
   │
   └──→ Phase 2 (D: attributes)      ← independent of 1/3
   │
   └──→ Phase 4 (G: build config)    ← independent; may land first as prep
```

- Each phase ends in a green build + green full test suite + green benchmarks.
- ✅ All four phases are implemented, tested (1174 tests green), and merged on `main`.
- Phase 1 is the entry point. Phase 4 may be executed before Phase 1 as housekeeping (no dependency).
- Each phase spec is written (writing-plans flow) only after the previous phase's implementation is merged and green.

## 12. Out of Scope

- Gen TFM change (stays netstandard2.0).
- Interceptor mechanism redesign.
- New format support; TokenType extension.
- Reader/writer algorithm rewrites beyond the audit call sites.
- Performance work beyond parity (the gates above are parity gates, not optimization budgets).
